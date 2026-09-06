using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ItemStackCodecTests
{
    [Fact]
    public void Codec_round_trips_stable_id_damage_and_components()
    {
        ItemRegistry items = Items();
        int pickaxe = items.IndexOf("tesseris:iron_pickaxe");
        ItemComponentMap components = ItemComponentMap.Empty
            .Set(
                new ResourceId("test:energy"),
                new ModSerializedValue(new ResourceId("test:int32"), 3, new byte[] { 120, 0, 0, 0 }));
        var expected = new ItemStack(pickaxe, 1, 37, components);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            ItemStackCodec.Write(writer, expected, items);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);

        Assert.Equal(expected, ItemStackCodec.Read(reader, items));
    }

    [Fact]
    public void Inventory_round_trip_preserves_cursor_slots_selection_and_components()
    {
        ItemRegistry items = Items();
        int stone = items.IndexOf("tesseris:stone");
        ItemComponentMap components = ItemComponentMap.Empty.Set(
            new ResourceId("test:quality"),
            new ModSerializedValue(new ResourceId("test:byte"), 1, new byte[] { 5 }));
        var inventory = new Inventory(items) { Held = new ItemStack(stone, 3, 0, components) };
        inventory[4] = new ItemStack(stone, 17, 0, components);
        inventory.Select(4);

        Inventory restored = InventorySerializer.Decode(InventorySerializer.Encode(inventory, items), items);

        Assert.Equal(4, restored.Selected);
        Assert.Equal(inventory.Held, restored.Held);
        Assert.Equal(inventory[4], restored[4]);
    }

    [Fact]
    public void Inventory_does_not_merge_stacks_with_different_components()
    {
        ItemRegistry items = Items();
        int stone = items.IndexOf("tesseris:stone");
        ItemComponentMap first = ItemComponentMap.Empty.Set(
            new ResourceId("test:variant"),
            new ModSerializedValue(new ResourceId("test:byte"), 1, new byte[] { 1 }));
        ItemComponentMap second = ItemComponentMap.Empty.Set(
            new ResourceId("test:variant"),
            new ModSerializedValue(new ResourceId("test:byte"), 1, new byte[] { 2 }));
        var inventory = new Inventory(items);
        inventory[0] = new ItemStack(stone, 10, 0, first);

        Assert.True(inventory.Add(new ItemStack(stone, 5, 0, second)).IsEmpty);
        Assert.Equal(10, inventory[0].Count);
        Assert.Equal(5, inventory[1].Count);
    }

    [Fact]
    public void Inventory_file_save_is_explicit_and_preserves_mod_components()
    {
        ItemRegistry items = Items();
        int stone = items.IndexOf("tesseris:stone");
        var expected = new ItemStack(
            stone,
            2,
            0,
            ItemComponentMap.Empty.Set(
                new ResourceId("test:owner_data"),
                new ModSerializedValue(new ResourceId("test:bytes"), 7, new byte[] { 8, 9 })));
        string directory = Path.Combine(Path.GetTempPath(), "Tesseris.InventoryCodec", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "inventory.dat");
        try
        {
            var inventory = new Inventory(items);
            inventory[0] = expected;

            InventorySerializer.Save(path, inventory, items);

            Assert.True(InventorySerializer.TryLoad(path, items, out Inventory? restored));
            Assert.NotNull(restored);
            Assert.Equal(expected, restored![0]);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ItemRegistry Items()
    {
        BlockRegistry blocks = BlockRegistry.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));
        return ItemRegistry.Create(blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));
    }
}
