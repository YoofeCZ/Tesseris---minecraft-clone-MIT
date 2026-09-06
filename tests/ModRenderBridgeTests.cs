using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Tesseris.Game.Content;
using Tesseris.Game.Modding;
using Tesseris.Game.Modding.Client;
using Tesseris.Game.Modding.Client.Rendering;
using Tesseris.ModApi;
using Xunit;
using ModResourceId = Tesseris.ModApi.ResourceId;

namespace Tesseris.Tests;

public sealed class ModRenderBridgeTests
{
    [Fact]
    public void Camera_snapshot_stays_renderer_neutral_and_preserves_viewport_pose()
    {
        var camera = new Camera(new Vector3(4, 5, 6))
        {
            YawDegrees = -90,
            PitchDegrees = 0,
        };

        ModRenderViewSnapshot snapshot = GameModRenderBridge.CreateViewSnapshot(camera, 1920, 1080, 0.25f);

        Assert.Equal(new ModVector3(4, 5, 6), snapshot.CameraPosition);
        Assert.InRange(MathF.Abs(snapshot.CameraRotation.W), 0.9999f, 1f);
        Assert.Equal(1920, snapshot.ViewportWidth);
        Assert.Equal(1080, snapshot.ViewportHeight);
        Assert.Equal(0.25f, snapshot.PartialTick);
    }

    [Fact]
    public void Every_phase_dispatches_in_deterministic_priority_then_id_order()
    {
        var platform = new ModClientPlatform();
        IModRenderRegistry registry = platform.ForMod("test").Rendering;
        foreach (ModRenderPhase phase in Enum.GetValues<ModRenderPhase>())
        {
            registry.Register(new ModResourceId($"test:{phase.ToString().ToLowerInvariant()}_z"), phase, 5,
                new BillboardCallback(phase, 2));
            registry.Register(new ModResourceId($"test:{phase.ToString().ToLowerInvariant()}_a"), phase, 5,
                new BillboardCallback(phase, 1));
            registry.Register(new ModResourceId($"test:{phase.ToString().ToLowerInvariant()}_first"), phase, -1,
                new BillboardCallback(phase, 0));
        }
        platform.Freeze();
        var stream = new ModRenderCommandStream(platform.Rendering);
        stream.BeginFrame();
        var phases = new List<ModRenderPhase>();

        foreach (ModRenderPhase phase in GameModRenderBridge.OrderedPhases)
        {
            IReadOnlyList<RecordedModRenderCommand> commands = stream.RecordPhase(phase, View());
            phases.Add(phase);
            Assert.Equal(
                [0f, 1f, 2f],
                commands.Cast<RecordedModBillboard>().Select(value => value.Position.X));
        }

        Assert.Equal(Enum.GetValues<ModRenderPhase>().Order(), phases);
        Assert.Equal(15, stream.FrameCommandCount);
        stream.EndFrame();
    }

