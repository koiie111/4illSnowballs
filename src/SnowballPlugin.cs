using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Vector3 = System.Numerics.Vector3;

namespace Snowballs;

[MinimumApiVersion(380)]
public sealed class SnowballPlugin : BasePlugin
{
    public override string ModuleName => "4illSnowballs";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "4ill";

    private const string Carrier = "weapon_healthshot";
    private const string ProjectileClass = "decoy_projectile";
    private const string BallModel = "models/particle/sin_sphere.vmdl";
    private const string Trail = "particles/weapons/cs_weapon_fx/weapon_snowball_trail.vpcf";
    private const string Impact = "particles/weapons/cs_weapon_fx/weapon_snowball_impact.vpcf";
    private const string Screen = "particles/weapons/cs_weapon_fx/weapon_snowball_impact_screen.vpcf";
    private const string ParticleClass = "info_particle_system";

    private sealed class Supply(uint weapon)
    {
        internal CHandle<CBasePlayerWeapon> Weapon { get; } = new(weapon);
        internal int NextThrow { get; set; }
        internal PlayerButtons Primed { get; set; }
    }

    private sealed class Flight(uint pawn, Vector3 origin, int expires)
    {
        internal uint Pawn { get; } = pawn;
        internal Vector3 Previous { get; set; } = origin;
        internal int Expires { get; } = expires;
        internal uint Trail { get; set; }
    }

    private readonly Dictionary<uint, Supply> _supplies = [];
    private readonly Dictionary<uint, Flight> _flights = [];
    private readonly Dictionary<uint, uint> _privateParticles = [];
    private readonly HashSet<uint> _particles = [];
    private readonly List<uint> _expired = [];
    private readonly TraceOptions _trace = new() { InteractsWith = Masks.Shot };
    private int _generation;
    private bool _trackingFlights;
    private bool _loaded;

    public override void Load(bool hotReload)
    {
        _loaded = true;
        RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        RegisterEventHandler<EventPlayerDeath>((e, _) =>
        {
            if (e.Userid?.PlayerPawn.Value is { } pawn) RemoveSupply(pawn.EntityHandle.Raw);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDisconnect>((e, _) =>
        {
            if (e.Userid?.PlayerPawn.Value is { } pawn) RemoveSupply(pawn.EntityHandle.Raw);
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnMapEnd>(Reset);
        RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            manifest.AddResource(BallModel);
            manifest.AddResource(Trail);
            manifest.AddResource(Impact);
            manifest.AddResource(Screen);
        });
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnButtons);
        RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnDamage);
        RegisterListener<Listeners.CheckTransmit>(OnTransmit);
        AddCommandListener("drop", (player, command) =>
            player?.PlayerPawn.Value is { } pawn && IsSelected(pawn, out _)
                ? HookResult.Handled : HookResult.Continue, HookMode.Pre);
        AddCommand("css_snowballs_status", "Snowball entity counts (server console)", (player, _) =>
        {
            if (player is null)
                Logger.LogInformation("Snowballs: {Supply} supplies, {Flight} projectiles, {Particle} particles",
                    _supplies.Count, _flights.Count, _particles.Count);
        });

        if (!hotReload) return;
        foreach (var player in Utilities.GetPlayers()) QueueSupply(player);
    }

    public override void Unload(bool hotReload)
    {
        _loaded = false;
        Reset();
    }

