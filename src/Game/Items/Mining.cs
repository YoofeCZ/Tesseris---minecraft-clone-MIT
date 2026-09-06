using OpenTK.Mathematics;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.Items;

/// <summary>
/// Kolik práce dá rozbít blok a jak daleko je hráč.
/// </summary>
/// <remarks>
/// <para><b>Blok se rozbíjí držením, ne klikáním.</b> Do téhle chvíle mizel na jedno
/// kliknutí, což znamenalo, že tvrdost bloku ani nástroj v ruce neměly na nic vliv —
/// hlína i kámen stály jeden klik.</para>
///
/// <para><b>Cíl se pamatuje a při přesunutí se postup zahodí.</b> Bez toho by šlo kopat
/// do jednoho bloku, přejet na druhý a ten druhý rozbít okamžitě, protože by zdědil
/// nasbíraný postup.</para>
/// </remarks>
public sealed class Mining
{
    private readonly BlockRegistry _blocks;
    private readonly ItemRegistry _items;

    public Mining(BlockRegistry blocks, ItemRegistry items)
    {
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _items = items ?? throw new ArgumentNullException(nameof(items));
    }

    /// <summary>Blok, do kterého se právě kope. Bez cíle je to <c>null</c>.</summary>
    public Vector3i? Target { get; private set; }

    /// <summary>Jak daleko je rozbití, 0 až 1.</summary>
    public float Progress { get; private set; }

    /// <summary>Nechal by tenhle nástroj po rozbití něco k sebrání?</summary>
    public bool Drops { get; private set; }

    public void Stop()
    {
        Target = null;
        Progress = 0f;
        Drops = false;
    }

    /// <summary>
    /// Posune kopání a řekne, jestli je blok hotový.
    /// </summary>
    /// <param name="tool">Co má hráč v ruce. Prázdná hromádka znamená holou ruku.</param>
    public bool Update(Vector3i target, ushort block, ItemStack tool, double deltaSeconds, bool creative)
    {
        if (Target != target)
        {
            Target = target;
            Progress = 0f;
        }

        Drops = CanHarvest(block, tool);

        // V kreativu se nekope. Blok mizí hned, protože stavění je tam celý smysl a čekání
        // na krumpáč by v něm bylo jen zdržení.
        if (creative)
        {
            Progress = 1f;
            return true;
        }

        // Kámen ani dřevo nejsou nouzově rozbitelné rukou nebo nesprávným nástrojem.
        // Při přepnutí ze správného nástroje se zahodí i rozpracovaný postup,
        // aby jej nešlo dokončit posledním úderem sekery do kamene (nebo naopak).
        if (!Drops)
        {
            Progress = 0f;
            return false;
        }

        float seconds = BreakSeconds(block, tool);

        Progress += seconds <= 0f ? 1f : (float)(deltaSeconds / seconds);

        if (Progress < 1f)
        {
            return false;
        }

        // CÍL SE ZAHODÍ, ALE PŘÍZNAK VÝPADKU ZŮSTANE.
        //
        // Bylo tu Stop(), které nulovalo obojí — jenže volající se na Drops ptá až POTOM,
        // co mu Update řekne, že je blok hotový. Četl proto vždycky nulu a ze země nikdy
        // nic nevypadlo. Příznak platí až do dalšího Update, kde se stejně přepočítá.
        Target = null;
        Progress = 0f;

        return true;
    }

    /// <summary>
    /// Jak dlouho trvá rozbít blok daným nástrojem, ve vteřinách.
    /// </summary>
    /// <remarks>
    /// Tvrdost je základ a správný nástroj ji dělí svou rychlostí. Blok, který
    /// vyžaduje jiný druh nebo vyšší stupeň nástroje, vrací nekonečný čas:
    /// jeho rozbití v survivalu není možné.
    /// </remarks>
    public float BreakSeconds(ushort block, ItemStack tool)
    {
        BlockDefinition definition = _blocks.Definition(block);

        if (!CanHarvest(block, tool))
        {
            return float.PositiveInfinity;
        }

        float hardness = MathF.Max(definition.Hardness, 0.05f);

        ToolKind needed = PreferredTool(definition.Material);
        (ToolKind kind, ToolTier tier) = Describe(tool);

        float speed = kind != ToolKind.None && kind == needed ? ToolSpeed(kind, tier) : 1f;

        return hardness / speed;
    }