    [Fact]
    public void Commands_validate_values_limits_and_callback_lifetime()
    {
        var retained = new RetainingCallback();
        var platform = new ModClientPlatform();
        IModRenderRegistry registry = platform.ForMod("test").Rendering;
        registry.Register(new ModResourceId("test:retained"), ModRenderPhase.AfterWorld, 0, retained);
        platform.Freeze();
        var stream = new ModRenderCommandStream(platform.Rendering, maximumCommandsPerFrame: 1);
        stream.BeginFrame();

        IReadOnlyList<RecordedModRenderCommand> commands = stream.RecordPhase(ModRenderPhase.AfterWorld, View());

        Assert.Single(commands);
        Assert.Throws<InvalidOperationException>(() => retained.Commands!.DrawLine(
            new ModVector3(0, 0, 0), new ModVector3(1, 0, 0), new ModColor(255, 255, 255), 1));

        var invalidPlatform = new ModClientPlatform();
        invalidPlatform.ForMod("bad").Rendering.Register(
            new ModResourceId("bad:nan"),
            ModRenderPhase.AfterWorld,
            0,
            new InvalidCallback());
        invalidPlatform.Freeze();
        var invalid = new ModRenderCommandStream(invalidPlatform.Rendering);
        invalid.BeginFrame();
        ModRenderCallbackException failure = Assert.Throws<ModRenderCallbackException>(() =>
            invalid.RecordPhase(ModRenderPhase.AfterWorld, View()));
        Assert.Equal("bad", failure.ModId);
        Assert.IsType<ArgumentOutOfRangeException>(failure.InnerException);

        var overflowPlatform = new ModClientPlatform();
        overflowPlatform.ForMod("overflow").Rendering.Register(
            new ModResourceId("overflow:two"),
            ModRenderPhase.AfterWorld,
            0,
            new TwoLineCallback());
        overflowPlatform.Freeze();
        var overflow = new ModRenderCommandStream(overflowPlatform.Rendering, maximumCommandsPerFrame: 1);
        overflow.BeginFrame();
        ModRenderCallbackException overflowFailure = Assert.Throws<ModRenderCallbackException>(() =>
            overflow.RecordPhase(ModRenderPhase.AfterWorld, View()));
        Assert.Contains("limit of 1", overflowFailure.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Particle_update_is_deterministic_moves_fades_and_expires()
    {
        ModParticleDefinition definition = new(
            new ModResourceId("test:spark"),
            new ModResourceId("test:spark_texture"),
            TimeSpan.FromSeconds(1),
            2f,
            new ModColor(100, 150, 200, 200));
        IReadOnlyDictionary<ModResourceId, ModParticleDefinition> definitions =
            new Dictionary<ModResourceId, ModParticleDefinition> { [definition.Id] = definition };
        ModParticleSpawn[] spawns =
        [
            new(definition.Id, new ModVector3(1, 2, 3), new ModVector3(2, -2, 4), 91),
        ];
        var first = new ModParticleSimulation();
        var second = new ModParticleSimulation();

        Assert.Equal(1, first.Update(TimeSpan.Zero, definitions, spawns));
        Assert.Equal(1, second.Update(TimeSpan.Zero, definitions, spawns));
        first.Update(TimeSpan.FromMilliseconds(500), definitions, []);
        second.Update(TimeSpan.FromMilliseconds(500), definitions, []);

        ModParticleRenderSnapshot actual = Assert.Single(first.Snapshot());
        Assert.Equal(second.Snapshot(), first.Snapshot());
        Assert.Equal(new ModVector3(2, 1, 5), actual.Position);
        Assert.Equal((byte)100, actual.Color.A);
        Assert.Equal((ulong)91, actual.DeterministicSeed);

        first.Update(TimeSpan.FromMilliseconds(500), definitions, []);
        Assert.Empty(first.Snapshot());
    }

    [Fact]
    public void Particle_limits_are_bounded_and_issue_order_is_preserved()
    {
        ModParticleDefinition definition = new(
            new ModResourceId("test:spark"), new ModResourceId("test:texture"),
            TimeSpan.FromSeconds(5), 1, new ModColor(255, 255, 255));
        var definitions = new Dictionary<ModResourceId, ModParticleDefinition> { [definition.Id] = definition };
        ModParticleSpawn[] spawns = Enumerable.Range(0, 5)
            .Select(value => new ModParticleSpawn(
                definition.Id, new ModVector3(value, 0, 0), new ModVector3(), (ulong)value))
            .ToArray();
        var simulation = new ModParticleSimulation(maximumActiveParticles: 2, maximumSpawnsPerUpdate: 3);

        Assert.Equal(2, simulation.Update(TimeSpan.Zero, definitions, spawns));
        Assert.Equal([0f, 1f], simulation.Snapshot().Select(value => value.Position.X));
    }

    [Fact]
    public void Namespaced_assets_resolve_and_geometry_draws_real_quads_and_visible_model_placeholder()
    {
        using var temporary = new RenderTemporaryDirectory();
        temporary.Write("textures/spark.png", "png-placeholder");
        temporary.Write("models/machine.json", "{}");
        var catalog = new ContentCatalog([new ContentSource("test", temporary.Path)]);
        var assets = new ModRenderAssetIndex(catalog, new ContentAssetResolver(catalog));
        var warnings = new List<string>();
        var builder = new ModRenderGeometryBuilder(assets, warnings.Add);
        RecordedModRenderCommand[] commands =
        [
            new RecordedModLine(new ModVector3(0, 0, 0), new ModVector3(1, 0, 0), new ModColor(255, 0, 0), 0.1f),
            new RecordedModBillboard(new ModResourceId("test:spark"), new ModVector3(0, 1, 0), 1, 2, new ModColor(255, 255, 255)),
            new RecordedModModel(
                new ModResourceId("test:machine"),
                new ModTransform(new ModVector3(), new ModQuaternion(0, 0, 0, 1), new ModVector3(1, 1, 1)),
                new ModColor(10, 20, 30)),
        ];
        ModParticleRenderSnapshot[] particles =
        [
            new(new ModResourceId("test:spark"), new ModVector3(2, 2, 2), 0.5f, new ModColor(255, 255, 255), 1, 1),
        ];

        ModRenderGeometry geometry = builder.Build(
            commands, particles, new ModVector3(0, 0, 5), Vector3.UnitX, Vector3.UnitY);

        Assert.Equal((1 + 1 + 12 + 1) * 6 * ModRenderGeometry.FloatsPerVertex, geometry.Vertices.Length);
        Assert.Equal(4, geometry.Batches.Count);
        Assert.Equal(0, geometry.DroppedQuads);
        Assert.Contains(warnings, value => value.Contains("visible placeholder", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(temporary.Path, "textures", "spark.png"), assets.ResolveTexture(new ModResourceId("test:spark")));
        Assert.Equal(Path.Combine(temporary.Path, "models", "machine.json"), assets.ResolveModel(new ModResourceId("test:machine")));
    }

    [Fact]
    public void Missing_model_is_not_silently_ignored_and_geometry_limit_is_reported()
    {
        using var temporary = new RenderTemporaryDirectory();
        var catalog = new ContentCatalog([new ContentSource("test", temporary.Path)]);
        var warnings = new List<string>();
        var builder = new ModRenderGeometryBuilder(
            new ModRenderAssetIndex(catalog, new ContentAssetResolver(catalog)),
            warnings.Add,
            maximumQuads: 1);
        RecordedModRenderCommand[] commands =
        [
            new RecordedModModel(
                new ModResourceId("test:missing"),
                new ModTransform(new ModVector3(), new ModQuaternion(0, 0, 0, 1), new ModVector3(1, 1, 1)),
                new ModColor(255, 255, 255)),
        ];

        ModRenderGeometry geometry = builder.Build(
            commands, [], new ModVector3(0, 0, 5), Vector3.UnitX, Vector3.UnitY);

        Assert.Equal(6 * ModRenderGeometry.FloatsPerVertex, geometry.Vertices.Length);
        Assert.Equal(11, geometry.DroppedQuads);
        Assert.Contains(warnings, value => value.Contains("was not found", StringComparison.Ordinal));
    }

    private static ModRenderViewSnapshot View() => new(
        new ModVector3(0, 0, 5), new ModQuaternion(0, 0, 0, 1), 1280, 720, 0.5f);

    private sealed class BillboardCallback(ModRenderPhase phase, float x) : IModRenderCallback
    {
        public void Record(ModRenderViewSnapshot view, IModRenderCommandBuffer commands)
        {
            Assert.Equal(phase, Enum.GetValues<ModRenderPhase>().Single(value =>
                value.ToString().Equals(phase.ToString(), StringComparison.Ordinal)));
            commands.DrawBillboard(
                new ModResourceId("test:texture"), new ModVector3(x, 0, 0), 1, 1, new ModColor(255, 255, 255));
        }
    }

    private sealed class RetainingCallback : IModRenderCallback
    {
        public IModRenderCommandBuffer? Commands { get; private set; }

        public void Record(ModRenderViewSnapshot view, IModRenderCommandBuffer commands)
        {
            Commands = commands;
            commands.DrawLine(new ModVector3(0, 0, 0), new ModVector3(1, 0, 0), new ModColor(255, 255, 255), 1);
        }
    }

    private sealed class InvalidCallback : IModRenderCallback
    {
        public void Record(ModRenderViewSnapshot view, IModRenderCommandBuffer commands) =>
            commands.DrawBillboard(
                new ModResourceId("bad:texture"), new ModVector3(float.NaN, 0, 0), 1, 1, new ModColor());
    }

    private sealed class TwoLineCallback : IModRenderCallback
    {
        public void Record(ModRenderViewSnapshot view, IModRenderCommandBuffer commands)
        {
            commands.DrawLine(new ModVector3(), new ModVector3(1, 0, 0), new ModColor(), 1);
            commands.DrawLine(new ModVector3(), new ModVector3(0, 1, 0), new ModColor(), 1);
        }
    }

    private sealed class RenderTemporaryDirectory : IDisposable
    {
        public RenderTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Tesseris.ModRenderTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string relative, string content)
        {
            string path = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
