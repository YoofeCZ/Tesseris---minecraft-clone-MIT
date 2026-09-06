using System.IO.Compression;
using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.Modding;
using Tesseris.Game.World;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Reads frozen files produced before the gameplay-platform refactor. These tests deliberately do
/// not call the corresponding current writers before reading, so a new encoder cannot accidentally
/// bless an incompatible decoder.
/// </summary>
public sealed class SaveCompatibilityFixtureTests
{
    [Fact]
    public void Chk2_fixture_decodes_palette_and_packed_indices()
    {
        BlockRegistry blocks = Blocks();
        byte[] compressed = Convert.FromBase64String(FixtureText("chunk-chk2-alternating.base64.gz"));
        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var payload = new MemoryStream();
        gzip.CopyTo(payload);

        Assert.Equal(4_136, payload.Length);
        Chunk chunk = ChunkSerializer.Decode(payload.ToArray(), blocks);
        ushort air = blocks.IndexOf("tesseris:air");
        ushort stone = blocks.IndexOf("tesseris:stone");

        Assert.False(chunk.IsHomogeneous);
        Assert.Equal(1, chunk.BitsPerIndex);
        Assert.Equal(2, chunk.PaletteCount);
        for (int index = 0; index < Chunk.Volume; index++)
        {
            int x = index & Chunk.SizeMask;
            int z = (index >> Chunk.SizeShift) & Chunk.SizeMask;
            int y = index >> (Chunk.SizeShift * 2);
            Assert.Equal((index & 1) == 0 ? air : stone, chunk.GetBlock(x, y, z));
        }
    }

    [Fact]
    public void Frn2_fixture_loads_stable_item_ids_and_runtime_state()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "pece.dat");
        File.WriteAllBytes(path, Convert.FromBase64String(FixtureText("furnaces-frn2.base64")));
        (ItemRegistry items, Furnaces furnaces) = FurnacesRuntime();

        Assert.Equal(1, furnaces.Load(path));

        Furnace furnace = furnaces.At(new Vector3i(3, 4, -5));
        AssertStack(furnace.Fuel, items, "tesseris:coal", count: 2, damage: 0);
        AssertStack(furnace.Input[0], items, "tesseris:raw_iron", count: 3, damage: 0);
        Assert.True(furnace.Input[1].IsEmpty);
        Assert.Equal(0.25f, furnace.Progress[0]);
        Assert.Equal(0f, furnace.Progress[1]);
        AssertStack(furnace.Output[0], items, "tesseris:iron_ingot", count: 1, damage: 0);
        Assert.True(furnace.Output[1].IsEmpty);
        AssertStack(furnace.Output[2], items, "tesseris:iron_pickaxe", count: 1, damage: 37);
        Assert.Equal(4.5f, furnace.BurnLeft);
        Assert.Equal(8f, furnace.BurnTotal);
        Assert.True(furnace.Running);
    }

    [Fact]
    public void World_json_v1_fixture_is_discovered_without_migration_write()
    {
        using var temporary = new TemporaryDirectory();
        string worldDirectory = Path.Combine(temporary.Path, "frozen-world");
        Directory.CreateDirectory(worldDirectory);
        string metadataPath = Path.Combine(worldDirectory, WorldCatalog.MetadataFileName);
        string fixture = FixtureText("world-v1.json");
        File.WriteAllText(metadataPath, fixture);
        var catalog = new WorldCatalog(temporary.Path);

        WorldInfo world = Assert.Single(catalog.Discover());

        Assert.Equal("Frozen compatibility world", world.DisplayName);
        Assert.Equal("frozen-world", world.FolderId);
        Assert.Equal(-20260810, world.Seed);
        Assert.Equal(fixture, File.ReadAllText(metadataPath));
    }

    [Fact]
    public void Moddata_v1_fixture_restores_world_and_position_blobs()
    {
        using var temporary = new TemporaryDirectory();
        string dataDirectory = Path.Combine(temporary.Path, ModDataStore.DirectoryName);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(
            Path.Combine(dataDirectory, ModDataStore.FileNameForMod("fixture")),
            FixtureText("moddata-v1.json"));
        var store = new ModDataStore();
        IModData data = store.ForMod("fixture");

        store.AttachWorld(temporary.Path);

        Assert.True(data.World!.TryGet(
            new ResourceId("fixture:greeting"),
            out ReadOnlyMemory<byte> greeting));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, greeting.ToArray());
        Assert.True(data.At(new ModBlockPosition(-2, 64, 9))!.TryGet(
            new ResourceId("fixture:counter"),
            out ReadOnlyMemory<byte> counter));
        Assert.Equal(42, BitConverter.ToInt32(counter.Span));
    }

    private static BlockRegistry Blocks() => BlockRegistry.LoadFromDirectory(
        Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    private static (ItemRegistry Items, Furnaces Furnaces) FurnacesRuntime()
    {
        BlockRegistry blocks = Blocks();
        ItemRegistry items = ItemRegistry.Create(
            blocks,
            Path.Combine(AppContext.BaseDirectory, "assets", "items"));
        RecipeBook recipes = RecipeBook.Load(
            items,
            Path.Combine(AppContext.BaseDirectory, "assets", "recipes"));
        return (items, new Furnaces(items, recipes));
    }

    private static void AssertStack(
        ItemStack stack,
        ItemRegistry items,
        string expectedId,
        int count,
        int damage)
    {
        Assert.False(stack.IsEmpty);
        Assert.Equal(expectedId, items.Definition(stack.Item).Id);
        Assert.Equal(count, stack.Count);
        Assert.Equal(damage, stack.Damage);
    }

    private static string FixtureText(string fileName) =>
        File.ReadAllText(Path.Combine(FixtureDirectory(), fileName)).Trim();

    private static string FixtureDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesseris.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "tests", "Fixtures", "Saves");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Tesseris.SaveCompatibilityFixtureTests",
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
