using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModUiScreenTests
{
    [Fact]
    public void Registration_is_owner_scoped_unique_and_frozen()
    {
        using var host = new ModUiScreenHost();
        ModUiScreenHost.OwnerView alpha = host.ForMod("alpha");
        var screen = new RecordingScreen();

        alpha.RegisterScreen(new ResourceId("alpha:main"), screen);

        Assert.Throws<ModHostException>(() =>
            alpha.RegisterScreen(new ResourceId("other:foreign"), new RecordingScreen()));
        Assert.Throws<ModHostException>(() =>
            alpha.RegisterScreen(new ResourceId("alpha:main"), new RecordingScreen()));

        host.Freeze();
        Assert.True(host.IsFrozen);
        Assert.Throws<InvalidOperationException>(() =>
            alpha.RegisterScreen(new ResourceId("alpha:late"), new RecordingScreen()));
    }

    [Fact]
    public void Replacing_the_global_screen_closes_old_before_opening_new()
    {
        using var host = new ModUiScreenHost();
        var calls = new List<string>();
        var states = new List<ActiveModScreen?>();
        host.ActiveScreenChanged += states.Add;
        ModUiScreenHost.OwnerView alpha = host.ForMod("alpha");
        ModUiScreenHost.OwnerView beta = host.ForMod("beta");
        alpha.RegisterScreen(new ResourceId("alpha:first"), new RecordingScreen(calls, "first"));
        beta.RegisterScreen(new ResourceId("beta:second"), new RecordingScreen(calls, "second"));

        Assert.True(alpha.OpenScreen(new ResourceId("alpha:first")));
        Assert.True(beta.OpenScreen(new ResourceId("beta:second")));

        Assert.Equal(new[] { "first:opened", "first:closed", "second:opened" }, calls);
        Assert.Equal(new ActiveModScreen("beta", new ResourceId("beta:second")), host.ActiveScreen);
        Assert.Equal(3, states.Count);
        Assert.Equal(new ActiveModScreen("alpha", new ResourceId("alpha:first")), states[0]);
        Assert.Null(states[1]);
        Assert.Equal(host.ActiveScreen, states[2]);
    }

    [Fact]
    public void Open_and_close_are_owner_scoped_and_reopening_active_screen_is_a_noop()
    {
        using var host = new ModUiScreenHost();
        var screen = new RecordingScreen();
        ModUiScreenHost.OwnerView owner = host.ForMod("owner");
        ModUiScreenHost.OwnerView stranger = host.ForMod("stranger");
        var id = new ResourceId("owner:screen");
        owner.RegisterScreen(id, screen);

        Assert.False(stranger.OpenScreen(id));
        Assert.False(owner.OpenScreen(new ResourceId("owner:missing")));
        Assert.True(owner.OpenScreen(id));
        Assert.True(owner.OpenScreen(id));
        Assert.False(stranger.CloseScreen());
        Assert.Equal(1, screen.Opened);
        Assert.True(owner.CloseScreen());
        Assert.False(owner.CloseScreen());
        Assert.Equal(1, screen.Closed);
        Assert.Null(host.ActiveScreen);
    }

    [Fact]
    public void Input_and_draw_reach_only_the_active_screen()
    {
        using var host = new ModUiScreenHost();
        var screen = new RecordingScreen { InputResult = ModActionResult.Handled };
        ModUiScreenHost.OwnerView owner = host.ForMod("owner");
        owner.RegisterScreen(new ResourceId("owner:screen"), screen);
        var input = new ModUiInput(
            ModUiInputKind.PointerButton,
            ModInputPhase.Pressed,
            12,
            34,
            ModPointerButton.Primary);

        Assert.Equal(ModActionResult.Pass, host.DispatchInput(input));
        host.Draw(new RecordingCanvas());
        Assert.True(owner.OpenScreen(new ResourceId("owner:screen")));
        Assert.Equal(ModActionResult.Handled, host.DispatchInput(input));
        host.Draw(new RecordingCanvas());

        Assert.Equal(input, screen.LastInput);
        Assert.Equal(1, screen.Drawn);
    }

    [Theory]
    [InlineData(ModUiScreenCallbackPhase.Opened)]
    [InlineData(ModUiScreenCallbackPhase.Closed)]
    [InlineData(ModUiScreenCallbackPhase.Input)]
    [InlineData(ModUiScreenCallbackPhase.Draw)]
    public void Callback_failures_include_owner_screen_and_phase(ModUiScreenCallbackPhase phase)
    {
        using var host = new ModUiScreenHost();
        var screen = new ThrowingScreen(phase);
        ModUiScreenHost.OwnerView owner = host.ForMod("broken");
        var id = new ResourceId("broken:screen");
        owner.RegisterScreen(id, screen);

        ModUiScreenCallbackException exception;
        if (phase == ModUiScreenCallbackPhase.Opened)
        {
            exception = Assert.Throws<ModUiScreenCallbackException>(() => owner.OpenScreen(id));
        }
        else
        {
            Assert.True(owner.OpenScreen(id));
            exception = phase switch
            {
                ModUiScreenCallbackPhase.Closed =>
                    Assert.Throws<ModUiScreenCallbackException>(() => owner.CloseScreen()),
                ModUiScreenCallbackPhase.Input =>
                    Assert.Throws<ModUiScreenCallbackException>(() => host.DispatchInput(default)),
                ModUiScreenCallbackPhase.Draw =>
                    Assert.Throws<ModUiScreenCallbackException>(() => host.Draw(new RecordingCanvas())),
                _ => throw new ArgumentOutOfRangeException(nameof(phase)),
            };
        }

        Assert.Equal("broken", exception.ModId);
        Assert.Equal(id, exception.ScreenId);
        Assert.Equal(phase, exception.Phase);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void Shutdown_closes_active_screen_and_is_idempotent()
    {
        var host = new ModUiScreenHost();
        var screen = new RecordingScreen();
        ModUiScreenHost.OwnerView owner = host.ForMod("owner");
        owner.RegisterScreen(new ResourceId("owner:screen"), screen);
        owner.OpenScreen(new ResourceId("owner:screen"));

        host.Shutdown();
        host.Shutdown();

        Assert.Equal(1, screen.Closed);
        Assert.False(host.HasActiveScreen);
        Assert.Throws<ObjectDisposedException>(() => host.DispatchInput(default));
    }

    [Fact]
    public void Operations_from_worker_threads_are_rejected()
    {
        using var host = new ModUiScreenHost();
        Exception? actual = null;
        var worker = new Thread(() => actual = Record.Exception(host.Freeze));

        worker.Start();
        worker.Join();

        InvalidOperationException exception = Assert.IsType<InvalidOperationException>(actual);
        Assert.Contains("main game thread", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingScreen : IModScreen
    {
        private readonly List<string>? calls;
        private readonly string name;

        public RecordingScreen(List<string>? calls = null, string name = "screen")
        {
            this.calls = calls;
            this.name = name;
        }

        public int Opened { get; private set; }
        public int Closed { get; private set; }
        public int Drawn { get; private set; }
        public ModUiInput? LastInput { get; private set; }
        public ModActionResult InputResult { get; init; } = ModActionResult.Pass;

        public void OnOpened()
        {
            Opened++;
            calls?.Add($"{name}:opened");
        }

        public void OnClosed()
        {
            Closed++;
            calls?.Add($"{name}:closed");
        }

        public void Draw(IModUiCanvas canvas) => Drawn++;

        public ModActionResult HandleInput(ModUiInput input)
        {
            LastInput = input;
            return InputResult;
        }
    }

    private sealed class ThrowingScreen : IModScreen
    {
        private readonly ModUiScreenCallbackPhase phase;

        public ThrowingScreen(ModUiScreenCallbackPhase phase) => this.phase = phase;

        public void OnOpened() => ThrowIf(ModUiScreenCallbackPhase.Opened);
        public void OnClosed() => ThrowIf(ModUiScreenCallbackPhase.Closed);
        public void Draw(IModUiCanvas canvas) => ThrowIf(ModUiScreenCallbackPhase.Draw);

        public ModActionResult HandleInput(ModUiInput input)
        {
            ThrowIf(ModUiScreenCallbackPhase.Input);
            return ModActionResult.Pass;
        }

        private void ThrowIf(ModUiScreenCallbackPhase current)
        {
            if (phase == current)
            {
                throw new InvalidOperationException("screen bug");
            }
        }
    }

    private sealed class RecordingCanvas : IModUiCanvas
    {
        public int ScreenWidth => 1280;
        public int ScreenHeight => 720;
        public void DrawText(string text, int x, int y, int scale = 1) { }
        public void DrawRect(int x, int y, int width, int height) { }
    }
}
