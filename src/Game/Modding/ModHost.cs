using Tesseris.Game.Entities;
using Tesseris.Game.Items;
using Tesseris.Loader;
using System.Runtime.CompilerServices;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

public sealed class ModHostOptions
{
    public IServiceProvider? Services { get; init; }
    public IModLogger? Logger { get; init; }
    public IReadOnlyList<IModLoader>? Loaders { get; init; }
    public IModGame? Game { get; init; }
    public ModServiceRegistry? InterModServices { get; init; }
    public bool IsClient { get; init; } = true;
    public ModNetworkSide NetworkSide { get; init; } = ModNetworkSide.Client;
    public string GameVersion { get; init; } = "1.0.0";
    public IModTrustPolicy? TrustPolicy { get; init; }
    public IReadOnlySet<string>? EnabledModIds { get; init; }
}

public sealed record LoadedModInfo(ModDescriptor Descriptor, string LoaderId, bool HasManagedInstance);

public sealed class ModHost : IDisposable
{
    private readonly List<LoadedEntry> entries = new();
    private readonly IServiceProvider services;
    private readonly IModLogger logger;
    private readonly IModGame game;
    private readonly ModServiceRegistry interModServices;
    private readonly bool isClient;
    private bool initialized;
    private bool stoppingRaised;
    private bool worldOpen;
    private bool disposed;

    private ModHost(
        IServiceProvider services,
        IModLogger logger,
        IModGame game,
        ModServiceRegistry interModServices,
        bool isClient,
        ModNetworkSide networkSide)
    {
        this.services = services;
        this.logger = logger;
        this.game = game;
        this.interModServices = interModServices;
        this.isClient = isClient;
        Screens = new ModUiScreenHost();
        Ui = new ModUiRegistry(Screens);
        Client = isClient ? new ModClientPlatform() : ModClientPlatform.CreateDisabled();
        Network = new ModNetworkRegistry(networkSide);
    }

    public IReadOnlyList<LoadedModInfo> LoadedMods => entries
        .Select(entry => new LoadedModInfo(entry.Descriptor, entry.Loader.Id, entry.Result.Instance is not null))
        .ToArray();

    public IReadOnlyList<ModContentSource> ContentSources => Content.Sources;

    public ModContentRegistry Content { get; } = new();

    public ModWorldGenerationRegistry WorldGeneration { get; } = new();

    public ModUiRegistry Ui { get; }

    public ModUiScreenHost Screens { get; }

    public ModBehaviorRegistry Behaviors { get; } = new();

    public ModDataStore Data { get; } = new();

    public ModEventHub Events { get; } = new();

    public ModEventBus EventBus { get; } = new();

    public EntityRegistry Entities { get; } = new();

    public ModStackPlatform ItemStacks { get; } = new();

    public ModContainerPlatform Containers { get; } = new();

    public ModWorldDefinitionRegistry WorldDefinitions { get; } = new();

    public ModClientPlatform Client { get; }

    public ModNetworkRegistry Network { get; }

    public ModSerializationRegistry Serialization { get; } = new();

    public ModSimulationClock SimulationClock { get; } = new();

    public static ModHost DiscoverAndLoad(string modsRoot, ModHostOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsRoot);
        options ??= new ModHostOptions();
        var host = new ModHost(
            options.Services ?? EmptyServiceProvider.Instance,
            options.Logger ?? NullModLogger.Instance,
            options.Game ?? NullModGame.Instance,
            options.InterModServices ?? new ModServiceRegistry(),
            options.IsClient,
            options.NetworkSide);
        try
        {
            host.Load(modsRoot, options.Loaders);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolves schema-v2 packages through the standalone loader and loads their runtime plus side-specific
    /// entrypoints. Unlike <see cref="DiscoverAndLoad"/>, this consumes the v2 dependency/trust model and
    /// retains collectible load contexts until the host shuts down.
    /// </summary>
    public static ModHost DiscoverAndLoadStandalone(string modsRoot, ModHostOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsRoot);
        options ??= new ModHostOptions();
        var host = new ModHost(
            options.Services ?? EmptyServiceProvider.Instance,
            options.Logger ?? NullModLogger.Instance,
            options.Game ?? NullModGame.Instance,
            options.InterModServices ?? new ModServiceRegistry(),
            options.IsClient,
            options.NetworkSide);
        try
        {
            string root = Path.GetFullPath(modsRoot);
            Directory.CreateDirectory(root);
            LoaderCompatibilityOptions compatibility = LoaderCompatibilityOptions.Create(
                options.GameVersion,
                options.TrustPolicy);
            IReadOnlyList<DiscoveredModPackage> discovered = ModManifestDiscovery.Discover(root);
            if (options.EnabledModIds is not null)
            {
                discovered = discovered
                    .Where(package => options.EnabledModIds.Contains(package.Package.Id))
                    .ToArray();
            }
            ModLoadPlan plan = ModDependencyResolver.Resolve(discovered, compatibility);
            ModRuntimeEnvironment environment = options.IsClient
                ? ModRuntimeEnvironment.Client
                : ModRuntimeEnvironment.DedicatedServer;
            host.LoadStandalonePlan(plan, environment, options.Loaders);
            return host;
        }
        catch (Exception exception)
        {
            host.Dispose();
            if (exception is ModHostException) throw;
            throw new ModHostException(
                $"Standalone mod loading failed for '{Path.GetFullPath(modsRoot)}'.",
                exception);
        }
    }

