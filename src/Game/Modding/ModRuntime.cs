using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

public sealed record RegisteredWorldGenerationHook(
    string ModId,
    WorldGenerationStage Stage,
    ResourceId Id,
    int Priority,
    IChunkGenerationHook Hook);

public sealed record RegisteredWorldGenerator(
    string ModId,
    ResourceId Id,
    IModWorldGenerator Generator);

/// <summary>A mod overlay together with the ownership metadata used for diagnostics.</summary>
public sealed record RegisteredModOverlay(
    string ModId,
    ResourceId Id,
    int Priority,
    IModOverlay Overlay)
{
    /// <summary>Invokes the overlay while preserving its mod and resource ID on failure.</summary>
    public void Draw(IModUiCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        try
        {
            Overlay.Draw(canvas);
        }
        catch (Exception exception)
        {
            throw new ModOverlayDrawException(ModId, Id, exception);
        }
    }
}

public sealed class ModOverlayDrawException : Exception
{
    public ModOverlayDrawException(string modId, ResourceId overlayId, Exception innerException)
        : base($"Mod '{modId}' overlay '{overlayId}' failed while drawing.", innerException)
    {
        ModId = modId;
        OverlayId = overlayId;
    }

    public string ModId { get; }

    public ResourceId OverlayId { get; }
}

public sealed class ModContentRegistry
{
    private readonly Dictionary<ResourceId, (string Owner, object Value)> values = new();
    private readonly List<ModContentSource> sources = new();

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<ModContentSource> Sources => sources.ToArray();

    internal IModContent ForMod(ModDescriptor descriptor) => new View(this, descriptor);

    internal void AddSource(ModContentSource source)
    {
        EnsureMutable();
        if (!sources.Contains(source)) sources.Add(source);
    }

    internal void Freeze() => IsFrozen = true;

    private void Add<T>(string owner, ResourceId id, T value) where T : notnull
    {
        EnsureMutable();
        if (!id.Value.StartsWith(owner + ":", StringComparison.Ordinal))
            throw new ModHostException($"Mod '{owner}' may only register resource IDs in its own namespace.");
        if (!values.TryAdd(id, (owner, value)))
            throw new ModHostException($"Resource '{id}' is already registered by '{values[id].Owner}'.");
    }

