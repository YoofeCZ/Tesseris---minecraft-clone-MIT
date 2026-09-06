namespace Tesseris.Game.Items;

/// <summary>
/// Inventář hráče: pás pod rukou a batoh nad ním.
/// </summary>
/// <remarks>
/// <para><b>Pás je prvních devět slotů téhož pole</b>, ne samostatná struktura. Předmět
/// se mezi pásem a batohem přesouvá pořád, a se dvěma poli by každý takový přesun byl
/// zvláštní případ — včetně toho, kdy se předmět bere z pásu a vrací do batohu.</para>
///
/// <para><see cref="Held"/> je hromádka „v kurzoru", tedy to, co hráč zrovna přenáší myší.
/// Není součástí slotů schválně: kdyby byla, dala by se omylem uložit do světa nebo
/// zdvojit při zavření panelu.</para>
/// </remarks>
public sealed class Inventory
{
    /// <summary>Kolik slotů má pás pod rukou.</summary>
    public const int HotbarSlots = 9;

    /// <summary>Kolik slotů má batoh nad pásem.</summary>
    public const int BackpackSlots = 27;

    public const int TotalSlots = HotbarSlots + BackpackSlots;

    /// <summary>
    /// Kolik má hráč míst na výstroj: hlava, hruď, nohy, boty.
    /// </summary>
    /// <remarks>
    /// <para>Jsou to <b>další sloty téhož inventáře</b>, ne zvláštní úložiště. Díky tomu
    /// s nimi umí pracovat všechno, co už existuje — přesouvání myší, ukládání i vysypání
    /// při smrti — a nemusí kvůli nim vzniknout druhá cesta.</para>
    ///
    /// <para>Pořadí je shora dolů, jak to vidí hráč: 0 hlava, 1 hruď, 2 nohy, 3 boty.</para>
    /// </remarks>
    public const int ArmourSlots = 4;

    /// <summary>Index prvního slotu výstroje.</summary>
    public const int FirstArmourSlot = TotalSlots;

    /// <summary>
    /// Patří ten předmět do zadaného slotu výstroje?
    /// </summary>
    /// <remarks>
    /// Prázdná hromádka projde vždycky — to je vytažení toho, co ve slotu bylo. Bez téhle
    /// kontroly by si hráč nasadil na hlavu kámen; a hlavně by pak nechápal, proč mu to
    /// nic nedělá.
    /// </remarks>
    public bool FitsArmourSlot(ItemStack stack, int slot)
    {
        if (stack.IsEmpty)
        {
            return true;
        }

        ItemDefinition definition = _items.Definition(stack.Item);

        return definition.Kind == ItemKind.Armour && (int)definition.Slot == slot;
    }

    /// <summary>
    /// Kolik zranění pohltí nasazená výstroj, 0 až <see cref="MaxProtection"/>.
    /// </summary>
    /// <remarks>
    /// Sčítá se přes všechny kusy a strop je pod jedničkou. Plná nezranitelnost by
    /// z brnění udělala vypínač obtížnosti.
    /// </remarks>
    public float Protection
    {
        get
        {
            float total = 0f;

            for (int slot = 0; slot < ArmourSlots; slot++)
            {
                ItemStack worn = _slots[FirstArmourSlot + slot];

                if (!worn.IsEmpty)
                {
                    total += _items.Definition(worn.Item).Protection;
                }
            }

            return MathF.Min(total, MaxProtection);
        }
    }

    /// <summary>Nejvíc, co výstroj pohltí. Zbytek projde vždycky.</summary>
    public const float MaxProtection = 0.8f;

    /// <summary>Kolik slotů má inventář dohromady, i s výstrojí.</summary>
    public const int AllSlots = TotalSlots + ArmourSlots;

    private readonly ItemStack[] _slots = new ItemStack[AllSlots];
    private readonly ItemRegistry _items;