    private HookResult OnSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player) QueueSupply(player);
        return HookResult.Continue;
    }

    private void QueueSupply(CCSPlayerController player)
    {
        int generation = _generation;
        Server.NextFrame(() =>
        {
            if (!_loaded || generation != _generation || !player.IsValid || !player.PawnIsAlive ||
                player.TeamNum is not (2 or 3) || player.PlayerPawn.Value is not { } pawn ||
                _supplies.ContainsKey(pawn.EntityHandle.Raw)) return;

            var weapons = pawn.WeaponServices;
            if (weapons is null) return;
            foreach (var item in weapons.MyWeapons)
            {
                if (item.Value is { IsValid: true, DesignerName: Carrier }) return;
            }

            var csWeapons = weapons.As<CCSPlayer_WeaponServices>();
            bool previous = csWeapons.DisableAutoDeploy;
            csWeapons.DisableAutoDeploy = true;
            try
            {
                var item = player.GiveNamedItem<CBasePlayerWeapon>(Carrier);
                if (item is not { IsValid: true }) return;
                item.NextPrimaryAttackTick = int.MaxValue;
                item.NextSecondaryAttackTick = int.MaxValue;
                Utilities.SetStateChanged(item, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
                Utilities.SetStateChanged(item, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
                _supplies.Add(pawn.EntityHandle.Raw, new Supply(item.EntityHandle.Raw));
            }
            finally
            {
                csWeapons.DisableAutoDeploy = previous;
            }
        });
    }

    private bool IsSelected(CCSPlayerPawn pawn, out Supply? supply) =>
        _supplies.TryGetValue(pawn.EntityHandle.Raw, out supply) &&
        pawn.WeaponServices?.ActiveWeapon.Raw == supply.Weapon.Raw;

    private void OnButtons(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        const PlayerButtons attacks = PlayerButtons.Attack | PlayerButtons.Attack2;
        if (((pressed | released) & attacks) == 0 || !player.PawnIsAlive ||
            player.PlayerPawn.Value is not { } pawn) return;
        if (!IsSelected(pawn, out var supply))
        {
            if (supply is not null) supply.Primed = 0;
            return;
        }

        supply!.Primed |= pressed & attacks;
        var thrown = supply.Primed & released;
        if (thrown == 0) return;
        supply.Primed = 0;
        if (Server.TickCount < supply.NextThrow) return;
        supply.NextThrow = Server.TickCount + ThrowPhysics.CooldownTicks;
        Throw(pawn, (thrown & PlayerButtons.Attack) == 0);
    }

    private void Throw(CCSPlayerPawn pawn, bool underhand)
    {
        if (pawn.AbsOrigin is not { } feet) return;
        var look = pawn.EyeAngles;
        var eye = Coordinates(feet) + new Vector3(pawn.ViewOffset.X, pawn.ViewOffset.Y, pawn.ViewOffset.Z);
        var velocity = ThrowPhysics.Velocity(look.X, look.Y, underhand, Coordinates(pawn.AbsVelocity));
        var projectile = Utilities.CreateEntityByName<CDecoyProjectile>(ProjectileClass);
        if (projectile is not { IsValid: true }) return;

        uint raw = projectile.EntityHandle.Raw;
        uint pawnHandle = pawn.EntityHandle.Raw;
        var flight = new Flight(pawnHandle, eye, Server.TickCount + ThrowPhysics.LifetimeTicks);
        _flights.Add(raw, flight);
        projectile.OwnerEntity.Raw = pawnHandle;
        projectile.Thrower.Raw = pawnHandle;
        projectile.Damage = 0;
        projectile.DispatchSpawn();
        if (!projectile.IsValid)
        {
            _flights.Remove(raw);
            return;
        }

        projectile.ExpireTime = Server.CurrentTime + 6;
        projectile.SetModel(BallModel);
        projectile.Teleport(Vector(eye), new QAngle(0, 0, 0), Vector(velocity));
        flight.Trail = SpawnParticle(Trail, eye, parent: projectile);
        if (!_trackingFlights)
        {
            _trackingFlights = true;
            RegisterListener<Listeners.OnTick>(TrackFlights);
        }
    }

    private HookResult OnDamage(CCSPlayerPawn pawn, CTakeDamageInfo info)
    {
        if (_flights.TryGetValue(info.Inflictor.Raw, out var flight))
        {
            uint projectile = info.Inflictor.Raw;
            var weapon = new CHandle<CBaseEntity>(projectile).Value;
            var point = weapon?.AbsOrigin is { } hit ? Coordinates(hit) : flight.Previous;
            // Defer removal until the engine has finished processing contact.
            int generation = _generation;
            uint victim = pawn.EntityHandle.Raw;
            Server.NextFrame(() =>
            {
                if (_loaded && generation == _generation) Finish(projectile, point, victim);
            });
            return HookResult.Handled;
        }
        return HookResult.Continue;
    }

    private void TrackFlights()
    {
        foreach (var (raw, flight) in _flights)
        {
            var projectile = new CHandle<CDecoyProjectile>(raw).Value;
            if (projectile is not { IsValid: true } || projectile.AbsOrigin is not { } origin)
            {
                _expired.Add(raw);
                continue;
            }
            var current = Coordinates(origin);
            var owner = new CHandle<CCSPlayerPawn>(flight.Pawn).Value;
            if (Vector3.DistanceSquared(current, flight.Previous) > 0.001f)
            {
                var result = Trace.TraceEndShape(Vector(flight.Previous), Vector(current), owner, _trace);
                if (result.DidHit())
                {
                    var entity = result.HitEntity();
                    uint victim = entity.Handle != nint.Zero && entity.IsValid && entity.DesignerName == "player"
                        ? entity.EntityHandle.Raw : 0;
                    Finish(raw, Coordinates(result.EndPos), victim);
                    continue;
                }
            }

            if (projectile.Bounces > 0)
                Finish(raw, current, 0);
            else if (Server.TickCount >= flight.Expires)
                _expired.Add(raw);
            else
                flight.Previous = current;
        }

        foreach (uint raw in _expired) RemoveFlight(raw);
        _expired.Clear();
    }

    private void Finish(uint raw, Vector3 position, uint victim)
    {
        if (!RemoveFlight(raw)) return;
        SpawnParticle(Impact, position, lifetime: 1.5f);
        if (victim != 0 && new CHandle<CCSPlayerPawn>(victim).Value is { IsValid: true } pawn &&
            pawn.AbsOrigin is { } feet)
        {
            SpawnParticle(Screen, Coordinates(feet) + new Vector3(pawn.ViewOffset.X, pawn.ViewOffset.Y, pawn.ViewOffset.Z),
                parent: pawn, privatePawn: victim, lifetime: 1.5f);
        }
    }

    private uint SpawnParticle(string effect, Vector3 position, CBaseEntity? parent = null,
        uint privatePawn = 0, float lifetime = 0)
    {
        var particle = Utilities.CreateEntityByName<CParticleSystem>(ParticleClass);
        if (particle is not { IsValid: true }) return 0;
        uint raw = particle.EntityHandle.Raw;
        _particles.Add(raw);
        if (privatePawn != 0) _privateParticles.Add(raw, privatePawn);
        particle.EffectName = effect;
        particle.DispatchSpawn();
        particle.Teleport(Vector(position));
        if (parent is { IsValid: true }) particle.AcceptInput("SetParent", parent, parent, "!activator");
        particle.AcceptInput("Start");
        if (lifetime > 0) particle.AddEntityIOEvent("Kill", delay: lifetime);
        return raw;
    }

    private void OnTransmit(CCheckTransmitInfoList infos)
    {
        if (_privateParticles.Count == 0) return;
        foreach (var (info, player) in infos)
        {
            uint pawn = player?.PlayerPawn.Raw ?? 0;
            foreach (var (raw, recipient) in _privateParticles)
            {
                if (pawn != recipient)
                {
                    int index = (int)(raw & 0x7fff);
                    info.TransmitEntities.Remove(index);
                    info.TransmitAlways.Remove(index);
                }
            }
        }
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        uint raw = entity.EntityHandle.Raw;
        if (_flights.TryGetValue(raw, out var flight))
        {
            int generation = _generation;
            _flights.Remove(raw);
            Server.NextFrame(() =>
            {
                if (_loaded && _generation == generation) RemoveParticle(flight.Trail);
            });
            StopTrackingIfIdle();
        }
        _particles.Remove(raw);
        _privateParticles.Remove(raw);
        if (_supplies.ContainsKey(raw))
        {
            int generation = _generation;
            Server.NextFrame(() =>
            {
                if (_loaded && _generation == generation) RemoveSupply(raw);
            });
        }
    }

    private bool RemoveFlight(uint raw)
    {
        if (!_flights.Remove(raw, out var flight)) return false;
        RemoveParticle(flight.Trail);
        if (new CHandle<CDecoyProjectile>(raw).Value is { IsValid: true } projectile)
            projectile.Remove();
        StopTrackingIfIdle();
        return true;
    }

    private void StopTrackingIfIdle()
    {
        if (!_trackingFlights || _flights.Count != 0) return;
        _trackingFlights = false;
        RemoveListener<Listeners.OnTick>(TrackFlights);
    }

    private void RemoveParticle(uint raw)
    {
        _privateParticles.Remove(raw);
        if (_particles.Remove(raw) && new CHandle<CParticleSystem>(raw).Value is { IsValid: true } particle)
            particle.Remove();
    }

    private void RemoveSupply(uint pawn)
    {
        if (_supplies.Remove(pawn, out var supply) && supply.Weapon.Value is { IsValid: true } weapon)
            weapon.Remove();
    }

    private void Reset()
    {
        _generation++;
        foreach (uint raw in _flights.Keys.ToArray()) RemoveFlight(raw);
        foreach (uint raw in _particles.ToArray()) RemoveParticle(raw);
        foreach (uint pawn in _supplies.Keys.ToArray()) RemoveSupply(pawn);
    }

    private static Vector3 Coordinates(Vector v) => new(v.X, v.Y, v.Z);
    private static Vector Vector(Vector3 v) => new(v.X, v.Y, v.Z);
}
