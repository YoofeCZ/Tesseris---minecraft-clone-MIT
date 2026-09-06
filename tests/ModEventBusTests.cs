using Tesseris.Game.Modding;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModEventBusTests
{
    [Fact]
    public void Dispatch_is_phase_priority_dependency_and_id_ordered()
    {
        var calls = new List<string>();
        var bus = new ModEventBus();
        IModEventBus first = bus.ForMod("first", 0);
        IModEventBus second = bus.ForMod("second", 1);

        second.Subscribe(new ResourceId("second:normal"), ModEventPhase.Normal, 0, Handler("second", calls));
        first.Subscribe(new ResourceId("first:z"), ModEventPhase.Normal, 0, Handler("z", calls));
        first.Subscribe(new ResourceId("first:a"), ModEventPhase.Normal, 0, Handler("a", calls));
        second.Subscribe(new ResourceId("second:before"), ModEventPhase.Before, 100, Handler("before", calls));
        first.Subscribe(new ResourceId("first:priority"), ModEventPhase.Normal, -1, Handler("priority", calls));
        bus.Freeze();

        Assert.Equal(ModEventResult.Continue, bus.Publish(new TestEvent(1)));
        Assert.Equal(["before", "priority", "a", "z", "second"], calls);
    }

    [Fact]
    public void Cancellation_stops_dispatch_and_handled_is_aggregated()
    {
        var calls = new List<string>();
        var bus = new ModEventBus();
        IModEventBus view = bus.ForMod("test", 0);
        view.Subscribe(new ResourceId("test:handled"), ModEventPhase.Before, 0,
            new DelegateHandler<TestEvent>(_ => { calls.Add("handled"); return ModEventResult.Handled; }));
        view.Subscribe(new ResourceId("test:cancel"), ModEventPhase.Normal, 0,
            new DelegateHandler<TestEvent>(_ => { calls.Add("cancel"); return ModEventResult.Cancelled; }));
        view.Subscribe(new ResourceId("test:late"), ModEventPhase.After, 0, Handler("late", calls));
        bus.Freeze();

        Assert.Equal(ModEventResult.Cancelled, bus.Publish(new TestEvent(1)));
        Assert.Equal(["handled", "cancel"], calls);
    }

    [Fact]
    public void Callback_failure_preserves_owner_subscription_event_and_phase()
    {
        var bus = new ModEventBus();
        bus.ForMod("ruby", 0).Subscribe(
            new ResourceId("ruby:broken"),
            ModEventPhase.Normal,
            0,
            new DelegateHandler<TestEvent>(_ => throw new InvalidOperationException("boom")));
        bus.Freeze();

        ModEventCallbackException failure = Assert.Throws<ModEventCallbackException>(() =>
            bus.Publish(new TestEvent(2)));

        Assert.Equal("ruby", failure.ModId);
        Assert.Equal("ruby:broken", failure.SubscriptionId.Value);
        Assert.Equal(typeof(TestEvent), failure.EventType);
        Assert.Equal(ModEventPhase.Normal, failure.Phase);
    }

    [Fact]
    public void Registration_is_scoped_unique_frozen_and_main_thread_only()
    {
        var bus = new ModEventBus();
        IModEventBus view = bus.ForMod("ruby", 0);
        Assert.Throws<ModHostException>(() => view.Subscribe(
            new ResourceId("foreign:x"), ModEventPhase.Normal, 0, Handler("x", [])));

        IModSubscription subscription = view.Subscribe(
            new ResourceId("ruby:x"), ModEventPhase.Normal, 0, Handler("x", []));
        Assert.Throws<ModHostException>(() => view.Subscribe(
            new ResourceId("ruby:x"), ModEventPhase.Normal, 0, Handler("x", [])));
        Assert.Single(view.Subscriptions);
        subscription.Dispose();
        Assert.Empty(view.Subscriptions);

        view.Subscribe(new ResourceId("ruby:y"), ModEventPhase.Normal, 0, Handler("y", []));
        bus.Freeze();
        Assert.Throws<InvalidOperationException>(() => view.Subscribe(
            new ResourceId("ruby:late"), ModEventPhase.Normal, 0, Handler("late", [])));
        Assert.IsType<InvalidOperationException>(RunOnDedicatedThread(
            () => bus.Publish(new TestEvent(3))));
    }

    private static Exception? RunOnDedicatedThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        thread.Join();
        return failure;
    }

    [Fact]
    public void After_observer_cannot_cancel()
    {
        var bus = new ModEventBus();
        bus.ForMod("test", 0).Subscribe(
            new ResourceId("test:bad_after"),
            ModEventPhase.After,
            0,
            new DelegateHandler<TestEvent>(_ => ModEventResult.Cancelled));
        bus.Freeze();

        ModEventCallbackException failure = Assert.Throws<ModEventCallbackException>(() =>
            bus.Publish(new TestEvent(0)));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    private static DelegateHandler<TestEvent> Handler(string name, List<string> calls) =>
        new(_ => { calls.Add(name); return ModEventResult.Continue; });

    private sealed record TestEvent(int Value) : IModEvent;

    private sealed class DelegateHandler<TEvent>(Func<TEvent, ModEventResult> callback) : IModEventHandler<TEvent>
        where TEvent : IModEvent
    {
        public ModEventResult Handle(TEvent value) => callback(value);
    }
}
