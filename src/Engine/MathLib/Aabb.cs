using OpenTK.Mathematics;

namespace Tesseris.Engine.MathLib;

/// <summary>Kvádr zarovnaný podle os, zadaný dvěma protilehlými rohy.</summary>
public readonly record struct Aabb(Vector3 Min, Vector3 Max)
{
    public Vector3 Center => (Min + Max) * 0.5f;

    public Vector3 Size => Max - Min;

    /// <summary>Kvádr z rohu a rozměrů.</summary>
    public static Aabb FromSize(Vector3 min, Vector3 size) => new(min, min + size);

    /// <summary>Krychle jednoho bloku na celočíselné pozici.</summary>
    public static Aabb Block(int x, int y, int z) =>
        new(new Vector3(x, y, z), new Vector3(x + 1, y + 1, z + 1));

    /// <summary>
    /// Protínají se dva kvádry? Dotyk stěnou se počítá jako průnik — pro rozhodování
    /// o viditelnosti i o kolizi je to bezpečnější strana.
    /// </summary>
    public bool Intersects(in Aabb other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    /// <summary>Nejmenší kvádr obsahující oba vstupy.</summary>
    public Aabb Union(in Aabb other) =>
        new(Vector3.ComponentMin(Min, other.Min), Vector3.ComponentMax(Max, other.Max));

    /// <summary>Kvádr posunutý o vektor.</summary>
    public Aabb Translated(Vector3 offset) => new(Min + offset, Max + offset);
}
