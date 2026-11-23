using System.Numerics;
using Snowballs;

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
}

Vector3 standing = ThrowPhysics.Velocity(0, 0, false, Vector3.Zero);
Vector3 underhand = ThrowPhysics.Velocity(0, 0, true, Vector3.Zero);
Vector3 rotated = ThrowPhysics.Velocity(0, 90, false, Vector3.Zero);
Vector3 movement = new(100, -20, 25);
Vector3 moving = ThrowPhysics.Velocity(0, 0, false, movement);
Check(standing.X > 600 && standing.Z > 0, "A level throw has forward speed and lift.");
Check(Math.Abs(standing.Length() - 675) < 0.01f, "Full throw speed.");
Check(Math.Abs(underhand.Length() - 292.5f) < 0.01f, "Underhand throw speed.");
Check(Vector3.Distance(rotated, new Vector3(0, standing.X, standing.Z)) < 0.01f, "Yaw rotates flight.");
Check(Vector3.Distance(moving - standing, movement * 1.25f) < 0.01f, "Movement is inherited.");
Check(ThrowPhysics.CooldownTicks < ThrowPhysics.LifetimeTicks, "Rapid throws can overlap.");
Console.WriteLine("6 snowball physics checks passed.");
