# 3. Gameplay behavior, stack data, containers, and persistence

## Arbitrary item behavior

JSON defines static item properties. `IModBehaviors` attaches arbitrary game-thread code to stable item and
block IDs:

```csharp
context.Behaviors.RegisterItemUse(
    new ResourceId("author.mymod:wand/use"),
    new ResourceId("author.mymod:wand"),
    priority: 0,
    new WandUse(context.Game));
```

```csharp
internal sealed class WandUse(IModGame game) : IModItemUseBehavior
{
    public ModActionResult OnUse(IModItemUseContext use)
    {
        if (use.Phase != ModInputPhase.Pressed || use.Use != ModUseKind.Secondary)
        {
            return ModActionResult.Pass;
        }

        ModVector3 p = game.Player.Position;
        game.Player.Teleport(new ModVector3(p.X, p.Y + 8f, p.Z));
        return ModActionResult.Handled;
    }
}
```

Return values have explicit dispatch semantics:

- `Pass`: continue to later handlers and then vanilla behavior;
- `Handled`: the mod completed the action and stops dispatch;
- `Denied`: reject the action and stop dispatch.

The same registry supports block interaction, placement/break/load/unload lifecycle, one-shot scheduled block
callbacks, and deterministic interval callbacks. There is no hardcoded machine or magic abstraction.

## Scheduled block logic

Register definitions during `Configure`:

```csharp
context.Behaviors.RegisterScheduledBlockBehavior(
    new ResourceId("author.mymod:altar/pulse"),
    new ResourceId("author.mymod:altar"),
    priority: 0,
    new AltarPulse());
```

Schedule from a game-thread interaction or lifecycle callback:

```csharp
context.Behaviors.ScheduleBlock(
    new ResourceId("author.mymod:altar/pulse"),
    position,
    delayTicks: 20);
```

The callback runs only if the position still contains the registered block. `ScheduleAgain` supports a
self-rescheduling process without scanning every loaded block each frame.

## Live world, player, and inventory

`context.Game` remains stable while `context.Game.World` is attached and detached as saves open and close:

```csharp
if (context.Game.World is { } world)
{
    ResourceId block = world.GetBlockId(10, 64, 10);
    world.TrySetBlockId(10, 64, 10, new ResourceId("author.mymod:blue_stone"));
}

context.Game.Inventory.Add(new ResourceId("author.mymod:gem"), 1);
context.Game.Player.Teleport(new ModVector3(0, 180, 0));
```

Mutations are game-thread-only. World writes require a valid, already-loaded target chunk; a mod write does
not silently generate an unloaded chunk.

## Per-inventory-stack state

An inventory stack snapshot contains item ID, count, damage, slot, and arbitrary serialized components.
Stateful tools can atomically replace the selected slot using optimistic comparison:

```csharp
ModInventoryStackSnapshot? current = use.Stack;
if (current is not null)
{
    var replacement = current with
    {
        Damage = current.Damage + 1,
        Components = updatedComponents
    };

    bool changed = context.Game.Inventory.TryReplaceSlot(
        current.Slot,
        current,
        replacement);
}
```

For persistent logical stacks outside the legacy player inventory, API v2 exposes `IModStackPlatform`.
Register a component with a serializer and split/merge rules:

```csharp
v2.ItemStacks.RegisterComponent(new ModStackComponentDescriptor(
    new ResourceId("author.mymod:charge"),
    new ResourceId("author.mymod:int32"),
    CopyWhenSplit: true,
    RequireEqualToMerge: true));
```

Snapshots are immutable and revisioned. Changes are issued through `IModStackCommandBuffer`; stale expected
revisions are rejected without partial mutation. Unknown serialized component data is preserved across saves.

## Generic containers and menus

```csharp
v2.Containers.RegisterContainerType(new ModContainerTypeDefinition(
    new ResourceId("author.mymod:altar"),
    SlotCount: 4,
    StateSerializerId: new ResourceId("author.mymod:int32")));

v2.Containers.RegisterMenu(
    new ModMenuDefinition(
        new ResourceId("author.mymod:altar"),
        new ResourceId("author.mymod:altar"),
        new ResourceId("author.mymod:altar_screen")),
    new AltarMenuHandler());
```

Menu actions carry an expected session revision and an optional serialized payload. The handler records slot,
state, or close operations into a command buffer. The host commits the complete action atomically only when
the handler returns `Handled`. Local and network-delivered actions use the same validation path.

See [`GameplaySystems.cs`](../../TotalConversionMod/GameplaySystems.cs) for a complete registration and
handler.

## Mod-owned world data

`context.Data` is an owner-scoped opaque byte store. Store global world data or attach data to a block position:

```csharp
ResourceId energy = new("author.mymod:energy");
IModDataContainer? data = context.Data.At(new ModBlockPosition(x, y, z));

Span<byte> bytes = stackalloc byte[sizeof(int)];
BinaryPrimitives.WriteInt32LittleEndian(bytes, 1200);
data?.Set(energy, bytes);
```

Keys must use the owning namespace. Reads return owned immutable snapshots. Writes flush with the next world
save, and unknown mod files are not deleted when their mod is absent.

Use `OnWorldOpened`, `OnWorldSaving`, and `OnWorldClosing` for per-world lifecycle. Do not retain a positional
container after the world closes.

## Transactional runtime save

The host persists ECS entities, logical stacks, generic containers, and the fixed simulation tick as one
checksummed generation. Publication uses an atomic pointer and retains a previous committed generation for
recovery. A torn or corrupt current generation is never partly loaded.

[Next: worlds and entities](04-worlds-and-entities.md)
