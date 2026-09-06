namespace Tesseris.ModApi;

public enum ModInputScope
{
    Gameplay,
    Screen,
    Global
}

public sealed record ModInputBinding(
    ResourceId Id,
    string DisplayName,
    ModInputScope Scope,
    int DefaultKeyCode = 0,
    ModUiModifiers DefaultModifiers = ModUiModifiers.None);

public readonly record struct ModInputActionEvent(
    ResourceId BindingId,
    ModInputPhase Phase,
    float Value,
    ModUiModifiers Modifiers);

public interface IModInputHandler
{
    ModActionResult Handle(ModInputActionEvent input);
}

public sealed record ModCommandInvocation(
    ResourceId CommandId,
    IReadOnlyList<string> Arguments,
    ulong? PlayerId,
    bool IsRemote);

public interface IModCommandOutput
{
    void Reply(string text);

    void Error(string text);
}

public interface IModCommandHandler
{
    ModActionResult Execute(ModCommandInvocation invocation, IModCommandOutput output);
}

/// <summary>
/// Client input registration. Bindings freeze at client startup. Handlers run on the client game thread in
/// priority and ID order; global bindings may receive input while a modal screen is open.
/// </summary>
public interface IModInputRegistry
{
    void Register(ModInputBinding binding, int priority, IModInputHandler handler);
}

/// <summary>
/// Namespaced command registry shared by local and network command sources. Parsing is deterministic and
/// handlers never receive engine console/player objects.
/// </summary>
public interface IModCommandRegistry
{
    void Register(ResourceId id, string usage, int priority, IModCommandHandler handler);
}
