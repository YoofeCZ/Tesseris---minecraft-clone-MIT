using System.Text.Json.Serialization;
using Tesseris.Game.World;

namespace Tesseris.Game.Blocks;

/// <summary>Která stěna bloku. Pořadí odpovídá pořadí v <see cref="BlockDefinition.FaceTextures"/>.</summary>
public enum BlockFace
{
    NegX = 0,
    PosX = 1,
    NegY = 2,
    PosY = 3,
    NegZ = 4,
    PosZ = 5,
}

/// <summary>
/// Materiál bloku. Řídí zvuky a částice; ve F1 se zatím jen načítá a ukládá.
/// </summary>
public enum BlockMaterial
{
    None = 0,
    Stone,
    Soil,
    Wood,
    Sand,
    Glass,
}

/// <summary>
/// Nejnižší stupeň nástroje, který z bloku získá drop. <see cref="MaterialDefault"/>
/// zachovává běžné pravidlo materiálu, takže starší bloky a mody nemusí nové pole doplňovat.
/// </summary>
public enum BlockHarvestTier
{
    MaterialDefault = 0,
    None,
    Flint,
    Stone,
    Iron,
    Diamond,
}

/// <summary>
/// Definice jednoho typu bloku, jak je zapsaná v <c>/assets/blocks/*.json</c>.
///
/// Vlastnosti jsou zapisovatelné, protože je plní deserializace <c>System.Text.Json</c>.
/// Po načtení je registry považuje za neměnné.
/// </summary>
public sealed class BlockDefinition
{
    /// <summary>Textový identifikátor, například <c>tesseris:stone</c>. Musí být v souboru vyplněný.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Textura použitá pro všechny stěny, které nemají vlastní záznam v <see cref="Faces"/>.</summary>
    public string Texture { get; set; } = string.Empty;

    /// <summary>Výjimky z <see cref="Texture"/> pro jednotlivé stěny, klíč je název stěny (<c>top</c>, <c>bottom</c>, …).</summary>
    public Dictionary<string, string> Faces { get; set; } = [];

    [JsonConverter(typeof(JsonStringEnumConverter<BlockMaterial>))]
    public BlockMaterial Material { get; set; } = BlockMaterial.None;

    /// <summary>Minimální stupeň správného nástroje; výchozí hodnota dědí pravidlo materiálu.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<BlockHarvestTier>))]
    public BlockHarvestTier RequiredTier { get; set; } = BlockHarvestTier.MaterialDefault;

    /// <summary>Tvrdost pro budoucí těžbu. Ve F1 se jen načítá.</summary>
    public float Hardness { get; set; } = 1f;

    /// <summary>
    /// Intenzita vlastního světla v rozsahu 0 až 15. Nula znamená běžný blok bez emise;
    /// hodnota 15 odpovídá silnému zdroji, například pochodni.
    /// </summary>
    public byte Emission { get; set; }

    /// <summary>Jde blok tesat na mikrovoxely? Používá se až ve F4.</summary>
    public bool Chiselable { get; set; } = true;

    /// <summary>
    /// Neprůhledný blok zakrývá stěny sousedů. Sklo je průhledné, takže se kreslí
    /// ve zvláštním průchodu a nezakrývá to, co je za ním.
    /// </summary>
    public bool Opaque { get; set; } = true;

    /// <summary>Tvar geometrie. Rostliny nejsou krychle, ale dvě zkřížené plochy.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<BlockShape>))]
    public BlockShape Shape { get; set; } = BlockShape.Cube;

    /// <summary>Vychozi maska osmi pulbloku. 255 je plna krychle, 15 spodni slab.</summary>
    public byte Pieces { get; set; } = PieceMask.Full;

    /// <summary>Jméno Blockbench modelu v assets/models/ground, bez přípony.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Sirky a vyska UV atlasu vlastniho modelu v pixelech.</summary>
    public int ModelTextureWidth { get; set; } = 64;
    public int ModelTextureHeight { get; set; } = 64;

