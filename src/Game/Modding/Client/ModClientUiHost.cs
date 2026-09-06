using Tesseris.ModApi;

namespace Tesseris.Game.Modding.Client;

public sealed record RegisteredModScreenV2(
    string ModId,
    ResourceId Id,
    IModScreenFactory Factory);

public sealed record RegisteredModHud(
    string ModId,
    ResourceId Id,
    ModHudAnchor Anchor,
    int Priority,
    IModHudLayer Layer);

public readonly record struct ActiveModScreenV2(string ModId, ResourceId ScreenId);

/// <summary>Factory-based modal screen and deterministic HUD registry.</summary>
public sealed class ModClientUiHost
{
    private readonly ClientThreadGuard guard;
    private readonly bool enabled;
    private readonly Dictionary<ResourceId, RegisteredModScreenV2> screens = [];
    private readonly Dictionary<ResourceId, RegisteredModHud> hud = [];
    private RegisteredModHud[] frozenHud = [];
    private ActiveSession? active;

    internal ModClientUiHost(ClientThreadGuard guard, bool enabled)
    {
        this.guard = guard;
        this.enabled = enabled;
    }

    public bool IsFrozen { get; private set; }
    public bool HasActiveScreen => active is not null;
    public ActiveModScreenV2? ActiveScreen => active is null
        ? null
        : new ActiveModScreenV2(active.Registration.ModId, active.Registration.Id);
    public IReadOnlyList<RegisteredModScreenV2> Screens => Array.AsReadOnly(screens.Values
        .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToArray());
    public IReadOnlyList<RegisteredModHud> HudLayers => IsFrozen
        ? Array.AsReadOnly(frozenHud)
        : Array.AsReadOnly(OrderedHud());

    public event Action<ActiveModScreenV2?>? ActiveScreenChanged;

    internal IModClientUiRegistry ForMod(string modId) => new View(this, modId);

    internal void Freeze()
    {
        guard.Ensure();
        if (IsFrozen) return;
        frozenHud = OrderedHud();
        IsFrozen = true;
    }

    public ModActionResult DispatchInput(ModUiInput input)
    {
        guard.Ensure();
        if (!enabled || active is not { } current) return ModActionResult.Pass;
        try
        {
            return current.Screen.HandleInput(input);
        }
        catch (Exception exception)
        {
            throw Failure(current.Registration, ModClientUiCallbackPhase.Input, exception);
        }
    }

