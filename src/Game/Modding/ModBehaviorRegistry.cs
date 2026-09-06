using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>Identifies the public behavior callback that failed.</summary>
public enum ModBehaviorCallbackKind
{
    ItemUse,
    BlockInteraction,
    BlockLifecycle,
    ScheduledBlock,
    IntervalBlock,
}

/// <summary>Preserves ownership and registration information when mod behavior fails.</summary>
public sealed class ModBehaviorCallbackException : Exception
{
    public ModBehaviorCallbackException(
        string modId,
        ResourceId registrationId,
        ModBehaviorCallbackKind callbackKind,
        Exception innerException)
        : base($"Mod '{modId}' behavior '{registrationId}' failed during {callbackKind}.", innerException)
    {
        ModId = modId;
        RegistrationId = registrationId;
        CallbackKind = callbackKind;
    }

    public string ModId { get; }

    public ResourceId RegistrationId { get; }

    public ModBehaviorCallbackKind CallbackKind { get; }
}

/// <summary>
/// Game-thread kernel for arbitrary mod-defined item and block mechanics. The registry knows
/// nothing about particular mechanics: it only owns deterministic dispatch and block tick state.
/// </summary>
public sealed class ModBehaviorRegistry
{
    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private readonly HashSet<ResourceId> registrationIds = [];
    private readonly Dictionary<ResourceId, List<ItemUseRegistration>> itemUse = [];
    private readonly Dictionary<ResourceId, List<BlockInteractionRegistration>> blockInteraction = [];
    private readonly Dictionary<ResourceId, List<BlockLifecycleRegistration>> blockLifecycle = [];
    private readonly Dictionary<ResourceId, ScheduledRegistration> scheduledRegistrations = [];
    private readonly Dictionary<ResourceId, List<IntervalRegistration>> intervalRegistrations = [];
    private readonly Dictionary<ScheduledKey, ulong> scheduled = [];
    private readonly Dictionary<ResourceId, HashSet<ModBlockPosition>> activeIntervalPositions = [];
    private IModWorld? world;

    public bool IsFrozen { get; private set; }

    /// <summary>Block IDs for which lifecycle dispatch has observable callbacks.</summary>
    public IReadOnlyCollection<ResourceId> LifecycleTargetBlockIds =>
        blockLifecycle.Keys.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();

    /// <summary>Block IDs whose loaded positions must be activated for interval ticks.</summary>
    public IReadOnlyCollection<ResourceId> IntervalTargetBlockIds =>
        intervalRegistrations.Keys.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();

    internal IModBehaviors ForMod(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        EnsureMainThread();
        return new View(this, modId.Trim().ToLowerInvariant());
    }

    internal void Freeze()
    {
        EnsureMainThread();
        if (IsFrozen)
        {
            return;
        }

        SortValues(itemUse, static registration => registration.Priority, static registration => registration.Id);
        SortValues(blockInteraction, static registration => registration.Priority, static registration => registration.Id);
        SortValues(blockLifecycle, static registration => registration.Priority, static registration => registration.Id);
        SortValues(intervalRegistrations, static registration => registration.Priority, static registration => registration.Id);
        IsFrozen = true;
    }

    /// <summary>Attaches the stable-ID world facade used to validate due block ticks.</summary>
    public void AttachWorld(IModWorld attachedWorld)
    {
        EnsureMainThread();
        world = attachedWorld ?? throw new ArgumentNullException(nameof(attachedWorld));
        currentTick = 0;
        scheduled.Clear();
        activeIntervalPositions.Clear();
    }

    /// <summary>Clears all transient scheduling state for the closing world.</summary>
    public void DetachWorld()
    {
        EnsureMainThread();
        world = null;
        scheduled.Clear();
        activeIntervalPositions.Clear();
    }

    public bool HasLifecycleTarget(ResourceId blockId) => blockLifecycle.ContainsKey(blockId);

    public bool HasIntervalTarget(ResourceId blockId) => intervalRegistrations.ContainsKey(blockId);

    /// <summary>Marks one loaded block position as eligible for registered interval callbacks.</summary>
    public void ActivateIntervalPosition(ResourceId blockId, ModBlockPosition position)
    {
        EnsureMainThread();
        if (world is null || !intervalRegistrations.ContainsKey(blockId))
        {
            return;
        }

        if (!activeIntervalPositions.TryGetValue(blockId, out HashSet<ModBlockPosition>? positions))
        {
            positions = [];
            activeIntervalPositions.Add(blockId, positions);
        }

        positions.Add(position);
    }

