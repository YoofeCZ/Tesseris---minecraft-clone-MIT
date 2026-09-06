using Tesseris.ModApi;

namespace Tesseris.Loader.Abstractions;

/// <summary>
/// Entry point executed in dependency/load-plan order before the Game assembly is loaded. It is trusted code,
/// runs once on the launcher thread and is not unloadable until process exit.
/// </summary>
public interface IPreLaunchMod
{
    void PreLaunch(IPreLaunchContext context);
}

/// <summary>
/// Stronger prelaunch entry point permitted to register Game method transformations. A coremod is fully
/// trusted native-equivalent code, is never sandboxed and requires restart when its patch plan changes.
/// </summary>
public interface ICoreMod
{
    void ConfigureCore(ICoreModContext context);
}

public interface IPreLaunchContext
{
    ModPackageDescriptor Mod { get; }

    ModLoaderEnvironment Environment { get; }

    ModLoadPlan LoadPlan { get; }

    ILoaderCapabilityProvider Capabilities { get; }

    IModServiceRegistry Services { get; }

    IModLogger Logger { get; }
}

public interface ICoreModContext : IPreLaunchContext
{
    IModPatchRegistry Patches { get; }
}
