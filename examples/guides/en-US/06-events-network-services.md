# 6. Lifecycle, events, networking, serialization, and services

## Lifecycle and typed events

Legacy lifecycle is exposed through `context.Events`: started, stopping, fixed tick, block action, world opened,
saving, and closing. Dispose returned subscriptions when no longer needed.

API v2 adds `EventBus.Subscribe<TEvent>`. Subscriptions are owner-scoped and ordered by phase, priority, mod
load order, and subscription ID. Before/normal handlers may return continue, handled, or cancelled; after-phase
observers cannot cancel. Callback failures include mod, subscription, event type, and phase.

Built-in typed events cover loader readiness, game start, simulation tick, world open/save/close, and player
join/leave. Mods may publish their own `IModEvent` contract through an approved shared assembly.

## Serialization

Register serializers and forward-only migrations during configuration. Serialized values always carry the
serializer ID and schema version. Migration gaps, cycles, invalid payloads, or partial decode fail the containing
load transaction. Pure serializers may be used from worker-safe code after freeze.

## Networking

Register a namespaced channel with protocol version, direction, and maximum payload. Incoming messages are
size/direction/version validated before the handler runs. Network callbacks must enqueue world mutations onto
the game thread through `IModNetworkMessageContext.Enqueue`.

The same contracts support loopback and a future remote transport boundary; gameplay code does not receive a
socket or engine connection object.

## Inter-mod services

`InterModServices.Publish<T>` exposes a versioned shared contract under a namespaced ID. Consumers query by
contract and compatible version. Services are removed automatically when their owner unloads. Put shared
interfaces in an approved contract assembly, not in one mod's private implementation DLL.

The working event/network/service example is in
[`IntegrationFeatures.cs`](../../TotalConversionMod/IntegrationFeatures.cs).

[Next: loader and CoreMods](07-loader-coremods-compatibility.md)
