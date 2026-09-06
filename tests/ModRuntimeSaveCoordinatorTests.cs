using System.Text;
using Tesseris.Game.Entities;
using Tesseris.Game.Items;
using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModRuntimeSaveCoordinatorTests
{
    [Fact]
    public void Roundtrip_attaches_all_runtime_objects_together_and_preserves_unknown_bytes()
    {
        using var temporary = new RuntimeTemporaryDirectory();
        RuntimeFixture fixture = CreateFixture();
        var coordinator = new ModRuntimeSaveCoordinator(temporary.Path);
        bool quiesced = false;

        string generation = coordinator.Save(
            fixture.Entities, fixture.Stacks, fixture.Containers, 987,
            () => quiesced = true);
        ModRuntimeLoadResult loaded = coordinator.Load(EmptyEntityRegistry(), EmptyStacks, EmptyContainers);

        Assert.True(quiesced);
        Assert.Equal("gen-0000000000000001", generation);
        Assert.Equal(ModRuntimeLoadStatus.Loaded, loaded.Status);
        Assert.Equal(987UL, loaded.SimulationTick);
        Assert.True(loaded.HasRuntimeState);
        Assert.True(loaded.Entities!.TryGet(fixture.EntityId, out ModEntitySnapshot? entity));
        Assert.Equal(new byte[] { 9, 8, 7 }, entity!.Components.Single().Value.Payload.ToArray());
        Assert.True(loaded.Stacks!.TryGet(fixture.StackId, out ModItemStackSnapshot? stack));
        Assert.Equal(new byte[] { 6, 5, 4 }, stack!.Components.Single().Value.Payload.ToArray());
        Assert.True(loaded.Containers!.TryGetContainer(fixture.ContainerId, out ModContainerSnapshot? container));
        Assert.Equal(new byte[] { 3, 2, 1 }, container!.Components.Single().Value.Payload.ToArray());
        Assert.Equal(new byte[] { 6, 5, 4 },
            container.Slots.Single().Stack!.Components.Single().Value.Payload.ToArray());
    }

    [Fact]
    public void Current_corruption_rolls_back_but_single_generation_corruption_fails_before_factories()
    {
        using var temporary = new RuntimeTemporaryDirectory();
        RuntimeFixture fixture = CreateFixture();
        var coordinator = new ModRuntimeSaveCoordinator(temporary.Path);
        coordinator.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 10);
        fixture.Stacks.Create(new ResourceId("absent:second"), 1);
        coordinator.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 20);
        string currentStacks = Path.Combine(
            coordinator.SaveRoot, "generations", "gen-0000000000000002", "stacks.bin");
        Corrupt(currentStacks);

        ModRuntimeLoadResult recovered = coordinator.Load(EmptyEntityRegistry(), EmptyStacks, EmptyContainers);

        Assert.Equal(ModRuntimeLoadStatus.RecoveredPreviousGeneration, recovered.Status);
        Assert.Equal(10UL, recovered.SimulationTick);
        Assert.Equal("gen-0000000000000001", recovered.Generation);

        string previousStacks = Path.Combine(
            coordinator.SaveRoot, "generations", "gen-0000000000000001", "stacks.bin");
        Corrupt(previousStacks);
        int stackFactories = 0;
        int containerFactories = 0;
        Assert.Throws<InvalidDataException>(() => coordinator.Load(
            EmptyEntityRegistry(),
            () => { stackFactories++; return EmptyStacks(); },
            () => { containerFactories++; return EmptyContainers(); }));
        Assert.Equal(0, stackFactories);
        Assert.Equal(0, containerFactories);
    }

    [Fact]
    public void Crash_before_pointer_commit_keeps_previous_generation_and_initial_crash_is_legacy()
    {
        using var temporary = new RuntimeTemporaryDirectory();
        RuntimeFixture fixture = CreateFixture();
        var stable = new ModRuntimeSaveCoordinator(temporary.Path);
        stable.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 1);
        var crashing = new ModRuntimeSaveCoordinator(
            temporary.Path,
            phaseHook: phase =>
            {
                if (phase == ModRuntimeSavePhase.GenerationPublished) throw new SimulatedCrashException();
            });
        Assert.Throws<SimulatedCrashException>(() =>
            crashing.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 2));

        ModRuntimeLoadResult loaded = stable.Load(EmptyEntityRegistry(), EmptyStacks, EmptyContainers);
        Assert.Equal(ModRuntimeLoadStatus.Loaded, loaded.Status);
        Assert.Equal(1UL, loaded.SimulationTick);
        Assert.Equal("gen-0000000000000001", loaded.Generation);

        Assert.Equal(
            "gen-0000000000000003",
            stable.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 3));
        Assert.False(Directory.Exists(Path.Combine(
            stable.SaveRoot, "generations", "gen-0000000000000002")));
        Corrupt(Path.Combine(
            stable.SaveRoot, "generations", "gen-0000000000000003", "containers.bin"));
        ModRuntimeLoadResult afterLaterCommit = stable.Load(
            EmptyEntityRegistry(), EmptyStacks, EmptyContainers);
        Assert.Equal(ModRuntimeLoadStatus.RecoveredPreviousGeneration, afterLaterCommit.Status);
        Assert.Equal(1UL, afterLaterCommit.SimulationTick);

        using var firstCrashDirectory = new RuntimeTemporaryDirectory();
        var firstCrash = new ModRuntimeSaveCoordinator(
            firstCrashDirectory.Path,
            phaseHook: phase =>
            {
                if (phase == ModRuntimeSavePhase.GenerationPublished) throw new SimulatedCrashException();
            });
        Assert.Throws<SimulatedCrashException>(() =>
            firstCrash.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 3));
        ModRuntimeLoadResult legacy = firstCrash.Load(EmptyEntityRegistry(), EmptyStacks, EmptyContainers);
        Assert.Equal(ModRuntimeLoadStatus.LegacyWorldWithoutRuntimeSave, legacy.Status);
        Assert.False(legacy.HasRuntimeState);
    }

    [Fact]
    public void Manifest_is_byte_deterministic_for_equal_generation_and_state()
    {
        using var firstDirectory = new RuntimeTemporaryDirectory();
        using var secondDirectory = new RuntimeTemporaryDirectory();
        RuntimeFixture fixture = CreateFixture();
        var first = new ModRuntimeSaveCoordinator(firstDirectory.Path);
        var second = new ModRuntimeSaveCoordinator(secondDirectory.Path);

        first.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 42);
        second.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 42);

        byte[] firstManifest = File.ReadAllBytes(Path.Combine(
            first.SaveRoot, "generations", "gen-0000000000000001", "manifest.json"));
        byte[] secondManifest = File.ReadAllBytes(Path.Combine(
            second.SaveRoot, "generations", "gen-0000000000000001", "manifest.json"));
        Assert.Equal(firstManifest, secondManifest);
        string json = Encoding.UTF8.GetString(firstManifest);
        Assert.True(json.IndexOf("containers.bin", StringComparison.Ordinal)
                    < json.IndexOf("entities.bin", StringComparison.Ordinal));
        Assert.True(json.IndexOf("entities.bin", StringComparison.Ordinal)
                    < json.IndexOf("stacks.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void Traversal_and_symlink_orphans_never_escape_the_owned_save_directory()
    {
        using var temporary = new RuntimeTemporaryDirectory();
        using var outside = new RuntimeTemporaryDirectory();
        RuntimeFixture fixture = CreateFixture();
        var coordinator = new ModRuntimeSaveCoordinator(temporary.Path);
        coordinator.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 1);
        string sentinel = Path.Combine(outside.Path, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        string pointer = Path.Combine(coordinator.SaveRoot, "current.json");
        byte[] committedPointer = File.ReadAllBytes(pointer);
        File.WriteAllText(pointer,
            "{\"formatVersion\":1,\"generation\":\"../outside\",\"manifestSha256\":\""
            + new string('0', 64) + "\"}");
        Assert.Throws<InvalidDataException>(() =>
            coordinator.Load(EmptyEntityRegistry(), EmptyStacks, EmptyContainers));
        Assert.Equal("keep", File.ReadAllText(sentinel));

        // Restore a valid commit, then verify cleanup removes only the link itself when supported.
        File.WriteAllBytes(pointer, committedPointer);
        coordinator.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 2);
        string link = Path.Combine(coordinator.SaveRoot, "generations", "staging-gen-linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }

        coordinator.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 3);
        Assert.False(Directory.Exists(link));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Missing_legacy_state_and_limits_are_explicit_and_do_not_publish_a_pointer()
    {
        using var temporary = new RuntimeTemporaryDirectory();
        var legacyCoordinator = new ModRuntimeSaveCoordinator(temporary.Path);
        ModRuntimeLoadResult legacy = legacyCoordinator.Load(
            EmptyEntityRegistry(),
            () => throw new InvalidOperationException("must not run"),
            () => throw new InvalidOperationException("must not run"));
        Assert.Equal(ModRuntimeLoadStatus.LegacyWorldWithoutRuntimeSave, legacy.Status);
        Assert.Null(legacy.Entities);

        RuntimeFixture fixture = CreateFixture();
        var limited = new ModRuntimeSaveCoordinator(
            temporary.Path,
            new ModRuntimeSaveLimits(
                MaximumFileBytes: 8,
                MaximumTotalBytes: 24,
                MaximumManifestBytes: 1024,
                RetainedGenerations: 2));
        Assert.Throws<InvalidOperationException>(() =>
            limited.Save(fixture.Entities, fixture.Stacks, fixture.Containers, 1));
        Assert.False(File.Exists(Path.Combine(limited.SaveRoot, "current.json")));
    }

    private static RuntimeFixture CreateFixture()
    {
        ResourceId entityComponent = new("absent:entity_data");
        ResourceId entitySerializer = new("absent:entity_serializer");
        ResourceId archetype = new("absent:entity");
        var entityRegistry = new EntityRegistry();
        IModEntityPlatform entityView = entityRegistry.ForMod("absent");
        entityView.RegisterComponent(new ModComponentDescriptor(entityComponent, entitySerializer));
        entityView.RegisterArchetype(new ModEntityArchetypeDefinition(
            archetype,
            [new ModComponentValue(
                entityComponent,
                new ModSerializedValue(entitySerializer, 17, new byte[] { 9, 8, 7 }))]));
        entityRegistry.Freeze();
        var entities = new EntityWorld(entityRegistry, worldSeed: 123, worldGeneration: 4);
        ModEntityId entityId = entities.Create(archetype);

        var stacks = new ModStackPlatform(idHigh: 5);
        IModStackPlatform stackView = stacks.ForMod("absent");
        ResourceId stackComponent = new("absent:stack_data");
        ResourceId stackSerializer = new("absent:stack_serializer");
        stackView.RegisterComponent(new ModStackComponentDescriptor(stackComponent, stackSerializer));
        stacks.Freeze();
        ModStackId stackId = stacks.Create(
            new ResourceId("absent:item"),
            2,
            [new ModStackComponentValue(
                stackComponent,
                new ModSerializedValue(stackSerializer, 18, new byte[] { 6, 5, 4 }))]);
        Assert.True(stacks.TryGet(stackId, out ModItemStackSnapshot? stackSnapshot));

        var containers = new ModContainerPlatform(idHigh: 6);
        IModContainerPlatform containerView = containers.ForMod("absent");
        ResourceId containerType = new("absent:box");
        ResourceId stateSerializer = new("absent:state_serializer");
        containerView.RegisterContainerType(new ModContainerTypeDefinition(containerType, 1, stateSerializer));
        containers.Freeze();
        ModContainerId containerId = containers.Create(
            containerType,
            [new ModContainerSlotSnapshot(0, stackSnapshot, true, true)],
            [new ModComponentValue(
                new ResourceId("absent:state"),
                new ModSerializedValue(stateSerializer, 19, new byte[] { 3, 2, 1 }))]);

        return new RuntimeFixture(entities, stacks, containers, entityId, stackId, containerId);
    }

    private static EntityRegistry EmptyEntityRegistry()
    {
        var registry = new EntityRegistry();
        registry.Freeze();
        return registry;
    }

    private static ModStackPlatform EmptyStacks()
    {
        var stacks = new ModStackPlatform();
        stacks.Freeze();
        return stacks;
    }

    private static ModContainerPlatform EmptyContainers()
    {
        var containers = new ModContainerPlatform();
        containers.Freeze();
        return containers;
    }

    private static void Corrupt(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    private sealed record RuntimeFixture(
        EntityWorld Entities,
        ModStackPlatform Stacks,
        ModContainerPlatform Containers,
        ModEntityId EntityId,
        ModStackId StackId,
        ModContainerId ContainerId);

    private sealed class SimulatedCrashException : Exception;

    private sealed class RuntimeTemporaryDirectory : IDisposable
    {
        public RuntimeTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"tesseris-runtime-save-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
