using Tesseris.Game.Micro;

namespace Tesseris.Game.World;

/// <summary>
/// Průchodnost otesaných bloků pro kapalinu.
/// </summary>
/// <remarks>
/// <para><b>Proč to musí existovat.</b> Otesaný blok má v blokové vrstvě vzduch — mřížka
/// mikrovoxelů je vedle ní, jako druhá vrstva. Pro <c>GetBlock</c> je tedy vytesaný kámen
/// k nerozeznání od díry, takže by do něj voda natekla celá, jako by tam nic nebylo.</para>
///
/// <para><b>Otvor musí mít šířku.</b> Bez toho by voda protekla škvírou o jednom mikrovoxelu,
/// tedy 6 cm — hráč by seknul dlátem do hráze a ta by protekla, aniž by na ní bylo co vidět.
/// Požaduje se souvislý čtverec volných mikrovoxelů, protože jediný volný pruh o šířce
/// jednoho voxelu není díra, ale rýha.</para>
/// </remarks>
internal static class MicroFlow
{
    /// <summary>
    /// Nejmenší otvor, kterým kapalina proteče, v mikrovoxelech na stranu.
    /// </summary>
    public const int MinOpening = 3;

    /// <summary>
    /// Kolik musí v otesaném bloku zbýt prázdna, aby se do něj voda vešla.
    /// </summary>
    /// <remarks>
    /// <para><b>Musí sedět s <see cref="MinOpening"/>.</b> Nejtenčí kanál, který se ještě
    /// prohlásí za průchodný, je otvor tři na tři vedený skrz blok: 3 × 3 × 16 = 144 mikro-
    /// voxelů ze 4096, tedy 3,5 % objemu. Kdyby byla hranice výš, vznikl by rozpor — voda by
    /// otvorem podle jednoho pravidla protéct směla, ale podle druhého by se do bloku
    /// nevešla, takže by neprotekla nikdy.</para>
    ///
    /// <para>Dvě procenta jsou tedy o kousek pod tou hodnotou. Blok osekaný ještě míň je
    /// pořád stěna, ne nádoba — a hlavně jím podle <see cref="HasOpening"/> stejně nic
    /// neprojde.</para>
    /// </remarks>
    public const float MinFreeFraction = 0.02f;

    /// <summary>
    /// Jakou část bloku smí zabrat kapalina, 0 až 1.
    /// </summary>
    /// <remarks>
    /// Blok bez mřížky je buď prázdný, nebo plný, a rozhoduje o tom volající. Otesaný blok
    /// pojme jen to, co v něm zbylo — vytesaná mísa tedy udrží méně vody než prázdný blok,
    /// což je přesně to, co hráč čeká, když si ji vyseká.
    /// </remarks>
    public static float FreeFraction(MicroBlock? micro)
    {
        if (micro is null)
        {
            return 1f;
        }

        return 1f - ((float)micro.SolidCount / MicroBlock.Volume);
    }

    /// <summary>
    /// Je mezi dvěma sousedními bloky otvor aspoň <see cref="MinOpening"/> mikrovoxelů
    /// na stranu?
    /// </summary>
    /// <param name="from">Mřížka výchozího bloku, nebo null když je celý prázdný.</param>
    /// <param name="to">Mřížka cílového bloku, nebo null když je celý prázdný.</param>
    /// <param name="axis">0 = X, 1 = Y, 2 = Z.</param>
    /// <param name="positive">Jde se ve směru rostoucí souřadnice?</param>
    /// <remarks>
    /// Porovnávají se dvě přiléhající vrstvy mikrovoxelů: poslední vrstva výchozího bloku
    /// a první vrstva cílového. Volno je tam, kde jsou obě prázdné — kdyby stačila jedna,
    /// protekla by voda do stěny, která na druhé straně pokračuje.
    /// </remarks>
    public static bool HasOpening(MicroBlock? from, MicroBlock? to, int axis, bool positive)
    {
        // Dva prázdné bloky se nemusí zkoumat - mezi nimi je otvor přes celou stěnu.
        if (from is null && to is null)
        {
            return true;
        }

        const int Size = MicroBlock.Size;

        // Vrstva u stěny: kdo jde nahoru, odchází svou horní vrstvou a přichází do spodní.
        int fromLayer = positive ? Size - 1 : 0;
        int toLayer = positive ? 0 : Size - 1;

        Span<bool> open = stackalloc bool[Size * Size];

        for (int b = 0; b < Size; b++)
        {
            for (int a = 0; a < Size; a++)
            {
                open[(b * Size) + a] =
                    !SolidAt(from, axis, fromLayer, a, b) &&
                    !SolidAt(to, axis, toLayer, a, b);
            }
        }

        return HasSquare(open, Size, MinOpening);
    }

    /// <summary>Je mikrovoxel ve vrstvě plný? Chybějící mřížka znamená prázdný blok.</summary>
    private static bool SolidAt(MicroBlock? micro, int axis, int layer, int a, int b)
    {
        if (micro is null)
        {
            return false;
        }

        return axis switch
        {
            0 => micro.IsSolid(layer, a, b),
            1 => micro.IsSolid(a, layer, b),
            _ => micro.IsSolid(a, b, layer),
        };
    }

    /// <summary>
    /// Obsahuje maska souvislý čtverec volných políček o zadané straně?
    /// </summary>
    /// <remarks>
    /// Počítá se to největším čtvercem, který v daném bodě končí — každé políčko si drží
    /// délku strany čtverce, jehož je pravým dolním rohem. Projde se maska jednou místo
    /// zkoušení všech poloh, což je u šestnácti na šestnáct rozdíl mezi 256 a 12 544 testy.
    /// </remarks>
    private static bool HasSquare(ReadOnlySpan<bool> open, int size, int side)
    {
        Span<int> best = stackalloc int[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = (y * size) + x;

                if (!open[index])
                {
                    best[index] = 0;
                    continue;
                }

                if (x == 0 || y == 0)
                {
                    best[index] = 1;
                }
                else
                {
                    int up = best[index - size];
                    int left = best[index - 1];
                    int corner = best[index - size - 1];

                    best[index] = Math.Min(Math.Min(up, left), corner) + 1;
                }

                if (best[index] >= side)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
