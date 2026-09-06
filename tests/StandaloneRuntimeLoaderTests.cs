using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Tesseris.Game.Modding;
using Tesseris.Loader;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class StandaloneRuntimeLoaderTests
{
    private static readonly Lazy<string> FixtureAssembly = new(BuildFixture, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Schema2_runtime_and_client_entries_form_deterministic_composite_and_convert_descriptor()
    {
        using var temporary = new TemporaryDirectory();
        string packageDirectory = InstallFixture(temporary.Path, "mixed");
        string content = Directory.CreateDirectory(Path.Combine(packageDirectory, "content")).FullName;
        ModEntrypointDescriptor[] entries =
        [
            Entry(ModEntrypointPhase.Client, "FixtureMods.ClientEntry"),
            Entry(ModEntrypointPhase.Runtime, "FixtureMods.RuntimeZ"),
            Entry(ModEntrypointPhase.Runtime, "FixtureMods.RuntimeA"),
        ];
        ModPackageDescriptor package = Package(
            "mixed",
            packageDirectory,
            entries,
            [new ModContentSource("mixed", content)],
            dependencies:
            [
                new ModPackageDependency("required", ">=1.0.0", ModDependencyKind.Required),
                new ModPackageDependency("optional", "*", ModDependencyKind.Optional),
            ],
            capabilities: ["custom-capability"]);
        string log = Path.Combine(temporary.Path, "configure.log");
        Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", log);
        try
        {
            StandaloneRuntimeLoadHandle loaded = Load(Plan(package), ModRuntimeEnvironment.Client);
            try
            {
                StandaloneRuntimePackage result = Assert.Single(loaded.Packages);

                result.Instance!.Configure(null!);

                Assert.Equal(new[] { "configure:RuntimeA", "configure:RuntimeZ", "configure:ClientEntry" }, File.ReadAllLines(log));
                Assert.Equal("mixed", result.Descriptor.Id);
                Assert.Equal("FixtureMods.RuntimeA", result.Descriptor.EntryType);
                Assert.Equal("FixtureMods.dll", result.Descriptor.EntryAssembly);
                Assert.Equal("2.0.0", result.Descriptor.ApiVersion);
                Assert.True(result.Descriptor.TrustedCode);
                Assert.Equal("content", result.Descriptor.ContentRoot);
                Assert.Equal(new[] { "custom-capability" }, result.Descriptor.Capabilities);
                ModDependency dependency = Assert.Single(result.Descriptor.Dependencies);
                Assert.Equal("required", dependency.Id);
                Assert.Equal(">=1.0.0", dependency.Version);
                Assert.Equal(content, Assert.Single(result.ContentSources).RootPath);
            }
            finally
            {
                loaded.Dispose();
            }
            Assert.Equal(
                new[]
                {
                    "configure:RuntimeA", "configure:RuntimeZ", "configure:ClientEntry",
                    "dispose:ClientEntry", "dispose:RuntimeZ", "dispose:RuntimeA",
                },
                File.ReadAllLines(log));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", null);
        }
    }

    [Fact]
    public void Dedicated_environment_filters_client_and_preserves_content_only_package()
    {
        using var temporary = new TemporaryDirectory();
        string managedDirectory = InstallFixture(temporary.Path, "managed");
        string contentDirectory = Directory.CreateDirectory(Path.Combine(temporary.Path, "content-only", "assets")).FullName;
        ModPackageDescriptor managed = Package(
            "managed",
            managedDirectory,
            [Entry(ModEntrypointPhase.Client, "FixtureMods.ClientEntry"),
             Entry(ModEntrypointPhase.DedicatedServer, "FixtureMods.ServerEntry"),
             Entry(ModEntrypointPhase.Runtime, "FixtureMods.RuntimeA")]);
        ModPackageDescriptor content = Package(
            "contentonly",
            Path.GetDirectoryName(contentDirectory)!,
            [],
            [new ModContentSource("contentonly", contentDirectory)],
            trust: ModTrustLevel.ContentOnly);
        string log = Path.Combine(temporary.Path, "dedicated.log");
        Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", log);
        try
        {
            using StandaloneRuntimeLoadHandle loaded = Load(Plan(managed, content), ModRuntimeEnvironment.DedicatedServer);
            Assert.Equal(new[] { "managed", "contentonly" }, loaded.Packages.Select(item => item.Descriptor.Id));
            loaded.Packages[0].Instance!.Configure(null!);

            Assert.Equal(new[] { "configure:RuntimeA", "configure:ServerEntry" }, File.ReadAllLines(log));
            Assert.Null(loaded.Packages[1].Instance);
            Assert.False(loaded.Packages[1].Descriptor.TrustedCode);
            Assert.Equal(contentDirectory, Assert.Single(loaded.Packages[1].ContentSources).RootPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", null);
        }
    }

    [Fact]
    public void Failure_is_attributed_and_rolls_back_previous_instances_and_contexts()
    {
        using var temporary = new TemporaryDirectory();
        string firstDirectory = InstallFixture(temporary.Path, "first");
        string brokenDirectory = InstallFixture(temporary.Path, "broken");
        ModPackageDescriptor first = Package(
            "first",
            firstDirectory,
            [Entry(ModEntrypointPhase.Runtime, "FixtureMods.RuntimeA")]);
        ModPackageDescriptor broken = Package(
            "broken",
            brokenDirectory,
            [Entry(ModEntrypointPhase.Runtime, "FixtureMods.DoesNotExist")]);
        string log = Path.Combine(temporary.Path, "rollback.log");
        Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", log);
        try
        {
            StandaloneRuntimeLoadException exception = CaptureFailure(Plan(first, broken));
            Assert.Contains("broken", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Runtime", exception.Message, StringComparison.Ordinal);
            Assert.Contains("FixtureMods.DoesNotExist", exception.Message, StringComparison.Ordinal);
            Assert.Equal(new[] { "dispose:RuntimeA" }, File.ReadAllLines(log));
            WeakReference context = Assert.Single(exception.RolledBackLoadContexts);
            ForceCollection(context);
            Assert.False(context.IsAlive);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", null);
        }
    }

    [Fact]
    public void Identical_entry_descriptor_is_rejected_but_distinct_multiple_entries_are_allowed()
    {
        using var temporary = new TemporaryDirectory();
        string directory = InstallFixture(temporary.Path, "duplicate");
        ModEntrypointDescriptor duplicate = Entry(ModEntrypointPhase.Runtime, "FixtureMods.RuntimeA");
        ModPackageDescriptor package = Package("duplicate", directory, [duplicate, duplicate]);

        StandaloneRuntimeLoadException exception = Assert.Throws<StandaloneRuntimeLoadException>(() =>
            Load(Plan(package), ModRuntimeEnvironment.Client));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Runtime", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModHost_standalone_factory_loads_v2_runtime_and_content_packages_in_plan_order()
    {
        using var temporary = new TemporaryDirectory();
        InstallV2RuntimePackage(temporary.Path, "second", "FixtureMods.RuntimeZ", dependencies: new { first = "*" });
        InstallV2RuntimePackage(temporary.Path, "first", "FixtureMods.RuntimeA", withContent: true);
        InstallV2ContentPackage(temporary.Path, "zcontent");
        string log = Path.Combine(temporary.Path, "host-order.log");
        Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", log);
        try
        {
            using ModHost host = ModHost.DiscoverAndLoadStandalone(temporary.Path);
            host.Initialize();

            Assert.Equal(new[] { "first", "second", "zcontent" }, host.LoadedMods.Select(item => item.Descriptor.Id));
            Assert.Equal(new[] { true, true, false }, host.LoadedMods.Select(item => item.HasManagedInstance));
            Assert.Equal(new[] { "first", "zcontent" }, host.ContentSources.Select(item => item.ModId));
            Assert.Equal(new[] { "configure:RuntimeA", "configure:RuntimeZ" }, File.ReadAllLines(log));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", null);
        }
    }

    [Fact]
    public void ModHost_standalone_shutdown_unregisters_owners_and_disposes_once_in_reverse_order()
    {
        using var temporary = new TemporaryDirectory();
        InstallV2RuntimePackage(temporary.Path, "owner", "FixtureMods.OwnerMod");
        InstallV2RuntimePackage(temporary.Path, "later", "FixtureMods.RuntimeA", dependencies: new { owner = "*" });
        string log = Path.Combine(temporary.Path, "host-cleanup.log");
        var services = new ModServiceRegistry();
        Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", log);
        try
        {
            ModHost host = ModHost.DiscoverAndLoadStandalone(
                temporary.Path,
                new ModHostOptions { InterModServices = services });
            host.Initialize();
            Assert.Single(services.Published);
            Assert.Single(host.EventBus.Subscriptions);

            host.Dispose();
            host.Dispose();

            Assert.Empty(services.Published);
            Assert.Empty(host.EventBus.Subscriptions);
            Assert.Equal(
                new[]
                {
                    "configure:OwnerMod", "configure:RuntimeA",
                    "dispose:RuntimeA", "dispose:OwnerMod",
                },
                File.ReadAllLines(log));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", null);
        }
    }

    [Fact]
    public void ModHost_standalone_failure_rolls_back_previously_loaded_package()
    {
        using var temporary = new TemporaryDirectory();
        InstallV2RuntimePackage(temporary.Path, "first", "FixtureMods.RuntimeA");
        InstallV2RuntimePackage(
            temporary.Path,
            "broken",
            "FixtureMods.DoesNotExist",
            dependencies: new { first = "*" });
        string log = Path.Combine(temporary.Path, "host-failure.log");
        Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", log);
        try
        {
            ModHostException exception = Assert.Throws<ModHostException>(() =>
                ModHost.DiscoverAndLoadStandalone(temporary.Path));

            Assert.Contains("broken", exception.ToString(), StringComparison.Ordinal);
            Assert.Contains("Runtime", exception.ToString(), StringComparison.Ordinal);
            Assert.Equal(new[] { "dispose:RuntimeA" }, File.ReadAllLines(log));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG", null);
        }
    }

    [Fact]
    public void Custom_loader_gets_first_chance_for_v2_package_without_double_loading()
    {
        using var temporary = new TemporaryDirectory();
        InstallV2ContentPackage(temporary.Path, "custom", capabilities: ["custom-loader"]);
        var mod = new HostRecordingMod();
        var loader = new HostCustomLoader(mod);

        ModHost host = ModHost.DiscoverAndLoadStandalone(
            temporary.Path,
            new ModHostOptions { Loaders = [loader] });
        host.Initialize();

        Assert.Equal(1, loader.LoadCount);
        Assert.True(mod.Configured);
        LoadedModInfo loaded = Assert.Single(host.LoadedMods);
        Assert.Equal("tests:standalone-custom", loaded.LoaderId);
        Assert.True(loaded.HasManagedInstance);
        Assert.Equal("custom", Assert.Single(host.ContentSources).ModId);
        host.Dispose();
        Assert.Equal(1, loader.DisposeCount);
    }

    private static StandaloneRuntimeLoadHandle Load(ModLoadPlan plan, ModRuntimeEnvironment environment) =>
        new StandaloneRuntimeLoader().Load(plan, environment, new ModAssemblyLoader(), TestLogger.Instance);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static StandaloneRuntimeLoadException CaptureFailure(ModLoadPlan plan) =>
        Assert.Throws<StandaloneRuntimeLoadException>(() => Load(plan, ModRuntimeEnvironment.Client));

    private static ModLoadPlan Plan(params ModPackageDescriptor[] packages) => new(
        packages.Select((package, index) => new ModLoadPlanEntry(
            package,
            index,
            new ModTrustDecision(package.Id, package.Trust, true, package.PackageHash, "test"))).ToArray(),
        "runtime-test-plan");

    private static ModPackageDescriptor Package(
        string id,
        string directory,
        IReadOnlyList<ModEntrypointDescriptor> entries,
        IReadOnlyList<ModContentSource>? content = null,
        IReadOnlyList<ModPackageDependency>? dependencies = null,
        IReadOnlyList<string>? capabilities = null,
        ModTrustLevel trust = ModTrustLevel.ManagedRuntime) => new(
        2,
        id,
        "Name " + id,
        "1.2.3",
        "2.0.0",
        "2.0.0",
        directory,
        trust,
        entries,
        dependencies ?? Array.Empty<ModPackageDependency>(),
        Array.Empty<string>(),
        content ?? Array.Empty<ModContentSource>(),
        capabilities ?? Array.Empty<string>(),
        id.PadRight(64, '0')[..64]);

    private static ModEntrypointDescriptor Entry(ModEntrypointPhase phase, string type) =>
        new(phase, "FixtureMods.dll", type);

    private static string InstallFixture(string root, string id)
    {
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.Copy(FixtureAssembly.Value, Path.Combine(directory, "FixtureMods.dll"));
        return directory;
    }

    private static void InstallV2RuntimePackage(
        string root,
        string id,
        string type,
        object? dependencies = null,
        bool withContent = false)
    {
        string directory = InstallFixture(root, id);
        if (withContent) Directory.CreateDirectory(Path.Combine(directory, "content"));
        WriteV2Manifest(directory, new
        {
            schemaVersion = 2,
            id,
            name = id,
            version = "1.0.0",
            loaderVersion = "*",
            modApiVersion = "*",
            trustedCode = true,
            dependencies,
            contentRoot = withContent ? "content" : null,
            entrypoints = new[] { new { phase = "Runtime", assembly = "FixtureMods.dll", type } },
        });
    }

    private static void InstallV2ContentPackage(
        string root,
        string id,
        IReadOnlyList<string>? capabilities = null)
    {
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(Path.Combine(directory, "content"));
        WriteV2Manifest(directory, new
        {
            schemaVersion = 2,
            id,
            name = id,
            version = "1.0.0",
            loaderVersion = "*",
            modApiVersion = "*",
            contentRoot = "content",
            capabilities = capabilities ?? Array.Empty<string>(),
        });
    }

    private static void WriteV2Manifest(string directory, object manifest) => File.WriteAllText(
        Path.Combine(directory, "tesseris.mod.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    private static string BuildFixture()
    {
        string root = FindRepositoryRoot();
        string directory = Path.Combine(root, "tests", "obj", "StandaloneRuntimeFixture");
        Directory.CreateDirectory(directory);
        string modApi = typeof(IMod).Assembly.Location;
        File.WriteAllText(Path.Combine(directory, "FixtureMods.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>FixtureMods</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Tesseris.ModApi"><HintPath>{EscapeXml(modApi)}</HintPath><Private>false</Private></Reference>
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "FixtureMods.cs"), FixtureSource);
        RunDotNet(directory, "build", "FixtureMods.csproj", "-c", "Release", "--nologo", "-v:q");
        return Path.Combine(directory, "bin", "Release", "net8.0", "FixtureMods.dll");
    }

    private static void RunDotNet(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Fixture build failed:\n{stdout}\n{stderr}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesseris.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Tesseris.sln was not found.");
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static void ForceCollection(WeakReference weak)
    {
        for (int index = 0; index < 20 && weak.IsAlive; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private const string FixtureSource = """
        using Tesseris.ModApi;
        namespace FixtureMods;
        public abstract class LoggedMod : IMod, IDisposable
        {
            public virtual void Configure(IModContext context) => Write("configure:" + GetType().Name);
            public void Dispose() => Write("dispose:" + GetType().Name);
            private static void Write(string value)
            {
                string? path = Environment.GetEnvironmentVariable("TESSERIS_RUNTIME_FIXTURE_LOG");
                if (path is not null) File.AppendAllLines(path, new[] { value });
            }
        }
        public sealed class RuntimeA : LoggedMod { }
        public sealed class RuntimeZ : LoggedMod { }
        public sealed class ClientEntry : LoggedMod { }
        public sealed class ServerEntry : LoggedMod { }
        public sealed class OwnerMod : LoggedMod, IModEventHandler<ModLoaderReadyEvent>
        {
            public override void Configure(IModContext context)
            {
                base.Configure(context);
                IModContextV2 v2 = (IModContextV2)context;
                v2.InterModServices.Publish<IMod>(new ResourceId("owner:service"), "1.0.0", this);
                v2.EventBus.Subscribe(
                    new ResourceId("owner:loader_ready"),
                    ModEventPhase.Normal,
                    0,
                    this);
            }
            public ModEventResult Handle(ModLoaderReadyEvent value) => ModEventResult.Continue;
        }
        """;

    private sealed class HostCustomLoader : IModLoader
    {
        private readonly IMod mod;

        public HostCustomLoader(IMod mod) => this.mod = mod;

        public string Id => "tests:standalone-custom";
        public int LoadCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool CanLoad(ModDescriptor descriptor) => descriptor.Capabilities.Contains("custom-loader", StringComparer.Ordinal);

        public IModLoadResult Load(ModDescriptor descriptor, IModLoadContext context)
        {
            LoadCount++;
            return new HostCustomResult(mod, () => DisposeCount++);
        }
    }

    private sealed class HostCustomResult : IModLoadResult
    {
        private Action? dispose;

        public HostCustomResult(IMod instance, Action dispose)
        {
            Instance = instance;
            this.dispose = dispose;
        }

        public IMod? Instance { get; }
        public IReadOnlyList<ModContentSource> ContentSources => Array.Empty<ModContentSource>();
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }

    private sealed class HostRecordingMod : IMod
    {
        public bool Configured { get; private set; }
        public void Configure(IModContext context) => Configured = true;
    }

    private sealed class TestLogger : IModLogger
    {
        public static readonly TestLogger Instance = new();
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TesserisRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
