using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Xunit;

namespace Tesseris.Tests;

public sealed class ChestTests
{
    private static ItemRegistry Items()
    {
        BlockRegistry blocks = BlockRegistry.LoadFromDirectory(
            Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));
        return ItemRegistry.Create(blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));
    }

    [Fact]
    public void Chest_has_27_slots_and_is_stable_per_position()
    {
        var chests = new Chests(Items());
        Chest chest = chests.At(new Vector3i(3, 4, 5));

        Assert.Equal(27, chest.Slots.Length);
        Assert.Same(chest, chests.At(new Vector3i(3, 4, 5)));
        Assert.Equal(1, chests.Count);
    }

    [Fact]
    public void Double_chest_exposes_54_slots_but_keeps_halves_independent()
    {
        var left = new Chest();
        var right = new Chest();
        var container = new ChestContainer(left, right);
        ItemRegistry items = Items();
        ItemStack coal = new(items.IndexOf("tesseris:coal"), 12, 0);

        container[Chest.SlotCount] = coal;

        Assert.True(container.IsDouble);
        Assert.Equal(54, container.SlotCount);
        Assert.True(left.IsEmpty);
        Assert.Equal(coal, right.Slots[0]);
    }

    [Fact]
    public void Container_drops_spawn_above_the_block_with_distinct_scatter()
    {
        ItemRegistry items = Items();
        var drops = new ItemEntities(items);
        var stack = new ItemStack(items.IndexOf("tesseris:coal"), 1, 0);
        Vector3i block = new(4, 12, -3);

        drops.SpawnFromContainer(stack, block, 101u);
        drops.SpawnFromContainer(stack, block, 202u);

        Assert.Equal(2, drops.Count);
        Assert.All(drops.All, drop => Assert.True(drop.Position.Y >= block.Y + 0.7f));
        Assert.NotEqual(drops.All[0].Position, drops.All[1].Position);
        Assert.NotEqual(drops.All[0].Velocity, drops.All[1].Velocity);
    }

    [Theory]
    [InlineData(PieceMask.DoorSouth, ChestPairSide.Single)]
    [InlineData(PieceMask.DoorEast, ChestPairSide.Left)]
    [InlineData(PieceMask.DoorNorth, ChestPairSide.Right)]
    [InlineData(PieceMask.DoorWest, ChestPairSide.Single)]
    public void Chest_state_preserves_rotation_pair_and_animation(
        int facing, ChestPairSide pair)
    {
        byte closed = PieceMask.ChestState(facing, pair, open: false);
        byte animated = PieceMask.ChestAnimatingState(closed, open: true);

        Assert.Equal(facing, PieceMask.ChestFacing(animated));
        Assert.Equal(pair, PieceMask.ChestPair(animated));
        Assert.True(PieceMask.ChestIsAnimating(animated));
        Assert.True(PieceMask.ChestIsOpen(animated));
        Assert.Equal(
            PieceMask.ChestState(facing, pair, open: true),
            PieceMask.ChestFinalState(animated));
    }

    [Fact]
    public void Removing_chest_returns_all_contents_and_forgets_storage()
    {
        ItemRegistry items = Items();
        var chests = new Chests(items);
        Vector3i position = new(8, 9, 10);
        chests.At(position).Slots[17] = new ItemStack(items.IndexOf("tesseris:coal"), 23, 0);

        ItemStack[] contents = [.. chests.Remove(position)];

        Assert.Equal(23, contents[17].Count);
        Assert.Equal(0, chests.Count);
    }

    [Fact]
    public void Save_and_load_preserves_item_ids_counts_damage_and_components()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tesseris-chest-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, Chests.FileName);
        try
        {
            ItemRegistry items = Items();
            var saved = new Chests(items);
            Vector3i position = new(-12, 301, 44);
            saved.At(position).Slots[0] = new ItemStack(items.IndexOf("tesseris:coal"), 41, 7);
            saved.Save(path);

            var loaded = new Chests(items);
            Assert.Equal(1, loaded.Load(path));
            ItemStack restored = loaded.At(position).Slots[0];
            Assert.Equal("tesseris:coal", items.Definition(restored.Item).Id);
            Assert.Equal(41, restored.Count);
            Assert.Equal(7, restored.Damage);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Empty_chests_are_not_persisted()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tesseris-chest-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, Chests.FileName);
        try
        {
            var chests = new Chests(Items());
            _ = chests.At(Vector3i.Zero);
            chests.Save(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Loot_is_deterministic_and_never_refills_after_being_taken()
    {
        ItemRegistry items = Items();
        Vector3i position = new(123, 287, -91);
        var first = new Chests(items);
        var second = new Chests(items);

        Chest a = first.AtLoot(position, 4567);
        Chest b = second.AtLoot(position, 4567);
        Assert.Equal(a.Slots, b.Slots);
        Assert.Contains(a.Slots, stack => !stack.IsEmpty);

        Array.Fill(a.Slots, ItemStack.Empty);
        Assert.Same(a, first.AtLoot(position, 4567));
        Assert.All(a.Slots, stack => Assert.True(stack.IsEmpty));
    }

    [Fact]
    public void Empty_loot_chest_remains_claimed_after_save_and_load()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tesseris-loot-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, Chests.FileName);
        try
        {
            ItemRegistry items = Items();
            Vector3i position = new(4, 5, 6);
            var saved = new Chests(items);
            Chest chest = saved.AtLoot(position, 99);
            Array.Fill(chest.Slots, ItemStack.Empty);
            saved.Save(path);

            var loaded = new Chests(items);
            Assert.Equal(1, loaded.Load(path));
            Chest restored = loaded.AtLoot(position, 99);
            Assert.True(restored.LootInitialized);
            Assert.All(restored.Slots, stack => Assert.True(stack.IsEmpty));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
