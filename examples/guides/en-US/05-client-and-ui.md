# 5. Client UI, input, commands, rendering, particles, and audio

Client features are available through `IModContextV2.Client`. Dedicated/headless hosts may omit the client
capability, so shared mods should keep client registration in a `Client` entrypoint or check capabilities.

## UI

- `Client.Ui.RegisterScreen` registers a modal screen factory.
- `Client.Ui.RegisterHud` registers an anchored HUD layer.
- A modal screen receives draw and key/pointer/text/wheel input and blocks normal gameplay input.
- `IModUiCanvas` provides text, colored rectangles, and item drawing without exposing renderer types.

See [`ClientFeatures.cs`](../../TotalConversionMod/ClientFeatures.cs) for a complete screen, HUD, and open/close
flow. The older `context.Ui.Register` overlay API remains compatible.

## Input and commands

```csharp
v2.Client.Input.Register(
    new ModInputBinding(Ids.Open, "Open panel", ModInputScope.Gameplay, DefaultKeyCode: 77),
    priority: 0,
    handler);

v2.Client.Commands.Register(Ids.Command, "/author.mymod:panel", priority: 0, handler);
```

Players open the command prompt with `/`; Tab completes registered namespaced commands. Input handlers receive
pressed/held/released phases and explicit gameplay, screen, or global scope.

## Renderer-neutral drawing

Register callbacks for `BeforeWorld`, `OpaqueWorld`, `TransparentWorld`, `AfterWorld`, or `Hud`. A callback can
record models, billboards, and width-aware lines using stable asset IDs and `ModTransform`; it never receives
Vulkan/OpenTK objects. Commands are bounded, validated, ordered, and rendered by the host.

## Particles and audio

Register `ModParticleDefinition` and call `Particles.Spawn` with position, velocity, and deterministic seed.
The host owns lifetime, fade, limits, texture loading, and GPU drawing.

Register `ModSoundDefinition` using a namespaced `.wav`/`.ogg` asset, then call `Audio.Play` with optional world
position, volume, pitch, and looping. Keep the returned playback ID to stop a loop. Headless audio drains safely.

[Next: events, networking, and services](06-events-network-services.md)