    public Inventory(ItemRegistry items)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        Array.Fill(_slots, ItemStack.Empty);
    }

    /// <summary>Hromádka v kurzoru, kterou hráč přenáší myší.</summary>
    public ItemStack Held { get; set; } = ItemStack.Empty;

    /// <summary>Který slot pásu je vybraný, 0 až 8.</summary>
    public int Selected { get; private set; }

    public ItemStack this[int slot]
    {
        get => _slots[slot];
        set => _slots[slot] = value.IsEmpty ? ItemStack.Empty : value;
    }

    public ItemStack SelectedStack => _slots[Selected];

    public void Select(int slot)
    {
        if (slot >= 0 && slot < HotbarSlots)
        {
            Selected = slot;
        }
    }

    /// <summary>Posune výběr po pásu a obtočí ho.</summary>
    public void Scroll(int delta)
    {
        int slot = (Selected + delta) % HotbarSlots;
        Selected = slot < 0 ? slot + HotbarSlots : slot;
    }

    /// <summary>Ubere jeden kus z vybraného slotu. Vrací, jestli tam co ubrat bylo.</summary>
    public bool ConsumeSelected()
    {
        ItemStack stack = _slots[Selected];

        if (stack.IsEmpty)
        {
            return false;
        }

        _slots[Selected] = stack.WithCount(stack.Count - 1);
        return true;
    }

    /// <summary>
    /// Přidá hromádku do inventáře. Vrací, co se nevešlo.
    /// </summary>
    /// <remarks>
    /// <para><b>Nejdřív se dosypává do rozdělaných hromádek, teprve pak se zabírá prázdný
    /// slot.</b> Obráceně by se inventář zaplnil devíti sloty po jednom kameni, přestože
    /// by se všechny vešly do jednoho.</para>
    ///
    /// <para>Pás má přednost před batohem, aby se vytěžený materiál objevil rovnou po ruce.</para>
    /// </remarks>
    public ItemStack Add(ItemStack stack)
    {
        if (stack.IsEmpty)
        {
            return ItemStack.Empty;
        }

        int max = _items.Definition(stack.Item).MaxStack;

        for (int slot = 0; slot < TotalSlots && stack.Count > 0; slot++)
        {
            ItemStack current = _slots[slot];

            if (current.IsEmpty || !current.Matches(stack) || current.Count >= max)
            {
                continue;
            }

            int room = max - current.Count;
            int moved = Math.Min(room, stack.Count);

            _slots[slot] = current.WithCount(current.Count + moved);
            stack = stack.WithCount(stack.Count - moved);
        }

        for (int slot = 0; slot < TotalSlots && stack.Count > 0; slot++)
        {
            if (!_slots[slot].IsEmpty)
            {
                continue;
            }

            int moved = Math.Min(max, stack.Count);

            _slots[slot] = stack.WithCount(moved);
            stack = stack.WithCount(stack.Count - moved);
        }

        return stack;
    }

    /// <summary>Kolik kusů daného předmětu inventář celkem drží.</summary>
    public int CountOf(int item)
    {
        int total = 0;

        // VÝSTROJ SE NEPOČÍTÁ. Kdyby ano, hlásila by výroba, že na recept je — a pak by
        // ho neuměla vyrobit, protože Remove sahá jen do pásu a batohu. Hráč by z toho
        // viděl recept, který jde označit a nic se nestane.
        for (int slot = 0; slot < TotalSlots; slot++)
        {
            ItemStack stack = _slots[slot];

            if (!stack.IsEmpty && stack.Item == item)
            {
                total += stack.Count;
            }
        }

        return total;
    }

    /// <summary>
    /// Odebere zadaný počet kusů. Vrací, jestli jich bylo dost — <b>a když nebylo,
    /// neodebere nic</b>.
    /// </summary>
    /// <remarks>
    /// Všechno nebo nic je tu podstatné: výroba se ptá na několik surovin a musí jít
    /// zrušit, když poslední z nich chybí. Částečný odběr by hráči suroviny sežral.
    /// </remarks>
    public bool Remove(int item, int count)
    {
        if (CountOf(item) < count)
        {
            return false;
        }

        for (int slot = 0; slot < TotalSlots && count > 0; slot++)
        {
            ItemStack stack = _slots[slot];

            if (stack.IsEmpty || stack.Item != item)
            {
                continue;
            }

            int taken = Math.Min(stack.Count, count);

            _slots[slot] = stack.WithCount(stack.Count - taken);
            count -= taken;
        }

        return true;
    }

    /// <summary>
    /// Připíše nástroji opotřebení. Vrací, jestli se tím rozpadl.
    /// </summary>
    public bool DamageSelected(int amount)
    {
        ItemStack stack = _slots[Selected];

        if (stack.IsEmpty)
        {
            return false;
        }

        int durability = _items.Definition(stack.Item).Durability;

        if (durability <= 0)
        {
            return false;
        }

        int damage = stack.Damage + amount;

        if (damage >= durability)
        {
            _slots[Selected] = ItemStack.Empty;
            return true;
        }

        _slots[Selected] = stack with { Damage = damage };
        return false;
    }

    public void Clear() => Array.Fill(_slots, ItemStack.Empty);
}