    private bool TryGet<T>(ResourceId id, out T? value) where T : notnull
    {
        if (values.TryGetValue(id, out var registration) && registration.Value is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    private void EnsureMutable()
    {
        if (IsFrozen) throw new InvalidOperationException("Mod content registration is frozen.");
    }

    private sealed class View : IModContent
    {
        private readonly ModContentRegistry registry;
        private readonly ModDescriptor descriptor;

        public View(ModContentRegistry registry, ModDescriptor descriptor)
        {
            this.registry = registry;
            this.descriptor = descriptor;
        }

        public IReadOnlyCollection<ResourceId> RegisteredIds => registry.values
            .Where(pair => pair.Value.Owner == descriptor.Id)
            .Select(pair => pair.Key)
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToArray();

        public void AddContentRoot(string path)
        {
            string root = ModPaths.ResolveInside(descriptor.Directory, path);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            registry.AddSource(new ModContentSource(descriptor.Id, root));
        }

        public void Register<T>(ResourceId id, T value) where T : notnull => registry.Add(descriptor.Id, id, value);

        public bool TryGet<T>(ResourceId id, out T? value) where T : notnull => registry.TryGet(id, out value);
    }
}

public sealed class ModWorldGenerationRegistry : IWorldGenerationRegistry
{
    private readonly List<RegisteredWorldGenerationHook> hooks = new();
    private RegisteredWorldGenerator? generator;

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<RegisteredWorldGenerationHook> Hooks => hooks
        .OrderBy(hook => hook.Stage)
        .ThenBy(hook => hook.Priority)
        .ThenBy(hook => hook.Id.Value, StringComparer.Ordinal)
        .ToArray();

    public RegisteredWorldGenerator? Generator => generator;

    internal IWorldGenerationRegistry ForMod(string modId) => new View(this, modId);

    public void Register(WorldGenerationStage stage, ResourceId id, int priority, IChunkGenerationHook hook)
    {
        throw new InvalidOperationException("Use the mod-scoped registry supplied through IModContext.");
    }

    public void SetBaseGenerator(ResourceId id, IModWorldGenerator worldGenerator)
    {
        throw new InvalidOperationException("Use the mod-scoped registry supplied through IModContext.");
    }

    internal void Freeze() => IsFrozen = true;

    private void Register(string modId, WorldGenerationStage stage, ResourceId id, int priority, IChunkGenerationHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (IsFrozen) throw new InvalidOperationException("World generation registration is frozen.");
        if (!id.Value.StartsWith(modId + ":", StringComparison.Ordinal))
            throw new ModHostException($"Mod '{modId}' may only register world generation hooks in its own namespace.");
        if (hooks.Any(existing => existing.Id == id)) throw new ModHostException($"World generation hook '{id}' is already registered.");
        hooks.Add(new RegisteredWorldGenerationHook(modId, stage, id, priority, hook));
    }

    private void SetBaseGenerator(string modId, ResourceId id, IModWorldGenerator worldGenerator)
    {
        ArgumentNullException.ThrowIfNull(worldGenerator);
        if (IsFrozen) throw new InvalidOperationException("World generation registration is frozen.");
        if (!id.Value.StartsWith(modId + ":", StringComparison.Ordinal))
            throw new ModHostException($"Mod '{modId}' may only register world generators in its own namespace.");
        if (generator is not null)
            throw new ModHostException(
                $"World generator '{generator.Id}' from mod '{generator.ModId}' is already active; "
                + $"'{id}' from '{modId}' cannot replace it implicitly.");
        generator = new RegisteredWorldGenerator(modId, id, worldGenerator);
    }

    private sealed class View : IWorldGenerationRegistry
    {
        private readonly ModWorldGenerationRegistry registry;
        private readonly string modId;

        public View(ModWorldGenerationRegistry registry, string modId)
        {
            this.registry = registry;
            this.modId = modId;
        }

        public void Register(WorldGenerationStage stage, ResourceId id, int priority, IChunkGenerationHook hook) =>
            registry.Register(modId, stage, id, priority, hook);

        public void SetBaseGenerator(ResourceId id, IModWorldGenerator generator) =>
            registry.SetBaseGenerator(modId, id, generator);
    }
}

public sealed class ModUiRegistry : IModUiRegistry
{
    private readonly List<RegisteredModOverlay> overlays = new();
    private readonly ModUiScreenHost screens;
    private IReadOnlyList<RegisteredModOverlay>? frozenOverlays;

    public ModUiRegistry() : this(new ModUiScreenHost()) { }

    public ModUiRegistry(ModUiScreenHost screens) =>
        this.screens = screens ?? throw new ArgumentNullException(nameof(screens));

    public bool IsFrozen { get; private set; }

    /// <summary>A stable snapshot ordered by priority and resource ID.</summary>
    public IReadOnlyList<RegisteredModOverlay> Overlays =>
        frozenOverlays ?? Array.AsReadOnly(OrderedOverlays());

    internal IModUiRegistry ForMod(string modId) =>
        new View(this, modId, screens.ForMod(modId));

    public void Register(ResourceId id, int priority, IModOverlay overlay)
    {
        throw new InvalidOperationException("Use the mod-scoped UI registry supplied through IModContext.");
    }

    /// <summary>Draws all overlays in their deterministic registration order.</summary>
    public void Draw(IModUiCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        foreach (RegisteredModOverlay overlay in Overlays)
        {
            overlay.Draw(canvas);
        }
    }

    internal void Freeze()
    {
        if (IsFrozen)
        {
            return;
        }

        frozenOverlays = Array.AsReadOnly(OrderedOverlays());
        IsFrozen = true;
    }

    private void Register(string modId, ResourceId id, int priority, IModOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (IsFrozen) throw new InvalidOperationException("Mod UI registration is frozen.");
        if (!id.Value.StartsWith(modId + ":", StringComparison.Ordinal))
            throw new ModHostException($"Mod '{modId}' may only register UI overlays in its own namespace.");
        if (overlays.Any(existing => existing.Id == id))
            throw new ModHostException($"UI overlay '{id}' is already registered.");
        overlays.Add(new RegisteredModOverlay(modId, id, priority, overlay));
    }

    private RegisteredModOverlay[] OrderedOverlays() => overlays
        .OrderBy(overlay => overlay.Priority)
        .ThenBy(overlay => overlay.Id.Value, StringComparer.Ordinal)
        .ToArray();

    private sealed class View : IModUiRegistry
    {
        private readonly ModUiRegistry registry;
        private readonly string modId;
        private readonly ModUiScreenHost.OwnerView screens;

        public View(
            ModUiRegistry registry,
            string modId,
            ModUiScreenHost.OwnerView screens)
        {
            this.registry = registry;
            this.modId = modId;
            this.screens = screens;
        }

        public void Register(ResourceId id, int priority, IModOverlay overlay) =>
            registry.Register(modId, id, priority, overlay);

        public void RegisterScreen(ResourceId id, IModScreen screen) =>
            screens.RegisterScreen(id, screen);

        public bool OpenScreen(ResourceId id) => screens.OpenScreen(id);

        public bool CloseScreen() => screens.CloseScreen();

        public ResourceId? OpenScreenId => screens.OpenScreenId;
    }
}

public sealed class ModEventHub
{
    private readonly List<Action> started = new();
    private readonly List<Action> stopping = new();
    private readonly List<Action<IGameTickContext>> tick = new();
    private readonly List<BlockActionRegistration> blockActions = new();
    private readonly List<WorldLifecycleRegistration> worldOpened = new();
    private readonly List<WorldLifecycleRegistration> worldSaving = new();
    private readonly List<WorldLifecycleRegistration> worldClosing = new();

    internal IModEvents ForMod(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        return new View(this, modId);
    }

    public void RaiseStarted() => InvokeSnapshot(started);

    public void RaiseStopping() => InvokeSnapshot(stopping);

    public void RaiseTick(IGameTickContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (Action<IGameTickContext> callback in tick.ToArray()) callback(context);
    }

    public void RaiseWorldOpened(Func<string, IModData> dataForMod) =>
        InvokeWorldSnapshot(worldOpened, dataForMod);

    public void RaiseWorldSaving(Func<string, IModData> dataForMod) =>
        InvokeWorldSnapshot(worldSaving, dataForMod);

    public void RaiseWorldClosing(Func<string, IModData> dataForMod) =>
        InvokeWorldSnapshot(worldClosing, dataForMod);

    /// <summary>
    /// Dispatches a block action in dependency/configuration order and then subscription order.
    /// Before dispatch stops as soon as a callback cancels the action. After dispatch cannot be
    /// cancelled. A failing callback is wrapped with the owning mod ID and dispatch stops.
    /// </summary>
    public void RaiseBlockAction(ModBlockActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (BlockActionRegistration registration in blockActions.ToArray())
        {
            try
            {
                registration.Callback(context);
            }
            catch (Exception exception)
            {
                throw new ModBlockActionCallbackException(
                    registration.ModId,
                    context.Kind,
                    context.Phase,
                    exception);
            }

            if (context.Phase == ModBlockActionPhase.Before && context.Cancel)
            {
                break;
            }
        }
    }

    private static void InvokeSnapshot(List<Action> callbacks)
    {
        foreach (Action callback in callbacks.ToArray()) callback();
    }

    private static void InvokeWorldSnapshot(
        List<WorldLifecycleRegistration> callbacks,
        Func<string, IModData> dataForMod)
    {
        ArgumentNullException.ThrowIfNull(dataForMod);
        foreach (WorldLifecycleRegistration registration in callbacks.ToArray())
        {
            registration.Callback(new ModWorldLifecycleContext(dataForMod(registration.ModId)));
        }
    }

    private sealed class View : IModEvents
    {
        private readonly ModEventHub hub;
        private readonly string modId;

        public View(ModEventHub hub, string modId)
        {
            this.hub = hub;
            this.modId = modId;
        }

        public IModSubscription OnStarted(Action callback) => Add(hub.started, callback);
        public IModSubscription OnStopping(Action callback) => Add(hub.stopping, callback);
        public IModSubscription OnTick(Action<IGameTickContext> callback) => Add(hub.tick, callback);

        public IModSubscription OnBlockAction(Action<IModBlockActionContext> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var registration = new BlockActionRegistration(modId, callback);
            hub.blockActions.Add(registration);
            return new Subscription(() => hub.blockActions.Remove(registration));
        }

        public IModSubscription OnWorldOpened(Action<IModWorldLifecycleContext> callback) =>
            AddWorld(hub.worldOpened, callback);

        public IModSubscription OnWorldSaving(Action<IModWorldLifecycleContext> callback) =>
            AddWorld(hub.worldSaving, callback);

        public IModSubscription OnWorldClosing(Action<IModWorldLifecycleContext> callback) =>
            AddWorld(hub.worldClosing, callback);

        private IModSubscription AddWorld(
            List<WorldLifecycleRegistration> list,
            Action<IModWorldLifecycleContext> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var registration = new WorldLifecycleRegistration(modId, callback);
            list.Add(registration);
            return new Subscription(() => list.Remove(registration));
        }

        private static IModSubscription Add<T>(List<T> list, T callback) where T : Delegate
        {
            ArgumentNullException.ThrowIfNull(callback);
            list.Add(callback);
            return new Subscription(() => list.Remove(callback));
        }
    }

    private sealed record BlockActionRegistration(
        string ModId,
        Action<IModBlockActionContext> Callback);

    private sealed record WorldLifecycleRegistration(
        string ModId,
        Action<IModWorldLifecycleContext> Callback);

    private sealed class Subscription : IModSubscription
    {
        private Action? unsubscribe;
        public Subscription(Action unsubscribe) => this.unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref unsubscribe, null)?.Invoke();
    }
}

public sealed class ModWorldLifecycleContext : IModWorldLifecycleContext
{
    public ModWorldLifecycleContext(IModData data) =>
        Data = data ?? throw new ArgumentNullException(nameof(data));