    public void DrawActiveScreen(IModUiCanvas canvas)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(canvas);
        if (!enabled || active is not { } current) return;
        try
        {
            current.Screen.Draw(canvas);
        }
        catch (Exception exception)
        {
            throw Failure(current.Registration, ModClientUiCallbackPhase.Draw, exception);
        }
    }

    public void DrawHud(ModHudAnchor anchor, IModUiCanvas canvas)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(canvas);
        if (!enabled) return;
        if (!Enum.IsDefined(anchor)) throw new ArgumentOutOfRangeException(nameof(anchor));
        foreach (RegisteredModHud registration in frozenHud)
        {
            if (registration.Anchor != anchor) continue;
            DrawHud(registration, canvas);
        }
    }

    public void DrawAllHud(IModUiCanvas canvas)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(canvas);
        if (!enabled) return;
        foreach (RegisteredModHud registration in frozenHud) DrawHud(registration, canvas);
    }

    public bool CloseActiveScreen()
    {
        guard.Ensure();
        if (!enabled || active is null) return false;
        CloseCurrent();
        return true;
    }

    private void RegisterScreen(string modId, ResourceId id, IModScreenFactory factory)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(factory);
        if (!enabled) return;
        EnsureMutable();
        ClientRegistration.EnsureOwned(modId, id, "client screen");
        if (!screens.TryAdd(id, new RegisteredModScreenV2(modId, id, factory)))
            throw new InvalidOperationException($"Client screen '{id}' is already registered.");
    }

    private void RegisterHud(string modId, ResourceId id, ModHudAnchor anchor, int priority, IModHudLayer layer)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(layer);
        if (!enabled) return;
        EnsureMutable();
        ClientRegistration.EnsureOwned(modId, id, "HUD layer");
        if (!Enum.IsDefined(anchor)) throw new ArgumentOutOfRangeException(nameof(anchor));
        if (!hud.TryAdd(id, new RegisteredModHud(modId, id, anchor, priority, layer)))
            throw new InvalidOperationException($"HUD layer '{id}' is already registered.");
    }

    private bool Open(string modId, ModScreenOpenRequest request)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(request);
        if (!enabled || !screens.TryGetValue(request.ScreenId, out RegisteredModScreenV2? registration)
            || !string.Equals(registration.ModId, modId, StringComparison.Ordinal))
        {
            return false;
        }

        CloseCurrent();
        IModScreenV2 screen;
        try
        {
            screen = registration.Factory.Create()
                ?? throw new InvalidOperationException("The mod screen factory returned null.");
        }
        catch (Exception exception)
        {
            throw Failure(registration, ModClientUiCallbackPhase.Create, exception);
        }

        var ownedRequest = new ModScreenOpenRequest(request.ScreenId, ClientRegistration.Clone(request.Arguments));
        active = new ActiveSession(registration, screen);
        try
        {
            screen.OnOpened(ownedRequest);
        }
        catch (Exception exception)
        {
            active = null;
            ActiveScreenChanged?.Invoke(null);
            throw Failure(registration, ModClientUiCallbackPhase.Opened, exception);
        }

        ActiveScreenChanged?.Invoke(ActiveScreen);
        return true;
    }

    private bool Close(string modId)
    {
        guard.Ensure();
        if (!enabled || active is null
            || !string.Equals(active.Registration.ModId, modId, StringComparison.Ordinal))
        {
            return false;
        }

        CloseCurrent();
        return true;
    }

    private void CloseCurrent()
    {
        ActiveSession? previous = active;
        if (previous is null) return;
        active = null;
        try
        {
            previous.Screen.OnClosed();
        }
        catch (Exception exception)
        {
            throw Failure(previous.Registration, ModClientUiCallbackPhase.Closed, exception);
        }
        finally
        {
            ActiveScreenChanged?.Invoke(null);
        }
    }

    private static void DrawHud(RegisteredModHud registration, IModUiCanvas canvas)
    {
        try
        {
            registration.Layer.Draw(canvas);
        }
        catch (Exception exception)
        {
            throw Failure(registration, ModClientUiCallbackPhase.Hud, exception);
        }
    }

    private void EnsureMutable()
    {
        if (IsFrozen) throw new InvalidOperationException("Mod client UI registration is frozen.");
    }

    private RegisteredModHud[] OrderedHud() => hud.Values
        .OrderBy(value => value.Priority)
        .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
        .ToArray();

    private static ModClientUiCallbackException Failure(
        RegisteredModScreenV2 registration,
        ModClientUiCallbackPhase phase,
        Exception exception) => new(registration.ModId, registration.Id, phase, exception);

    private static ModClientUiCallbackException Failure(
        RegisteredModHud registration,
        ModClientUiCallbackPhase phase,
        Exception exception) => new(registration.ModId, registration.Id, phase, exception);

    private sealed record ActiveSession(RegisteredModScreenV2 Registration, IModScreenV2 Screen);

    private sealed class View : IModClientUiRegistry
    {
        private readonly ModClientUiHost host;
        private readonly string modId;
        public View(ModClientUiHost host, string modId) { this.host = host; this.modId = modId; }
        public void RegisterScreen(ResourceId id, IModScreenFactory factory) => host.RegisterScreen(modId, id, factory);
        public void RegisterHud(ResourceId id, ModHudAnchor anchor, int priority, IModHudLayer layer) =>
            host.RegisterHud(modId, id, anchor, priority, layer);
        public bool Open(ModScreenOpenRequest request) => host.Open(modId, request);
        public bool Close() => host.Close(modId);
    }
}
