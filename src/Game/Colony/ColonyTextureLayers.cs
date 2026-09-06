namespace Tesseris.Game.Colony;

/// <summary>
/// Vrstvy atlasu pro to, co kolonie postavila: pás, stroj, vkládač a item na pásu.
/// </summary>
/// <remarks>
/// <para><b>Proč to není čtyři čísla v okně.</b> Vrstvy atlasu jsou tříděné podle jména
/// (<see cref="Blocks.BlockRegistry"/>), takže natvrdo psaný index se rozejde s obsahem,
/// jakmile někdo přidá blok. V tomhle projektu to skončilo čtyřikrát tím, že se něco
/// kreslilo jako kůra, obličej, listí nebo prkna. Naposledy tady: 5, 7, 9 a 11 vycházelo
/// na <c>acacia_planks</c>, <c>ancient_tech_panel</c>, <c>basalt</c> a <c>birch_leaves_02</c>.</para>
///
/// <para><b>Nula je platný index, a proto nebezpečná.</b> Zapomenutá vrstva by tiše spadla na
/// první texturu v atlasu a vypadala by jako záměr. Chybějící vrstva je proto −1 a s ní se
/// nekreslí nic — chybějící pás se hlásí sám, špatně obarvený ne. Je to totéž rozhodnutí
/// jako u <see cref="World.ChunkRenderer.PlayerSkinLayer"/>, jen na jiné entitě.</para>
///
/// <para><b>Vrstvy jsou uložené o jedničku posunuté, a to schválně.</b> U struktury se
/// inicializátory vlastností při <c>default</c> nespustí, takže výchozí <c>-1f</c> by se tiše
/// vrátilo na nulu — tedy zase na <c>acacia_leaves</c>. Posun znamená, že nula v paměti
/// znamená „nepřihlášeno", takže se ta chyba nedá udělat ani zapomenutým přiřazením.
/// Chytil to test <c>Chybejici_vrstva_je_minus_jedna_a_nikdy_nula</c>.</para>
///
/// <para><b>Jména jsou tady, ne v okně.</b> Vzniknou přes <see cref="Names"/>, přihlásí se
/// do atlasu a stejným seznamem se pak vrstvy vyhledají. Tím nemá kde vzniknout druhý
/// seznam, který by se s tímhle rozešel.</para>
/// </remarks>
public readonly record struct ColonyTextureLayers
{
    /// <summary>Vrstva, která říká „tuhle nikdo nepřihlásil". Nekreslí se s ní nic.</summary>
    public const float Missing = -1f;

    /// <summary>Textura tělesa pásu. Plech, aby pás nesplynul s kamenem.</summary>
    public const string BeltTexture = "corrugated_metal";

    /// <summary>Textura stroje. Je to doslova kresba drtiče z <c>tesseris:ore_crusher</c>.</summary>
    public const string MachineTexture = "hypro_ore_crusher";

    /// <summary>Textura vkládače. Zelený měděnec ho odliší od pásu i od stroje.</summary>
    public const string InserterTexture = "oxidized_copper";

    /// <summary>
    /// Textura itemu jedoucího po pásu.
    /// </summary>
    /// <remarks>
    /// <b>Musí být neprůhledná.</b> Item má hranu 0,3 bloku a kreslí se výřezovým průchodem,
    /// který zahazuje texely pod polovinou alfy. Předtím tu bylo listí s 2 612 ze 4 096 texelů
    /// pod prahem, takže z 28 metrů, odkud se dívá velitel, nebylo po itemu skoro nic.
    /// </remarks>
    public const string BeltItemTexture = "copper_block";

    // Uložené o jedničku výš, aby nula znamenala „nepřihlášeno" — viz poznámka u typu.
    private readonly int _beltPlusOne;
    private readonly int _machinePlusOne;
    private readonly int _inserterPlusOne;
    private readonly int _beltItemPlusOne;

    private ColonyTextureLayers(int belt, int machine, int inserter, int beltItem)
    {
        _beltPlusOne = belt + 1;
        _machinePlusOne = machine + 1;
        _inserterPlusOne = inserter + 1;
        _beltItemPlusOne = beltItem + 1;
    }

    /// <summary>Vrstva tělesa pásu, nebo <see cref="Missing"/>.</summary>
    public float Belt => _beltPlusOne - 1;

    /// <summary>Vrstva stroje, nebo <see cref="Missing"/>.</summary>
    public float Machine => _machinePlusOne - 1;

    /// <summary>Vrstva vkládače, nebo <see cref="Missing"/>.</summary>
    public float Inserter => _inserterPlusOne - 1;

    /// <summary>Vrstva itemu na pásu, nebo <see cref="Missing"/>.</summary>
    public float BeltItem => _beltItemPlusOne - 1;

    /// <summary>
    /// Jména, která se musí do atlasu přihlásit, aby šlo kolonii vykreslit.
    /// </summary>
    /// <remarks>
    /// Tenhle seznam je jediný zdroj pravdy. Kdo staví atlas, projde ho a přihlásí; kdo
    /// vrstvy hledá, projde týž seznam. Druhá kopie by se dřív nebo později rozešla.
    /// </remarks>
    public static IReadOnlyList<string> Names { get; } =
        [BeltTexture, MachineTexture, InserterTexture, BeltItemTexture];

    /// <summary>Je všechno přihlášené? Bez toho se kolonie kreslit nesmí.</summary>
    public bool IsComplete =>
        _beltPlusOne > 0 && _machinePlusOne > 0 && _inserterPlusOne > 0 && _beltItemPlusOne > 0;

    /// <summary>
    /// Vyhledá vrstvy podle JMÉNA textury.
    /// </summary>
    /// <param name="layerOf">
    /// Vrátí vrstvu atlasu pro dané jméno, nebo záporné číslo, když ho atlas nezná.
    /// Typicky <c>layers.IndexOf</c>, který na neznámé jméno vrací −1 sám.
    /// </param>
    /// <remarks>
    /// Co se nenajde, zůstane na <see cref="Missing"/>. Nedosazuje se nula ani nic jiného,
    /// co by šlo splést s platnou vrstvou — o to celé tady jde.
    /// </remarks>
    public static ColonyTextureLayers Resolve(Func<string, int> layerOf)
    {
        ArgumentNullException.ThrowIfNull(layerOf);

        static int Lookup(Func<string, int> layerOf, string name)
        {
            int layer = layerOf(name);
            return layer >= 0 ? layer : -1;
        }

        return new ColonyTextureLayers(
            Lookup(layerOf, BeltTexture),
            Lookup(layerOf, MachineTexture),
            Lookup(layerOf, InserterTexture),
            Lookup(layerOf, BeltItemTexture));
    }

    /// <summary>Které vrstvy chybí. Prázdné pole znamená, že je všechno v pořádku.</summary>
    /// <remarks>
    /// Slouží k hlášení do logu. Vypsat jména je jediný způsob, jak se o chybějící vrstvě
    /// dozvědět dřív než očima ve hře.
    /// </remarks>
    public IReadOnlyList<string> MissingNames()
    {
        var missing = new List<string>();

        if (_beltPlusOne <= 0)
        {
            missing.Add(BeltTexture);
        }

        if (_machinePlusOne <= 0)
        {
            missing.Add(MachineTexture);
        }

        if (_inserterPlusOne <= 0)
        {
            missing.Add(InserterTexture);
        }

        if (_beltItemPlusOne <= 0)
        {
            missing.Add(BeltItemTexture);
        }

        return missing;
    }
}
