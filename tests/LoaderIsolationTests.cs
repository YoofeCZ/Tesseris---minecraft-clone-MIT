using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Tesseris.Loader;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;
using Xunit;

namespace Tesseris.Tests;

public sealed class LoaderIsolationTests
{
    [Fact]
    public void Bundled_platform_assembly_is_rejected_even_when_renamed()
    {
        using var temporary = new LoaderTemporaryDirectory();
        string directory = temporary.CreatePackage("bundled");
        string renamed = Path.Combine(directory, "DefinitelyNotModApi.dll");
        File.Copy(typeof(IMod).Assembly.Location, renamed);
        ModPackageDescriptor package = Package("bundled", directory, "Entry.dll", "Entry.Point");

        LoaderException exception = Assert.Throws<LoaderException>(() =>
            new ModAssemblyLoader().Load(package, package.Entrypoints[0]));

        Assert.Contains("bundled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Tesseris.ModApi", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DefinitelyNotModApi.dll", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_mods_load_conflicting_private_dependency_versions_in_separate_contexts()
    {
        using var temporary = new LoaderTemporaryDirectory();
        BuiltFixture one = BuildFixture(temporary.Path, "one", "1.0.0", "one");
        BuiltFixture two = BuildFixture(temporary.Path, "two", "2.0.0", "two");
        var loader = new ModAssemblyLoader();
        using ModAssemblyLoadHandle first = loader.Load(one.Package, one.Entrypoint);
        using ModAssemblyLoadHandle second = loader.Load(two.Package, two.Entrypoint);

        string firstValue = (string)first.EntryType.GetMethod("Read", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        string secondValue = (string)second.EntryType.GetMethod("Read", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        Assembly firstDependency = first.Assembly.GetReferencedAssemblies().Any(name => name.Name == "Fixture.Dependency")
            ? first.EntryType.GetMethod("DependencyAssembly", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null) as Assembly
                ?? throw new InvalidOperationException()
            : throw new InvalidOperationException();
        Assembly secondDependency = second.EntryType.GetMethod("DependencyAssembly", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null) as Assembly
            ?? throw new InvalidOperationException();

        Assert.Equal("one", firstValue);
        Assert.Equal("two", secondValue);
        Assert.NotSame(firstDependency, secondDependency);
        Assert.Equal(new Version(1, 0, 0, 0), firstDependency.GetName().Version);
        Assert.Equal(new Version(2, 0, 0, 0), secondDependency.GetName().Version);
    }

    [Fact]
    public void Host_mod_api_is_shared_and_entrypoint_can_be_created_through_host_contract()
    {
        using var temporary = new LoaderTemporaryDirectory();
        BuiltFixture fixture = BuildModApiFixture(temporary.Path);
        var loader = new ModAssemblyLoader();
        using ModAssemblyLoadHandle handle = loader.Load(fixture.Package, fixture.Entrypoint);

        IMod mod = handle.CreateInstance<IMod>();
        Type implemented = Assert.Single(handle.EntryType.GetInterfaces(), type => type.FullName == typeof(IMod).FullName);

        Assert.NotNull(mod);
        Assert.Same(typeof(IMod).Assembly, implemented.Assembly);
    }

    [Fact]
    public void Disposed_handle_releases_collectible_context_and_never_changes_original_assembly()
    {
        using var temporary = new LoaderTemporaryDirectory();
        BuiltFixture fixture = BuildFixture(temporary.Path, "unload", "1.0.0", "value");
        string entryPath = Path.Combine(fixture.Package.Directory, fixture.Entrypoint.Assembly);
        byte[] before = File.ReadAllBytes(entryPath);

        WeakReference context = LoadReadAndDispose(fixture);
        ForceCollection(context);

        Assert.False(context.IsAlive);
        Assert.Equal(before, File.ReadAllBytes(entryPath));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadReadAndDispose(BuiltFixture fixture)
    {
        var loader = new ModAssemblyLoader();
        ModAssemblyLoadHandle handle = loader.Load(fixture.Package, fixture.Entrypoint);
        _ = handle.EntryType.GetMethod("Read", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        WeakReference weak = handle.LoadContextReference;
        handle.Dispose();
        return weak;
    }

    private static BuiltFixture BuildFixture(string root, string id, string dependencyVersion, string value)
    {
        string directory = Path.Combine(root, id);
        string dependency = Path.Combine(directory, "Dependency");
        string entry = Path.Combine(directory, "Entry");
        Directory.CreateDirectory(dependency);
        Directory.CreateDirectory(entry);
        File.WriteAllText(Path.Combine(dependency, "Dependency.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>Fixture.Dependency</AssemblyName>
                <Version>{dependencyVersion}</Version>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dependency, "Value.cs"), $$"""
            namespace Fixture.Dependency;
            public static class Value { public const string Text = "{{value}}"; }
            """);
        File.WriteAllText(Path.Combine(entry, "Entry.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>Fixture.Entry</AssemblyName>
              </PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Dependency\Dependency.csproj" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(entry, "Entry.cs"), """
            using System.Reflection;
            namespace Fixture.Entry;
            public sealed class Point
            {
                public static string Read() => Fixture.Dependency.Value.Text;
                public static Assembly DependencyAssembly() => typeof(Fixture.Dependency.Value).Assembly;
            }
            """);
        BuildProject(Path.Combine(entry, "Entry.csproj"));
        string output = Path.Combine(entry, "bin", "Release", "net8.0");
        return Fixture(id, output, "Fixture.Entry.dll", "Fixture.Entry.Point");
    }

    private static BuiltFixture BuildModApiFixture(string root)
    {
        string directory = Path.Combine(root, "api", "Entry");
        Directory.CreateDirectory(directory);
        string modApi = typeof(IMod).Assembly.Location;
        File.WriteAllText(Path.Combine(directory, "Entry.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Fixture.ApiEntry</AssemblyName></PropertyGroup>
              <ItemGroup><Reference Include="Tesseris.ModApi"><HintPath>{EscapeXml(modApi)}</HintPath><Private>false</Private></Reference></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Entry.cs"), """
            using Tesseris.ModApi;
            namespace Fixture.ApiEntry;
            public sealed class Entry : IMod { public void Configure(IModContext context) { } }
            """);
        BuildProject(Path.Combine(directory, "Entry.csproj"));
        string output = Path.Combine(directory, "bin", "Release", "net8.0");
        return Fixture("api", output, "Fixture.ApiEntry.dll", "Fixture.ApiEntry.Entry");
    }

    private static BuiltFixture Fixture(string id, string directory, string assembly, string type)
    {
        ModEntrypointDescriptor entrypoint = new(ModEntrypointPhase.Runtime, assembly, type);
        var package = new ModPackageDescriptor(
            2, id, id, "1.0.0", "2.0.0", "2.0.0", directory, ModTrustLevel.ManagedRuntime,
            [entrypoint], [], [], [], [], id.PadRight(64, '0')[..64]);
        return new BuiltFixture(package, entrypoint);
    }

    private static ModPackageDescriptor Package(string id, string directory, string assembly, string type) =>
        Fixture(id, directory, assembly, type).Package;

    private static void BuildProject(string project)
    {
        var start = new ProcessStartInfo("dotnet", $"build \"{project}\" -c Release --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet build.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Fixture build failed.\n{stdout}\n{stderr}");
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

    private sealed record BuiltFixture(ModPackageDescriptor Package, ModEntrypointDescriptor Entrypoint);
}
