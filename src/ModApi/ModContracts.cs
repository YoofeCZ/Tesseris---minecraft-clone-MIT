namespace Tesseris.ModApi;

/// <summary>The entry point implemented by a managed Tesseris mod.</summary>
public interface IMod
{
    /// <summary>
    /// Registers content, generation hooks and event callbacks. This is called once, in dependency order,
    /// before the game's registries are frozen.
    /// </summary>
    void Configure(IModContext context);
}

public interface IModContext
{
    ModDescriptor Mod { get; }

    /// <summary>Live, game-independent access to the current world, player and inventory.</summary>
    IModGame Game { get; }

    IModContent Content { get; }

    IWorldGenerationRegistry WorldGeneration { get; }

    /// <summary>Registers renderer-independent overlays owned by this mod.</summary>
    IModUiRegistry Ui { get; }

    IModEvents Events { get; }

    /// <summary>
    /// Registers arbitrary item and block mechanics. Registrations and callbacks are handled on the
    /// game thread unless a future API explicitly documents otherwise.
    /// </summary>
    IModBehaviors Behaviors => throw new NotSupportedException("This mod host does not expose behavior registrations.");

    /// <summary>
    /// Stable access to opaque, namespaced data stored with the currently open world. The service
    /// object remains valid while worlds are opened and closed; its containers do not.
    /// </summary>
    IModData Data => throw new NotSupportedException("This mod host does not expose persistent mod data.");

    IServiceProvider Services { get; }

    IModLogger Logger { get; }
}

/// <summary>Data and asset registrations owned by one mod.</summary>
public interface IModContent
{
    /// <summary>Adds another asset root. Relative paths are resolved inside the mod directory.</summary>
    void AddContentRoot(string path);

    void Register<T>(ResourceId id, T value) where T : notnull;

    bool TryGet<T>(ResourceId id, out T? value) where T : notnull;

    IReadOnlyCollection<ResourceId> RegisteredIds { get; }
}

public interface IModLogger
{
    void Info(string message);

    void Warning(string message);

    void Error(string message, Exception? exception = null);
}

public interface IGameTickContext
{
    ulong Tick { get; }

    TimeSpan Delta { get; }
}

public interface IModEvents
{
    IModSubscription OnStarted(Action callback);

    IModSubscription OnStopping(Action callback);

    IModSubscription OnTick(Action<IGameTickContext> callback);

    /// <summary>
    /// Observes ordinary player block break and place actions. Before callbacks run in subscription
    /// order and may cancel an action; cancellation stops dispatch to later callbacks. After callbacks
    /// are notifications for actions that actually succeeded and cannot cancel them.
    /// </summary>
    IModSubscription OnBlockAction(Action<IModBlockActionContext> callback);

    /// <summary>Runs on the game thread after a world and its mod data have been opened.</summary>
    IModSubscription OnWorldOpened(Action<IModWorldLifecycleContext> callback)
        => throw new NotSupportedException("This mod host does not expose world lifecycle events.");

    /// <summary>
    /// Runs deterministically on the game thread before the world's mod data is flushed to storage.
    /// A callback must not retain the supplied context.
    /// </summary>
    IModSubscription OnWorldSaving(Action<IModWorldLifecycleContext> callback)
        => throw new NotSupportedException("This mod host does not expose world lifecycle events.");

    /// <summary>Runs on the game thread immediately before the current world is detached.</summary>
    IModSubscription OnWorldClosing(Action<IModWorldLifecycleContext> callback)
        => throw new NotSupportedException("This mod host does not expose world lifecycle events.");
}

public interface IModSubscription : IDisposable;

/// <summary>
/// Extensible loading boundary. Custom loaders can create an <see cref="IMod"/> instance or expose
/// content-only sources without depending on Tesseris.Game internals.
/// </summary>
public interface IModLoader
{
    string Id { get; }

    bool CanLoad(ModDescriptor descriptor);

    IModLoadResult Load(ModDescriptor descriptor, IModLoadContext context);
}

public interface IModLoadContext
{
    IServiceProvider Services { get; }

    IModLogger Logger { get; }
}

/// <summary>
/// Host-owned immutable loader context. It lives in the contract assembly so it keeps a single
/// type identity even when the game itself is loaded in an isolated assembly context.
/// </summary>
public sealed class ModLoadContext : IModLoadContext
{
    public ModLoadContext(IServiceProvider services, IModLogger logger)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IServiceProvider Services { get; }
    public IModLogger Logger { get; }
}

public interface IModLoadResult : IDisposable
{
    IMod? Instance { get; }

    IReadOnlyList<ModContentSource> ContentSources { get; }
}