    public void Initialize()
    {
        ThrowIfDisposed();
        if (initialized) throw new InvalidOperationException("The mod host is already initialized.");
        try
        {
            for (int loadIndex = 0; loadIndex < entries.Count; loadIndex++)
            {
                LoadedEntry entry = entries[loadIndex];
                IModServiceRegistry modServices = interModServices.ForOwner(entry.Descriptor.Id);
                IModEventBus eventBus = EventBus.ForMod(entry.Descriptor.Id, loadIndex);
                IModEntityPlatform entities = Entities.ForMod(entry.Descriptor.Id);
                IModStackPlatform itemStacks = ItemStacks.ForMod(entry.Descriptor.Id);
                IModContainerPlatform containers = Containers.ForMod(entry.Descriptor.Id);
                IModWorldDefinitionRegistry worldDefinitions = WorldDefinitions.ForMod(entry.Descriptor.Id);
                IModClientPlatform client = Client.ForMod(entry.Descriptor.Id);
                IModNetworkRegistry network = Network.ForMod(entry.Descriptor.Id);
                IModSerializationRegistry serialization = Serialization.ForMod(entry.Descriptor.Id);
                var capabilities = new List<(ModCapabilityDescriptor, object)>
                {
                    (new ModCapabilityDescriptor(ModCapabilityIds.Services, "2.0.0", typeof(IModServiceRegistry)), modServices),
                    (new ModCapabilityDescriptor(ModCapabilityIds.Events, "2.0.0", typeof(IModEventBus)), eventBus),
                    (new ModCapabilityDescriptor(ModCapabilityIds.Entities, "2.0.0", typeof(IModEntityPlatform)), entities),
                    (new ModCapabilityDescriptor(ModCapabilityIds.ItemStacks, "2.0.0", typeof(IModStackPlatform)), itemStacks),
                    (new ModCapabilityDescriptor(ModCapabilityIds.Containers, "2.0.0", typeof(IModContainerPlatform)), containers),
                    (new ModCapabilityDescriptor(ModCapabilityIds.WorldDefinitions, "2.0.0", typeof(IModWorldDefinitionRegistry)), worldDefinitions),
                    (new ModCapabilityDescriptor(ModCapabilityIds.Network, "2.0.0", typeof(IModNetworkRegistry)), network),
                    (new ModCapabilityDescriptor(ModCapabilityIds.Serialization, "2.0.0", typeof(IModSerializationRegistry)), serialization),
                };
                if (Client.CapabilityDescriptor is { } clientDescriptor)
                {
                    capabilities.Add((clientDescriptor, client));
                }

                entry.Result.Instance?.Configure(new ModContext(
                    entry.Descriptor,
                    game,
                    Content.ForMod(entry.Descriptor),
                    WorldGeneration.ForMod(entry.Descriptor.Id),
                    Ui.ForMod(entry.Descriptor.Id),
                    Events.ForMod(entry.Descriptor.Id),
                    Behaviors.ForMod(entry.Descriptor.Id),
                    Data.ForMod(entry.Descriptor.Id),
                    new ModCapabilityProvider(capabilities),
                    modServices,
                    eventBus,
                    entities,
                    itemStacks,
                    containers,
                    worldDefinitions,
                    client,
                    network,
                    serialization,
                    services,
                    logger));
            }

            initialized = true;
            Events.RaiseStarted();
        }
        catch (Exception exception)
        {
            ShutdownCore();
            throw new ModHostException("A mod failed while it was being configured.", exception);
        }
    }

