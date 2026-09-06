# Tesseris Modding Guide — English

This is the complete English authoring guide for the Tesseris Mod API v2 and standalone loader.

## Chapters

1. [Projects, SDK, build, and `.tmod` packaging](01-project-and-packaging.md)
2. [Content, IDs, textures, and models](02-content-and-assets.md)
3. [Gameplay behavior, item data, containers, and persistence](03-gameplay-and-data.md)
4. [World presets, dimensions, generation, biomes, and ECS](04-worlds-and-entities.md)
5. [Client UI, input, commands, rendering, particles, and audio](05-client-and-ui.md)
6. [Lifecycle, typed events, networking, serialization, and services](06-events-network-services.md)
7. [Loader phases, CoreMods, custom loaders, compatibility, and security](07-loader-coremods-compatibility.md)
8. [Public API capability map](api-map.md)

## Buildable references

- [`RubyWorldgenMod`](../../RubyWorldgenMod/README.md) is the shortest useful starting project.
- [`TotalConversionMod`](../../TotalConversionMod/README.en.md) demonstrates the complete platform without
  relying on a hardcoded machine, magic, or quest type.

All public mod-facing types live in [`src/ModApi`](../../../src/ModApi). Pre-load and CoreMod contracts live
in [`src/Loader.Abstractions`](../../../src/Loader.Abstractions). No Engine, OpenTK, Silk.NET, or Vulkan type
crosses the stable Mod API boundary.
