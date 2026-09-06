using System.Text.Json;
using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModUiTests
{
    [Fact]
    public void Mod_host_supplies_scoped_ui_and_freezes_it_with_other_registries()
    {
        using var temporary = new TemporaryDirectory();
        string modDirectory = Path.Combine(temporary.Path, "example");
        Directory.CreateDirectory(modDirectory);
        File.WriteAllText(
            Path.Combine(modDirectory, "tesseris.mod.json"),
            JsonSerializer.Serialize(new
            {
                id = "example",
                name = "Example",
                version = "1.0.0",
                apiVersion = "^1.0.0",
                capabilities = new[] { "ui-test" }
            }));
        var mod = new UiMod();

        using ModHost host = ModHost.DiscoverAndLoad(
            temporary.Path,
            new ModHostOptions { Loaders = new[] { new UiTestLoader(mod) } });
        host.Initialize();
        host.Freeze();

        Assert.True(host.Ui.IsFrozen);
        RegisteredModOverlay overlay = Assert.Single(host.Ui.Overlays);
        Assert.Equal("example", overlay.ModId);
        Assert.Equal(new ResourceId("example:hud"), overlay.Id);
        Assert.Throws<InvalidOperationException>(() =>
            mod.Ui!.Register(new ResourceId("example:late"), 0, new RecordingOverlay()));
    }

    [Fact]
    public void Scoped_registry_orders_overlays_by_priority_then_id()
    {
        var registry = new ModUiRegistry();
        IModUiRegistry ui = registry.ForMod("example");

        ui.Register(new ResourceId("example:zulu"), 10, new RecordingOverlay());
        ui.Register(new ResourceId("example:alpha"), 10, new RecordingOverlay());
        ui.Register(new ResourceId("example:background"), -5, new RecordingOverlay());

        Assert.Equal(
            new[] { "example:background", "example:alpha", "example:zulu" },
            registry.Overlays.Select(overlay => overlay.Id.Value));
        Assert.Equal(new[] { "example", "example", "example" }, registry.Overlays.Select(overlay => overlay.ModId));
    }

    [Fact]
    public void Scoped_registry_enforces_namespace_unique_ids_and_freeze()
    {
        var registry = new ModUiRegistry();
        IModUiRegistry ui = registry.ForMod("example");
        var overlay = new RecordingOverlay();

        ModHostException ownership = Assert.Throws<ModHostException>(() =>
            ui.Register(new ResourceId("other:overlay"), 0, overlay));
        Assert.Contains("own namespace", ownership.Message, StringComparison.Ordinal);

        ui.Register(new ResourceId("example:overlay"), 0, overlay);
        Assert.Throws<ModHostException>(() =>
            ui.Register(new ResourceId("example:overlay"), 1, new RecordingOverlay()));

        registry.Freeze();

        Assert.True(registry.IsFrozen);
        Assert.Throws<InvalidOperationException>(() =>
            ui.Register(new ResourceId("example:late"), 0, overlay));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<RegisteredModOverlay>)registry.Overlays).Add(
                new RegisteredModOverlay("example", new ResourceId("example:injected"), 0, overlay)));
    }

    [Fact]
    public void Draw_passes_screen_dimensions_and_integer_primitives()
    {
        var registry = new ModUiRegistry();
        var overlay = new RecordingOverlay();
        registry.ForMod("example").Register(new ResourceId("example:hud"), 0, overlay);
        registry.Freeze();
        var canvas = new RecordingCanvas(1920, 1080);

        registry.Draw(canvas);

        Assert.Equal((1920, 1080), overlay.ScreenSize);
        Assert.Equal(("Tesseris", 12, 24, 3), Assert.Single(canvas.Text));
        Assert.Equal((4, 5, 60, 20), Assert.Single(canvas.Rectangles));
    }

    [Fact]
    public void Draw_failure_identifies_mod_and_overlay()
    {
        var registry = new ModUiRegistry();
        registry.ForMod("broken").Register(
            new ResourceId("broken:hud"),
            0,
            new ThrowingOverlay());

        ModOverlayDrawException exception = Assert.Throws<ModOverlayDrawException>(() =>
            registry.Draw(new RecordingCanvas(800, 600)));

        Assert.Equal("broken", exception.ModId);
        Assert.Equal(new ResourceId("broken:hud"), exception.OverlayId);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("broken:hud", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unscoped_registry_cannot_be_used_to_bypass_ownership()
    {
        IModUiRegistry registry = new ModUiRegistry();

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new ResourceId("example:hud"), 0, new RecordingOverlay()));
    }

    private sealed class RecordingOverlay : IModOverlay
    {
        public (int Width, int Height) ScreenSize { get; private set; }

        public void Draw(IModUiCanvas canvas)
        {
            ScreenSize = (canvas.ScreenWidth, canvas.ScreenHeight);
            canvas.DrawText("Tesseris", 12, 24, 3);
            canvas.DrawRect(4, 5, 60, 20);
        }
    }

    private sealed class ThrowingOverlay : IModOverlay
    {
        public void Draw(IModUiCanvas canvas) => throw new InvalidOperationException("overlay bug");
    }

    private sealed class RecordingCanvas : IModUiCanvas
    {
        public RecordingCanvas(int screenWidth, int screenHeight)
        {
            ScreenWidth = screenWidth;
            ScreenHeight = screenHeight;
        }

        public int ScreenWidth { get; }
        public int ScreenHeight { get; }
        public List<(string Text, int X, int Y, int Scale)> Text { get; } = new();
        public List<(int X, int Y, int Width, int Height)> Rectangles { get; } = new();

        public void DrawText(string text, int x, int y, int scale = 1) => Text.Add((text, x, y, scale));

        public void DrawRect(int x, int y, int width, int height) => Rectangles.Add((x, y, width, height));
    }

    private sealed class UiMod : IMod
    {
        public IModUiRegistry? Ui { get; private set; }

        public void Configure(IModContext context)
        {
            Ui = context.Ui;
            Ui.Register(new ResourceId("example:hud"), 25, new RecordingOverlay());
        }
    }

    private sealed class UiTestLoader : IModLoader
    {
        private readonly IMod mod;

        public UiTestLoader(IMod mod) => this.mod = mod;

        public string Id => "tests:ui";

        public bool CanLoad(ModDescriptor descriptor) =>
            descriptor.Capabilities.Contains("ui-test", StringComparer.Ordinal);

        public IModLoadResult Load(ModDescriptor descriptor, IModLoadContext context) => new Result(mod);

        private sealed class Result : IModLoadResult
        {
            public Result(IMod instance) => Instance = instance;

            public IMod? Instance { get; }

            public IReadOnlyList<ModContentSource> ContentSources => Array.Empty<ModContentSource>();

            public void Dispose() { }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Tesseris.ModUiTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
