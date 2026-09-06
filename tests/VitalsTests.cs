using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.Player;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Životy, dech pod vodou a zranění z pádu.
/// </summary>
/// <remarks>
/// <para>Je to jediné místo ve hře, kde se hráči něco odebírá, takže chyba tady se pozná
/// buď tím, že je nesmrtelný, nebo tím, že umře bez příčiny. Obojí se špatně hlásí.</para>
/// </remarks>
public sealed class VitalsTests
{
    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    private static ItemRegistry Items(BlockRegistry blocks) =>
        ItemRegistry.Create(blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));

    /// <summary>Svět s kamennou podlahou a volným vzduchem nad ní.</summary>
    private static VoxelWorld Ground(BlockRegistry registry, int floor = 64)
    {
        var world = new VoxelWorld(registry);
        ushort stone = registry.IndexOf("tesseris:stone");

        List<(Vector3i, ushort)> blocks = [];

        for (int z = -4; z <= 4; z++)
        {
            for (int x = -4; x <= 4; x++)
            {
                blocks.Add((new Vector3i(x, floor, z), stone));
            }
        }

        world.SetBlocks(blocks);
        return world;
    }

    /// <summary>Hráč stojící na podlaze, který právě dopadl zadanou rychlostí.</summary>
    private static PlayerController Lander(float speed, int floor = 64)
    {
        var player = new PlayerController(new Vector3(0.5f, floor + 1f, 0.5f));
        player.Teleport(new Vector3(0.5f, floor + 1f, 0.5f));
        player.Velocity = new Vector3(0f, -speed, 0f);
        return player;
    }

    /// <summary>Nechá hráče skutečně spadnout ze zadané výšky a vrátí utržené zranění.</summary>
    private static float DropDamage(float blocks, int floor = 64)
    {
        BlockRegistry registry = Registry();
        ItemRegistry items = Items(registry);
        VoxelWorld world = Ground(registry, floor);
        var inventory = new Inventory(items);
        var player = new PlayerController(new Vector3(0.5f, floor + 1f + blocks, 0.5f));
        var vitals = new Vitals();

        const float step = 1f / 120f;

        for (int frame = 0; frame < 2400; frame++)
        {
            player.Update(world, Vector3.Zero, jump: false, sprint: false, 0f, step);
            vitals.Update(world, player, inventory, creative: false, step);

            if (player.OnGround)
            {
                return Vitals.MaxHealth - vitals.Health;
            }
        }

        throw new InvalidOperationException($"Hráč nedopadl z výšky {blocks} bloků.");
    }

    /// <summary>Zdravý hráč má plné životy i dech.</summary>
    [Fact]
    public void Zdravy_hrac_ma_plny_stav()
    {
        var vitals = new Vitals();

        Assert.Equal(Vitals.MaxHealth, vitals.Health, 3);
        Assert.Equal(Vitals.MaxBreath, vitals.Breath, 3);
        Assert.False(vitals.Dead);
    }

    /// <summary>
    /// Seskok z nízka nebolí.
    /// </summary>
    /// <remarks>
    /// Při stavění hráč skáče z kostky pořád. Kdyby ho to zraňovalo, byla by z bezpečné
    /// meze past a hráč by si toho všiml až na prázdném řádku srdcí.
    /// </remarks>
    [Fact]
    public void Seskok_z_nizka_neboli()
    {
        var vitals = new Vitals();

        vitals.Hurt(0f, ignoreArmour: true, armour: null);

        Assert.Equal(Vitals.MaxHealth, vitals.Health, 3);
    }

    /// <summary>Tři bloky jsou bezpečné, každý další vezme půl srdce.</summary>
    [Theory]
    [InlineData(3f, 0f)]
    [InlineData(4f, 1f)]
    [InlineData(5f, 2f)]
    [InlineData(8f, 5f)]
    [InlineData(22f, 19f)]
    public void Poskozeni_roste_s_vyskou_padu(float blocks, float expectedDamage)
    {
        float impactSpeed = MathF.Sqrt(2f * PlayerController.Gravity * blocks);

        Assert.Equal(expectedDamage, Vitals.FallDamage(impactSpeed), 3);
    }

    /// <summary>Práh platí i v celé fyzice, ne jen v samotném vzorci.</summary>
    [Fact]
    public void Skutecny_pad_zacne_bolit_od_ctvrteho_bloku()
    {
        float threeBlocks = DropDamage(3f);
        float fourBlocks = DropDamage(4f);
        float eightBlocks = DropDamage(8f);

        Assert.Equal(0f, threeBlocks, 3);
        Assert.InRange(fourBlocks, 0.95f, 1.2f);
        Assert.True(eightBlocks > fourBlocks, "Vyšší pád neudělil vyšší poškození.");
    }

    /// <summary>Dvaadvacet bloků je poslední pád, po kterém plný hráč přežije s půlsrdcem.</summary>
    [Fact]
    public void Pad_z_dvaadvaceti_bloku_necha_pul_srdce()
    {
        float damage = DropDamage(22f);

        Assert.InRange(damage, 18.8f, 19.2f);
        Assert.InRange(Vitals.MaxHealth - damage, 0.8f, 1.2f);
    }

    /// <summary>Zranění ubere přesně tolik, kolik má.</summary>
    [Fact]
    public void Zraneni_ubere_kolik_ma()
    {
        var vitals = new Vitals();

        vitals.Hurt(5f, ignoreArmour: true, armour: null);

        Assert.Equal(Vitals.MaxHealth - 5f, vitals.Health, 3);
        Assert.Equal(5f, vitals.LastDamage, 3);
    }

    /// <summary>
    /// Hned po zranění je hráč chvíli nezranitelný.
    /// </summary>
    /// <remarks>
    /// Bez toho by utopení i pád ubíraly každý snímek a hráč by zemřel dřív, než by stihl
    /// zareagovat — vypadalo by to jako okamžitá smrt bez příčiny.
    /// </remarks>
    [Fact]
    public void Po_zraneni_je_chvili_nezranitelny()
    {
        var vitals = new Vitals();

        vitals.Hurt(3f, ignoreArmour: true, armour: null);
        vitals.Hurt(3f, ignoreArmour: true, armour: null);

        Assert.Equal(Vitals.MaxHealth - 3f, vitals.Health, 3);
    }

    /// <summary>Brnění zranění zkrátí, ale nezruší.</summary>
    [Fact]
    public void Brneni_zraneni_zkrati()
    {
        BlockRegistry blocks = Registry();
        ItemRegistry items = Items(blocks);

        var armour = new Inventory(items);

        foreach ((ArmourSlot slot, string piece) in new[]
        {
            (ArmourSlot.Helmet, "helmet"), (ArmourSlot.Chestplate, "chestplate"),
            (ArmourSlot.Leggings, "leggings"), (ArmourSlot.Boots, "boots"),
        })
        {
            armour[Inventory.FirstArmourSlot + (int)slot] =
                new ItemStack(items.IndexOf($"tesseris:diamond_{piece}"), 1, 0);
        }

        var vitals = new Vitals();
        vitals.Hurt(10f, ignoreArmour: false, armour);

        // Strop ochrany je pod jedničkou, takže něco vždycky projde.
        Assert.True(vitals.LastDamage > 0f, "Plná zbroj dala nezranitelnost.");
        Assert.True(vitals.LastDamage < 10f, "Brnění nezkrátilo zranění.");
        Assert.Equal(10f * (1f - Inventory.MaxProtection), vitals.LastDamage, 2);
    }

    /// <summary>Utopení brnění nezastaví — chrání proti nárazu, ne proti nedýchání.</summary>
    [Fact]
    public void Utopeni_brneni_nezastavi()
    {
        BlockRegistry blocks = Registry();
        ItemRegistry items = Items(blocks);

        var armour = new Inventory(items);
        armour[Inventory.FirstArmourSlot] = new ItemStack(items.IndexOf("tesseris:diamond_helmet"), 1, 0);

        var vitals = new Vitals();
        vitals.Hurt(4f, ignoreArmour: true, armour);

        Assert.Equal(4f, vitals.LastDamage, 3);
    }

    /// <summary>
    /// Pod vodou ubývá dech a po jeho vyčerpání se hráč topí.
    /// </summary>
    /// <remarks>
    /// Patnáct vteřin je dost na to, aby se dalo potápět, a málo na to, aby se pod hladinou
    /// dalo bydlet.
    /// </remarks>
    [Fact]
    public void Pod_vodou_dochazi_dech_a_pak_se_hrac_topi()
    {
        BlockRegistry blocks = Registry();
        ItemRegistry items = Items(blocks);

        var world = new VoxelWorld(blocks);
        ushort water = blocks.IndexOf("tesseris:water");

        // Sloup vody kolem hlavy hráče.
        List<(Vector3i, ushort)> pool = [];

        for (int y = 60; y <= 70; y++)
        {
            pool.Add((new Vector3i(0, y, 0), water));
        }

        world.SetBlocks(pool);

        var player = new PlayerController(new Vector3(0.5f, 64f, 0.5f));
        player.Teleport(new Vector3(0.5f, 64f, 0.5f));

        var vitals = new Vitals();
        var inventory = new Inventory(items);

        // Vteřina pod vodou: dech ubyl, životy zatím ne.
        vitals.Update(world, player, inventory, creative: false, 1f);

        Assert.True(vitals.Submerged, "Hlava pod hladinou se nepoznala.");
        Assert.True(vitals.Breath < Vitals.MaxBreath, "Dech pod vodou neubývá.");
        Assert.Equal(Vitals.MaxHealth, vitals.Health, 3);

        // Dost dlouho na to, aby dech došel i aby přišlo první utopení.
        for (int i = 0; i < 40; i++)
        {
            vitals.Update(world, player, inventory, creative: false, 0.5f);
        }

        Assert.Equal(0f, vitals.Breath, 3);
        Assert.True(vitals.Health < Vitals.MaxHealth, "Bez dechu se hráč netopí.");
    }

    /// <summary>Nad hladinou se dech vrací.</summary>
    [Fact]
    public void Nad_hladinou_se_dech_vraci()
    {
        BlockRegistry blocks = Registry();
        ItemRegistry items = Items(blocks);

        VoxelWorld world = Ground(blocks);

        var player = new PlayerController(new Vector3(0.5f, 65f, 0.5f));
        player.Teleport(new Vector3(0.5f, 65f, 0.5f));

        var vitals = new Vitals();
        var inventory = new Inventory(items);

        vitals.Hurt(0f, ignoreArmour: true, null);
        vitals.Update(world, player, inventory, creative: false, 5f);

        Assert.False(vitals.Submerged);
        Assert.Equal(Vitals.MaxBreath, vitals.Breath, 3);
    }

    /// <summary>V kreativu se neubližuje: stavění je tam celý smysl.</summary>
    [Fact]
    public void V_kreativu_se_neublizuje()
    {
        BlockRegistry blocks = Registry();
        ItemRegistry items = Items(blocks);

        VoxelWorld world = Ground(blocks);

        var vitals = new Vitals();
        var inventory = new Inventory(items);

        PlayerController player = Lander(60f);

        vitals.Update(world, player, inventory, creative: true, 0.1f);

        Assert.Equal(Vitals.MaxHealth, vitals.Health, 3);
        Assert.Equal(Vitals.MaxBreath, vitals.Breath, 3);
    }

    /// <summary>Po návratu na začátek je hráč zase zdravý.</summary>
    [Fact]
    public void Reset_vrati_plny_stav()
    {
        var vitals = new Vitals();

        vitals.Hurt(Vitals.MaxHealth, ignoreArmour: true, null);
        Assert.True(vitals.Dead);

        vitals.Reset();

        Assert.False(vitals.Dead);
        Assert.Equal(Vitals.MaxHealth, vitals.Health, 3);
    }
}
