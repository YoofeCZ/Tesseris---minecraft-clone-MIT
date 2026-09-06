using OpenTK.Mathematics;

namespace Tesseris.Game.Blocks;

/// <summary>
/// Rozměry a poloha jednoho trsu rostliny.
///
/// <para><b>Jediný zdroj pravdy pro mesher i paprsek.</b> Rostlina se nekreslí jako kostka,
/// ale jako dvě svislé plochy po úhlopříčkách bloku — a její výška i posun stranou se losují
/// z hashe světové souřadnice, aby louka nevypadala razítkovaně. Kdyby si tyhle rozměry
/// počítal každý sám, rozešlo by se to, na co hráč míří, s tím, co vidí: zaměřovač by trefil
/// kytku i půl bloku vedle stébla.</para>
/// </summary>
public static class PlantShape
{
    /// <summary>
    /// Zatažení plochy od rohů bloku. Stejná hodnota, jakou používají blokové hry: plocha
    /// se nepere o hloubku se sousedním terénem a přitom zůstane skoro celá úhlopříčka.
    /// </summary>
    public const float Inset = 0.0854f;

    /// <summary>Jedna svislá plocha trsu: úsečka v půdorysu a rozsah výšky.</summary>
    public readonly record struct Plane(Vector2 From, Vector2 To, float Bottom, float Top);

    /// <summary>
    /// Obě plochy trsu na dané souřadnici, v souřadnicích bloku (0 až 1).
    /// </summary>
    /// <param name="submerged">
    /// Roste rostlina pod vodou? Podvodní trs má výšku přesně jedna a posun bez ohledu
    /// na patro, protože se skládá z několika bloků nad sebou a musí tvořit jeden stvol.
    /// </param>
    public static (Plane First, Plane Second) Planes(int worldX, int worldY, int worldZ, bool submerged)
    {
        uint hash = submerged ? Hash(worldX, 0, worldZ) : Hash(worldX, worldY, worldZ);

        float offsetX = ((int)((hash >> 16) % 7u) - 3) * 0.025f;
        float offsetZ = ((int)((hash >> 20) % 7u) - 3) * 0.025f;

        float low = Inset;
        float high = 1f - Inset;

        float x0 = low + offsetX;
        float x1 = high + offsetX;
        float z0 = low + offsetZ;
        float z1 = high + offsetZ;

        // Výška 85 až 100 % bloku. Podvodní trs má přesně jedna: skládá se z několika bloků
        // na sebe a každé procento chybějící výšky by byla vodorovná mezera pod dalším patrem.
        float top = submerged ? 1f : 0.85f + (((hash >> 8) % 16u) * 0.01f);

        return (
            new Plane(new Vector2(x0, z0), new Vector2(x1, z1), 0f, top),
            new Plane(new Vector2(x0, z1), new Vector2(x1, z0), 0f, top));
    }

    /// <summary>Hash pozice rostliny. Musí být stabilní, jinak by trs po přemeshování poskočil.</summary>
    public static uint Hash(int x, int y, int z)
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
