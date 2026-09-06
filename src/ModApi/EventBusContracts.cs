namespace Tesseris.ModApi;

/// <summary>Marker for immutable typed events.</summary>
public interface IModEvent;

public enum ModEventPhase
{
    Before = 0,
    Normal = 100,
    After = 200
}

public enum ModEventResult
{
    Continue,
    Handled,
    Cancelled
}

public interface IModEventHandler<in TEvent> where TEvent : IModEvent
{
    ModEventResult Handle(TEvent value);
}

public sealed record ModEventSubscriptionDescriptor(
    ResourceId Id,
    Type EventType,
    ModEventPhase Phase,
    int Priority,
    string OwnerModId);

/// <summary>
/// Typed game-thread event bus. Subscribers run by phase, priority, owner dependency order and subscription
/// ID. Cancellation stops later cancellable phases; observer failures are attributed to owner and ID. Custom
/// event contracts crossing isolated mods must live in an approved shared contract assembly.
/// </summary>
public interface IModEventBus
{
    IReadOnlyList<ModEventSubscriptionDescriptor> Subscriptions { get; }

    IModSubscription Subscribe<TEvent>(
        ResourceId subscriptionId,
        ModEventPhase phase,
        int priority,
        IModEventHandler<TEvent> handler)
        where TEvent : IModEvent;

    ModEventResult Publish<TEvent>(TEvent value) where TEvent : IModEvent;
}

public sealed record ModLoaderReadyEvent(string LoaderVersion) : IModEvent;

public sealed record ModGameStartingEvent(bool IsClient) : IModEvent;

public sealed record ModSimulationTickEvent(ulong Tick, TimeSpan Delta) : IModEvent;

public sealed record ModWorldOpenedEvent(ResourceId PresetId, long WorldSeed) : IModEvent;

public sealed record ModWorldSavingEvent(ulong Tick) : IModEvent;

public sealed record ModWorldClosingEvent(ulong Tick) : IModEvent;

public sealed record ModPlayerJoinedEvent(ulong PlayerId, string DisplayName) : IModEvent;

public sealed record ModPlayerLeavingEvent(ulong PlayerId) : IModEvent;