    public IModData Data { get; }
}

/// <summary>
/// Mutable game-side context passed to mod block-action callbacks without exposing game implementation types.
/// </summary>
public sealed class ModBlockActionContext : IModBlockActionContext
{
    private bool cancel;

    public ModBlockActionContext(
        ModBlockActionPhase phase,
        ModBlockActionKind kind,
        int x,
        int y,
        int z,
        ResourceId blockId,
        ResourceId? heldItemId)
    {
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(blockId.Value)) throw new ArgumentException("A block ID is required.", nameof(blockId));
        if (heldItemId is { } held && string.IsNullOrWhiteSpace(held.Value))
            throw new ArgumentException("A held item ID must be valid when supplied.", nameof(heldItemId));

        Phase = phase;
        Kind = kind;
        X = x;
        Y = y;
        Z = z;
        BlockId = blockId;
        HeldItemId = heldItemId;
    }

    public ModBlockActionPhase Phase { get; }
    public ModBlockActionKind Kind { get; }
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public ResourceId BlockId { get; }
    public ResourceId? HeldItemId { get; }

    public bool Cancel
    {
        get => cancel;
        set
        {
            if (Phase != ModBlockActionPhase.Before)
                throw new InvalidOperationException("A completed block action cannot be cancelled.");
            cancel = value;
        }
    }
}