    /// <summary>Stops interval callbacks for one unloaded, replaced or removed block position.</summary>
    public void DeactivateIntervalPosition(ResourceId blockId, ModBlockPosition position)
    {
        EnsureMainThread();
        if (!activeIntervalPositions.TryGetValue(blockId, out HashSet<ModBlockPosition>? positions))
        {
            return;
        }

        positions.Remove(position);
        if (positions.Count == 0)
        {
            activeIntervalPositions.Remove(blockId);
        }
    }

    public ModActionResult DispatchItemUse(
        ResourceId itemId,
        ModUseKind use,
        ModInputPhase phase,
        ModHit? hit,
        ulong gameTick,
        ModInventoryStackSnapshot? stack = null)
    {
        EnsureMainThread();
        currentTick = gameTick;
        if (!itemUse.TryGetValue(itemId, out List<ItemUseRegistration>? registrations))
        {
            return ModActionResult.Pass;
        }

        var context = new ItemUseContext(itemId, stack, use, phase, hit, gameTick);
        foreach (ItemUseRegistration registration in registrations)
        {
            ModActionResult result;
            try
            {
                result = registration.Behavior.OnUse(context);
            }
            catch (Exception exception)
            {
                throw Failure(registration, ModBehaviorCallbackKind.ItemUse, exception);
            }

            if (result != ModActionResult.Pass)
            {
                return result;
            }
        }

        return ModActionResult.Pass;
    }

    public ModActionResult DispatchBlockInteraction(
        ResourceId blockId,
        ModBlockPosition position,
        ResourceId? heldItemId,
        ModUseKind use,
        ModInputPhase phase,
        ModHit hit,
        ulong gameTick,
        ModInventoryStackSnapshot? heldStack = null)
    {
        EnsureMainThread();
        currentTick = gameTick;
        if (!blockInteraction.TryGetValue(blockId, out List<BlockInteractionRegistration>? registrations))
        {
            return ModActionResult.Pass;
        }

        var context = new BlockInteractionContext(
            blockId, position, heldItemId, heldStack, use, phase, hit, gameTick);
        foreach (BlockInteractionRegistration registration in registrations)
        {
            ModActionResult result;
            try
            {
                result = registration.Behavior.OnInteract(context);
            }
            catch (Exception exception)
            {
                throw Failure(registration, ModBehaviorCallbackKind.BlockInteraction, exception);
            }

            if (result != ModActionResult.Pass)
            {
                return result;
            }
        }

        return ModActionResult.Pass;
    }

    /// <summary>
    /// Dispatches lifecycle callbacks and keeps interval activation in sync. Loaded/placed blocks
    /// are activated after callbacks; broken/unloaded blocks are deactivated before callbacks.
    /// </summary>
    public void DispatchBlockLifecycle(
        ModBlockLifecycleKind kind,
        ResourceId blockId,
        ModBlockPosition position,
        ResourceId? causingItemId,
        ulong gameTick)
    {
        EnsureMainThread();
        currentTick = gameTick;
        if (kind is ModBlockLifecycleKind.Broken or ModBlockLifecycleKind.Unloaded)
        {
            DeactivateIntervalPosition(blockId, position);
        }

        if (blockLifecycle.TryGetValue(blockId, out List<BlockLifecycleRegistration>? registrations))
        {
            var context = new BlockLifecycleContext(kind, blockId, position, causingItemId, gameTick);
            foreach (BlockLifecycleRegistration registration in registrations)
            {
                try
                {
                    registration.Behavior.OnBlockLifecycle(context);
                }
                catch (Exception exception)
                {
                    throw Failure(registration, ModBehaviorCallbackKind.BlockLifecycle, exception);
                }
            }
        }

        if (kind is ModBlockLifecycleKind.Placed or ModBlockLifecycleKind.Loaded)
        {
            ActivateIntervalPosition(blockId, position);
        }
    }

