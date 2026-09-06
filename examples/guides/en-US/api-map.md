# 8. Public API capability map

| Need | Primary API |
|---|---|
| Mod entrypoint and metadata | `IMod`, `IModContext`, `ModDescriptor`, `ResourceId` |
| Static content and extra roots | `IModContent`, JSON under `content/` |
| Vanilla terrain modifiers | `IWorldGenerationRegistry`, `IChunkGenerationHook` |
| Full terrain replacement | `IModWorldGenerator`, `IWorldColumnContext`, `IChunkGenerationContext` |
| Presets, dimensions, biomes | `IModWorldDefinitionRegistry`, `IModBiomeSource` |
| Item/block mechanics | `IModBehaviors` and behavior callback interfaces |
| Live world/player/inventory | `IModGame`, `IModWorld`, `IModPlayer`, `IModInventory` |
| Persistent mod data | `IModData`, `IModDataContainer` |
| Serialization and migration | `IModSerializationRegistry`, `IModSerializer<T>`, `IModDataMigration` |
| Entities/components/systems | `IModEntityPlatform`, `IModSystem`, entity queries/command buffers |
| Per-stack components | `IModStackPlatform`, `ModStackComponentDescriptor` |
| Containers and menus | `IModContainerPlatform`, `IModMenuHandler` |
| Screens and HUD | `IModClientUiRegistry`, `IModScreenV2`, `IModHudLayer` |
| Input and commands | `IModInputRegistry`, `IModCommandRegistry` |
| Rendering and particles | `IModRenderRegistry`, `IModRenderCommandBuffer`, `IModParticleRegistry` |
| Audio | `IModAudioRegistry` |
| Typed events | `IModEventBus`, `IModEventHandler<TEvent>` |
| Networking | `IModNetworkRegistry`, `IModNetworkHandler` |
| Inter-mod contracts | `IModServiceRegistry`, `IModCapabilityProvider` |
| Pre-load patching | `IPreLaunchMod`, `ICoreMod`, `IModPatchRegistry` |
| Alternative package loading | `IModLoader`, `IModLoadResult` |

Public contracts: [`src/ModApi`](../../../src/ModApi). Loader contracts:
[`src/Loader.Abstractions`](../../../src/Loader.Abstractions). For exact signatures and lifetime/threading rules,
read the XML documentation on the interface member and the corresponding use in
[`TotalConversionMod`](../../TotalConversionMod/README.en.md).
