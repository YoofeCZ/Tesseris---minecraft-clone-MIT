using Tesseris.Game.Modding;
using Tesseris.Game.Modding.Client;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModClientPlatformTests
{
    [Fact]
    public void Headless_platform_omits_capability_and_all_scoped_surfaces_are_no_op()
    {
        ModClientPlatform platform = ModClientPlatform.CreateDisabled();
        IModClientPlatform client = platform.ForMod("test");
        client.Input.Register(
            new ModInputBinding(new ResourceId("test:key"), "Key", ModInputScope.Global, 1),
            0,
            new InputHandler());
        client.Commands.Register(new ResourceId("test:command"), "/test:command", 0, new CommandHandler());
        client.Ui.RegisterScreen(new ResourceId("test:screen"), new ScreenFactory());
        client.Ui.RegisterHud(new ResourceId("test:hud"), ModHudAnchor.Top, 0, new HudLayer([]));
        client.Rendering.Register(new ResourceId("test:render"), ModRenderPhase.Hud, 0, new RenderCallback([]));
        client.Particles.Register(Particle("test:particle"));
        client.Audio.Register(Sound("test:sound"));
        platform.Freeze();

        Assert.False(platform.IsAvailable);
        Assert.Null(platform.CapabilityDescriptor);
        Assert.False(platform.TryGetCapability("test", out IModClientPlatform? capability));
        Assert.Null(capability);
        Assert.False(client.Ui.Open(new ModScreenOpenRequest(new ResourceId("test:screen"))));
        Assert.Equal(0UL, client.Audio.Play(new ModSoundPlayRequest(new ResourceId("test:sound"))));
        Assert.Empty(platform.Input.Registrations);
        Assert.Empty(platform.Commands.Registrations);
    }

    [Fact]
    public void Screen_factories_create_per_open_sessions_and_owner_controls_close()
    {
        var platform = new ModClientPlatform();
        IModClientUiRegistry owner = platform.ForMod("owner").Ui;
        IModClientUiRegistry stranger = platform.ForMod("stranger").Ui;
        var factory = new ScreenFactory();
        owner.RegisterScreen(new ResourceId("owner:screen"), factory);
        platform.Freeze();

        Assert.False(stranger.Open(new ModScreenOpenRequest(new ResourceId("owner:screen"))));
        Assert.True(owner.Open(new ModScreenOpenRequest(new ResourceId("owner:screen"))));
        Assert.False(stranger.Close());
        Assert.True(owner.Close());
        Assert.True(owner.Open(new ModScreenOpenRequest(new ResourceId("owner:screen"))));

        Assert.Equal(2, factory.Created.Count);
        Assert.NotSame(factory.Created[0], factory.Created[1]);
        Assert.Equal(1, factory.Created[0].Opened);
        Assert.Equal(1, factory.Created[0].Closed);
    }

    [Fact]
    public void Hud_and_render_callbacks_are_deterministic_and_failures_are_attributed()
    {
        var platform = new ModClientPlatform();
        IModClientPlatform client = platform.ForMod("test");
        var calls = new List<string>();
        client.Ui.RegisterHud(new ResourceId("test:z"), ModHudAnchor.Top, 0, new HudLayer(calls, "hud-z"));
        client.Ui.RegisterHud(new ResourceId("test:a"), ModHudAnchor.Top, 0, new HudLayer(calls, "hud-a"));
        client.Rendering.Register(
            new ResourceId("test:render_z"), ModRenderPhase.AfterWorld, 1, new RenderCallback(calls, "render-z"));
        client.Rendering.Register(
            new ResourceId("test:render_a"), ModRenderPhase.AfterWorld, 1, new RenderCallback(calls, "render-a"));
        platform.Freeze();

        platform.Ui.DrawHud(ModHudAnchor.Top, new Canvas());
        platform.Rendering.Dispatch(
            ModRenderPhase.AfterWorld,
            new ModRenderViewSnapshot(default, default, 800, 600, 0.5f),
            new RenderCommands());

        Assert.Equal(["hud-a", "hud-z", "render-a", "render-z"], calls);
    }

    [Fact]
    public void Particle_and_audio_requests_are_validated_and_drained_in_issue_order()
    {
        var platform = new ModClientPlatform();
        IModClientPlatform client = platform.ForMod("test");
        client.Particles.Register(Particle("test:spark"));
        client.Audio.Register(Sound("test:beep"));
        platform.Freeze();

        client.Particles.Spawn(new ModParticleSpawn(
            new ResourceId("test:spark"), new ModVector3(1, 2, 3), default, 55));
        ulong playback = client.Audio.Play(new ModSoundPlayRequest(new ResourceId("test:beep")));
        Assert.True(client.Audio.Stop(playback));

        Assert.Equal(new ResourceId("test:spark"), Assert.Single(platform.Particles.DrainSpawns()).ParticleId);
        IReadOnlyList<ModAudioCommand> audio = platform.Audio.DrainCommands();
        Assert.IsType<ModAudioPlayCommand>(audio[0]);
        Assert.IsType<ModAudioStopCommand>(audio[1]);
        Assert.Empty(platform.Particles.DrainSpawns());
        Assert.Empty(platform.Audio.DrainCommands());
    }

    [Fact]
    public void Ui_and_render_failures_include_owner_registration_and_phase()
    {
        var platform = new ModClientPlatform();
        IModClientPlatform client = platform.ForMod("broken");
        client.Ui.RegisterHud(
            new ResourceId("broken:hud"),
            ModHudAnchor.Top,
            0,
            new ThrowingHud());
        client.Rendering.Register(
            new ResourceId("broken:render"),
            ModRenderPhase.Hud,
            0,
            new ThrowingRender());
        platform.Freeze();

        ModClientUiCallbackException hud = Assert.Throws<ModClientUiCallbackException>(() =>
            platform.Ui.DrawHud(ModHudAnchor.Top, new Canvas()));
        Assert.Equal("broken", hud.ModId);
        Assert.Equal(new ResourceId("broken:hud"), hud.RegistrationId);
        Assert.Equal(ModClientUiCallbackPhase.Hud, hud.Phase);

        ModRenderCallbackException render = Assert.Throws<ModRenderCallbackException>(() =>
            platform.Rendering.Dispatch(
                ModRenderPhase.Hud,
                new ModRenderViewSnapshot(default, default, 10, 10, 0),
                new RenderCommands()));
        Assert.Equal("broken", render.ModId);
        Assert.Equal(new ResourceId("broken:render"), render.RegistrationId);
        Assert.Equal(ModRenderPhase.Hud, render.Phase);
    }

    private static ModParticleDefinition Particle(string id) => new(
        new ResourceId(id), new ResourceId("test:texture"), TimeSpan.FromSeconds(1), 1, ModColor.White);

    private static ModSoundDefinition Sound(string id) =>
        new(new ResourceId(id), new ResourceId("test:asset"));

    private sealed class InputHandler : IModInputHandler
    {
        public ModActionResult Handle(ModInputActionEvent input) => ModActionResult.Pass;
    }

    private sealed class CommandHandler : IModCommandHandler
    {
        public ModActionResult Execute(ModCommandInvocation invocation, IModCommandOutput output) =>
            ModActionResult.Pass;
    }

    private sealed class ScreenFactory : IModScreenFactory
    {
        public List<Screen> Created { get; } = [];
        public IModScreenV2 Create()
        {
            var screen = new Screen();
            Created.Add(screen);
            return screen;
        }
    }

    private sealed class Screen : IModScreenV2
    {
        public int Opened { get; private set; }
        public int Closed { get; private set; }
        public void OnOpened(ModScreenOpenRequest request) => Opened++;
        public void OnClosed() => Closed++;
        public void Draw(IModUiCanvas canvas) { }
        public ModActionResult HandleInput(ModUiInput input) => ModActionResult.Pass;
    }

    private sealed class HudLayer : IModHudLayer
    {
        private readonly List<string> calls;
        private readonly string value;
        public HudLayer(List<string> calls, string value = "hud") { this.calls = calls; this.value = value; }
        public void Draw(IModUiCanvas canvas) => calls.Add(value);
    }

    private sealed class RenderCallback : IModRenderCallback
    {
        private readonly List<string> calls;
        private readonly string value;
        public RenderCallback(List<string> calls, string value = "render") { this.calls = calls; this.value = value; }
        public void Record(ModRenderViewSnapshot view, IModRenderCommandBuffer commands) => calls.Add(value);
    }

    private sealed class ThrowingHud : IModHudLayer
    {
        public void Draw(IModUiCanvas canvas) => throw new ApplicationException("hud");
    }

    private sealed class ThrowingRender : IModRenderCallback
    {
        public void Record(ModRenderViewSnapshot view, IModRenderCommandBuffer commands) =>
            throw new ApplicationException("render");
    }

    private sealed class Canvas : IModUiCanvas
    {
        public int ScreenWidth => 800;
        public int ScreenHeight => 600;
        public void DrawText(string text, int x, int y, int scale = 1) { }
        public void DrawRect(int x, int y, int width, int height) { }
    }

    private sealed class RenderCommands : IModRenderCommandBuffer
    {
        public void DrawModel(ResourceId modelId, ModTransform transform, ModColor tint) { }
        public void DrawBillboard(ResourceId textureId, ModVector3 position, float width, float height, ModColor tint) { }
        public void DrawLine(ModVector3 from, ModVector3 to, ModColor color, float width) { }
    }
}