/// <summary>Identifies the mod whose block-action callback failed.</summary>
public sealed class ModBlockActionCallbackException : Exception
{
    public ModBlockActionCallbackException(
        string modId,
        ModBlockActionKind kind,
        ModBlockActionPhase phase,
        Exception innerException)
        : base($"Mod '{modId}' failed during the {phase.ToString().ToLowerInvariant()} {kind.ToString().ToLowerInvariant()} block action.", innerException)
    {
        ModId = modId;
        Kind = kind;
        Phase = phase;
    }

    public string ModId { get; }
    public ModBlockActionKind Kind { get; }
    public ModBlockActionPhase Phase { get; }
}

internal sealed class ModContext : IModContextV2
{
    public ModContext(
        ModDescriptor mod,
        IModGame game,
        IModContent content,
        IWorldGenerationRegistry worldGeneration,
        IModUiRegistry ui,
        IModEvents events,
        IModBehaviors behaviors,
        IModData data,
        IModCapabilityProvider capabilitiesV2,
        IModServiceRegistry interModServices,
        IModEventBus eventBus,
        IModEntityPlatform entities,
        IModStackPlatform itemStacks,
        IModContainerPlatform containers,
        IModWorldDefinitionRegistry worldDefinitions,
        IModClientPlatform client,
        IModNetworkRegistry network,
        IModSerializationRegistry serialization,
        IServiceProvider services,
        IModLogger logger)
    {
        Mod = mod;
        Game = game;
        Content = content;
        WorldGeneration = worldGeneration;
        Ui = ui;
        Events = events;
        Behaviors = behaviors;
        Data = data;
        CapabilitiesV2 = capabilitiesV2;
        InterModServices = interModServices;
        EventBus = eventBus;
        Entities = entities;
        ItemStacks = itemStacks;
        Containers = containers;
        WorldDefinitions = worldDefinitions;
        Client = client;
        Network = network;
        Serialization = serialization;
        Services = services;
        Logger = logger;
    }

