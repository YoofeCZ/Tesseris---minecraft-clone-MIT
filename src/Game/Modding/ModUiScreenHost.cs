using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>Identifies the single modal mod screen currently owned by the game.</summary>
public readonly record struct ActiveModScreen(string ModId, ResourceId ScreenId);

public enum ModUiScreenCallbackPhase
{
    Opened,
    Closed,
    Input,
    Draw
}

/// <summary>Preserves screen ownership when a callback crosses the public mod boundary.</summary>
public sealed class ModUiScreenCallbackException : Exception
{
    public ModUiScreenCallbackException(
        string modId,
        ResourceId screenId,
        ModUiScreenCallbackPhase phase,
        Exception innerException)
        : base($"Mod '{modId}' screen '{screenId}' failed during {phase.ToString().ToLowerInvariant()}.", innerException)
    {
        ModId = modId;
        ScreenId = screenId;
        Phase = phase;
    }

    public string ModId { get; }

    public ResourceId ScreenId { get; }

    public ModUiScreenCallbackPhase Phase { get; }
}

/// <summary>
/// Game-thread host for renderer-independent modal mod screens. Registrations are owner scoped,
/// while the active modal is global so two mods can never receive the same input simultaneously.
/// </summary>
public sealed class ModUiScreenHost : IDisposable
{
    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ResourceId, Registration> registrations = new();
    private Registration? active;
    private bool disposed;

    public bool IsFrozen { get; private set; }

    public ActiveModScreen? ActiveScreen => active is null
        ? null
        : new ActiveModScreen(active.ModId, active.ScreenId);

    public bool HasActiveScreen => active is not null;

    public ResourceId? ActiveScreenId => active?.ScreenId;

    /// <summary>Raised after the observable active modal changes, including failed opens and shutdown.</summary>
    public event Action<ActiveModScreen?>? ActiveScreenChanged;

    internal OwnerView ForMod(string modId)
    {
        EnsureMainThread();
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        return new OwnerView(this, modId);
    }

    public void Freeze()
    {
        EnsureMainThread();
        ThrowIfDisposed();
        IsFrozen = true;
    }

    /// <summary>Passes one input event to the active modal, or returns Pass when no modal is open.</summary>
    public ModActionResult DispatchInput(ModUiInput input)
    {
        EnsureMainThread();
        ThrowIfDisposed();
        Registration? current = active;
        return current is null
            ? ModActionResult.Pass
            : Invoke(current, ModUiScreenCallbackPhase.Input, () => current.Screen.HandleInput(input));
    }

    public void Draw(IModUiCanvas canvas)
    {
        EnsureMainThread();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(canvas);
        Registration? current = active;
        if (current is not null)
        {
            Invoke(current, ModUiScreenCallbackPhase.Draw, () => current.Screen.Draw(canvas));
        }
    }

    /// <summary>Engine-side escape/focus path that closes whichever mod owns the active modal.</summary>
    public bool CloseActiveScreen()
    {
        EnsureMainThread();
        ThrowIfDisposed();
        if (active is null)
        {
            return false;
        }

        CloseCurrent();
        return true;
    }

    /// <summary>Closes the global modal during game/mod shutdown. Safe to call repeatedly.</summary>
    public void Shutdown()
    {
        EnsureMainThread();
        if (disposed)
        {
            return;
        }

        try
        {
            CloseCurrent();
        }
        finally
        {
            disposed = true;
            registrations.Clear();
        }
    }

    public void Dispose()
    {
        Shutdown();
        GC.SuppressFinalize(this);
    }

    private void Register(string modId, ResourceId id, IModScreen screen)
    {
        EnsureMainThread();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(screen);
        if (IsFrozen)
        {
            throw new InvalidOperationException("Mod screen registration is frozen.");
        }

        if (!id.Value.StartsWith(modId + ":", StringComparison.Ordinal))
        {
            throw new ModHostException($"Mod '{modId}' may only register UI screens in its own namespace.");
        }

        if (!registrations.TryAdd(id, new Registration(modId, id, screen)))
        {
            Registration owner = registrations[id];
            throw new ModHostException($"UI screen '{id}' is already registered by '{owner.ModId}'.");
        }
    }

    private bool Open(string modId, ResourceId id)
    {
        EnsureMainThread();
        ThrowIfDisposed();
        if (!registrations.TryGetValue(id, out Registration? target)
            || !string.Equals(target.ModId, modId, StringComparison.Ordinal))
        {
            return false;
        }

        if (ReferenceEquals(active, target))
        {
            return true;
        }

        CloseCurrent();
        active = target;
        try
        {
            Invoke(target, ModUiScreenCallbackPhase.Opened, target.Screen.OnOpened);
        }
        catch
        {
            active = null;
            ActiveScreenChanged?.Invoke(null);
            throw;
        }

        ActiveScreenChanged?.Invoke(ActiveScreen);
        return true;
    }

    private bool Close(string modId)
    {
        EnsureMainThread();
        ThrowIfDisposed();
        if (active is null || !string.Equals(active.ModId, modId, StringComparison.Ordinal))
        {
            return false;
        }

        CloseCurrent();
        return true;
    }

    private void CloseCurrent()
    {
        Registration? previous = active;
        if (previous is null)
        {
            return;
        }

        active = null;
        try
        {
            Invoke(previous, ModUiScreenCallbackPhase.Closed, previous.Screen.OnClosed);
        }
        finally
        {
            ActiveScreenChanged?.Invoke(null);
        }
    }

    private static void Invoke(
        Registration registration,
        ModUiScreenCallbackPhase phase,
        Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception exception)
        {
            throw new ModUiScreenCallbackException(
                registration.ModId,
                registration.ScreenId,
                phase,
                exception);
        }
    }

    private static T Invoke<T>(
        Registration registration,
        ModUiScreenCallbackPhase phase,
        Func<T> callback)
    {
        try
        {
            return callback();
        }
        catch (Exception exception)
        {
            throw new ModUiScreenCallbackException(
                registration.ModId,
                registration.ScreenId,
                phase,
                exception);
        }
    }

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
        {
            throw new InvalidOperationException("Mod UI screens must be accessed on the main game thread.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed record Registration(string ModId, ResourceId ScreenId, IModScreen Screen);

    internal sealed class OwnerView
    {
        private readonly ModUiScreenHost host;
        private readonly string modId;

        internal OwnerView(ModUiScreenHost host, string modId)
        {
            this.host = host;
            this.modId = modId;
        }

        public ResourceId? OpenScreenId => host.ActiveScreenId;

        public void RegisterScreen(ResourceId id, IModScreen screen) => host.Register(modId, id, screen);

        public bool OpenScreen(ResourceId id) => host.Open(modId, id);

        public bool CloseScreen() => host.Close(modId);
    }
}
