using System.Numerics;

namespace Snowballs;

internal static class ThrowPhysics
{
    internal const int CooldownTicks = 48;
    internal const int LifetimeTicks = 160;

    internal static Vector3 Velocity(float pitch, float yaw, bool underhand, Vector3 movement)
    {
        const float degrees = MathF.PI / 180;
        pitch -= (90 - MathF.Abs(pitch)) * (10f / 90);
        float horizontal = MathF.Cos(pitch * degrees);
        var forward = new Vector3(
            horizontal * MathF.Cos(yaw * degrees),
            horizontal * MathF.Sin(yaw * degrees),
            -MathF.Sin(pitch * degrees));
        return forward * (underhand ? 292.5f : 675f) + movement * 1.25f;
    }
}
