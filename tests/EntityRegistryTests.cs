using Tesseris.Game.Entities;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class EntityRegistryTests
{
    [Fact]
    public void Registrations_are_owner_scoped_and_freeze_to_id_order()
    {
        var registry = new EntityRegistry();
        IModEntityPlatform alpha = registry.ForMod("alpha");
        IModEntityPlatform beta = registry.ForMod("beta");
        alpha.RegisterComponent(EntityTest.Component("alpha:z"));
        beta.RegisterComponent(EntityTest.Component("beta:a"));
        alpha.RegisterArchetype(new ModEntityArchetypeDefinition(
            new ResourceId("alpha:composed"),
            [EntityTest.Value("beta:a", 4), EntityTest.Value("alpha:z", 3)]));

        Assert.Throws<InvalidOperationException>(() => alpha.RegisterComponent(EntityTest.Component("beta:not_owned")));

        registry.Freeze();

        Assert.True(registry.IsFrozen);
        Assert.Equal(["alpha:z", "beta:a"], registry.Components.Select(value => value.Descriptor.Id.Value));
        Assert.Equal(
            ["alpha:z", "beta:a"],
            registry.Archetypes.Single().Definition.InitialComponents.Select(value => value.ComponentId.Value));
        Assert.Throws<InvalidOperationException>(() =>
            alpha.RegisterComponent(EntityTest.Component("alpha:late")));
    }

    [Fact]
    public void Freeze_rejects_unknown_duplicate_and_wrong_serializer_components()
    {
        var unknown = new EntityRegistry();
        unknown.ForMod("test").RegisterArchetype(new ModEntityArchetypeDefinition(
            new ResourceId("test:unknown"),
            [EntityTest.Value("test:missing", 1)]));
        Assert.Throws<InvalidOperationException>(unknown.Freeze);

        var duplicate = new EntityRegistry();
        IModEntityPlatform duplicateView = duplicate.ForMod("test");
        duplicateView.RegisterComponent(EntityTest.Component("test:value"));
        duplicateView.RegisterArchetype(new ModEntityArchetypeDefinition(
            new ResourceId("test:duplicate"),
            [EntityTest.Value("test:value", 1), EntityTest.Value("test:value", 2)]));
        Assert.Throws<InvalidOperationException>(duplicate.Freeze);

        var wrongSerializer = new EntityRegistry();
        IModEntityPlatform wrongView = wrongSerializer.ForMod("test");
        wrongView.RegisterComponent(EntityTest.Component("test:value"));
        ModComponentValue value = EntityTest.Value("test:value", 1) with
        {
            Value = new ModSerializedValue(new ResourceId("test:other_serializer"), 1, new byte[] { 1 })
        };
        wrongView.RegisterArchetype(new ModEntityArchetypeDefinition(new ResourceId("test:wrong"), [value]));
        Assert.Throws<InvalidOperationException>(wrongSerializer.Freeze);
    }

    [Fact]
    public void Registry_owns_archetype_payload_snapshots()
    {
        byte[] payload = [1, 2, 3];
        var registry = new EntityRegistry();
        IModEntityPlatform view = registry.ForMod("test");
        view.RegisterComponent(EntityTest.Component("test:value"));
        view.RegisterArchetype(new ModEntityArchetypeDefinition(
            new ResourceId("test:thing"),
            [new ModComponentValue(
                new ResourceId("test:value"),
                new ModSerializedValue(EntityTest.Serializer, 2, payload))]));
        payload[0] = 99;
        registry.Freeze();

        Assert.Equal(
            new byte[] { 1, 2, 3 },
            registry.Archetypes.Single().Definition.InitialComponents.Single().Value.Payload.ToArray());
    }

    [Fact]
    public void Registration_from_another_thread_is_rejected()
    {
        var registry = new EntityRegistry();
        IModEntityPlatform view = registry.ForMod("test");
        Exception? actual = null;
        var thread = new Thread(() =>
            actual = Record.Exception(() => view.RegisterComponent(EntityTest.Component("test:value"))));
        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(actual);
    }
}

internal static class EntityTest
{
    public static readonly ResourceId Serializer = new("test:bytes");

    public static ModComponentDescriptor Component(string id, ResourceId? serializer = null) =>
        new(new ResourceId(id), serializer ?? Serializer);

    public static ModComponentValue Value(string id, int value, int schemaVersion = 1) => new(
        new ResourceId(id),
        new ModSerializedValue(Serializer, schemaVersion, BitConverter.GetBytes(value)));

    public static int Int(ModComponentValue value) => BitConverter.ToInt32(value.Value.Payload.Span);

    public static EntityRegistry RegistryWithValue(
        IEnumerable<(ResourceId Id, ModSystemPhase Phase, int Priority, IModSystem System)>? systems = null)
    {
        var registry = new EntityRegistry();
        IModEntityPlatform view = registry.ForMod("test");
        view.RegisterComponent(Component("test:value"));
        view.RegisterArchetype(new ModEntityArchetypeDefinition(
            new ResourceId("test:thing"),
            [Value("test:value", 0)]));
        if (systems is not null)
        {
            foreach ((ResourceId id, ModSystemPhase phase, int priority, IModSystem system) in systems)
            {
                view.RegisterSystem(id, phase, priority, system);
            }
        }

        registry.Freeze();
        return registry;
    }

    public sealed class System(Action<IModSystemContext> callback) : IModSystem
    {
        public void Execute(IModSystemContext context) => callback(context);
    }
}
