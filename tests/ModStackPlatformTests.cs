using Tesseris.Game.Items;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModStackPlatformTests
{
    [Fact]
    public void Registrations_are_owner_scoped_ordered_frozen_and_main_thread_only()
    {
        var platform = new ModStackPlatform();
        IModStackPlatform alpha = platform.ForMod("alpha");
        IModStackPlatform beta = platform.ForMod("beta");
        alpha.RegisterComponent(Component("alpha:z"));
        beta.RegisterComponent(Component("beta:a"));
        Assert.Throws<InvalidOperationException>(() => alpha.RegisterComponent(Component("beta:no")));

        platform.Freeze();

        Assert.Equal(["alpha:z", "beta:a"], platform.Components.Select(value => value.Descriptor.Id.Value));
        Assert.Throws<InvalidOperationException>(() => alpha.RegisterComponent(Component("alpha:late")));
        Exception? crossThread = null;
        var thread = new Thread(() => crossThread = Record.Exception(() => alpha.TryGet(default, out _)));
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(crossThread);
    }

    [Fact]
    public void Command_buffers_are_atomic_revisioned_owner_scoped_and_defensive()
    {
        byte[] initialPayload = [1, 2, 3];
        var platform = new ModStackPlatform();
        IModStackPlatform view = platform.ForMod("test");
        view.RegisterComponent(Component("test:data"));
        platform.Freeze();
        ModStackId first = platform.Create(Item, 4, [Value("test:data", initialPayload)]);
        ModStackId second = platform.Create(Item, 2);
        initialPayload[0] = 99;

        IModStackCommandBuffer commands = view.CreateCommandBuffer();
        commands.SetCount(first, 0, 7);
        commands.SetComponent(first, 0, Value("test:data", [8, 9]));
        Assert.True(platform.Commit(commands));
        Assert.True(platform.TryGet(first, out ModItemStackSnapshot? changed));
        Assert.Equal(1U, changed!.Revision);
        Assert.Equal(7, changed.Count);
        Assert.Equal(new byte[] { 8, 9 }, changed.Components.Single().Value.Payload.ToArray());

        byte[] leaked = changed.Components.Single().Value.Payload.ToArray();
        leaked[0] = 44;
        Assert.Equal(8, view.TryGet(first, out ModItemStackSnapshot? again)
            ? again!.Components.Single().Value.Payload.Span[0]
            : -1);

        IModStackCommandBuffer stale = view.CreateCommandBuffer();
        stale.SetCount(first, 0, 10);
        stale.SetCount(second, 0, 10);
        Assert.False(platform.Commit(stale));
        Assert.Equal(2, Get(platform, second).Count);
        Assert.Throws<InvalidOperationException>(() => platform.Commit(stale));

        IModStackCommandBuffer wrongOwner = platform.ForMod("other").CreateCommandBuffer();
        Assert.Throws<InvalidOperationException>(() =>
            wrongOwner.SetComponent(first, 1, Value("test:data", [1])));
    }

    [Fact]
    public void Split_copy_and_merge_follow_component_descriptors()
    {
        var platform = new ModStackPlatform(idHigh: 7);
        IModStackPlatform view = platform.ForMod("test");
        view.RegisterComponent(Component("test:copied", copyWhenSplit: true, requireEqual: true));
        view.RegisterComponent(Component("test:anchored", copyWhenSplit: false, requireEqual: false));
        platform.Freeze();
        ModStackId source = platform.Create(Item, 10,
            [Value("test:copied", [1]), Value("test:anchored", [5])]);

        Assert.True(platform.TrySplit(source, 0, 3, out ModStackId split));
        Assert.Equal((7UL, 2UL), (split.High, split.Low));
        Assert.Equal(["test:copied"], Get(platform, split).Components.Select(value => value.ComponentId.Value));
        Assert.True(platform.TryCopy(split, 0, out ModStackId copy));
        Assert.Equal(new byte[] { 1 }, Get(platform, copy).Components.Single().Value.Payload.ToArray());

        ModStackId incompatible = platform.Create(Item, 2, [Value("test:copied", [9])]);
        Assert.False(platform.TryMerge(split, 0, incompatible, 0, 64, out _));
        ModStackId compatible = platform.Create(Item, 5, [Value("test:copied", [1])]);
        Assert.True(platform.TryMerge(split, 0, compatible, 0, 6, out int moved));
        Assert.Equal(3, moved);
        Assert.Equal(6, Get(platform, split).Count);
        Assert.Equal(2, Get(platform, compatible).Count);
    }

    [Fact]
    public void Save_is_deterministic_and_load_preserves_unknown_opaque_components_and_ids()
    {
        var source = new ModStackPlatform(idHigh: 55);
        IModStackPlatform view = source.ForMod("absent");
        view.RegisterComponent(new ModStackComponentDescriptor(
            new ResourceId("absent:future"), new ResourceId("absent:serializer")));
        source.Freeze();
        ModStackId id = source.Create(new ResourceId("absent:item"), 3,
            [new ModStackComponentValue(
                new ResourceId("absent:future"),
                new ModSerializedValue(new ResourceId("absent:serializer"), 99, new byte[] { 4, 3, 2, 1 }))]);
        byte[] first = source.SaveToBytes();
        Assert.Equal(first, source.SaveToBytes());

        var loaded = new ModStackPlatform();
        loaded.Freeze();
        loaded.Load(new MemoryStream(first));

        ModItemStackSnapshot snapshot = Get(loaded, id);
        Assert.Equal("absent:future", snapshot.Components.Single().ComponentId.Value);
        Assert.Equal(99, snapshot.Components.Single().Value.SchemaVersion);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, snapshot.Components.Single().Value.Payload.ToArray());
        Assert.Equal(first, loaded.SaveToBytes());
        Assert.Equal(new ModStackId(55, 2), loaded.Create(new ResourceId("tesseris:next"), 1));
    }

    private static readonly ResourceId Item = new("test:item");
    private static readonly ResourceId Serializer = new("test:bytes");

    private static ModStackComponentDescriptor Component(
        string id,
        bool copyWhenSplit = true,
        bool requireEqual = true) =>
        new(new ResourceId(id), Serializer, copyWhenSplit, requireEqual);

    private static ModStackComponentValue Value(string id, byte[] payload) =>
        new(new ResourceId(id), new ModSerializedValue(Serializer, 1, payload));

    private static ModItemStackSnapshot Get(ModStackPlatform platform, ModStackId id)
    {
        Assert.True(platform.TryGet(id, out ModItemStackSnapshot? stack));
        return stack!;
    }
}
