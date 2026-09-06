using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModApiCompatibilityTests
{
    [Fact]
    public void Legacy_context_implementation_does_not_need_to_implement_v2_platforms()
    {
        IModContext context = new LegacyContext();

        Assert.Equal("legacy", context.Mod.Id);
        Assert.False(context is IModContextV2);
        Assert.Throws<NotSupportedException>(() => _ = context.Behaviors);
        Assert.Throws<NotSupportedException>(() => _ = context.Data);
    }

    [Fact]
    public void Legacy_ui_and_worldgen_implementations_keep_their_original_required_surface()
    {
        IModUiRegistry ui = new LegacyUi();
        IWorldGenerationRegistry worldGeneration = new LegacyWorldGeneration();

        Assert.Throws<NotSupportedException>(() => ui.OpenScreen(new ResourceId("legacy:screen")));
        Assert.Throws<NotSupportedException>(() =>
            worldGeneration.SetBaseGenerator(new ResourceId("legacy:generator"), new EmptyGenerator()));
    }

    [Fact]
    public void Legacy_descriptor_constructor_and_host_version_remain_available_during_v2_rollout()
    {
        var descriptor = new ModDescriptor(
            "legacy",
            "Legacy",
            "1.0.0",
            "1.0.0",
            ".",
            "Legacy.dll",
            "Legacy.Entry",
            null,
            true,
            Array.Empty<string>(),
            Array.Empty<ModDependency>());

        Assert.Equal("legacy", descriptor.Id);
        Assert.Equal("1.0.0", ModApiInfo.CurrentVersion);
        Assert.Equal("2.0.0", ModApiInfo.LatestContractVersion);
    }

    private sealed class LegacyContext : IModContext
    {
        public ModDescriptor Mod { get; } = new(
            "legacy", "Legacy", "1.0.0", "1.0.0", ".", null, null, null, false, [], []);

        public IModGame Game => throw new NotSupportedException();
        public IModContent Content => throw new NotSupportedException();
        public IWorldGenerationRegistry WorldGeneration => throw new NotSupportedException();
        public IModUiRegistry Ui => throw new NotSupportedException();
        public IModEvents Events => throw new NotSupportedException();
        public IServiceProvider Services => EmptyServices.Instance;
        public IModLogger Logger => EmptyLogger.Instance;
    }

    private sealed class LegacyUi : IModUiRegistry
    {
        public void Register(ResourceId id, int priority, IModOverlay overlay) { }
    }

    private sealed class LegacyWorldGeneration : IWorldGenerationRegistry
    {
        public void Register(WorldGenerationStage stage, ResourceId id, int priority, IChunkGenerationHook hook) { }
    }

    private sealed class EmptyGenerator : IModWorldGenerator
    {
        public ModWorldColumn SampleColumn(IWorldColumnContext context) => default;
        public void Generate(IChunkGenerationContext context) { }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    private sealed class EmptyLogger : IModLogger
    {
        public static readonly EmptyLogger Instance = new();
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