    /// <summary>Runs due one-shot and interval block callbacks in deterministic order.</summary>
    public void Tick(ulong gameTick)
    {
        EnsureMainThread();
        currentTick = gameTick;
        if (world is null || (scheduled.Count == 0 && activeIntervalPositions.Count == 0))
        {
            return;
        }

        List<DueCallback> due = CollectDueCallbacks(gameTick);
        if (due.Count == 0)
        {
            return;
        }

        due.Sort(DueCallbackComparer.Instance);
        foreach (DueCallback callback in due)
        {
            ResourceId currentBlock = world.GetBlockId(callback.Position.X, callback.Position.Y, callback.Position.Z);
            if (currentBlock != callback.TargetBlockId)
            {
                DeactivateIntervalPosition(callback.TargetBlockId, callback.Position);
                continue;
            }

            var context = new BlockTickContext(this, callback, gameTick);
            try
            {
                callback.Behavior.OnScheduledTick(context);
            }
            catch (Exception exception)
            {
                throw new ModBehaviorCallbackException(
                    callback.ModId,
                    callback.Id,
                    callback.Kind,
                    exception);
            }
        }
    }

    private List<DueCallback> CollectDueCallbacks(ulong gameTick)
    {
        var due = new List<DueCallback>();
        foreach ((ScheduledKey key, ulong dueTick) in scheduled.ToArray())
        {
            if (dueTick > gameTick)
            {
                continue;
            }

            scheduled.Remove(key);
            ScheduledRegistration registration = scheduledRegistrations[key.RegistrationId];
            due.Add(new DueCallback(
                registration.ModId,
                registration.Id,
                registration.TargetId,
                registration.Priority,
                key.Position,
                ModBehaviorCallbackKind.ScheduledBlock,
                registration.Behavior));
        }

        foreach ((ResourceId targetId, HashSet<ModBlockPosition> positions) in activeIntervalPositions.ToArray())
        {
            foreach (IntervalRegistration registration in intervalRegistrations[targetId])
            {
                if (gameTick % registration.IntervalTicks != 0)
                {
                    continue;
                }

                foreach (ModBlockPosition position in positions)
                {
                    due.Add(new DueCallback(
                        registration.ModId,
                        registration.Id,
                        registration.TargetId,
                        registration.Priority,
                        position,
                        ModBehaviorCallbackKind.IntervalBlock,
                        registration.Behavior));
                }
            }
        }

        return due;
    }

    private void RegisterItemUse(
        string modId,
        ResourceId registrationId,
        ResourceId targetItemId,
        int priority,
        IModItemUseBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        Register(new ItemUseRegistration(modId, registrationId, targetItemId, priority, behavior));
        Add(itemUse, targetItemId, new ItemUseRegistration(modId, registrationId, targetItemId, priority, behavior));
    }

    private void RegisterBlockInteraction(
        string modId,
        ResourceId registrationId,
        ResourceId targetBlockId,
        int priority,
        IModBlockInteractionBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        Register(new BlockInteractionRegistration(modId, registrationId, targetBlockId, priority, behavior));
        Add(blockInteraction, targetBlockId, new BlockInteractionRegistration(modId, registrationId, targetBlockId, priority, behavior));
    }

    private void RegisterBlockLifecycle(
        string modId,
        ResourceId registrationId,
        ResourceId targetBlockId,
        int priority,
        IModBlockLifecycleBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        Register(new BlockLifecycleRegistration(modId, registrationId, targetBlockId, priority, behavior));
        Add(blockLifecycle, targetBlockId, new BlockLifecycleRegistration(modId, registrationId, targetBlockId, priority, behavior));
    }

    private void RegisterScheduledBlockBehavior(
        string modId,
        ResourceId registrationId,
        ResourceId targetBlockId,
        int priority,
        IModBlockScheduledBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        var registration = new ScheduledRegistration(modId, registrationId, targetBlockId, priority, behavior);
        Register(registration);
        scheduledRegistrations.Add(registrationId, registration);
    }

    private void RegisterIntervalBlockBehavior(
        string modId,
        ResourceId registrationId,
        ResourceId targetBlockId,
        int priority,
        ulong intervalTicks,
        IModBlockScheduledBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        ArgumentOutOfRangeException.ThrowIfZero(intervalTicks);
        var registration = new IntervalRegistration(
            modId,
            registrationId,
            targetBlockId,
            priority,
            behavior,
            intervalTicks);
        Register(registration);
        Add(intervalRegistrations, targetBlockId, registration);
    }

