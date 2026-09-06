using Tesseris.ModApi;

namespace Tesseris.Game.Modding.Client;

public sealed record RegisteredModRenderCallback(
    string ModId,
    ResourceId Id,
    ModRenderPhase Phase,
    int Priority,
    IModRenderCallback Callback);

public sealed class ModRenderHost
{
    private readonly ClientThreadGuard guard;
    private readonly bool enabled;
    private readonly Dictionary<ResourceId, RegisteredModRenderCallback> registrations = [];
    private RegisteredModRenderCallback[] frozen = [];

    internal ModRenderHost(ClientThreadGuard guard, bool enabled)
    {
        this.guard = guard;
        this.enabled = enabled;
    }

    public bool IsFrozen { get; private set; }
    public IReadOnlyList<RegisteredModRenderCallback> Registrations => IsFrozen
        ? Array.AsReadOnly(frozen)
        : Array.AsReadOnly(Ordered());
    internal IModRenderRegistry ForMod(string modId) => new View(this, modId);

    internal void Freeze()
    {
        guard.Ensure();
        if (IsFrozen) return;
        frozen = Ordered();
        IsFrozen = true;
    }

    public void Dispatch(
        ModRenderPhase phase,
        ModRenderViewSnapshot view,
        IModRenderCommandBuffer commands)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(commands);
        if (!enabled || frozen.Length == 0) return;
        foreach (RegisteredModRenderCallback registration in frozen)
        {
            if (registration.Phase != phase) continue;
            try
            {
                registration.Callback.Record(view, commands);
            }
            catch (Exception exception)
            {
                throw new ModRenderCallbackException(
                    registration.ModId, registration.Id, phase, exception);
            }
        }
    }

    private void Register(
        string modId,
        ResourceId id,
        ModRenderPhase phase,
        int priority,
        IModRenderCallback callback)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(callback);
        if (!enabled) return;
        if (IsFrozen) throw new InvalidOperationException("Mod render registration is frozen.");
        ClientRegistration.EnsureOwned(modId, id, "render callback");
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (!registrations.TryAdd(id, new RegisteredModRenderCallback(modId, id, phase, priority, callback)))
            throw new InvalidOperationException($"Render callback '{id}' is already registered.");
    }

    private RegisteredModRenderCallback[] Ordered() => registrations.Values
        .OrderBy(value => value.Phase)
        .ThenBy(value => value.Priority)
        .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
        .ToArray();

    private sealed class View : IModRenderRegistry
    {
        private readonly ModRenderHost host;
        private readonly string modId;
        public View(ModRenderHost host, string modId) { this.host = host; this.modId = modId; }
        public void Register(ResourceId id, ModRenderPhase phase, int priority, IModRenderCallback callback) =>
            host.Register(modId, id, phase, priority, callback);
    }
}
