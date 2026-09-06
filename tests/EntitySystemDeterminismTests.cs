using Tesseris.Game.Entities;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class EntitySystemDeterminismTests
{
    [Fact]
    public void Systems_run_by_phase_priority_and_id_and_commit_after_each_phase()
    {
        var observations = new List<string>();
        var systems = new[]
        {
            Entry("test:z_create", ModSystemPhase.PreSimulation, 0, context =>
            {
                observations.Add("z:" + context.Entities.WithAll([]).Count);
                context.Commands.Create(new ResourceId("test:thing"), new ResourceId("test:create"));
            }),
            Entry("test:a_observe", ModSystemPhase.PreSimulation, 0, context =>
                observations.Add("a:" + context.Entities.WithAll([]).Count)),
            Entry("test:simulation", ModSystemPhase.Simulation, 0, context =>
                observations.Add("simulation:" + context.Entities.WithAll([]).Count)),
        };
        EntityRegistry registry = EntityTest.RegistryWithValue(systems);
        var world = new EntityWorld(registry, 1);

        world.RunSystems(1, TimeSpan.FromMilliseconds(16));

        Assert.Equal(["a:0", "z:0", "simulation:1"], observations);
        Assert.Equal(1, world.Count);
    }

    [Fact]
    public void Deferred_conflicts_resolve_in_system_order_then_issue_order()
    {
        ModEntityId entity = default;
        var systems = new[]
        {
            Entry("test:b", ModSystemPhase.Simulation, 0, context =>
                context.Commands.SetComponent(entity, 0, EntityTest.Value("test:value", 2))),
            Entry("test:a", ModSystemPhase.Simulation, 0, context =>
                context.Commands.SetComponent(entity, 0, EntityTest.Value("test:value", 1))),
        };
        EntityRegistry registry = EntityTest.RegistryWithValue(systems);
        var world = new EntityWorld(registry, 1);
        entity = world.Create(new ResourceId("test:thing"));

        world.RunSystems(1, TimeSpan.Zero);

        Assert.True(world.TryGet(entity, out ModEntitySnapshot? snapshot));
        Assert.Equal(1, EntityTest.Int(snapshot!.Components.Single()));
        Assert.Equal(1U, snapshot.Revision);
    }

    [Fact]
    public void Random_stream_is_repeatable_for_world_tick_phase_and_system()
    {
        ulong[] first = RunRandom(seed: 1234, tick: 99);
        ulong[] second = RunRandom(seed: 1234, tick: 99);
        ulong[] otherTick = RunRandom(seed: 1234, tick: 100);

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherTick);
    }

    [Fact]
    public void Failing_system_aborts_phase_and_attributes_last_accessed_entity()
    {
        ModEntityId entity = default;
        var systems = new[]
        {
            Entry("test:first", ModSystemPhase.Simulation, 0, context =>
                context.Commands.SetComponent(entity, 0, EntityTest.Value("test:value", 7))),
            Entry("test:failing", ModSystemPhase.Simulation, 1, context =>
            {
                Assert.True(context.Entities.TryGet(entity, out _));
                throw new ApplicationException("boom");
            }),
        };
        EntityRegistry registry = EntityTest.RegistryWithValue(systems);
        var world = new EntityWorld(registry, 1);
        entity = world.Create(new ResourceId("test:thing"));

        ModEntitySystemException exception = Assert.Throws<ModEntitySystemException>(() =>
            world.RunSystems(1, TimeSpan.Zero));

        Assert.Equal("test", exception.ModId);
        Assert.Equal(new ResourceId("test:failing"), exception.SystemId);
        Assert.Equal(ModSystemPhase.Simulation, exception.Phase);
        Assert.Equal(entity, exception.EntityId);
        Assert.IsType<ApplicationException>(exception.InnerException);
        Assert.True(world.TryGet(entity, out ModEntitySnapshot? unchanged));
        Assert.Equal(0, EntityTest.Int(unchanged!.Components.Single()));
        Assert.Equal(0U, unchanged.Revision);
    }

    private static (ResourceId Id, ModSystemPhase Phase, int Priority, IModSystem System) Entry(
        string id,
        ModSystemPhase phase,
        int priority,
        Action<IModSystemContext> callback) =>
        (new ResourceId(id), phase, priority, new EntityTest.System(callback));

    private static ulong[] RunRandom(long seed, ulong tick)
    {
        ulong[] values = new ulong[3];
        var systems = new[]
        {
            Entry("test:random", ModSystemPhase.PostSimulation, 0, context =>
            {
                IDeterministicRandom random = Assert.IsAssignableFrom<IEntitySystemRuntimeContext>(context).Random;
                values[0] = random.NextUInt64();
                values[1] = random.NextUInt64();
                values[2] = random.Fork("child").NextUInt64();
            }),
        };
        EntityRegistry registry = EntityTest.RegistryWithValue(systems);
        new EntityWorld(registry, seed).RunSystems(tick, TimeSpan.Zero);
        return values;
    }
}