    private void Register(Registration registration)
    {
        EnsureMainThread();
        if (IsFrozen)
        {
            throw new InvalidOperationException("Mod behavior registration is frozen.");
        }

        EnsureOwnedId(registration.ModId, registration.Id);
        if (!registrationIds.Add(registration.Id))
        {
            throw new ModHostException($"Mod behavior registration '{registration.Id}' already exists.");
        }
    }

    private bool ScheduleBlock(string modId, ResourceId registrationId, ModBlockPosition position, ulong delayTicks)
    {
        EnsureMainThread();
        if (world is null ||
            !scheduledRegistrations.TryGetValue(registrationId, out ScheduledRegistration? registration) ||
            !string.Equals(registration.ModId, modId, StringComparison.Ordinal))
        {
            return false;
        }

        scheduled[new ScheduledKey(registrationId, position)] = SaturatingAdd(currentTick, delayTicks);
        return true;
    }

    private bool CancelScheduledBlock(string modId, ResourceId registrationId, ModBlockPosition position)
    {
        EnsureMainThread();
        return scheduledRegistrations.TryGetValue(registrationId, out ScheduledRegistration? registration) &&
               string.Equals(registration.ModId, modId, StringComparison.Ordinal) &&
               scheduled.Remove(new ScheduledKey(registrationId, position));
    }

    private ulong currentTick;

    private bool ScheduleAgain(DueCallback callback, ulong delayTicks)
    {
        if (callback.Kind != ModBehaviorCallbackKind.ScheduledBlock)
        {
            return false;
        }

        scheduled[new ScheduledKey(callback.Id, callback.Position)] = SaturatingAdd(currentTick, delayTicks);
        return true;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static void EnsureOwnedId(string modId, ResourceId id)
    {
        int separator = id.Value.IndexOf(':');
        if (!id.Value.AsSpan(0, separator).Equals(modId, StringComparison.Ordinal))
        {
            throw new ModHostException($"Mod '{modId}' may only register behavior IDs in its own namespace.");
        }
    }

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
        {
            throw new InvalidOperationException("Mod behavior operations must run on the main game thread.");
        }
    }

    private static void Add<T>(Dictionary<ResourceId, List<T>> values, ResourceId targetId, T registration)
    {
        if (!values.TryGetValue(targetId, out List<T>? registrations))
        {
            registrations = [];
            values.Add(targetId, registrations);
        }

        registrations.Add(registration);
    }

    private static void SortValues<T>(
        Dictionary<ResourceId, List<T>> values,
        Func<T, int> priority,
        Func<T, ResourceId> id)
    {
        foreach (List<T> registrations in values.Values)
        {
            registrations.Sort((left, right) =>
            {
                int comparison = priority(left).CompareTo(priority(right));
                return comparison != 0
                    ? comparison
                    : StringComparer.Ordinal.Compare(id(left).Value, id(right).Value);
            });
        }
    }

    private static ModBehaviorCallbackException Failure(
        Registration registration,
        ModBehaviorCallbackKind kind,
        Exception exception) => new(registration.ModId, registration.Id, kind, exception);

    private sealed class View : IModBehaviors
    {
        private readonly ModBehaviorRegistry registry;
        private readonly string modId;

        public View(ModBehaviorRegistry registry, string modId)
        {
            this.registry = registry;
            this.modId = modId;
        }

        public void RegisterItemUse(ResourceId registrationId, ResourceId targetItemId, int priority, IModItemUseBehavior behavior) =>
            registry.RegisterItemUse(modId, registrationId, targetItemId, priority, behavior);

        public void RegisterBlockInteraction(ResourceId registrationId, ResourceId targetBlockId, int priority, IModBlockInteractionBehavior behavior) =>
            registry.RegisterBlockInteraction(modId, registrationId, targetBlockId, priority, behavior);

        public void RegisterBlockLifecycle(ResourceId registrationId, ResourceId targetBlockId, int priority, IModBlockLifecycleBehavior behavior) =>
            registry.RegisterBlockLifecycle(modId, registrationId, targetBlockId, priority, behavior);

        public void RegisterScheduledBlockBehavior(ResourceId registrationId, ResourceId targetBlockId, int priority, IModBlockScheduledBehavior behavior) =>
            registry.RegisterScheduledBlockBehavior(modId, registrationId, targetBlockId, priority, behavior);

        public void RegisterIntervalBlockBehavior(ResourceId registrationId, ResourceId targetBlockId, int priority, ulong intervalTicks, IModBlockScheduledBehavior behavior) =>
            registry.RegisterIntervalBlockBehavior(modId, registrationId, targetBlockId, priority, intervalTicks, behavior);

