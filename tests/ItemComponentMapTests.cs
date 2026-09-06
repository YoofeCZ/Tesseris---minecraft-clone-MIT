using Tesseris.Game.Items;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ItemComponentMapTests
{
    [Fact]
    public void Values_are_defensively_copied_sorted_and_structurally_equal()
    {
        byte[] source = [1, 2, 3];
        ItemComponentMap first = ItemComponentMap.Empty
            .Set(new ResourceId("test:z"), Value(source))
            .Set(new ResourceId("test:a"), Value([9]));
        source[0] = 77;

        Assert.Equal(["test:a", "test:z"], first.Keys.Select(id => id.Value));
        Assert.True(first.TryGet(new ResourceId("test:z"), out ModSerializedValue stored));
        Assert.Equal(new byte[] { 1, 2, 3 }, stored.Payload.ToArray());

        ItemComponentMap second = ItemComponentMap.Empty
            .Set(new ResourceId("test:a"), Value([9]))
            .Set(new ResourceId("test:z"), Value([1, 2, 3]));
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Stack_matching_requires_equal_component_state()
    {
        ItemComponentMap red = ItemComponentMap.Empty.Set(new ResourceId("test:color"), Value([1]));
        ItemComponentMap blue = ItemComponentMap.Empty.Set(new ResourceId("test:color"), Value([2]));

        var first = new ItemStack(7, 2, 0, red);
        var same = new ItemStack(7, 60, 0, red);
        var different = new ItemStack(7, 2, 0, blue);

        Assert.True(first.Matches(same));
        Assert.False(first.Matches(different));
        Assert.Equal(red, first.WithCount(1).Components);
    }

    [Fact]
    public void Removing_last_value_returns_canonical_empty_map()
    {
        ItemComponentMap map = ItemComponentMap.Empty.Set(new ResourceId("test:value"), Value([4]));
        Assert.Same(ItemComponentMap.Empty, map.Remove(new ResourceId("test:value")));
    }

    private static ModSerializedValue Value(byte[] payload) =>
        new(new ResourceId("test:bytes"), 1, payload);
}