    /// <summary>Jiný předmět, který blok po rozbití vyhodí. Prázdné znamená sebe.</summary>
    public string DropItem { get; set; } = string.Empty;

    /// <summary>Počet kusů vlastního nebo přesměrovaného dropu.</summary>
    public int DropCount { get; set; } = 1;

    [JsonIgnore]
    internal GroundClutterModel? GroundGeometry { get; set; }

    [JsonIgnore]
    internal HytaleBlockModel? HytaleGeometry { get; set; }

    [JsonIgnore]
    internal IReadOnlyList<ItemShape.Face>? HytaleFaces { get; set; }

    /// <summary>
    /// Na kterých blocích tenhle blok smí stát. Prázdné znamená „na čemkoli".
    ///
    /// <para><b>Platí pro rostliny.</b> Tráva ani kytka nesmí růst z kamene, písku nebo
    /// ze vzduchu — a hlavně na takovém místě nesmí ani zůstat. Když se podklad odtěží,
    /// rostlina se rozpadne, protože jinak by v krajině zůstávaly kytky visící nad dírou.</para>
    ///
    /// <para>Vícepatrová rostlina (chaluha) se smí opřít i sama o sebe: patro nad kořenem
    /// stojí na témž druhu. Řeší to <see cref="BlockRegistry.CanStandOn"/>, ne tenhle seznam.</para>
    /// </summary>
    public string[] GroundBlocks { get; set; } = [];

    /// <summary>
    /// Dá se do bloku narazit?
    ///
    /// <para>Tráva a kytky ne — jde se jimi projít. Je to zvlášť od
    /// <see cref="Opaque"/>, protože sklo je průhledné, ale narazit se do něj dá.</para>
    /// </summary>
    public bool Solid { get; set; } = true;

    /// <summary>
    /// Kapalina. Neblokuje pohyb, ale plave se v ní: mění fyziku hráče a zaplavuje
    /// prostor pod hladinou.
    /// </summary>
    public bool Liquid { get; set; }

    /// <summary>
    /// Rostlina, která vždy sdílí svůj voxel s vodou. Zůstává běžným zaměřitelným blokem,
    /// ale mesher, plavání a simulace vody ji současně považují za plný vodní voxel.
    /// </summary>
    public bool Aquatic { get; set; }

    /// <summary>
    /// Kreslit <b>výřezem</b> místo mícháním? Platí jen pro bloky, které nejsou
    /// <see cref="Opaque"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Rozděluje dvě věci, které se do téhle chvíle házely na jednu hromadu.</b>
    /// Sklo je opravdu poloprůhledné a míchat se musí. Listí ale poloprůhledné není — jeho
    /// textura je buď plná, nebo díra, nic mezi tím.</para>
    ///
    /// <para>Míchaný průchod nezapisuje do hloubky, aby se skla navzájem neořezávala.
    /// U listí to znamenalo, že se vrstvy koruny nezakrývaly a stromem bylo vidět skrz až
    /// na oblohu. S výřezem se hloubka zapisuje, takže přední list ty za sebou schová —
    /// a je to i výrazně levnější, protože odpadne stínování schovaných vrstev.</para>
    /// </remarks>
    public bool Cutout { get; set; }

    /// <summary>
    /// O kolik bloku mesh přesahuje za hranice své krychle. 0 = přesně na mřížce.
    ///
    /// <para><b>K čemu to je.</b> Listí je celý blok, ne zmenšený — ale kreslí se o kousek
    /// větší, než ve skutečnosti je. Sousední koruny a kmen se tím do sebe zanoří a mez
    /// mezi nimi zmizí: strom přestane vypadat jako hromada kostek postavených na sebe
    /// a začne vypadat jako jeden kus. Data se nemění, jen geometrie.</para>
    ///
    /// <para>Působí jen na to, co je vidět zvenku — vnitřní stěny koruny se stejně
    /// nekreslí, protože mají plné sousedy. Kolize a kopání zůstávají na blokové mřížce.</para>
    /// </summary>
    public float Overhang { get; set; }

