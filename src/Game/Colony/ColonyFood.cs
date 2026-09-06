using Tesseris.Game.Blocks;

namespace Tesseris.Game.Colony;

/// <summary>
/// Co kolonie považuje za jídlo.
/// </summary>
/// <remarks>
/// <para><b>V obsahu žádné poživatelné jídlo neexistuje.</b> V <c>assets/items</c> jsou
/// <c>tesseris:apple</c>, <c>tesseris:raw_mutton</c> a <c>tesseris:raw_venison</c>, ale žádný
/// z nich nenese nic, co by šlo sníst — v celé codebase není pole s výživou ani metoda, která
/// by jídlo konzumovala. Navíc kolonie pracuje v <b>id bloků</b> (kolonista nese to, co vykopal,
/// pás i stroj berou totéž), a jako blok žádný z těch tří předmětů neexistuje.</para>
///
/// <para><b>Nejmenší řešení, které nevymýšlí obsah:</b> seznam jmen na jednom místě. Rozloží se
/// přes registr bloků a co v obsahu není, se tiše přeskočí. Až jablko nebo maso blok dostanou,
/// stanou se jídlem bez zásahu do kódu. Aby šel hlad ukázat v běžící hře už teď, jsou v seznamu
/// i dva bloky, které v obsahu <b>už jsou</b> a jsou poživatelné i v předloze žánru — kaktus
/// a chaluha. Je to náhrada do doby, než bude farmaření; patří to do „k rozhodnutí".</para>
///
/// <para><b>Váže se na JMÉNO, nikdy na číslo</b>. Id bloků jsou setříděná
/// podle jména, takže natvrdo psané číslo se rozejde s obsahem, jakmile někdo přidá blok —
/// v tomhle projektu už čtyřikrát. Nula je přitom platné id, takže zapomenuté jídlo by tiše
/// spadlo na první blok v registru.</para>
/// </remarks>
public static class ColonyFood
{
    /// <summary>Kolik hladu jedno jídlo zažene. Jedno jídlo = plný žaludek.</summary>
    public const int Nourishment = int.MaxValue;

    /// <summary>
    /// Jména bloků, které kolonie sní. Jediný zdroj pravdy.
    /// </summary>
    /// <remarks>
    /// První tři jsou skutečné jídlo z <c>assets/items</c> a dnes se nerozloží, protože jako
    /// blok neexistují. Zbylé dva existují a drží hlad v chodu, dokud nebude čím kolonii živit.
    /// </remarks>
    public static IReadOnlyList<string> Names { get; } =
    [
        "tesseris:apple",
        "tesseris:raw_mutton",
        "tesseris:raw_venison",
        "tesseris:cactus",
        "tesseris:kelp",
    ];

    /// <summary>
    /// Přeloží jména na id bloků. Co obsah nezná, se vynechá.
    /// </summary>
    /// <remarks>
    /// <para><b>Přes <see cref="BlockRegistry.TryIndexOf"/>, ne <c>IndexOf</c>.</b> To druhé na
    /// neznámém jménu <b>vyhodí výjimku</b>, a tohle se volá z herní smyčky — chybějící jídlo
    /// v obsahu by shodilo celý tick. Je to stejné rozhodnutí jako u načítání uloženého světa:
    /// odebraný typ nesmí znepřístupnit hru.</para>
    ///
    /// <para><b>Vzduch se do výsledku nikdy nedostane.</b> Kdyby ano, byl by jídlem každý
    /// prázdný voxel a hlad by se dal zahnat vzduchem — přesně ta chyba, kterou dělá
    /// dosazená nula.</para>
    /// </remarks>
    public static ushort[] Resolve(BlockRegistry blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        List<ushort> resolved = [];
        foreach (string name in Names)
        {
            if (blocks.TryIndexOf(name, out ushort block)
                && block != BlockRegistry.Air
                && !resolved.Contains(block))
            {
                resolved.Add(block);
            }
        }

        return [.. resolved];
    }

    /// <summary>Která jména obsah nezná. Jen pro hlášku do logu.</summary>
    public static IReadOnlyList<string> MissingNames(BlockRegistry blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        List<string> missing = [];
        foreach (string name in Names)
        {
            if (!blocks.TryIndexOf(name, out ushort block) || block == BlockRegistry.Air)
            {
                missing.Add(name);
            }
        }

        return missing;
    }
}
