# 2. Content, IDs, textures, and models

## Namespaced resource IDs

Every public resource uses `namespace:path`. A bare reference inside a package is resolved against the owning
mod namespace:

```text
ruby_ore                 -> ruby:ruby_ore
tesseris:stone           -> vanilla stone
another_mod:component    -> explicit cross-mod reference
```

Use lowercase stable IDs. Resource paths must not be rooted and cannot contain `.` or `..` traversal. Two mods
may both define `copper_ore` because their namespaces differ.

In C#:

```csharp
ResourceId ore = new("author.mymod:copper_ore");
```

## Content directory

```text
content/
  blocks/
  items/
  recipes/
  models/
  textures/
  audio/
```

Catalog ordering is deterministic: vanilla first, followed by the resolved mod load plan. A model or texture
is resolved from the content source that defines it. Unsafe paths and ambiguous duplicates are rejected.

## Blocks

`content/blocks/blue_stone.json`:

```json
{
  "texture": "blue_stone",
  "material": "Stone",
  "hardness": 2.0,
  "chiselable": true,
  "opaque": true
}
```

This defines `author.mymod:blue_stone`. A same-ID placeable item is generated for a normal block unless a
more specific item definition overrides it. `texture` may be a local bare name or an explicit resource ID such
as `tesseris:stone`.

Per-face textures are supported by the block definition's face mapping. Keep texture references namespaced
when consuming assets owned by another package.

## Items and tools

Material item:

```json
{
  "name": "Blue Crystal",
  "texture": "blue_crystal",
  "kind": "Material",
  "maxStack": 64
}
```

Tool:

```json
{
  "name": "Blue Pickaxe",
  "texture": "blue_pickaxe",
  "iconTexture": "blue_pickaxe",
  "model": "blue_pickaxe",
  "kind": "Tool",
  "tool": "Pickaxe",
  "tier": "Diamond",
  "durability": 1800,
  "maxStack": 1
}
```

JSON describes ordinary static properties. Arbitrary use behavior, state, UI, and systems are registered from
C#; they are not limited to built-in tool categories.

## Recipes

Crafting:

```json
{
  "output": "blue_pickaxe",
  "count": 1,
  "smelting": false,
  "inputs": [
    { "item": "blue_crystal", "count": 3 },
    { "item": "tesseris:stick", "count": 2 }
  ]
}
```

Smelting:

```json
{
  "output": "blue_crystal",
  "count": 1,
  "smelting": true,
  "inputs": [
    { "item": "blue_ore", "count": 1 }
  ]
}
```

## Textures

Put block and item PNG files under `content/textures`. Use `.png` for render assets and `.wav` or `.ogg` for
audio. The resolver validates extension, ownership, traversal, and reparse/symlink escapes.

Examples:

```text
content/textures/blue_stone.png       -> author.mymod:blue_stone
content/audio/ui/click.wav            -> author.mymod:ui/click.wav
```

## Item models

Models live under `content/models` and may use a namespaced texture reference. Item model IDs are catalog IDs,
not global file basenames, so separate mods can use the same local filename without collision.

The canonical example contains:

- [`sky_compass.json`](../../TotalConversionMod/content/models/sky_compass.json)
- [`sky_compass.json` item definition](../../TotalConversionMod/content/items/sky_compass.json)
- [`sky_stone.png`](../../TotalConversionMod/content/textures/sky_stone.png)

Model and texture paths are kept inside their defining content root. A missing resource produces an attributed
diagnostic instead of silently resolving to an unrelated mod.

## Programmatic content registry

`context.Content` can add another content root or expose a namespaced object to other code during configuration:

```csharp
context.Content.AddContentRoot("compat-content");
context.Content.Register(new ResourceId("author.mymod:settings"), settings);
```

Use `IModServiceRegistry` rather than arbitrary content objects for a versioned inter-mod public contract.

[Next: gameplay and data](03-gameplay-and-data.md)
