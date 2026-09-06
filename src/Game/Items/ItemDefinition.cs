using System.Text.Json.Serialization;

namespace Tesseris.Game.Items;

/// <summary>Co ten předmět vlastně je.</summary>
public enum ItemKind
{
    /// <summary>Blok, který jde položit do světa.</summary>
    Block,

    /// <summary>Nástroj: těží rychleji a opotřebovává se.</summary>
    Tool,

    /// <summary>Surovina. Sama nic nedělá, je vstupem do výroby.</summary>
    Material,

    /// <summary>Kbelík. Nese jeden bit stavu navíc, viz <c>Hotbar</c>.</summary>
    Bucket,

    /// <summary>Kus výstroje. Patří do jednoho ze slotů na těle, ne do ruky.</summary>
    Armour,
}

/// <summary>Kam na tělo kus výstroje patří. Pořadí sedí na sloty inventáře.</summary>
public enum ArmourSlot
{
    None = -1,
    Helmet = 0,
    Chestplate = 1,
    Leggings = 2,
    Boots = 3,
}

/// <summary>
/// Na co je nástroj. Ne na čem se opotřebuje — na to, co těží rychle.
/// </summary>
public enum ToolKind
{
    None,
    Pickaxe,
    Axe,
    Shovel,
}

/// <summary>
/// Stupeň nástroje.
/// </summary>
/// <remarks>
/// Pořadí je závazné: porovnává se číselně, takže <c>Iron &gt; Stone</c> rozhoduje o tom,
/// jestli z bloku něco vypadne. Vkládat nový stupeň doprostřed znamená přečíslovat
/// všechny bloky, které si o stupeň říkají.
/// </remarks>
public enum ToolTier
{
    None = 0,
    Flint = 1,
    Stone = 2,
    Iron = 3,
    Diamond = 4,
}

/// <summary>
/// Definice předmětu načtená z JSON, nebo odvozená z bloku.
/// </summary>
/// <remarks>
/// <para><b>Bloky se do předmětů převádějí samy.</b> Psát ke každému bloku ještě soubor
/// s předmětem by znamenalo dvě místa, která se musí držet v souladu — a to se rozejde
/// při prvním přidaném bloku. Soubory v <c>assets/items</c> proto popisují jen to, co
/// blok není: nástroje, suroviny a kbelík.</para>
/// </remarks>
public sealed class ItemDefinition
{
    /// <summary>Jednoznačný identifikátor, třeba <c>tesseris:iron_pickaxe</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Jak se předmět jmenuje na obrazovce. Česky, protože to čte hráč.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Textura ikony. U bloků se doplní z jejich vrchní stěny.</summary>
    public string Texture { get; set; } = string.Empty;

    /// <summary>
    /// Samostatná miniatura pro inventář. Je-li prázdná, použije se <see cref="Texture"/>.
    /// </summary>
    /// <remarks>
    /// Modelové textury bývají rozvinuté UV mapy (například 64×256 sekera). Když se takový
    /// obrázek bez úpravy zmenší do slotu, nevznikne nástroj, ale změť jeho jednotlivých
    /// ploch. Model proto může mít detailní texturu a inventář vedle ní čistou siluetu.
    /// </remarks>
    public string IconTexture { get; set; } = string.Empty;

    /// <summary>
    /// Model z <c>assets/models</c>, kterým se předmět kreslí ve světě. Prázdné znamená,
    /// že se těleso vytáhne z ikony.
    /// </summary>
    /// <remarks>
    /// <b>Netýká se ikony v inventáři.</b> Ta zůstává obrázkem — vykreslovat do slotu
    /// osmnáct kvádrů by znamenalo druhou cestu kreslení kvůli miniatuře, na které stejně
    /// není nic z toho detailu vidět.
    /// </remarks>
    public string Model { get; set; } = string.Empty;

    /// <summary>Blok, který předmět položí. Jen u <see cref="ItemKind.Block"/>.</summary>
    public string Block { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter<ItemKind>))]
    public ItemKind Kind { get; set; } = ItemKind.Material;

    [JsonConverter(typeof(JsonStringEnumConverter<ToolKind>))]
    public ToolKind Tool { get; set; } = ToolKind.None;

    [JsonConverter(typeof(JsonStringEnumConverter<ToolTier>))]
    public ToolTier Tier { get; set; } = ToolTier.None;

    /// <summary>Kolik se jich vejde do jednoho slotu.</summary>
    /// <remarks>Nástroje mají jedničku: opotřebení je vlastnost kusu, ne hromádky.</remarks>
    public int MaxStack { get; set; } = 64;

    /// <summary>Kolik použití nástroj vydrží. Nula znamená, že se neopotřebovává.</summary>
    public int Durability { get; set; }

    /// <summary>Palivo do pece: na kolik vteřin hoření vydrží. Nula = nehoří.</summary>
    public float BurnSeconds { get; set; }

    /// <summary>
    /// Co po shoření kusu zbude v peci. Prázdné znamená, že nezbude nic.
    /// </summary>
    /// <remarks>
    /// <para>Tímhle vzniká dřevěné uhlí: dřevo je palivo a po každém shořelém kusu zůstane
    /// v peci uhel. Nemá to vlastní recept, protože to není tavení — hráč dřevo přiloží
    /// a uhlí je vedlejší produkt topení, ne to, co pec zpracovává.</para>
    ///
    /// <para>Uhlí samo zbytek nemá, jinak by pec vyráběla palivo z paliva donekonečna.</para>
    /// </remarks>
    public string BurnResidue { get; set; } = string.Empty;

    /// <summary>Do kterého slotu výstroje kus patří. Jen u <see cref="ItemKind.Armour"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ArmourSlot>))]
    public ArmourSlot Slot { get; set; } = ArmourSlot.None;

    /// <summary>
    /// Jakou část zranění kus pohltí, 0 až 1.
    /// </summary>
    /// <remarks>
    /// Sčítá se přes celou výstroj a strop je pod jedničkou — plná nezranitelnost by z
    /// brnění udělala vypínač obtížnosti. Kyrys nese nejvíc, boty nejmíň, protože kryjí
    /// nejmenší plochu; hráč to tak intuitivně čeká.
    /// </remarks>
    public float Protection { get; set; }
}