    /// <summary>
    /// Palivo do pece: na kolik vteřin hoření kus vydrží. Nula znamená, že blok nehoří.
    /// </summary>
    /// <remarks>
    /// <b>Patří to k bloku, ne do <c>assets/items</c>.</b> Blok se na předmět převádí sám,
    /// takže ruční soubor jen kvůli hořlavosti by znamenal dvě místa se stejným seznamem —
    /// a ta se rozejdou při prvním přidaném dřevě.
    /// </remarks>
    public float BurnSeconds { get; set; }

    /// <summary>
    /// Co po shoření kusu paliva zbude v peci, třeba <c>tesseris:charcoal</c>.
    /// Prázdné znamená, že nezbude nic.
    /// </summary>
    public string BurnResidue { get; set; } = string.Empty;
}

/// <summary>Jak se blok kreslí.</summary>
public enum BlockShape
{
    /// <summary>Krychle. Stěny se slučují greedy meshingem a zakrývají sousedy.</summary>
    Cube = 0,

    /// <summary>
    /// Dvě svislé plochy zkřížené přes úhlopříčky, jak se v blokových hrách kreslí
    /// tráva a kytky. Nemá co slučovat ani co zakrývat.
    /// </summary>
    Cross,

    /// <summary>
    /// Chomáč listí: několik ploch v různých úhlech a výškách uvnitř bloku.
    ///
    /// <para><b>Proč ne Cross a proč ne krychle.</b> Krychlové listí dělá z koruny hromadu
    /// kostek — hrana bloku je vidět, ať se textura kreslí jakkoli. Cross je opak: dvě
    /// plochy jsou na korunu málo a mezi nimi je vidět obloha, protože se navzájem
    /// nezakrývají. Chomáč je mezi tím: ploch je šest, prostupují se navzájem a přesahují
    /// za hranice bloku, takže sousední listy do sebe zapadnou a hrana zmizí.</para>
    ///
    /// <para>Data zůstávají bloková: kope se a staví po celých blocích jako dřív.</para>
    /// </summary>
    Foliage,

    /// <summary>
    /// Svislý sloupek uprostřed bloku, o poloviční šířce a přes celou výšku. Kmeny.
    ///
    /// <para><b>Musí to být vlastní tvar, ne dílky.</b> Dílek je vždycky půlka bloku, takže
    /// leží buď vlevo, nebo vpravo — sloupek z jednoho dílku proto nikdy nevyjde doprostřed
    /// a strom stál viditelně u kraje svého bloku. Kvádr 0,25 až 0,75 střed trefí.</para>
    ///
    /// <para>Listí kolem kmene se do téhož bloku vejde: nese ho druhá vrstva materiálů
    /// (<see cref="ExtraPieces"/>), která kvůli tomu vznikla.</para>
    /// </summary>
    Post,

    /// <summary>
    /// Nízký sbíratelný předmět ležící na povrchu: klacík, kamínky nebo pazourek.
    /// Dvě krátké karty se náhodně natočí uvnitř voxelu; blok nemá kolizi ani stín.
    /// </summary>
    GroundClutter,

    /// <summary>Libovolne velky kvadrovany model nacteny z formatu Hytale Blocky.</summary>
    HytaleModel,

    /// <summary>Tenký svislý panel dveří; orientaci nese maska dílků.</summary>
    Door,

    /// <summary>Tenký vodorovný nebo svislý panel padacích dveří.</summary>
    Trapdoor,

    /// <summary>Orientovane schody s rovnym, vnitrnim nebo vnejsim rohem.</summary>
    Stairs,

    /// <summary>Tenký svislý žebřík přichycený ke stěně; orientaci nese stav bloku.</summary>
    Ladder,

    /// <summary>Volně stojící výřezová pochodeň bez kolize.</summary>
    Torch,

    /// <summary>Bedna se samostatným tělem a animovaným víkem.</summary>
    Chest,
}
