using Tesseris.Loader;
using Tesseris.Loader.Abstractions;

namespace Tesseris.Launcher;

public sealed record LauncherOptions(
    string InstallationDirectory,
    string GameAssemblyPath,
    string ModsDirectory,
    string CacheDirectory,
    string GameVersion,
    string GameEntryType = "Tesseris.Game.Program",
    string GameEntryMethod = "Main",
    bool IsDevelopment = false,
    IModTrustPolicy? TrustPolicy = null,
    ILauncherObserver? Observer = null);

public enum LauncherPhase
{
    ModsResolved,
    PreLaunch,
    CoreMod,
    PatchRegistryFrozen,
    BeforeTransform,
    AfterTransform,
    BeforeGameLoad,
    GameLoaded,
    Cleanup
}

public sealed record LauncherPhaseEvent(
    LauncherPhase Phase,
    string? ModId = null,
    string? Detail = null,
    string? AssemblyPath = null);

/// <summary>Diagnostic observer. Callbacks run synchronously on the launcher thread and must not mutate bootstrap state.</summary>
public interface ILauncherObserver
{
    void OnPhase(LauncherPhaseEvent phase);
}

public sealed class LauncherBootstrapException : Exception
{
    public LauncherBootstrapException(string message) : base(message) { }

    public LauncherBootstrapException(string message, Exception innerException) : base(message, innerException) { }
}
