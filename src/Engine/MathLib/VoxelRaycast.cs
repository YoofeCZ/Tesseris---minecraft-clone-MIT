using OpenTK.Mathematics;

namespace Tesseris.Engine.MathLib;

/// <summary>Výsledek průchodu paprsku voxelovou mřížkou.</summary>
/// <param name="Block">Souřadnice zasaženého voxelu.</param>
/// <param name="Normal">
/// Normála zasažené stěny, tedy jednotkový vektor ven z voxelu. Je nulová, když paprsek
/// začal už uvnitř plného voxelu — tam žádná stěna zasažená není.
/// </param>
/// <param name="Distance">Vzdálenost od počátku ke vstupu do voxelu.</param>
/// <param name="Position">Bod vstupu do voxelu ve světových souřadnicích.</param>
public readonly record struct VoxelHit(Vector3i Block, Vector3i Normal, float Distance, Vector3 Position);

/// <summary>
/// Průchod paprsku pravidelnou mřížkou algoritmem Amanatides–Woo.
///
/// Voxel (x, y, z) zabírá krychli od (x, y, z) do (x+1, y+1, z+1), takže index voxelu je
/// dolní celá část souřadnice.
///
/// Přístup k obsahu mřížky jde přes delegát schválně: MathLib nesmí záviset na herní vrstvě
/// a testy si tak můžou podstrčit vlastní mřížku bez zakládání světa. Na výkonu to nevadí,
/// paprsků je za frame řádově jednotky.
/// </summary>
public static class VoxelRaycast
{
    /// <summary>Pojistka proti zacyklení, kdyby vyšla degenerovaná čísla.</summary>
    private const int MaxSteps = 4096;

    /// <summary>
    /// Vystřelí paprsek a vrátí první plný voxel.
    /// </summary>
    /// <param name="direction">Nemusí být jednotkový; normalizuje se, takže vzdálenost je ve světových jednotkách.</param>
    /// <returns>true, když se něco trefilo do zadané vzdálenosti.</returns>
    public static bool Cast(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        Func<int, int, int, bool> isSolid,
        out VoxelHit hit)
    {
        ArgumentNullException.ThrowIfNull(isSolid);

        hit = default;

        if (maxDistance <= 0f)
        {
            return false;
        }

        float lengthSquared = direction.LengthSquared;
        if (lengthSquared < 1e-12f || !float.IsFinite(lengthSquared))
        {
            return false;
        }

        Vector3 dir = direction / MathF.Sqrt(lengthSquared);

        int x = (int)MathF.Floor(origin.X);
        int y = (int)MathF.Floor(origin.Y);
        int z = (int)MathF.Floor(origin.Z);

        int stepX = Math.Sign(dir.X);
        int stepY = Math.Sign(dir.Y);
        int stepZ = Math.Sign(dir.Z);

        float tDeltaX = stepX != 0 ? MathF.Abs(1f / dir.X) : float.PositiveInfinity;
        float tDeltaY = stepY != 0 ? MathF.Abs(1f / dir.Y) : float.PositiveInfinity;
        float tDeltaZ = stepZ != 0 ? MathF.Abs(1f / dir.Z) : float.PositiveInfinity;

        float tMaxX = BoundaryDistance(origin.X, dir.X, x, stepX);
        float tMaxY = BoundaryDistance(origin.Y, dir.Y, y, stepY);
        float tMaxZ = BoundaryDistance(origin.Z, dir.Z, z, stepZ);

        var normal = Vector3i.Zero;
        float t = 0f;

        for (int step = 0; step < MaxSteps; step++)
        {
            if (t > maxDistance)
            {
                return false;
            }

            if (isSolid(x, y, z))
            {
                hit = new VoxelHit(new Vector3i(x, y, z), normal, t, origin + (dir * t));
                return true;
            }

            // Postoupí se po té ose, jejíž další hranice je nejblíž. Při shodě (paprsek míří
            // přesně do rohu) vyhrává X, pak Y — pořadí je libovolné, ale musí být určité,
            // jinak by výsledek závisel na zaokrouhlení.
            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                t = tMaxX;
                tMaxX += tDeltaX;
                x += stepX;
                normal = new Vector3i(-stepX, 0, 0);
            }
            else if (tMaxY <= tMaxZ)
            {
                t = tMaxY;
                tMaxY += tDeltaY;
                y += stepY;
                normal = new Vector3i(0, -stepY, 0);
            }
            else
            {
                t = tMaxZ;
                tMaxZ += tDeltaZ;
                z += stepZ;
                normal = new Vector3i(0, 0, -stepZ);
            }
        }

        return false;
    }

    /// <summary>Vzdálenost od počátku k první hranici mřížky na dané ose.</summary>
    private static float BoundaryDistance(float origin, float direction, int cell, int step)
    {
        if (step > 0)
        {
            return (cell + 1 - origin) / direction;
        }

        if (step < 0)
        {
            // Čitatel i jmenovatel jsou záporné nebo nulové, podíl tedy vyjde nezáporný.
            return (cell - origin) / direction;
        }

        return float.PositiveInfinity;
    }
}
