using System.Diagnostics;
using System.Text.Json;
using Tesseris.Launcher;
using Tesseris.Loader;
using Xunit;

namespace Tesseris.Tests;

public sealed class LauncherBootstrapTests
{
    private static readonly Lazy<LauncherFixture> Fixture = new(BuildFixture, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void No_mod_bootstrap_transforms_before_load_passes_original_args_and_propagates_exit_code()
    {
        LauncherFixture fixture = Fixture.Value;
        string[] launcherReferences = typeof(LauncherBootstrap).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain("Tesseris", launcherReferences);
        Assert.DoesNotContain("Tesseris.Engine", launcherReferences);
        Assert.DoesNotContain(launcherReferences, name => name.StartsWith("OpenTK", StringComparison.Ordinal));
        using var temporary = new TemporaryDirectory();
        string argumentLog = Path.Combine(temporary.Path, "args.txt");
        Environment.SetEnvironmentVariable("TESSERIS_LAUNCHER_TEST_LOG", argumentLog);
        var observer = new RecordingObserver();
        try
        {
            int exit = new LauncherBootstrap().Run(
                Options(fixture.GameAssembly, Path.Combine(temporary.Path, "mods"), temporary.Path, observer),
                ["27", "literal argument"]);

            Assert.Equal(27, exit);
            Assert.Equal("27|literal argument", File.ReadAllText(argumentLog));
            Assert.DoesNotContain(observer.Events, item => item.ModId is not null);
            Assert.True(Index(observer, LauncherPhase.BeforeTransform) < Index(observer, LauncherPhase.GameLoaded));
            LauncherPhaseEvent beforeTransform = observer.Events.Single(item => item.Phase == LauncherPhase.BeforeTransform);
            Assert.DoesNotContain(
                Path.GetFullPath(fixture.GameAssembly),
                observer.LoadedAssemblyPaths[beforeTransform]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSERIS_LAUNCHER_TEST_LOG", null);
        }
    }

    [Fact]
    public void Prelaunch_runs_in_dependency_plan_order_and_cleanup_is_reverse_order()
    {
        LauncherFixture fixture = Fixture.Value;
        using var temporary = new TemporaryDirectory();
        string mods = Path.Combine(temporary.Path, "mods");
        string lifecycleLog = Path.Combine(temporary.Path, "lifecycle.txt");
        InstallPreLaunchMod(fixture.OrderModAssembly, mods, "first", dependencies: null);
        InstallPreLaunchMod(fixture.OrderModAssembly, mods, "second", dependencies: new { first = "*" });
        Environment.SetEnvironmentVariable("TESSERIS_LAUNCHER_TEST_LIFECYCLE", lifecycleLog);
        try
        {
            int exit = new LauncherBootstrap().Run(
                Options(fixture.GameAssembly, mods, temporary.Path),
                ["0"]);

            Assert.Equal(0, exit);
            Assert.Equal(
                new[] { "start:first", "start:second", "dispose:second", "dispose:first" },
                File.ReadAllLines(lifecycleLog));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSERIS_LAUNCHER_TEST_LIFECYCLE", null);
        }
    }

    [Fact]
    public void Disabled_prelaunch_mod_is_filtered_before_its_code_runs()
    {
        LauncherFixture fixture = Fixture.Value;
        using var temporary = new TemporaryDirectory();
        string mods = Path.Combine(temporary.Path, "mods");
        string lifecycleLog = Path.Combine(temporary.Path, "lifecycle.txt");
        InstallPreLaunchMod(fixture.OrderModAssembly, mods, "disabled", dependencies: null);
        ModEnablementSettings.WriteDisabled(mods, ["disabled"]);
        Environment.SetEnvironmentVariable("TESSERIS_LAUNCHER_TEST_LIFECYCLE", lifecycleLog);
        try
        {
            int exit = new LauncherBootstrap().Run(
                Options(fixture.GameAssembly, mods, temporary.Path),
                ["0"]);

            Assert.Equal(0, exit);
            Assert.False(File.Exists(lifecycleLog));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESSERIS_LAUNCHER_TEST_LIFECYCLE", null);
        }
    }

    [Fact]
    public void Coremod_patch_is_applied_before_game_load_and_second_launch_uses_cache()
    {
        LauncherFixture fixture = Fixture.Value;
        using var temporary = new TemporaryDirectory();
        string mods = Path.Combine(temporary.Path, "mods");
        InstallCoreMod(fixture.CoreModAssembly, mods);
        var firstObserver = new RecordingObserver();
        var secondObserver = new RecordingObserver();
        LauncherOptions firstOptions = Options(fixture.GameAssembly, mods, temporary.Path, firstObserver);

        int first = new LauncherBootstrap().Run(firstOptions, ["4", "unchanged"]);
        int second = new LauncherBootstrap().Run(firstOptions with { Observer = secondObserver }, ["9"]);

        Assert.Equal(73, first);
        Assert.Equal(73, second);
        Assert.Contains(firstObserver.Events, item => item.Phase == LauncherPhase.AfterTransform && item.Detail == "cache-miss");
        Assert.Contains(secondObserver.Events, item => item.Phase == LauncherPhase.AfterTransform && item.Detail == "cache-hit");
        Assert.True(Index(firstObserver, LauncherPhase.BeforeTransform) < Index(firstObserver, LauncherPhase.GameLoaded));
    }

    private static LauncherOptions Options(
        string gameAssembly,
        string mods,
        string root,
        ILauncherObserver? observer = null) => new(
        root,
        gameAssembly,
        mods,
        Path.Combine(root, "cache"),
        "1.0.0",
        "LauncherProbeGame.Entry",
        "Main",
        IsDevelopment: true,
        Observer: observer);

    private static int Index(RecordingObserver observer, LauncherPhase phase) =>
        observer.Events.FindIndex(item => item.Phase == phase);

    private static void InstallPreLaunchMod(string assembly, string mods, string id, object? dependencies)
    {
        string directory = Path.Combine(mods, id);
        Directory.CreateDirectory(directory);
        File.Copy(assembly, Path.Combine(directory, "OrderMod.dll"));
        WriteManifest(directory, new
        {
            schemaVersion = 2,
            id,
            name = id,
            version = "1.0.0",
            loaderVersion = "*",
            modApiVersion = "*",
            trustedCode = true,
            dependencies,
            entrypoints = new[] { new { phase = "PreLaunch", assembly = "OrderMod.dll", type = "OrderMod.Entry" } },
        });
    }

    private static void InstallCoreMod(string assembly, string mods)
    {
        string directory = Path.Combine(mods, "corepatch");
        Directory.CreateDirectory(directory);
        File.Copy(assembly, Path.Combine(directory, "CorePatch.dll"));
        WriteManifest(directory, new
        {
            schemaVersion = 2,
            id = "corepatch",
            name = "corepatch",
            version = "1.0.0",
            loaderVersion = "*",
            modApiVersion = "*",
            trustedCode = true,
            coreMod = true,
            entrypoints = new[] { new { phase = "CoreMod", assembly = "CorePatch.dll", type = "CorePatch.Entry" } },
        });
    }

    private static void WriteManifest(string directory, object manifest) => File.WriteAllText(
        Path.Combine(directory, "tesseris.mod.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    private static LauncherFixture BuildFixture()
    {
        string root = FindRepositoryRoot();
        string output = Path.Combine(root, "tests", "obj", "LauncherBootstrapFixture");
        Directory.CreateDirectory(output);
        string modApi = Path.Combine(root, "src", "ModApi", "bin", "Release", "net8.0", "Tesseris.ModApi.dll");
        string abstractions = Path.Combine(root, "src", "Loader.Abstractions", "bin", "Release", "net8.0", "Tesseris.Loader.Abstractions.dll");
        BuildProject(output, "LauncherProbeGame", GameSource, Array.Empty<string>());
        BuildProject(output, "OrderMod", OrderModSource, [modApi, abstractions]);
        BuildProject(output, "CorePatch", CoreModSource, [modApi, abstractions]);
        return new(
            Path.Combine(output, "LauncherProbeGame", "bin", "Release", "net8.0", "LauncherProbeGame.dll"),
            Path.Combine(output, "OrderMod", "bin", "Release", "net8.0", "OrderMod.dll"),
            Path.Combine(output, "CorePatch", "bin", "Release", "net8.0", "CorePatch.dll"));
    }

    private static void BuildProject(string root, string name, string source, IReadOnlyList<string> references)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        string referenceXml = string.Join(Environment.NewLine, references.Select((path, index) =>
            $"<Reference Include=\"FixtureReference{index}\"><HintPath>{path}</HintPath><Private>false</Private></Reference>"));
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>{{name}}</AssemblyName>
              </PropertyGroup>
              <ItemGroup>{{referenceXml}}</ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Fixture.cs"), source);
        RunDotNet(directory, "build", name + ".csproj", "-c", "Release", "--nologo", "-v:q");
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
        Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', arguments)} failed:\n{stdout}\n{stderr}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesseris.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Tesseris.sln was not found.");
    }

    private const string GameSource = """
        namespace LauncherProbeGame;
        public static class Entry
        {
            public static int Main(string[] args)
            {
                string? log = Environment.GetEnvironmentVariable("TESSERIS_LAUNCHER_TEST_LOG");
                if (log is not null) File.WriteAllText(log, string.Join("|", args));
                return int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        """;

    private const string OrderModSource = """
        using Tesseris.Loader.Abstractions;
        namespace OrderMod;
        public sealed class Entry : IPreLaunchMod, IDisposable
        {
            private string? owner;
            public void PreLaunch(IPreLaunchContext context)
            {
                owner = context.Mod.Id;
                Append("start:" + owner);
            }
            public void Dispose() => Append("dispose:" + owner);
            private static void Append(string value)
            {
                string path = Environment.GetEnvironmentVariable("TESSERIS_LAUNCHER_TEST_LIFECYCLE")!;
                File.AppendAllLines(path, new[] { value });
            }
        }
        """;

    private const string CoreModSource = """
        using Tesseris.Loader.Abstractions;
        using Tesseris.ModApi;
        namespace CorePatch;
        public sealed class Entry : ICoreMod
        {
            public void ConfigureCore(ICoreModContext context)
            {
                string integer = typeof(int).AssemblyQualifiedName!;
                string strings = typeof(string[]).AssemblyQualifiedName!;
                context.Patches.Register(new ModMethodPatchDescriptor(
                    new ResourceId("corepatch:replace_main"),
                    "corepatch",
                    ModMethodPatchKind.Replace,
                    new ModMethodTarget("LauncherProbeGame", "LauncherProbeGame.Entry", "Main", 0, true, integer, new[] { strings }),
                    new ModPatchEntrypoint("CorePatch", "CorePatch.Entry", "Replace", integer, new[] { strings }),
                    0,
                    Array.Empty<ResourceId>(),
                    Array.Empty<ResourceId>()));
            }
            public static int Replace(string[] args) => 73;
        }
        """;

    private sealed class RecordingObserver : ILauncherObserver
    {
        public List<LauncherPhaseEvent> Events { get; } = new();
        public Dictionary<LauncherPhaseEvent, IReadOnlySet<string>> LoadedAssemblyPaths { get; } = new();

        public void OnPhase(LauncherPhaseEvent phase)
        {
            Events.Add(phase);
            LoadedAssemblyPaths[phase] = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly =>
                {
                    try { return string.IsNullOrEmpty(assembly.Location) ? string.Empty : Path.GetFullPath(assembly.Location); }
                    catch (NotSupportedException) { return string.Empty; }
                })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed record LauncherFixture(string GameAssembly, string OrderModAssembly, string CoreModAssembly);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TesserisLauncherTests", Guid.NewGuid().ToString("N"));
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
