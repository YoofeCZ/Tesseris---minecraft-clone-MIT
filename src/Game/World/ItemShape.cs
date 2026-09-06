using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;

namespace Tesseris.Game.World;

/// <summary>
/// Těleso předmětu vytažené z jeho ploché ikony.
/// </summary>
/// <remarks>
/// <para><b>Meč není plachta.</b> Předměty na zemi se kreslily jako dvě zkřížené karty
/// s obrázkem. Z každého úhlu z toho byl papír a při pohledu z boku dvě čáry — což je
/// u kostkové hry, kde má všechno objem, jediná věc bez objemu.</para>
///
/// <para><b>Jak se to dělá.</b> Ikona je nakreslená v šestnácti texelech. Vezme se její
/// obrys a udělá se z něj deska: přední a zadní stěna přes celou dlaždici (průhledné texely
/// zahodí výřezový průchod, takže je netřeba řešit) a k tomu <b>boční stěny všude, kde
/// kresba končí</b>. Výsledek je tvarem přesně to, co je na obrázku, jen s tloušťkou —
/// stejně jako to dělají blokové hry.</para>
///
/// <para><b>Boky se vzorkují ze středu svého texelu.</b> Bok je tenký proužek: kdyby přes
/// něj šla celá textura, byla by na něm zmenšená celá kresba. Takhle má barvu toho texelu,
/// ke kterému patří, což je přesně to, co má hrana čepele mít.</para>
///
/// <para><b>Tvar se počítá jednou na vrstvu</b> a pak se jen posouvá a otáčí. Přepočítávat
/// ho na každý předmět a snímek by znamenalo projít šestnáct na druhou texelů u každé
/// vypadlé věci.</para>
/// </remarks>
public sealed class ItemShape
{
    /// <summary>
    /// Jedna stěna tělesa v jeho vlastní soustavě, se souřadnicemi v textuře.
    /// </summary>
    /// <param name="Layer">
    /// Vrstva atlasu, nebo záporné číslo, když se má vzít vrstva předmětu.
    ///
    /// <para>Vytažený obrys má celý jednu texturu, takže mu stačí vrstva předmětu. Model
    /// z Blockbenche je jiný případ: jeho textura je vysoká a rozřezaná na pruhy, takže
    /// každá stěna může sedět v jiné vrstvě.</para>
    /// </param>
    public readonly record struct Face(
        Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3,
        Vector2 U0, Vector2 U1, Vector2 U2, Vector2 U3,
        float Shade,
        int Layer = -1);

    private ItemShape(Face[] faces) => Faces = faces;

    /// <summary>Těleso z hotových stěn. Pro modely, které se nevytahují z obrysu.</summary>
    public static ItemShape FromFaces(Face[] faces) => new(faces ?? []);

    /// <summary>Totéž těleso s přepočítanými vrstvami. Slouží k dosazení vrstev atlasu.</summary>
    public ItemShape WithLayers(Func<int, int> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var mapped = new Face[Faces.Length];

        for (int i = 0; i < Faces.Length; i++)
        {
            mapped[i] = Faces[i] with { Layer = map(Faces[i].Layer) };
        }

        return new ItemShape(mapped);
    }

    /// <summary>Stěny tělesa. Souřadnice jsou v rozsahu ±<c>half</c> kolem počátku.</summary>
    public Face[] Faces { get; }

    /// <summary>
    /// Vytáhne z obrysu těleso.
    /// </summary>
    /// <param name="silhouette">Maska z <see cref="TextureArray.Silhouette"/>.</param>
    /// <param name="half">Poloviční šířka výsledku ve světě.</param>
    /// <param name="thickness">Tloušťka desky ve světě.</param>
    public static ItemShape Extrude(ReadOnlySpan<byte> silhouette, float half, float thickness)
    {
        int size = TextureArray.ArtSize;

        if (silhouette.Length < size * size)
        {
            return new ItemShape([]);
        }

        List<Face> faces = [];

        float depth = thickness * 0.5f;
        float step = (half * 2f) / size;

        // Přední a zadní stěna přes celou dlaždici. Průhledné texely zahodí výřezový
        // průchod, takže se nemusí obkreslovat obrys.
        float front = FaceShading.ForAxis(2, positive: true);
        float back = FaceShading.ForAxis(2, positive: false);

        faces.Add(new Face(
            new Vector3(-half, -half, depth), new Vector3(half, -half, depth),
            new Vector3(half, half, depth), new Vector3(-half, half, depth),
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f),
            front));

        faces.Add(new Face(
            new Vector3(half, -half, -depth), new Vector3(-half, -half, -depth),
            new Vector3(-half, half, -depth), new Vector3(half, half, -depth),
            new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f),
            back));

        float sideX = FaceShading.ForAxis(0, positive: true);
        float sideY = FaceShading.ForAxis(1, positive: true);

        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                if (silhouette[(row * size) + column] == 0)
                {
                    continue;
                }

                // Střed texelu v textuře. Řádek 0 je nahoře, proto se svislá osa obrací.
                var uv = new Vector2((column + 0.5f) / size, (row + 0.5f) / size);

                float x0 = -half + (column * step);
                float x1 = x0 + step;
                float y1 = half - (row * step);
                float y0 = y1 - step;

                // Bok se kreslí jen tam, kde kresba končí. Uvnitř tělesa by byl schovaný
                // a stál by stejně jako ten na okraji.
                if (Empty(silhouette, size, column - 1, row))
                {
                    Side(faces, new Vector3(x0, y0, -depth), new Vector3(x0, y0, depth),
                        new Vector3(x0, y1, depth), new Vector3(x0, y1, -depth), uv, sideX);
                }

                if (Empty(silhouette, size, column + 1, row))
                {
                    Side(faces, new Vector3(x1, y0, depth), new Vector3(x1, y0, -depth),
                        new Vector3(x1, y1, -depth), new Vector3(x1, y1, depth), uv, sideX);
                }

                if (Empty(silhouette, size, column, row - 1))
                {
                    Side(faces, new Vector3(x0, y1, depth), new Vector3(x1, y1, depth),
                        new Vector3(x1, y1, -depth), new Vector3(x0, y1, -depth), uv, sideY);
                }

                if (Empty(silhouette, size, column, row + 1))
                {
                    Side(faces, new Vector3(x0, y0, -depth), new Vector3(x1, y0, -depth),
                        new Vector3(x1, y0, depth), new Vector3(x0, y0, depth), uv, sideY);
                }
            }
        }

        return new ItemShape([.. faces]);
    }

    /// <summary>Bok vzorkovaný jedním texelem: všechny čtyři rohy mají tutéž souřadnici.</summary>
    private static void Side(
        List<Face> faces, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector2 uv, float shade) =>
        faces.Add(new Face(p0, p1, p2, p3, uv, uv, uv, uv, shade));

    /// <summary>Je ten texel prázdný, nebo za okrajem? Za okrajem se počítá jako prázdno.</summary>
    private static bool Empty(ReadOnlySpan<byte> silhouette, int size, int column, int row) =>
        column < 0 || row < 0 || column >= size || row >= size
        || silhouette[(row * size) + column] == 0;
}