    public void Freeze()
    {
        ThrowIfDisposed();
        if (!initialized) throw new InvalidOperationException("Initialize the mod host before freezing registrations.");
        Content.Freeze();
        WorldGeneration.Freeze();
        Ui.Freeze();
        Screens.Freeze();
        Behaviors.Freeze();
        Serialization.Freeze();
        Entities.Freeze();
        ItemStacks.Freeze();
        Containers.Freeze();
        WorldDefinitions.Freeze();
        Client.Freeze();
        Network.Freeze();
        EventBus.Freeze();
        EventBus.Publish(new ModLoaderReadyEvent(TesserisLoaderApiInfo.CurrentVersion));
        EventBus.Publish(new ModGameStartingEvent(isClient));
    }

    /// <summary>Attaches a runtime world and, for persistent saves, opens mod-owned data.</summary>
    public void OpenWorld(
        string? worldDirectory,
        ResourceId? presetId = null,
        long worldSeed = 0,
        ulong savedSimulationTick = 0)
    {
        ThrowIfDisposed();
        if (game.World is null)
        {
            throw new InvalidOperationException("Attach the game world before opening it for mods.");
        }
        if (worldOpen)
        {
            throw new InvalidOperationException("Close the current mod world before opening another one.");
        }

        Behaviors.AttachWorld(game.World);
        SimulationClock.Reset(savedSimulationTick);
        if (worldDirectory is not null)
        {
            Data.AttachWorld(worldDirectory);
            Events.RaiseWorldOpened(Data.ForMod);
        }

        EventBus.Publish(new ModWorldOpenedEvent(
            presetId ?? new ResourceId("tesseris:default"),
            worldSeed));
        worldOpen = true;
    }

    public void SaveWorld()
    {
        ThrowIfDisposed();
        if (!worldOpen)
        {
            return;
        }

        EventBus.Publish(new ModWorldSavingEvent(SimulationClock.Tick));
        if (Data.IsWorldOpen)
        {
            Events.RaiseWorldSaving(Data.ForMod);
            Data.SaveWorld();
        }
    }

    public void CloseWorld()
    {
        if (disposed)
        {
            return;
        }
        if (!worldOpen)
        {
            Behaviors.DetachWorld();
            return;
        }

        EventBus.Publish(new ModWorldClosingEvent(SimulationClock.Tick));
        if (Data.IsWorldOpen)
        {
            Events.RaiseWorldClosing(Data.ForMod);
            Data.DetachWorld();
        }

        Behaviors.DetachWorld();
        worldOpen = false;
    }

    /// <summary>Advances deterministic mod simulation independently of the render frame rate.</summary>
    public int AdvanceSimulation(TimeSpan elapsed, Action<ulong, TimeSpan>? simulate = null)
    {
        ThrowIfDisposed();
        return SimulationClock.Advance(elapsed, (tick, delta) =>
        {
            var legacyContext = new GameTickContext(tick, delta);
            Events.RaiseTick(legacyContext);
            EventBus.Publish(new ModSimulationTickEvent(tick, delta));
            Behaviors.Tick(tick);
            simulate?.Invoke(tick, delta);
        });
    }

    public void Shutdown()
    {
        if (disposed) return;
        ShutdownCore();
    }

