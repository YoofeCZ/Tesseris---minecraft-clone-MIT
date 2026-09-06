using OpenTK.Mathematics;

namespace Tesseris.Game.Blocks;

/// <summary>Geometrie nízkého sbíratelného předmětu ležícího uvnitř voxelu.</summary>
public static class GroundClutterShape
{
    public const int AngleStepDegrees = 15;
    private const int AngleSteps = 360 / AngleStepDegrees;

    public static float AngleRadians(int worldX, int worldY, int worldZ) =>
        (Hash(worldX, worldY, worldZ) % AngleSteps) * (MathF.PI / 12f);

    /// <summary>Otočí bod po zemi kolem středu voxelu.</summary>
    public static Vector3 TransformPoint(Vector3 point, float angle)
    {
        var centre = new Vector3(0.5f, 0f, 0.5f);
        return centre + TransformDirection(point - centre, angle);
    }

    public static Vector3 InversePoint(Vector3 point, float angle)
    {
        var centre = new Vector3(0.5f, 0f, 0.5f);
        return centre + TransformDirection(point - centre, -angle);
    }

    public static Vector3 TransformDirection(Vector3 direction, float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);

        return new Vector3(
            (direction.X * cos) + (direction.Z * sin),
            direction.Y,
            (direction.Z * cos) - (direction.X * sin));
    }

    public static Vector3 InverseDirection(Vector3 direction, float angle) =>
        TransformDirection(direction, -angle);

    private static uint Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)x) * 16777619u;
            h = (h ^ (uint)y) * 16777619u;
            h = (h ^ (uint)z) * 16777619u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            return h ^ (h >> 15);
        }
    }
}
