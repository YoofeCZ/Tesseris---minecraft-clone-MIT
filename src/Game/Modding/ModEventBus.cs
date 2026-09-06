using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

public sealed class ModEventCallbackException : Exception
{
    public ModEventCallbackException(
        string modId,
        ResourceId subscriptionId,
        Type eventType,
        ModEventPhase phase,
        Exception innerException)
        : base(
            $"Mod '{modId}' subscription '{subscriptionId}' failed while handling "
            + $"'{eventType.FullName}' in phase '{phase}'.",
            innerException)
    {
        ModId = modId;
        SubscriptionId = subscriptionId;
        EventType = eventType;
        Phase = phase;
    }

    public string ModId { get; }

    public ResourceId SubscriptionId { get; }

    public Type EventType { get; }

    public ModEventPhase Phase { get; }
}

/// <summary>
/// Typed, owner-scoped game-thread event bus. Registration order never depends on filesystem or hash-map
/// enumeration: callbacks are frozen by phase, priority, mod load order and subscription ID.
/// </summary>
public sealed class ModEventBus
{
    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private readonly List<Registration> registrations = [];
    private Registration[] frozen = [];

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<ModEventSubscriptionDescriptor> Subscriptions => Array.AsReadOnly(
        (IsFrozen ? frozen : Ordered(registrations))
        .Where(registration => !registration.IsDisposed)
        .Select(registration => registration.Descriptor)
        .ToArray());

    internal IModEventBus ForMod(string modId, int loadOrder)
    {
        EnsureMainThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (loadOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(loadOrder));
        }

        return new View(this, modId.Trim().ToLowerInvariant(), loadOrder);
    }

    internal void Freeze()
    {
        EnsureMainThread();
        if (IsFrozen)
        {
            return;
        }

        frozen = Ordered(registrations);
        IsFrozen = true;
    }

    public ModEventResult Publish<TEvent>(TEvent value) where TEvent : IModEvent
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(value);
        if (!IsFrozen)
        {
            throw new InvalidOperationException("Freeze the mod event bus before publishing events.");
        }

        ModEventResult aggregate = ModEventResult.Continue;
        foreach (Registration registration in frozen)
        {
            if (registration.IsDisposed || registration.EventType != typeof(TEvent))
            {
                continue;
            }

            ModEventResult result;
            try
            {
                result = registration.Invoke(value);
            }
            catch (Exception exception) when (exception is not ModEventCallbackException)
            {
                throw new ModEventCallbackException(
                    registration.ModId,
                    registration.Id,
                    registration.EventType,
                    registration.Phase,
                    exception);
            }

            if (!Enum.IsDefined(result))
            {
                throw new ModEventCallbackException(
                    registration.ModId,
                    registration.Id,
                    registration.EventType,
                    registration.Phase,
                    new InvalidOperationException($"Handler returned invalid event result '{result}'."));
            }

            if (registration.Phase == ModEventPhase.After && result == ModEventResult.Cancelled)
            {
                throw new ModEventCallbackException(
                    registration.ModId,
                    registration.Id,
                    registration.EventType,
                    registration.Phase,
                    new InvalidOperationException("After-phase observers cannot cancel an event."));
            }

            if (result == ModEventResult.Cancelled)
            {
                return ModEventResult.Cancelled;
            }

            if (result == ModEventResult.Handled)
            {
                aggregate = ModEventResult.Handled;
            }
        }

        return aggregate;
    }

    internal void RemoveOwner(string modId)
    {
        EnsureMainThread();
        foreach (Registration registration in registrations.Where(registration =>
                     string.Equals(registration.ModId, modId, StringComparison.Ordinal)))
        {
            registration.Dispose();
        }
    }

    private IModSubscription Subscribe<TEvent>(
        string modId,
        int loadOrder,
        ResourceId subscriptionId,
        ModEventPhase phase,
        int priority,
        IModEventHandler<TEvent> handler)
        where TEvent : IModEvent
    {
        EnsureMainThread();
        if (IsFrozen)
        {
            throw new InvalidOperationException("Mod event subscriptions are frozen.");
        }

        ArgumentNullException.ThrowIfNull(handler);
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (!subscriptionId.Value.StartsWith(modId + ":", StringComparison.Ordinal))
        {
            throw new ModHostException($"Mod '{modId}' may only register event subscriptions in its own namespace.");
        }

        if (registrations.Any(registration => registration.Id == subscriptionId && !registration.IsDisposed))
        {
            throw new ModHostException($"Event subscription '{subscriptionId}' is already registered.");
        }

        var registration = new Registration<TEvent>(modId, loadOrder, subscriptionId, phase, priority, handler);
        registrations.Add(registration);
        return registration;
    }

    private static Registration[] Ordered(IEnumerable<Registration> source) => source
        .OrderBy(registration => registration.Phase)
        .ThenBy(registration => registration.Priority)
        .ThenBy(registration => registration.LoadOrder)
        .ThenBy(registration => registration.Id.Value, StringComparer.Ordinal)
        .ToArray();

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
        {
            throw new InvalidOperationException("The mod event bus may only be used on the game thread.");
        }
    }

    private abstract class Registration : IModSubscription
    {
        protected Registration(
            string modId,
            int loadOrder,
            ResourceId id,
            Type eventType,
            ModEventPhase phase,
            int priority)
        {
            ModId = modId;
            LoadOrder = loadOrder;
            Id = id;
            EventType = eventType;
            Phase = phase;
            Priority = priority;
            Descriptor = new ModEventSubscriptionDescriptor(id, eventType, phase, priority, modId);
        }

        public string ModId { get; }

        public int LoadOrder { get; }

        public ResourceId Id { get; }

        public Type EventType { get; }

        public ModEventPhase Phase { get; }

        public int Priority { get; }

        public ModEventSubscriptionDescriptor Descriptor { get; }

        public bool IsDisposed { get; private set; }

        public abstract ModEventResult Invoke(IModEvent value);

        public void Dispose() => IsDisposed = true;
    }

    private sealed class Registration<TEvent> : Registration where TEvent : IModEvent
    {
        private readonly IModEventHandler<TEvent> handler;

        public Registration(
            string modId,
            int loadOrder,
            ResourceId id,
            ModEventPhase phase,
            int priority,
            IModEventHandler<TEvent> handler)
            : base(modId, loadOrder, id, typeof(TEvent), phase, priority) =>
            this.handler = handler;

        public override ModEventResult Invoke(IModEvent value) => handler.Handle((TEvent)value);
    }

    private sealed class View(ModEventBus bus, string modId, int loadOrder) : IModEventBus
    {
        public IReadOnlyList<ModEventSubscriptionDescriptor> Subscriptions =>
            bus.Subscriptions.Where(descriptor => descriptor.OwnerModId == modId).ToArray();

        public IModSubscription Subscribe<TEvent>(
            ResourceId subscriptionId,
            ModEventPhase phase,
            int priority,
            IModEventHandler<TEvent> handler)
            where TEvent : IModEvent =>
            bus.Subscribe(modId, loadOrder, subscriptionId, phase, priority, handler);

        public ModEventResult Publish<TEvent>(TEvent value) where TEvent : IModEvent => bus.Publish(value);
    }
}
