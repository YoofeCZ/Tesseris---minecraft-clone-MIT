using System.Text.Json;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class WorldCatalogTests
{
    [Fact]
    public void Create_sanitizuje_traversal_a_diakritiku_a_zapise_metadata()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = new WorldCatalog(temporary.Path);

        WorldInfo world = catalog.Create("../Žluťoučký svět", -42);

        Assert.Equal("zlutoucky-svet", world.FolderId);
        Assert.Equal(Path.Combine(temporary.Path, "zlutoucky-svet"), world.Directory);
        Assert.True(Directory.Exists(world.Directory));

        string metadataPath = Path.Combine(world.Directory, WorldCatalog.MetadataFileName);
        using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
        Assert.Equal(WorldCatalog.CurrentMetadataVersion, metadata.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("../Žluťoučký svět", metadata.RootElement.GetProperty("name").GetString());
        Assert.Equal(-42, metadata.RootElement.GetProperty("seed").GetInt32());
        Assert.False(metadata.RootElement.TryGetProperty("pregen", out _));
    }

    [Fact]
    public void Create_a_discover_zachovaji_namespaced_world_preset()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = new WorldCatalog(temporary.Path);

        WorldInfo created = catalog.Create("Mod world", 1234, "total:skylands");
        WorldInfo restored = Assert.Single(catalog.Discover());

        Assert.Equal("total:skylands", created.WorldPresetId);
        Assert.Equal(created, restored);
        using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(created.Directory, WorldCatalog.MetadataFileName)));
        Assert.Equal("total:skylands", metadata.RootElement.GetProperty("worldPresetId").GetString());
    }

    [Fact]
    public void Discover_preskoci_metadata_s_neplatnym_world_preset_id()
    {
        using var temporary = new TemporaryDirectory();
        string directory = Path.Combine(temporary.Path, "broken-preset");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, WorldCatalog.MetadataFileName),
            """
            { "version": 2, "name": "Broken", "seed": 1, "worldPresetId": "not namespaced" }
            """);

        Assert.Empty(new WorldCatalog(temporary.Path).Discover());
    }

    [Fact]
    public void Create_neprepise_existujici_svet()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = new WorldCatalog(temporary.Path);

        WorldInfo first = catalog.Create("Můj svět", 1);
        WorldInfo second = catalog.Create("Můj svět", 2);

        Assert.Equal("muj-svet", first.FolderId);
        Assert.Equal("muj-svet-2", second.FolderId);
        Assert.NotEqual(first.Directory, second.Directory);

        var discovered = catalog.Discover();
        Assert.Contains(discovered, world => world.FolderId == first.FolderId && world.Seed == 1);
        Assert.Contains(discovered, world => world.FolderId == second.FolderId && world.Seed == 2);
    }

    [Fact]
    public void Discover_nacte_platna_a_preskoci_poskozena_metadata()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = new WorldCatalog(temporary.Path);
        WorldInfo valid = catalog.Create("Platný", 123);

        string broken = Path.Combine(temporary.Path, "rozbity");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, WorldCatalog.MetadataFileName), "{ toto neni json");

        IReadOnlyList<WorldInfo> worlds = catalog.Discover();

        WorldInfo found = Assert.Single(worlds);
        Assert.Equal(valid, found);
    }

    [Fact]
    public void Discover_ignoruje_stare_pregen_nastaveni_protoze_priprava_je_povinna()
    {
        using var temporary = new TemporaryDirectory();
        string directory = Path.Combine(temporary.Path, "budouci");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, WorldCatalog.MetadataFileName),
            """
            { "version": 1, "name": "Budoucí", "seed": 12, "pregen": 3 }
            """);
        var catalog = new WorldCatalog(temporary.Path);

        WorldInfo world = Assert.Single(catalog.Discover());
        Assert.Equal("Budoucí", world.DisplayName);
        Assert.Equal(12, world.Seed);
    }

    [Fact]
    public void Legacy_svet_bez_metadat_zustane_nabidnuty_a_dostane_metadata()
    {
        using var temporary = new TemporaryDirectory();
        string legacyDirectory = Path.Combine(temporary.Path, "svet");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "stary-soubor.dat"), "data");
        var catalog = new WorldCatalog(temporary.Path);

        WorldInfo legacy = Assert.Single(catalog.Discover());

        Assert.Equal("svet", legacy.DisplayName);
        Assert.Equal("svet", legacy.FolderId);
        Assert.Equal(WorldCatalog.LegacySeed, legacy.Seed);
        Assert.True(File.Exists(Path.Combine(legacyDirectory, WorldCatalog.MetadataFileName)));
    }

    [Fact]
    public void Poskozena_metadata_legacy_sveta_jej_neshodi_ani_neschovaji()
    {
        using var temporary = new TemporaryDirectory();
        string legacyDirectory = Path.Combine(temporary.Path, "svet");
        Directory.CreateDirectory(legacyDirectory);
        string metadataPath = Path.Combine(legacyDirectory, WorldCatalog.MetadataFileName);
        File.WriteAllText(metadataPath, "neplatne");
        var catalog = new WorldCatalog(temporary.Path);

        WorldInfo legacy = Assert.Single(catalog.Discover());

        Assert.Equal(WorldCatalog.LegacySeed, legacy.Seed);
        Assert.Equal("neplatne", File.ReadAllText(metadataPath));
    }

    [Theory]
    [InlineData("CON", "svet-con")]
    [InlineData("../../", "svet")]
    [InlineData("  Dům & zahrada  ", "dum-zahrada")]
    public void Folder_id_je_vzdy_bezpecne_ascii(string name, string expected)
    {
        Assert.Equal(expected, WorldCatalog.SanitizeFolderId(name));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tesseris-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
