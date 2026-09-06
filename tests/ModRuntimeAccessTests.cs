using System.Text.Json;
using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.Modding;
using Tesseris.Game.Player;
using Tesseris.Game.World;
using Tesseris.ModApi;
using ModResourceId = Tesseris.ModApi.ResourceId;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModRuntimeAccessTests
{
    [Fact]
    public void World_access_uses_stable_ids_and_never_creates_unloaded_chunks()
    {
        RuntimeFixture fixture = CreateFixture();
        var loaded = new Chunk();
        Assert.True(fixture.World.TryAddChunk(Vector3i.Zero, loaded));
        fixture.Access.AttachWorld(fixture.World);

        Assert.Equal(new ModResourceId("tesseris:air"), fixture.Access.World!.GetBlockId(2, 3, 4));
        Assert.True(fixture.Access.World.TrySetBlockId(2, 3, 4, new ModResourceId("test:stone")));
        Assert.Equal(new ModResourceId("test:stone"), fixture.Access.World.GetBlockId(2, 3, 4));
        Assert.Equal(fixture.Blocks.IndexOf("test:stone"), fixture.World.GetBlock(2, 3, 4));
        Assert.True(fixture.World.GetChunk(Vector3i.Zero)!.IsModified);

        int before = fixture.World.ChunkCount;
        Assert.False(fixture.Access.World.TrySetBlockId(1000, 3, 1000, new ModResourceId("test:stone")));
        Assert.False(fixture.Access.World.TrySetBlockId(2, 3, 4, new ModResourceId("test:missing")));
        Assert.False(fixture.Access.World.TrySetBlockId(2, TerrainGenerator.WorldHeight, 4, new ModResourceId("test:stone")));
        Assert.Equal(before, fixture.World.ChunkCount);
    }

    [Fact]
    public void World_facade_changes_dynamically_when_a_save_is_detached()
    {
        RuntimeFixture fixture = CreateFixture();
        fixture.Access.AttachWorld(fixture.World);
        IModGame handedToMod = fixture.Access;

        Assert.True(handedToMod.IsWorldLoaded);
        Assert.NotNull(handedToMod.World);

        fixture.Access.DetachWorld();

        Assert.False(handedToMod.IsWorldLoaded);
        Assert.Null(handedToMod.World);
    }

    [Fact]
    public void Player_and_inventory_operations_use_public_values_and_stable_item_ids()
    {
        RuntimeFixture fixture = CreateFixture();
        IModPlayer player = fixture.Access.Player;
        IModInventory inventory = fixture.Access.Inventory;
        var stone = new ModResourceId("test:stone");

        Assert.True(player.Teleport(new ModVector3(12.5f, 50f, -7.25f)));
        Assert.Equal(new ModVector3(12.5f, 50f, -7.25f), player.Position);
        Assert.Equal(new Vector3(12.5f, 50f, -7.25f), fixture.Player.Position);

        fixture.Vitals.Hurt(3f, ignoreArmour: true, armour: null);
        Assert.Equal(17f, player.Health);
        Assert.False(player.IsDead);

        Assert.Equal(0, inventory.Add(stone, 70));
        Assert.Equal(70, inventory.CountOf(stone));
        Assert.True(inventory.Remove(stone, 65));
        Assert.Equal(5, inventory.CountOf(stone));
        Assert.False(inventory.Remove(stone, 6));
        Assert.Equal(5, inventory.CountOf(stone));
        Assert.Equal(8, inventory.Add(new ModResourceId("test:missing"), 8));
    }

    [Fact]
    public void Inventory_slots_expose_and_atomically_replace_arbitrary_stack_components()
    {
        RuntimeFixture fixture = CreateFixture();
        IModInventory inventory = fixture.Access.Inventory;
        var stone = new ModResourceId("test:stone");
        Assert.Equal(0, inventory.Add(stone, 1));
        Assert.True(inventory.TryGetSlot(0, out ModInventoryStackSnapshot? original));
        Assert.NotNull(original);

        var component = new ModStackComponentValue(
            new ModResourceId("test:charge"),
            new ModSerializedValue(new ModResourceId("test:int32"), 1, BitConverter.GetBytes(73)));
        ModInventoryStackSnapshot replacement = original! with { Components = [component] };

        Assert.True(inventory.TryReplaceSlot(0, original, replacement));
        Assert.False(inventory.TryReplaceSlot(0, original, replacement));
        Assert.True(inventory.TryGetSlot(0, out ModInventoryStackSnapshot? updated));
        ModStackComponentValue actual = Assert.Single(updated!.Components);
        Assert.Equal(component.ComponentId, actual.ComponentId);
        Assert.Equal(73, BitConverter.ToInt32(actual.Value.Payload.Span));
    }

    [Fact]
    public void Mutations_from_worker_threads_are_rejected()
    {
        RuntimeFixture fixture = CreateFixture();
        fixture.World.TryAddChunk(Vector3i.Zero, new Chunk());
        fixture.Access.AttachWorld(fixture.World);

        Exception? actual = null;
        var worker = new Thread(() =>
        {
            actual = Record.Exception(() => fixture.Access.World!.TrySetBlockId(
                0, 0, 0, new ModResourceId("test:stone")));
        });
        worker.Start();
        worker.Join();

        InvalidOperationException exception = Assert.IsType<InvalidOperationException>(actual);

        Assert.Contains("main game thread", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mod_host_injects_the_exact_dynamic_game_facade()
    {
        using var temporary = new TemporaryDirectory();
        string directory = Path.Combine(temporary.Path, "runtime-test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "tesseris.mod.json"),
            JsonSerializer.Serialize(new
            {
                id = "runtime-test",
                name = "Runtime test",
                version = "1.0.0",
                apiVersion = "^1.0.0",
                capabilities = new[] { "runtime-test" },
            }));

        RuntimeFixture fixture = CreateFixture();
        var mod = new CapturingMod();
        using ModHost host = ModHost.DiscoverAndLoad(
            temporary.Path,
            new ModHostOptions
            {
                Game = fixture.Access,
                Loaders = [new TestLoader(mod)],
            });

        host.Initialize();

        Assert.Same(fixture.Access, mod.Game);
        Assert.False(mod.Game!.IsWorldLoaded);
        fixture.Access.AttachWorld(fixture.World);
        Assert.True(mod.Game.IsWorldLoaded);
    }

    [Fact]
    public void Mod_host_default_game_access_is_safe_and_unavailable()
    {
        using var temporary = new TemporaryDirectory();
        string directory = Path.Combine(temporary.Path, "runtime-test");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "tesseris.mod.json"),
            JsonSerializer.Serialize(new
            {
                id = "runtime-test",
                name = "Runtime test",
                version = "1.0.0",
                apiVersion = "^1.0.0",
                capabilities = new[] { "runtime-test" },
            }));
        var mod = new CapturingMod();

        using ModHost host = ModHost.DiscoverAndLoad(
            temporary.Path,
            new ModHostOptions { Loaders = [new TestLoader(mod)] });
        host.Initialize();

        Assert.NotNull(mod.Game);
        Assert.False(mod.Game!.IsWorldLoaded);
        Assert.Null(mod.Game.World);
        Assert.True(mod.Game.Player.IsDead);
        Assert.Equal(4, mod.Game.Inventory.Add(new ModResourceId("test:stone"), 4));
    }

    private static RuntimeFixture CreateFixture()
    {
        BlockRegistry blocks = BlockRegistry.Create(
        [
            new BlockDefinition
            {
                Id = "test:stone",
                Texture = "stone",
                Opaque = true,
                Solid = true,
            },
        ]);
        ItemRegistry items = ItemRegistry.Create(blocks, itemDirectory: null);
        var inventory = new Inventory(items);
        var player = new PlayerController(new Vector3(0.5f, 20f, 0.5f));
        var vitals = new Vitals();
        var world = new VoxelWorld(blocks);
        var access = new GameModAccess();
        access.AttachStaticContent(blocks, items, inventory, player, vitals);
        return new RuntimeFixture(blocks, inventory, player, vitals, world, access);
    }

    private sealed record RuntimeFixture(
        BlockRegistry Blocks,
        Inventory Inventory,
        PlayerController Player,
        Vitals Vitals,
        VoxelWorld World,
        GameModAccess Access);

    private sealed class CapturingMod : IMod
    {
        public IModGame? Game { get; private set; }

        public void Configure(IModContext context) => Game = context.Game;
    }

    private sealed class TestLoader : IModLoader
    {
        private readonly IMod mod;

        public TestLoader(IMod mod) => this.mod = mod;

        public string Id => "tests:runtime";

        public bool CanLoad(ModDescriptor descriptor) =>
            descriptor.Capabilities.Contains("runtime-test", StringComparer.Ordinal);

        public IModLoadResult Load(ModDescriptor descriptor, IModLoadContext context) => new Result(mod);
    }

    private sealed class Result : IModLoadResult
    {
        public Result(IMod instance) => Instance = instance;

        public IMod? Instance { get; }

        public IReadOnlyList<ModContentSource> ContentSources => [];

        public void Dispose() { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Tesseris.ModRuntimeAccessTests",
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
