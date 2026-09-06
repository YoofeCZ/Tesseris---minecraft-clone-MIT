namespace Tesseris.Game.Items;

/// <summary>
/// Hromádka předmětů v jednom slotu.
/// </summary>
/// <remarks>
/// <para>Hodnotový typ schválně. Sloty se kopírují, přesouvají a porovnávají pořád dokola,
/// a s referencí by se snadno stalo, že dva sloty ukazují na tutéž hromádku — chyba, která
/// se projeví až tím, že se předmět v inventáři zdvojí.</para>
///
/// <para><see cref="Empty"/> je jediná platná podoba prázdna. Nula kusů s nenulovým
/// předmětem se nikde nesmí objevit, proto to hlídá <see cref="IsEmpty"/> obojím.</para>
/// </remarks>
public readonly struct ItemStack : IEquatable<ItemStack>
{
    private readonly ItemComponentMap? components;

    public ItemStack(int Item, int Count, int Damage, ItemComponentMap? Components = null)
    {
        this.Item = Item;
        this.Count = Count;
        this.Damage = Damage;
        components = Components ?? ItemComponentMap.Empty;
    }

    public int Item { get; init; }

    public int Count { get; init; }

    public int Damage { get; init; }

    public ItemComponentMap Components
    {
        get => components ?? ItemComponentMap.Empty;
        init => components = value ?? ItemComponentMap.Empty;
    }

    /// <summary>Prázdný slot.</summary>
    public static readonly ItemStack Empty = new(ItemRegistry.Nothing, 0, 0);

    public bool IsEmpty => Item == ItemRegistry.Nothing || Count <= 0;

    /// <summary>Táž věc ve stejném stavu? Podmínka pro slití dvou hromádek.</summary>
    /// <remarks>
    /// Poškození se porovnává taky. Dva krumpáče s různým opotřebením se slít nesmí —
    /// jinak by se opotřebení jednoho z nich tiše ztratilo.
    /// </remarks>
    public bool Matches(ItemStack other) =>
        Item == other.Item
        && Damage == other.Damage
        && Components.Equals(other.Components);

    public ItemStack WithCount(int count) => count <= 0 ? Empty : this with { Count = count };

    public ItemStack WithComponents(ItemComponentMap components) =>
        this with { Components = components ?? throw new ArgumentNullException(nameof(components)) };

    public bool Equals(ItemStack other) =>
        Item == other.Item
        && Count == other.Count
        && Damage == other.Damage
        && Components.Equals(other.Components);

    public override bool Equals(object? obj) => obj is ItemStack other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Item, Count, Damage, Components);

    public static bool operator ==(ItemStack left, ItemStack right) => left.Equals(right);

    public static bool operator !=(ItemStack left, ItemStack right) => !left.Equals(right);
}
