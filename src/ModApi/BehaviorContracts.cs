namespace Tesseris.ModApi;

/// <summary>The outcome of a mod-defined interaction.</summary>
public enum ModActionResult
{
    /// <summary>Continue with lower-priority handlers and, if none handles the action, vanilla behavior.</summary>
    Pass,

    /// <summary>The mod completed the action; stop dispatch without treating it as a denial.</summary>
    Handled,

    /// <summary>Reject the action and stop dispatch.</summary>
    Denied
}

/// <summary>The player's primary or secondary use binding, independent of a physical input device.</summary>
public enum ModUseKind
{
    Primary,
    Secondary
}

/// <summary>The temporal phase of a button, key or use binding.</summary>
public enum ModInputPhase
{
    Pressed,
    Held,
    Released
}

/// <summary>An integer block coordinate in the current world.</summary>
public readonly record struct ModBlockPosition(int X, int Y, int Z);

/// <summary>An integer direction, normally a block-face normal with components in the range -1..1.</summary>
public readonly record struct ModInt3(int X, int Y, int Z);

/// <summary>
/// Renderer- and physics-independent information about a world hit. <see cref="Point"/> is the
/// world-space hit point and <see cref="Normal"/> identifies the selected face.
/// </summary>
public readonly record struct ModHit(ModBlockPosition Block, ModVector3 Point, ModInt3 Normal);

/// <summary>
/// Registration and scheduling surface for arbitrary item and block mechanics. Registration IDs
/// must belong to the registering mod's namespace. Lower priorities run first; ties are ordered by
/// registration ID. Registration, scheduling and every callback occur on the game thread.
/// </summary>
public interface IModBehaviors
{
    void RegisterItemUse(
        ResourceId registrationId,
        ResourceId targetItemId,
        int priority,
        IModItemUseBehavior behavior);

    void RegisterBlockInteraction(
        ResourceId registrationId,
        ResourceId targetBlockId,
        int priority,
        IModBlockInteractionBehavior behavior);

    void RegisterBlockLifecycle(
        ResourceId registrationId,
        ResourceId targetBlockId,
        int priority,
        IModBlockLifecycleBehavior behavior);

    /// <summary>
    /// Registers a one-shot scheduled callback. It runs only after <see cref="ScheduleBlock"/> is
    /// called and only if the target position still contains <paramref name="targetBlockId"/>.
    /// </summary>
    void RegisterScheduledBlockBehavior(
        ResourceId registrationId,
        ResourceId targetBlockId,
        int priority,
        IModBlockScheduledBehavior behavior);

    /// <summary>
    /// Registers a callback for loaded instances of a block at a deterministic tick interval.
    /// The host may spread positions across ticks, but the same world seed and actions must produce
    /// the same callback order.
    /// </summary>
    void RegisterIntervalBlockBehavior(
        ResourceId registrationId,
        ResourceId targetBlockId,
        int priority,
        ulong intervalTicks,
        IModBlockScheduledBehavior behavior);

    /// <summary>
    /// Schedules a registered one-shot block callback. Re-scheduling the same registration and
    /// position replaces its due tick. Returns false when no world is open or the registration is unknown.
    /// </summary>
    bool ScheduleBlock(ResourceId registrationId, ModBlockPosition position, ulong delayTicks);

    bool CancelScheduledBlock(ResourceId registrationId, ModBlockPosition position);
}

public interface IModItemUseBehavior
{
    ModActionResult OnUse(IModItemUseContext context);
}

public interface IModBlockInteractionBehavior
{
    ModActionResult OnInteract(IModBlockInteractionContext context);
}

public interface IModBlockLifecycleBehavior
{
    void OnBlockLifecycle(IModBlockLifecycleContext context);
}

public interface IModBlockScheduledBehavior
{
    void OnScheduledTick(IModBlockTickContext context);
}

/// <summary>A short-lived context valid only for the duration of its game-thread callback.</summary>
public interface IModItemUseContext
{
    ResourceId ItemId { get; }

    ModInventoryStackSnapshot? Stack => null;

    ModUseKind Use { get; }

    ModInputPhase Phase { get; }

    ModHit? Hit { get; }

    ulong GameTick { get; }
}

/// <summary>A short-lived context valid only for the duration of its game-thread callback.</summary>
public interface IModBlockInteractionContext
{
    ResourceId BlockId { get; }

    ModBlockPosition Position { get; }

    ResourceId? HeldItemId { get; }

    ModInventoryStackSnapshot? HeldStack => null;

    ModUseKind Use { get; }

    ModInputPhase Phase { get; }

    ModHit Hit { get; }

    ulong GameTick { get; }
}

public enum ModBlockLifecycleKind
{
    Placed,
    Breaking,
    Broken,
    Loaded,
    Unloaded
}

/// <summary>A short-lived lifecycle context dispatched deterministically on the game thread.</summary>
public interface IModBlockLifecycleContext
{
    ModBlockLifecycleKind Kind { get; }

    ResourceId BlockId { get; }

    ModBlockPosition Position { get; }

    ResourceId? CausingItemId { get; }

    ulong GameTick { get; }
}

/// <summary>A short-lived deterministic tick context dispatched on the game thread.</summary>
public interface IModBlockTickContext
{
    ResourceId RegistrationId { get; }

    ResourceId BlockId { get; }

    ModBlockPosition Position { get; }

    ulong GameTick { get; }

    /// <summary>Schedules this registration again for the same position.</summary>
    bool ScheduleAgain(ulong delayTicks);
}
