using Tesseris.Game.Modding;
using Tesseris.Game.Modding.Client;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModInputTests
{
    [Fact]
    public void Key_dispatch_is_priority_then_id_order_and_non_pass_stops()
    {
        var platform = new ModClientPlatform();
        IModInputRegistry input = platform.ForMod("test").Input;
        var calls = new List<string>();
        input.Register(Binding("test:z", ModInputScope.Gameplay, 20), 0,
            new InputHandler(_ => Add(calls, "z", ModActionResult.Pass)));
        input.Register(Binding("test:b", ModInputScope.Gameplay, 20), -1,
            new InputHandler(_ => Add(calls, "b", ModActionResult.Handled)));
        input.Register(Binding("test:a", ModInputScope.Gameplay, 20), -1,
            new InputHandler(value =>
            {
                Assert.Equal(ModInputPhase.Pressed, value.Phase);
                Assert.Equal(1f, value.Value);
                return Add(calls, "a", ModActionResult.Pass);
            }));
        platform.Freeze();

        ModActionResult result = platform.Input.DispatchKey(
            20,
            ModInputPhase.Pressed,
            ModUiModifiers.None,
            ModInputDispatchContext.Gameplay,
            modalCaptured: false);

        Assert.Equal(ModActionResult.Handled, result);
        Assert.Equal(["a", "b"], calls);
    }

    [Fact]
    public void Gameplay_screen_and_global_scopes_respect_modal_capture_and_repeat_phases()
    {
        var platform = new ModClientPlatform();
        IModInputRegistry input = platform.ForMod("test").Input;
        var calls = new List<string>();
        input.Register(Binding("test:game", ModInputScope.Gameplay, 7), 0,
            new InputHandler(value => { calls.Add("game:" + value.Phase); return ModActionResult.Pass; }));
        input.Register(Binding("test:screen", ModInputScope.Screen, 7), 1,
            new InputHandler(value => { calls.Add("screen:" + value.Phase); return ModActionResult.Pass; }));
        input.Register(Binding("test:global", ModInputScope.Global, 7), -1,
            new InputHandler(value => { calls.Add("global:" + value.Phase); return ModActionResult.Pass; }));
        platform.Freeze();

        platform.Input.DispatchKey(
            7, ModInputPhase.Held, ModUiModifiers.None,
            ModInputDispatchContext.Gameplay, modalCaptured: false);
        Assert.Equal(["global:Held", "game:Held"], calls);

        calls.Clear();
        platform.Input.DispatchKey(
            7, ModInputPhase.Released, ModUiModifiers.None,
            ModInputDispatchContext.Screen, modalCaptured: true);
        Assert.Equal(["global:Released", "screen:Released"], calls);
    }

    [Fact]
    public void Input_registration_is_owner_scoped_frozen_and_snapshots_are_read_only()
    {
        var platform = new ModClientPlatform();
        IModInputRegistry input = platform.ForMod("owner").Input;
        Assert.Throws<InvalidOperationException>(() => input.Register(
            Binding("other:bad", ModInputScope.Global, 1), 0, new InputHandler(_ => ModActionResult.Pass)));
        input.Register(
            Binding("owner:good", ModInputScope.Global, 1), 0, new InputHandler(_ => ModActionResult.Pass));
        platform.Freeze();

        Assert.Throws<InvalidOperationException>(() => input.Register(
            Binding("owner:late", ModInputScope.Global, 1), 0, new InputHandler(_ => ModActionResult.Pass)));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<RegisteredModInput>)platform.Input.Registrations).Add(platform.Input.Registrations[0]));
    }

    [Fact]
    public void Input_failures_are_attributed_and_dispatch_is_client_thread_only()
    {
        var platform = new ModClientPlatform();
        platform.ForMod("broken").Input.Register(
            Binding("broken:input", ModInputScope.Global, 3),
            0,
            new InputHandler(_ => throw new ApplicationException("boom")));
        platform.Freeze();

        ModInputCallbackException exception = Assert.Throws<ModInputCallbackException>(() =>
            platform.Input.DispatchKey(
                3, ModInputPhase.Pressed, ModUiModifiers.None,
                ModInputDispatchContext.Gameplay, modalCaptured: false));
        Assert.Equal("broken", exception.ModId);
        Assert.Equal(new ResourceId("broken:input"), exception.BindingId);
        Assert.Equal(ModInputPhase.Pressed, exception.Phase);

        Exception? workerFailure = null;
        var thread = new Thread(() => workerFailure = Record.Exception(() =>
            platform.Input.DispatchKey(
                3, ModInputPhase.Pressed, ModUiModifiers.None,
                ModInputDispatchContext.Gameplay, modalCaptured: false)));
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(workerFailure);
    }

    private static ModInputBinding Binding(string id, ModInputScope scope, int key) =>
        new(new ResourceId(id), id, scope, key);

    private static ModActionResult Add(List<string> values, string value, ModActionResult result)
    {
        values.Add(value);
        return result;
    }

    private sealed class InputHandler(Func<ModInputActionEvent, ModActionResult> callback) : IModInputHandler
    {
        public ModActionResult Handle(ModInputActionEvent input) => callback(input);
    }
}