    /// <summary>
    /// Announces shutdown while managed mod assemblies are still loaded. The game calls this before
    /// saving world state, then drains worker jobs and calls <see cref="Shutdown"/> to unload code.
    /// </summary>
    public void BeginShutdown()
    {
        if (disposed || !initialized || stoppingRaised)
        {
            return;
        }

        try
        {
            Events.RaiseStopping();
        }
        catch (Exception exception)
        {
            logger.Error("A mod failed during shutdown.", exception);
        }

        stoppingRaised = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        ShutdownCore();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private void Load(string modsRoot, IReadOnlyList<IModLoader>? customLoaders)
    {
        string root = Path.GetFullPath(modsRoot);
        Directory.CreateDirectory(root);
        ModDescriptor[] descriptors = new[] { ModManifestReader.FileName, ModManifestReader.LegacyFileName }
            .SelectMany(fileName => Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(ModManifestReader.Read)
            .ToArray();
        IReadOnlyList<ModDescriptor> ordered = ValidateAndOrder(descriptors);

        var loaders = new List<IModLoader>();
        if (customLoaders is not null) loaders.AddRange(customLoaders);
        loaders.Add(new ManagedModLoader());
        loaders.Add(new ContentModLoader());
        if (loaders.Select(loader => loader.Id).Distinct(StringComparer.Ordinal).Count() != loaders.Count)
            throw new ModHostException("Mod loader IDs must be unique.");

        var loadContext = new ModLoadContext(services, logger);
        foreach (ModDescriptor descriptor in ordered)
        {
            IModLoader? loader = loaders.FirstOrDefault(candidate => candidate.CanLoad(descriptor));
            if (loader is null) throw new ModHostException($"No loader accepts mod '{descriptor.Id}'.");
            try
            {
                IModLoadResult result = loader.Load(descriptor, loadContext);
                entries.Add(new LoadedEntry(descriptor, loader, result));
                foreach (ModContentSource source in result.ContentSources) Content.AddSource(source);
            }
            catch (Exception exception) when (exception is not ModHostException)
            {
                throw new ModHostException($"Loader '{loader.Id}' failed to load mod '{descriptor.Id}'.", exception);
            }
        }
    }

    private void LoadStandalonePlan(
        ModLoadPlan plan,
        ModRuntimeEnvironment environment,
        IReadOnlyList<IModLoader>? customLoaders)
    {
        IModLoader[] custom = customLoaders?.ToArray() ?? Array.Empty<IModLoader>();
        if (custom.Select(loader => loader.Id).Distinct(StringComparer.Ordinal).Count() != custom.Length)
            throw new ModHostException("Mod loader IDs must be unique.");
        var runtimeLoader = new StandaloneRuntimeLoader();
        var assemblyLoader = new ModAssemblyLoader();
        foreach (ModLoadPlanEntry planEntry in plan.Entries.OrderBy(entry => entry.LoadIndex))
        {
            ModDescriptor descriptor = StandaloneRuntimeLoader.ConvertDescriptor(planEntry.Package, environment);
            IModLoader? selected = custom.FirstOrDefault(loader => loader.CanLoad(descriptor));
            if (selected is not null)
            {
                LoadWithCustomLoader(selected, descriptor, planEntry.Package);
                continue;
            }

            var singlePackagePlan = new ModLoadPlan(
                Array.AsReadOnly(new[] { planEntry }),
                plan.Fingerprint);
            StandaloneRuntimeLoadHandle loaded = runtimeLoader.Load(
                singlePackagePlan,
                environment,
                assemblyLoader,
                logger);
            StandaloneRuntimePackage package = AssertSinglePackage(planEntry.Package.Id, loaded);
            var standaloneResult = new StandalonePackageResult(loaded, package);
            entries.Add(new LoadedEntry(package.Descriptor, StandalonePackageLoader.Instance, standaloneResult));
            foreach (ModContentSource source in package.ContentSources) Content.AddSource(source);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LoadWithCustomLoader(
        IModLoader selected,
        ModDescriptor descriptor,
        ModPackageDescriptor package)
    {
        try
        {
            IModLoadResult result = selected.Load(descriptor, new ModLoadContext(services, logger))
                ?? throw new ModHostException(
                    $"Loader '{selected.Id}' returned no result for standalone mod '{descriptor.Id}'.");
            entries.Add(new LoadedEntry(descriptor, selected, result));
            foreach (ModContentSource source in package.ContentSources) Content.AddSource(source);
            foreach (ModContentSource source in result.ContentSources) Content.AddSource(source);
        }
        catch (Exception exception) when (exception is not ModHostException)
        {
            throw new ModHostException(
                $"Loader '{selected.Id}' failed to load standalone mod '{descriptor.Id}'.",
                exception);
        }
    }

    private static StandaloneRuntimePackage AssertSinglePackage(
        string modId,
        StandaloneRuntimeLoadHandle loaded)
    {
        if (loaded.Packages.Count == 1) return loaded.Packages[0];
        int count = loaded.Packages.Count;
        loaded.Dispose();
        throw new ModHostException(
            $"Standalone runtime loader returned {count} packages for single package '{modId}'.");
    }

    private static IReadOnlyList<ModDescriptor> ValidateAndOrder(IReadOnlyList<ModDescriptor> descriptors)
    {
        var byId = new Dictionary<string, ModDescriptor>(StringComparer.Ordinal);
        foreach (ModDescriptor descriptor in descriptors)
        {
            if (!byId.TryAdd(descriptor.Id, descriptor)) throw new ModHostException($"Duplicate mod ID '{descriptor.Id}'.");
            VersionConstraint apiConstraint = VersionConstraint.Parse(descriptor.ApiVersion);
            if (!apiConstraint.Allows(ModApiInfo.CurrentVersion)
                && !apiConstraint.Allows(ModApiInfo.LatestContractVersion))
            {
                throw new ModHostException(
                    $"Mod '{descriptor.Id}' requires API '{descriptor.ApiVersion}', but Tesseris provides "
                    + $"legacy {ModApiInfo.CurrentVersion} and v2 {ModApiInfo.LatestContractVersion} contracts.");
            }
        }

        foreach (ModDescriptor descriptor in descriptors)
        {
            foreach (ModDependency dependency in descriptor.Dependencies)
            {
                if (!byId.TryGetValue(dependency.Id, out ModDescriptor? installed))
                    throw new ModHostException($"Mod '{descriptor.Id}' is missing dependency '{dependency.Id}'.");
                if (!VersionConstraint.Parse(dependency.Version).Allows(installed.Version))
                    throw new ModHostException($"Mod '{descriptor.Id}' requires '{dependency.Id}' version '{dependency.Version}', but {installed.Version} is installed.");
            }
        }

        var remainingDependencies = descriptors.ToDictionary(
            descriptor => descriptor.Id,
            descriptor => descriptor.Dependencies.Count,
            StringComparer.Ordinal);
        var dependents = descriptors.ToDictionary(descriptor => descriptor.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (ModDescriptor descriptor in descriptors)
            foreach (ModDependency dependency in descriptor.Dependencies)
                dependents[dependency.Id].Add(descriptor.Id);

        var ready = new SortedSet<string>(remainingDependencies.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
        var ordered = new List<ModDescriptor>(descriptors.Count);
        while (ready.Count > 0)
        {
            string id = ready.Min!;
            ready.Remove(id);
            ordered.Add(byId[id]);
            foreach (string dependent in dependents[id].Order(StringComparer.Ordinal))
                if (--remainingDependencies[dependent] == 0) ready.Add(dependent);
        }

        if (ordered.Count != descriptors.Count)
        {
            string cycle = string.Join(", ", remainingDependencies.Where(pair => pair.Value > 0).Select(pair => pair.Key).Order(StringComparer.Ordinal));
            throw new ModHostException($"Mod dependency cycle detected: {cycle}.");
        }

        return ordered;
    }

    private void ShutdownCore()
    {
        Screens.Shutdown();
        CloseWorld();

        if (initialized)
        {
            try
            {
                if (!stoppingRaised)
                {
                    Events.RaiseStopping();
                }
            }
            catch (Exception exception) { logger.Error("A mod failed during shutdown.", exception); }
            initialized = false;
        }

        for (int index = entries.Count - 1; index >= 0; index--)
        {
            EventBus.RemoveOwner(entries[index].Descriptor.Id);
            interModServices.UnregisterOwner(entries[index].Descriptor.Id);
            try { entries[index].Result.Dispose(); }
            catch (Exception exception) { logger.Error($"Could not unload mod '{entries[index].Descriptor.Id}'.", exception); }
        }
        entries.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed record LoadedEntry(ModDescriptor Descriptor, IModLoader Loader, IModLoadResult Result);

    private sealed class StandalonePackageLoader : IModLoader
    {
        public static readonly StandalonePackageLoader Instance = new();
        public string Id => "tesseris:standalone";
        public bool CanLoad(ModDescriptor descriptor) => false;
        public IModLoadResult Load(ModDescriptor descriptor, IModLoadContext context) =>
            throw new NotSupportedException("Standalone packages are created by StandaloneRuntimeLoader.");
    }

    /// <summary>The standalone load handle owns disposal; this adapter only exposes packages to ModHost.</summary>
    private sealed class StandalonePackageResult : IModLoadResult
    {
        private StandaloneRuntimeLoadHandle? handle;

        public StandalonePackageResult(StandaloneRuntimeLoadHandle handle, StandaloneRuntimePackage package)
        {
            this.handle = handle;
            Instance = package.Instance;
            ContentSources = package.ContentSources;
        }

        public IMod? Instance { get; }
        public IReadOnlyList<ModContentSource> ContentSources { get; }
        public void Dispose()
        {
            Interlocked.Exchange(ref handle, null)?.Dispose();
        }
    }

}
