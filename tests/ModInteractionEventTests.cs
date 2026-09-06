using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModInteractionEventTests
{
    [Fact]
    public void Before_action_exposes_values_in_registration_order_and_cancellation_stops_dispatch()
    {
        var hub = new ModEventHub();
        var calls = new List<string>();
        IModBlockActionContext? observed = null;
        hub.ForMod("base").OnBlockAction(context =>
        {
            calls.Add("base:first");
            observed = context;
        });
        hub.ForMod("dependent").OnBlockAction(context =>
        {
            calls.Add("dependent:first");
            context.Cancel = true;
        });
        hub.ForMod("dependent").OnBlockAction(_ => calls.Add("dependent:skipped"));
        var context = CreateContext(ModBlockActionPhase.Before);

        hub.RaiseBlockAction(context);

        Assert.Equal(new[] { "base:first", "dependent:first" }, calls);
        Assert.True(context.Cancel);
        Assert.Same(context, observed);
        Assert.Equal(ModBlockActionKind.Break, observed!.Kind);
        Assert.Equal(ModBlockActionPhase.Before, observed.Phase);
        Assert.Equal((4, 5, 6), (observed.X, observed.Y, observed.Z));
        Assert.Equal(new ResourceId("tesseris:stone"), observed.BlockId);
        Assert.Equal(new ResourceId("tesseris:iron_pickaxe"), observed.HeldItemId);
    }

    [Fact]
    public void After_action_is_immutable_and_non_cancellable()
    {
        var context = CreateContext(ModBlockActionPhase.After);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => context.Cancel = true);

        Assert.False(context.Cancel);
        Assert.Contains("cannot be cancelled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void After_action_notifies_every_registered_callback_in_order()
    {
        var hub = new ModEventHub();
        var calls = new List<string>();
        hub.ForMod("alpha").OnBlockAction(_ => calls.Add("alpha:1"));
        hub.ForMod("alpha").OnBlockAction(_ => calls.Add("alpha:2"));
        hub.ForMod("beta").OnBlockAction(_ => calls.Add("beta:1"));

        hub.RaiseBlockAction(CreateContext(ModBlockActionPhase.After));

        Assert.Equal(new[] { "alpha:1", "alpha:2", "beta:1" }, calls);
    }

    [Fact]
    public void Disposed_subscription_is_not_invoked_and_disposal_is_idempotent()
    {
        var hub = new ModEventHub();
        int calls = 0;
        IModSubscription subscription = hub.ForMod("example").OnBlockAction(_ => calls++);

        subscription.Dispose();
        subscription.Dispose();
        hub.RaiseBlockAction(CreateContext(ModBlockActionPhase.Before));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Callback_failure_is_attributed_to_its_owning_mod()
    {
        var hub = new ModEventHub();
        hub.ForMod("healthy").OnBlockAction(_ => { });
        hub.ForMod("broken").OnBlockAction(_ => throw new InvalidOperationException("mod bug"));

        ModBlockActionCallbackException exception = Assert.Throws<ModBlockActionCallbackException>(() =>
            hub.RaiseBlockAction(CreateContext(ModBlockActionPhase.Before)));

        Assert.Equal("broken", exception.ModId);
        Assert.Equal(ModBlockActionKind.Break, exception.Kind);
        Assert.Equal(ModBlockActionPhase.Before, exception.Phase);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("broken", exception.Message, StringComparison.Ordinal);
    }

    private static ModBlockActionContext CreateContext(ModBlockActionPhase phase) => new(
        phase,
        ModBlockActionKind.Break,
        4,
        5,
        6,
        new ResourceId("tesseris:stone"),
        new ResourceId("tesseris:iron_pickaxe"));
}
