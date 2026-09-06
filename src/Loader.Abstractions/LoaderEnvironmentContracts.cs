using Tesseris.ModApi;

namespace Tesseris.Loader.Abstractions;

public static class TesserisLoaderApiInfo
{
    public const string CurrentVersion = "2.0.0";
}

public enum ModRuntimeEnvironment
{
    Client,
    DedicatedServer,
    Tooling
}

public enum ModTrustLevel
{
    ContentOnly,
    ManagedRuntime,
    PreLaunch,
    CoreMod
}

/// <summary>
/// Immutable loader environment captured before the Game assembly is loaded. Paths are informational and
/// normalized absolute paths. They do not restrict what trusted code can access.
/// </summary>
public sealed record ModLoaderEnvironment(
    string LoaderVersion,
    string ModApiVersion,
    string GameVersion,
    string RuntimeVersion,
    string OperatingSystem,
    string Architecture,
    ModRuntimeEnvironment Environment,
    string InstallationDirectory,
    string GameAssemblyPath,
    string CacheDirectory,
    bool IsDevelopment);

/// <summary>
/// Immutable trust decision recorded by the loader. Allowed means the user or policy explicitly accepted
/// execution; it never means sandboxed, safe, signed or harmless. In-process managed/core code has the same
/// operating-system permissions as the game.
/// </summary>
public sealed record ModTrustDecision(
    string ModId,
    ModTrustLevel Requested,
    bool Allowed,
    string PackageHash,
    string Reason);

public interface ILoaderCapabilityProvider
{
    IReadOnlyList<ModCapabilityDescriptor> Available { get; }

    bool TryGet<TContract>(ResourceId id, out TContract? capability) where TContract : class;
}
