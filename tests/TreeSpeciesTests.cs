using Tesseris.Game.Blocks;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Druhy stromů a jejich bloky.
///
/// <para>Testuje se hlavně to, co se pozná až ve hře a pozdě: <b>chybějící textura</b>.
/// Definice bloku se načte i tehdy, když soubor s texturou neexistuje — a v tu chvíli je
/// v atlasu díra. Přesně to se stalo akácii, která na kmen odkazovala
/// <c>acacia_log_side</c>, jenže ten soubor v assetech nikdy nebyl.</para>
/// </summary>
public sealed class TreeSpeciesTests
{
    private static readonly string BlocksDirectory =
        Path.Combine(AppContext.BaseDirectory, "assets", "blocks");

    private static readonly string TexturesDirectory =
        Path.Combine(AppContext.BaseDirectory, "assets", "textures");

    private static BlockRegistry Registry() => BlockRegistry.LoadFromDirectory(BlocksDirectory);

    /// <summary>Druhy, které umí <c>TreePlanter</c> zasadit.</summary>
    public static TheoryData<string> Species() => ["oak", "spruce", "acacia", "birch", "maple"];

    [Theory]
    [MemberData(nameof(Species))]
    public void Kazdy_druh_ma_kmen_i_listi(string species)
    {
        BlockRegistry registry = Registry();

        ushort log = registry.IndexOf($"tesseris:{species}_log");
        ushort leaves = registry.IndexOf($"tesseris:{species}_leaves");

        Assert.NotEqual(BlockRegistry.Air, log);
        Assert.NotEqual(BlockRegistry.Air, leaves);

        // Listí musí jít VÝŘEZEM, kmen ne. Kdyby se to prohodilo, byla by z koruny plná
        // kostka — a přesně tohle je na nové definici to jediné, co se dá splést.
        //
        // Neprůhlednost ani průchodnost se nekontrolují: sloupek blok nevyplňuje celý,
        // takže se za neprůhledný nepočítá, a listí je naopak pevné, jako v jiných
        // blokových hrách.
        Assert.True(registry.IsCutout(leaves), $"{species}: listí musí jít výřezem.");
        Assert.False(registry.IsCutout(log), $"{species}: kmen nemá jít výřezem.");
    }

    /// <summary>
    /// Každá textura, na kterou se definice odkazuje, musí existovat jako soubor.
    /// </summary>
    [Theory]
    [MemberData(nameof(Species))]
    public void Kazda_textura_druhu_existuje(string species)
    {
        string[] expected =
        [
            $"{species}_log_side.png",
            $"{species}_log_top.png",
            $"{species}_leaves.png",
            $"{species}_leaves_02.png",
            $"{species}_leaves_03.png",
        ];

        foreach (string file in expected)
        {
            Assert.True(
                File.Exists(Path.Combine(TexturesDirectory, file)),
                $"Chybí textura {file}. Definice bloku se načte i bez ní a v atlasu zůstane díra.");
        }
    }

    /// <summary>
    /// Listí každého druhu musí mít tři různé karty. Kdyby dvě z nich byly tatáž, koruna
    /// by vypadala razítkovaně — a pozná se to jen okem, na které je pozdě.
    /// </summary>
    [Theory]
    [MemberData(nameof(Species))]
    public void Karty_listi_se_navzajem_lisi(string species)
    {
        byte[] first = File.ReadAllBytes(Path.Combine(TexturesDirectory, $"{species}_leaves.png"));
        byte[] second = File.ReadAllBytes(Path.Combine(TexturesDirectory, $"{species}_leaves_02.png"));
        byte[] third = File.ReadAllBytes(Path.Combine(TexturesDirectory, $"{species}_leaves_03.png"));

        Assert.False(first.AsSpan().SequenceEqual(second), $"{species}: karty 1 a 2 jsou totožné.");
        Assert.False(second.AsSpan().SequenceEqual(third), $"{species}: karty 2 a 3 jsou totožné.");
        Assert.False(first.AsSpan().SequenceEqual(third), $"{species}: karty 1 a 3 jsou totožné.");
    }

    /// <summary>
    /// Každý druh ve světě opravdu roste.
    /// </summary>
    /// <remarks>
    /// <para><b>Textury nejsou důkaz, že strom existuje.</b> Ostatní testy tady porovnávají
    /// obrázky, takže by prošly i pro druh, který se nikde nerozsazuje — a přesně na to se
    /// narazilo: „mapple tree jsem nikde nenašel".</para>
    ///
    /// <para>Ptá se to <see cref="TerrainGenerator.TryCanopyAt"/>, tedy týmž výpočtem, jakým
    /// se stromy sázejí do chunků. Prochází se souvislá plocha, ne mřížka se skokem — strom
    /// stojí na konkrétním sloupci a skok by ho minul.</para>
    /// </remarks>
    [Fact]
    public void Kazdy_druh_ve_svete_opravdu_roste()
    {
        BlockRegistry registry = BlockRegistry.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

        var generator = new TerrainGenerator(registry, 20260727);
        generator.EnableTrees(registry);

        string[] species = ["oak", "spruce", "acacia", "birch", "maple"];

        var counts = new Dictionary<ushort, int>();

        foreach (string name in species)
        {
            counts[registry.IndexOf($"tesseris:{name}_leaves")] = 0;
        }

        // Plocha kolem počátku, dost velká na to, aby zabrala víc biomů.
        const int Reach = 700;

        for (int z = -Reach; z <= Reach; z++)
        {
            for (int x = -Reach; x <= Reach; x++)
            {
                if (generator.TryCanopyAt(x, z, out TreePlanter.Canopy canopy)
                    && counts.ContainsKey(canopy.Leaves))
                {
                    counts[canopy.Leaves]++;
                }
            }
        }

        foreach (string name in species)
        {
            ushort leaves = registry.IndexOf($"tesseris:{name}_leaves");

            Assert.True(
                counts[leaves] > 0,
                $"'{name}' neroste nikde v okolí {Reach} bloků od počátku — "
                + $"nalezeno: {string.Join(", ", species.Select(s => $"{s}={counts[registry.IndexOf($"tesseris:{s}_leaves")]}"))}");
        }
    }

    /// <summary>
    /// A druhy se musí lišit navzájem. Smrk dlouho nesl textury břízy a nikdo si toho
    /// nevšiml, protože se to nikde neporovnávalo.
    /// </summary>
    [Fact]
    public void Druhy_nesdileji_tytez_textury()
    {
        string[] species = ["oak", "spruce", "acacia", "birch", "maple"];

        for (int i = 0; i < species.Length; i++)
        {
            for (int j = i + 1; j < species.Length; j++)
            {
                byte[] left = File.ReadAllBytes(Path.Combine(TexturesDirectory, $"{species[i]}_log_side.png"));
                byte[] right = File.ReadAllBytes(Path.Combine(TexturesDirectory, $"{species[j]}_log_side.png"));

                Assert.False(
                    left.AsSpan().SequenceEqual(right),
                    $"{species[i]} a {species[j]} mají tutéž kůru.");
            }
        }
    }
}
