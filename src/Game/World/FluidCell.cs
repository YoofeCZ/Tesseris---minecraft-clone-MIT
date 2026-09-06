namespace Tesseris.Game.World;

/// <summary>
/// Úrovně vody, ve stylu Minecraftu.
/// </summary>
/// <remarks>
/// <para>Voda má osm stupňů výšky a jeden zvláštní stav navíc: <b>zdroj</b>. Zdroj se nikdy
/// nevyčerpá a je jediné, co vodu do světa dodává — všechno ostatní je jen její doběh,
/// který se přepočítává z okolí.</para>
///
/// <para><b>Proč se nepřesouvá hmota.</b> Předchozí pokus stavěl na buněčném automatu, který
/// hmotu přeléval mezi buňkami. Je to fyzikálně poctivější, ale těžko se to ladí: hladina
/// se dlouho ustaluje, tenké zbytky se rozlézají do všech stran a každá chyba v zaokrouhlení
/// se projeví až po tisících tiků. Tenhle model místo toho každou buňku <b>přepočítá</b>
/// z jejích sousedů — výsledek nezávisí na pořadí ani na historii, takže se nemá kde nasčítat
/// chyba a voda vypadá tak, jak ji hráč zná.</para>
/// </remarks>
public static class FluidCell
{
    /// <summary>Zdroj. Nevysychá a drží plnou výšku.</summary>
    public const byte Source = 8;

    /// <summary>
    /// Voda, nad kterou je další voda. Vyplňuje blok úplně, až po strop.
    /// </summary>
    /// <remarks>
    /// Není to úroveň, kterou by simulace někdy zapsala — vzniká až v mesheru a jde o stupeň
    /// NAD zdrojem. Důvod: i zdroj se kreslí o kousek níž než strop bloku, aby hladina
    /// nesplývala s břehem. Uvnitř sloupce by to ale udělalo pruh vzduchu mezi patry, takže
    /// se tam nesnižuje vůbec nic.
    /// </remarks>
    public const byte Continuous = 9;

    /// <summary>Nejvyšší úroveň doběhu. Voda ze zdroje začíná o stupeň níž.</summary>
    public const byte MaxFlowing = 7;

    /// <summary>Prázdno.</summary>
    public const byte Empty = 0;

    /// <summary>
    /// Jak daleko doteče voda po rovině. Sedm stupňů znamená sedm bloků od zdroje, pak
    /// úroveň klesne na nulu a voda skončí.
    /// </summary>
    public const int Reach = MaxFlowing;

    /// <summary>Je to zdroj?</summary>
    public static bool IsSource(byte level) => level >= Source;

    /// <summary>Výška hladiny v bloku, 0 až 1. Zdroj je plný blok.</summary>
    public static float Height(byte level) => Math.Clamp(level / (float)Source, 0f, 1f);
}
