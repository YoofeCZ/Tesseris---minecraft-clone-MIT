using Tesseris.ModApi;

namespace Tesseris.Game.Modding.Client;

public enum ModInputDispatchContext
{
    Gameplay,
    Screen
}

public sealed record RegisteredModInput(
    string ModId,
    ModInputBinding Binding,
    int Priority,
    IModInputHandler Handler);

/// <summary>Action-based input dispatcher independent of windowing-library key types.</summary>
public sealed class ModInputHost
{
    private readonly ClientThreadGuard guard;
    private readonly bool enabled;
    private readonly Dictionary<ResourceId, RegisteredModInput> registrations = [];
    private RegisteredModInput[] frozen = [];

    internal ModInputHost(ClientThreadGuard guard, bool enabled)
    {
        this.guard = guard;
        this.enabled = enabled;
    }

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<RegisteredModInput> Registrations => IsFrozen
        ? Array.AsReadOnly(frozen)
        : Array.AsReadOnly(registrations.Values.OrderBy(value => value.Priority)
            .ThenBy(value => value.Binding.Id.Value, StringComparer.Ordinal).ToArray());

    internal IModInputRegistry ForMod(string modId) => new View(this, modId);

    internal void Freeze()
    {
        guard.Ensure();
        if (IsFrozen) return;
        frozen = registrations.Values
            .OrderBy(value => value.Priority)
            .ThenBy(value => value.Binding.Id.Value, StringComparer.Ordinal)
            .ToArray();
        IsFrozen = true;
    }

    public ModActionResult DispatchKey(
        int keyCode,
        ModInputPhase phase,
        ModUiModifiers modifiers,
        ModInputDispatchContext context,
        bool modalCaptured,
        float value = 1f)
    {
        guard.Ensure();
        ValidateEvent(phase, value);
        if (!enabled || keyCode == 0 || frozen.Length == 0)
        {
            return ModActionResult.Pass;
        }

        foreach (RegisteredModInput registration in frozen)
        {
            if (registration.Binding.DefaultKeyCode != keyCode
                || registration.Binding.DefaultModifiers != modifiers
                || !Receives(registration.Binding.Scope, context, modalCaptured))
            {
                continue;
            }

            ModActionResult result = Invoke(
                registration,
                new ModInputActionEvent(registration.Binding.Id, phase, value, modifiers));
            if (result != ModActionResult.Pass)
            {
                return result;
            }
        }

        return ModActionResult.Pass;
    }

    public ModActionResult DispatchAction(
        ResourceId bindingId,
        ModInputPhase phase,
        float value,
        ModUiModifiers modifiers,
        ModInputDispatchContext context,
        bool modalCaptured)
    {
        guard.Ensure();
        ValidateEvent(phase, value);
        if (!enabled || !registrations.TryGetValue(bindingId, out RegisteredModInput? registration)
            || !Receives(registration.Binding.Scope, context, modalCaptured))
        {
            return ModActionResult.Pass;
        }

        return Invoke(registration, new ModInputActionEvent(bindingId, phase, value, modifiers));
    }

    private void Register(string modId, ModInputBinding binding, int priority, IModInputHandler handler)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(handler);
        if (!enabled) return;
        if (IsFrozen) throw new InvalidOperationException("Mod input registration is frozen.");
        ClientRegistration.EnsureOwned(modId, binding.Id, "input binding");
        if (string.IsNullOrWhiteSpace(binding.DisplayName))
            throw new ArgumentException("An input binding display name is required.", nameof(binding));
        if (!Enum.IsDefined(binding.Scope)) throw new ArgumentOutOfRangeException(nameof(binding));
        if (binding.DefaultKeyCode < 0) throw new ArgumentOutOfRangeException(nameof(binding));
        if (!registrations.TryAdd(binding.Id, new RegisteredModInput(modId, binding, priority, handler)))
            throw new InvalidOperationException($"Input binding '{binding.Id}' is already registered.");
    }

    private static bool Receives(
        ModInputScope scope,
        ModInputDispatchContext context,
        bool modalCaptured) => scope switch
    {
        ModInputScope.Global => true,
        ModInputScope.Gameplay => context == ModInputDispatchContext.Gameplay && !modalCaptured,
        ModInputScope.Screen => context == ModInputDispatchContext.Screen && modalCaptured,
        _ => false
    };

    private static ModActionResult Invoke(RegisteredModInput registration, ModInputActionEvent input)
    {
        try
        {
            return registration.Handler.Handle(input);
        }
        catch (Exception exception)
        {
            throw new ModInputCallbackException(
                registration.ModId,
                registration.Binding.Id,
                input.Phase,
                exception);
        }
    }

    private static void ValidateEvent(ModInputPhase phase, float value)
    {
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private sealed class View : IModInputRegistry
    {
        private readonly ModInputHost host;
        private readonly string modId;
        public View(ModInputHost host, string modId) { this.host = host; this.modId = modId; }
        public void Register(ModInputBinding binding, int priority, IModInputHandler handler) =>
            host.Register(modId, binding, priority, handler);
    }
}