    public ModDescriptor Mod { get; }
    public IModGame Game { get; }
    public IModContent Content { get; }
    public IWorldGenerationRegistry WorldGeneration { get; }
    public IModUiRegistry Ui { get; }
    public IModEvents Events { get; }
    public IModBehaviors Behaviors { get; }
    public IModData Data { get; }
    public IModCapabilityProvider CapabilitiesV2 { get; }
    public IModServiceRegistry InterModServices { get; }
    public IModEventBus EventBus { get; }
    public IModEntityPlatform Entities { get; }
    public IModStackPlatform ItemStacks { get; }
    public IModContainerPlatform Containers { get; }
    public IModWorldDefinitionRegistry WorldDefinitions { get; }
    public IModClientPlatform Client { get; }
    public IModNetworkRegistry Network { get; }
    public IModSerializationRegistry Serialization { get; }
    public IServiceProvider Services { get; }
    public IModLogger Logger { get; }
}

/// <summary>Inert default supplied when a host has not been connected to a running game.</summary>
internal sealed class NullModGame : IModGame, IModPlayer, IModInventory
{
    public static readonly NullModGame Instance = new();

    private NullModGame() { }

    public bool IsWorldLoaded => false;
    public IModWorld? World => null;
    public IModPlayer Player => this;
    public IModInventory Inventory => this;
    public ModVector3 Position => default;
    public float Health => 0f;
    public bool IsDead => true;

    public bool Teleport(ModVector3 position) => false;
    public int CountOf(ResourceId itemId) => 0;
    public int Add(ResourceId itemId, int count) => Math.Max(0, count);
    public bool Remove(ResourceId itemId, int count) => false;
}

internal sealed class EmptyServiceProvider : IServiceProvider
{
    public static readonly EmptyServiceProvider Instance = new();
    public object? GetService(Type serviceType) => null;
}

public sealed class NullModLogger : IModLogger
{
    public static readonly NullModLogger Instance = new();
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