        public bool ScheduleBlock(ResourceId registrationId, ModBlockPosition position, ulong delayTicks) =>
            registry.ScheduleBlock(modId, registrationId, position, delayTicks);

        public bool CancelScheduledBlock(ResourceId registrationId, ModBlockPosition position) =>
            registry.CancelScheduledBlock(modId, registrationId, position);
    }

    private abstract record Registration(string ModId, ResourceId Id, ResourceId TargetId, int Priority);

    private sealed record ItemUseRegistration(
        string ModId,
        ResourceId Id,
        ResourceId TargetId,
        int Priority,
        IModItemUseBehavior Behavior) : Registration(ModId, Id, TargetId, Priority);

    private sealed record BlockInteractionRegistration(
        string ModId,
        ResourceId Id,
        ResourceId TargetId,
        int Priority,
        IModBlockInteractionBehavior Behavior) : Registration(ModId, Id, TargetId, Priority);

    private sealed record BlockLifecycleRegistration(
        string ModId,
        ResourceId Id,
        ResourceId TargetId,
        int Priority,
        IModBlockLifecycleBehavior Behavior) : Registration(ModId, Id, TargetId, Priority);

    private sealed record ScheduledRegistration(
        string ModId,
        ResourceId Id,
        ResourceId TargetId,
        int Priority,
        IModBlockScheduledBehavior Behavior) : Registration(ModId, Id, TargetId, Priority);

    private sealed record IntervalRegistration(
        string ModId,
        ResourceId Id,
        ResourceId TargetId,
        int Priority,
        IModBlockScheduledBehavior Behavior,
        ulong IntervalTicks) : Registration(ModId, Id, TargetId, Priority);

    private readonly record struct ScheduledKey(ResourceId RegistrationId, ModBlockPosition Position);

    private sealed record DueCallback(
        string ModId,
        ResourceId Id,
        ResourceId TargetBlockId,
        int Priority,
        ModBlockPosition Position,
        ModBehaviorCallbackKind Kind,
        IModBlockScheduledBehavior Behavior);

    private sealed class DueCallbackComparer : IComparer<DueCallback>
    {
        public static readonly DueCallbackComparer Instance = new();

        public int Compare(DueCallback? left, DueCallback? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            int comparison = left.Priority.CompareTo(right.Priority);
            if (comparison != 0) return comparison;
            comparison = StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
            if (comparison != 0) return comparison;
            comparison = left.Position.X.CompareTo(right.Position.X);
            if (comparison != 0) return comparison;
            comparison = left.Position.Y.CompareTo(right.Position.Y);
            return comparison != 0 ? comparison : left.Position.Z.CompareTo(right.Position.Z);
        }
    }

    private sealed record ItemUseContext(
        ResourceId ItemId,
        ModInventoryStackSnapshot? Stack,
        ModUseKind Use,
        ModInputPhase Phase,
        ModHit? Hit,
        ulong GameTick) : IModItemUseContext;

    private sealed record BlockInteractionContext(
        ResourceId BlockId,
        ModBlockPosition Position,
        ResourceId? HeldItemId,
        ModInventoryStackSnapshot? HeldStack,
        ModUseKind Use,
        ModInputPhase Phase,
        ModHit Hit,
        ulong GameTick) : IModBlockInteractionContext;

    private sealed record BlockLifecycleContext(
        ModBlockLifecycleKind Kind,
        ResourceId BlockId,
        ModBlockPosition Position,
        ResourceId? CausingItemId,
        ulong GameTick) : IModBlockLifecycleContext;

    private sealed class BlockTickContext : IModBlockTickContext
    {
        private readonly ModBehaviorRegistry registry;
        private readonly DueCallback callback;

        public BlockTickContext(ModBehaviorRegistry registry, DueCallback callback, ulong gameTick)
        {
            this.registry = registry;
            this.callback = callback;
            GameTick = gameTick;
        }

        public ResourceId RegistrationId => callback.Id;
        public ResourceId BlockId => callback.TargetBlockId;
        public ModBlockPosition Position => callback.Position;
        public ulong GameTick { get; }
        public bool ScheduleAgain(ulong delayTicks) => registry.ScheduleAgain(callback, delayTicks);
    }
}
