using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Tesseris.Game.Modding;
using Xunit;

namespace Tesseris.Tests;

public sealed class ModSdkBuildTests
{
    [Fact]
    public void Packed_sdk_builds_and_installs_a_standalone_mod_deterministically()
    {
        string repository = FindRepositoryRoot();
        using var temporary = new SdkTemporaryDirectory();
        string feed = Path.Combine(temporary.Path, "feed");
        string cliHome = Path.Combine(temporary.Path, "cli-home");
        string packages = Path.Combine(temporary.Path, "packages");
        string project = Path.Combine(temporary.Path, "external", "ExternalMod");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(cliHome);
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(project);

        RunDotnet(repository,
            ["pack", Path.Combine(repository, "src", "ModApi", "Tesseris.ModApi.csproj"),
             "-c", "Release", "--nologo", "-m:1", "-o", feed]);
        RunDotnet(repository,
            ["pack", Path.Combine(repository, "src", "Loader.Abstractions", "Tesseris.Loader.Abstractions.csproj"),
             "-c", "Release", "--nologo", "-m:1", "-o", feed]);
        RunDotnet(repository,
            ["pack", Path.Combine(repository, "src", "ModSdk", "Tesseris.ModSdk.csproj"),
             "-c", "Release", "--nologo", "-m:1", "-o", feed]);

        string apiPackage = Path.Combine(feed, "Tesseris.ModApi.2.0.0.nupkg");
        string loaderPackage = Path.Combine(feed, "Tesseris.Loader.Abstractions.2.0.0.nupkg");
        string sdkPackage = Path.Combine(feed, "Tesseris.ModSdk.2.0.1.nupkg");
        Assert.True(File.Exists(apiPackage), apiPackage);
        Assert.True(File.Exists(loaderPackage), loaderPackage);
        Assert.True(File.Exists(sdkPackage), sdkPackage);
        Assert.Equal("1.0.0.0", ReadAssemblyVersionFromPackage(apiPackage));

        IReadOnlyDictionary<string, string> isolatedEnvironment = new Dictionary<string, string>
        {
            ["DOTNET_CLI_HOME"] = cliHome,
            ["NUGET_PACKAGES"] = packages,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1"
        };

        RunDotnet(temporary.Path, ["new", "install", sdkPackage, "--force"], isolatedEnvironment);
        RunDotnet(temporary.Path,
            ["new", "tesseris-mod", "-n", "ExternalMod", "-o", project,
             "--mod-id", "fixture.external", "--mod-name", "External Fixture"],
            isolatedEnvironment);

        WriteNuGetConfig(project, feed);
        AddManagedProjectDependency(project);

        string projectFile = Path.Combine(project, "ExternalMod.csproj");
        string projectText = File.ReadAllText(projectFile);
        Assert.Contains("Tesseris.ModSdk/2.0.1", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("Tesseris.Game", projectText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tesseris.Engine", projectText, StringComparison.OrdinalIgnoreCase);

        RunDotnet(project, ["restore", projectFile], isolatedEnvironment);
        string binlog = Path.Combine(temporary.Path, "external.binlog");
        ProcessResult build = RunDotnet(project,
            ["build", projectFile, "-c", "Release", "--no-restore", "--nologo", "-m:1",
             "-v:normal", $"-bl:{binlog}"],
            isolatedEnvironment);
        AssertNoEngineProjects(build.Output);
        Assert.True(File.Exists(binlog));
        AssertNoEngineProjects(ReadSearchableBinaryText(binlog));

        string assets = File.ReadAllText(Path.Combine(project, "obj", "project.assets.json"));
        Assert.Contains("Tesseris.ModApi/2.0.0", assets, StringComparison.Ordinal);
        Assert.Contains("Tesseris.Loader.Abstractions/2.0.0", assets, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(packages, "tesseris.modsdk", "2.0.1")));
        AssertNoEngineProjects(assets);

        string artifactRoot = Path.Combine(project, "bin", "Release", "net8.0", "tesseris-mod");
        string artifactDirectory = Path.Combine(artifactRoot, "fixture.external");
        string archive = Path.Combine(artifactRoot, "fixture.external-1.0.0.tmod");
        Assert.True(File.Exists(Path.Combine(artifactDirectory, "ExternalMod.dll")));
        Assert.True(File.Exists(Path.Combine(artifactDirectory, "External.Helper.dll")));
        Assert.True(File.Exists(Path.Combine(artifactDirectory, "tesseris.mod.json")));
        Assert.True(File.Exists(Path.Combine(artifactDirectory, "content", "README.txt")));
        Assert.False(File.Exists(Path.Combine(artifactDirectory, "Tesseris.ModApi.dll")));
        Assert.False(File.Exists(Path.Combine(artifactDirectory, "Tesseris.Loader.Abstractions.dll")));
        Assert.False(Directory.Exists(Path.Combine(project, "mods")));
        Assert.True(File.Exists(archive));
        Assert.Equal(
            ["External.Helper.dll", "ExternalMod.dll", "content/README.txt", "tesseris.mod.json"],
            ReadArchiveEntries(archive));

        string firstHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
        RunDotnet(project,
            ["build", projectFile, "-c", "Release", "--no-restore", "--nologo", "-m:1"],
            isolatedEnvironment);
        string secondHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
        Assert.Equal(firstHash, secondHash);

        string installRoot = Path.Combine(temporary.Path, "explicit-mods");
        RunDotnet(project,
            ["build", projectFile, "-c", "Release", "--no-restore", "--nologo", "-m:1",
             "-t:InstallTesserisMod", $"-p:TesserisModsDir={installRoot}"],
            isolatedEnvironment);
        string installedArchive = Path.Combine(installRoot, "fixture.external-1.0.0.tmod");
        Assert.True(File.Exists(installedArchive));
        Assert.Equal(ReadArchiveEntries(archive), ReadArchiveEntries(installedArchive));

        using (ModHost installed = ModHost.DiscoverAndLoadStandalone(installRoot))
        {
            installed.Initialize();
            installed.Freeze();
            Assert.Equal("fixture.external", Assert.Single(installed.LoadedMods).Descriptor.Id);
        }

        string manifest = Path.Combine(project, "tesseris.mod.json");
        File.WriteAllText(manifest,
            File.ReadAllText(manifest).Replace("fixture.external", "wrong.owner", StringComparison.Ordinal));
        ProcessResult invalid = RunDotnet(project,
            ["build", projectFile, "-c", "Release", "--no-restore", "--nologo", "-m:1",
             "-t:BuildTesserisMod"],
            isolatedEnvironment,
            expectSuccess: false);
        Assert.Contains("Property 'id' must equal 'fixture.external'", invalid.Output, StringComparison.Ordinal);
    }

    private static void AddManagedProjectDependency(string project)
    {
        string helper = Path.Combine(Path.GetDirectoryName(project)!, "External.Helper");
        Directory.CreateDirectory(helper);
        File.WriteAllText(Path.Combine(helper, "External.Helper.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>External.Helper</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(helper, "HelperValue.cs"), """
            namespace External.Helper;
            public static class HelperValue { public const int Value = 42; }
            """);

        string projectFile = Path.Combine(project, "ExternalMod.csproj");
        string text = File.ReadAllText(projectFile).Replace("</Project>", """
              <ItemGroup>
                <ProjectReference Include="..\External.Helper\External.Helper.csproj" />
              </ItemGroup>
            </Project>
            """, StringComparison.Ordinal);
        File.WriteAllText(projectFile, text);
    }

    private static void WriteNuGetConfig(string project, string feed)
    {
        string escapedFeed = System.Security.SecurityElement.Escape(feed) ?? feed;
        File.WriteAllText(Path.Combine(project, "NuGet.Config"), $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="tesseris-local" value="{{escapedFeed}}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);
    }

    private static string ReadAssemblyVersionFromPackage(string package)
    {
        using ZipArchive archive = ZipFile.OpenRead(package);
        ZipArchiveEntry assembly = Assert.Single(archive.Entries,
            entry => entry.FullName.EndsWith("/Tesseris.ModApi.dll", StringComparison.Ordinal));
        string temporaryAssembly = Path.Combine(Path.GetTempPath(), $"Tesseris.ModApi-{Guid.NewGuid():N}.dll");
        try
        {
            assembly.ExtractToFile(temporaryAssembly);
            return System.Reflection.AssemblyName.GetAssemblyName(temporaryAssembly).Version?.ToString()
                   ?? throw new InvalidDataException("Packed ModApi has no assembly version.");
        }
        finally
        {
            if (File.Exists(temporaryAssembly)) File.Delete(temporaryAssembly);
        }
    }

    private static string[] ReadArchiveEntries(string archivePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    private static string ReadSearchableBinaryText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Encoding.UTF8.GetString(bytes) + Encoding.Unicode.GetString(bytes);
    }

    private static void AssertNoEngineProjects(string text)
    {
        Assert.DoesNotContain("Game.csproj", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Engine.csproj", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Shaders.csproj", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src\\Game", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src\\Engine", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\shaders\\", text, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessResult RunDotnet(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        bool expectSuccess = true)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Test si níž přesměrovává DOTNET_CLI_HOME do dočasného adresáře. Bez tohohle řádku
        // si .NET CLI při prvním spuštění pod novým domovem zapíše <home>\.dotnet\tools
        // natrvalo do uživatelské PATH v registru (HKCU\Environment). Adresář po testu
        // zmizí, ale záznam v PATH zůstane a nasčítá se z každého běhu.
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0";
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach ((string name, string value) in environment) start.Environment[name] = value;
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} timed out.");
        }
        string output = stdout.GetAwaiter().GetResult() + Environment.NewLine + stderr.GetAwaiter().GetResult();
        Assert.True(expectSuccess ? process.ExitCode == 0 : process.ExitCode != 0,
            $"Unexpected exit code {process.ExitCode} for dotnet {string.Join(' ', arguments)}.{Environment.NewLine}{output}");
        return new ProcessResult(process.ExitCode, output);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Tesseris.sln"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Tesseris repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed class SdkTemporaryDirectory : IDisposable
    {
        public SdkTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tesseris-modsdk-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }
    }
}