    /// <summary>Vypadne z bloku něco, když ho rozbije tenhle nástroj?</summary>
    public bool CanHarvest(ushort block, ItemStack tool)
    {
        BlockDefinition definition = _blocks.Definition(block);

        ToolTier needed = RequiredTier(definition);

        if (needed == ToolTier.None)
        {
            return true;
        }

        (ToolKind kind, ToolTier tier) = Describe(tool);

        return kind == PreferredTool(definition.Material) && tier >= needed;
    }

    private (ToolKind Kind, ToolTier Tier) Describe(ItemStack stack)
    {
        if (stack.IsEmpty)
        {
            return (ToolKind.None, ToolTier.None);
        }

        ItemDefinition definition = _items.Definition(stack.Item);

        return definition.Kind == ItemKind.Tool
            ? (definition.Tool, definition.Tier)
            : (ToolKind.None, ToolTier.None);
    }

    /// <summary>
    /// Kterým nástrojem se materiál těží rychle.
    /// </summary>
    /// <remarks>
    /// Odvozuje se z materiálu, ne z vlastního pole v definici bloku. Materiál už každý
    /// blok má a ta vazba je jednoznačná — kámen krumpáčem, dřevo sekerou, hlína lopatou.
    /// Vlastní pole by znamenalo vyplnit ho u čtyřiceti bloků a u každého dalšího na to
    /// nezapomenout.
    /// </remarks>
    public static ToolKind PreferredTool(BlockMaterial material) => material switch
    {
        BlockMaterial.Stone => ToolKind.Pickaxe,
        BlockMaterial.Wood => ToolKind.Axe,
        BlockMaterial.Soil or BlockMaterial.Sand => ToolKind.Shovel,
        _ => ToolKind.None,
    };

    /// <summary>
    /// Jaký stupeň nástroje je potřeba, aby z bloku něco vypadlo.
    /// </summary>
    /// <remarks>
    /// Kámen chce aspoň pazourkový krumpáč a dřevo aspoň pazourkovou sekeru.
    /// Půda, písek a drobné sběratelné předměty zůstávají rozbitelné rukou.
    /// </remarks>
    public static ToolTier RequiredTier(BlockMaterial material) => material switch
    {
        BlockMaterial.Stone or BlockMaterial.Wood => ToolTier.Flint,
        _ => ToolTier.None,
    };

    /// <summary>
    /// Vrátí skutečný požadavek bloku. Explicitní hodnota v JSON má přednost před
    /// obecným pravidlem materiálu; staré definice bez pole se proto chovají beze změny.
    /// </summary>
    public static ToolTier RequiredTier(BlockDefinition definition) => definition.RequiredTier switch
    {
        BlockHarvestTier.None => ToolTier.None,
        BlockHarvestTier.Flint => ToolTier.Flint,
        BlockHarvestTier.Stone => ToolTier.Stone,
        BlockHarvestTier.Iron => ToolTier.Iron,
        BlockHarvestTier.Diamond => ToolTier.Diamond,
        _ => RequiredTier(definition.Material),
    };

    /// <summary>
    /// Kolikrát rychleji kope správný nástroj. Sekery mají samostatnou stupnici: strom
    /// nesmí zmizet po jediném máchnutí, zatímco diamantová má pořád působit výrazně lépe.
    /// Jeden celý švih ruky trvá 1 / 2,2 s, takže běžný kmen (tvrdost 1,2) vychází pro
    /// kamennou sekeru na 3,75 s = 8,25 švihu a pro diamantovou na 0,91 s = 2 švihy.
    /// </summary>
    private static float ToolSpeed(ToolKind kind, ToolTier tier)
    {
        if (kind == ToolKind.Axe)
        {
            return tier switch
            {
                ToolTier.Flint => 0.22f,
                ToolTier.Stone => 0.32f,
                ToolTier.Iron => 0.66f,
                ToolTier.Diamond => 1.32f,
                _ => 1f,
            };
        }

        return tier switch
        {
            ToolTier.Flint => 2f,
            ToolTier.Stone => 4f,
            ToolTier.Iron => 6f,
            ToolTier.Diamond => 8f,
            _ => 1f,
        };
    }
}
