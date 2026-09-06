using System.Text;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding.Client;

public enum ModCommandStatus
{
    Executed,
    UnknownCommand,
    ParseError,
    NotHandled,
    Disabled
}

public sealed record ModCommandExecutionResult(
    ModCommandStatus Status,
    ResourceId? CommandId,
    ModActionResult Result,
    string? Diagnostic);

public sealed record RegisteredModCommand(
    string ModId,
    ResourceId Id,
    string Usage,
    int Priority,
    IModCommandHandler Handler);

/// <summary>Deterministic namespaced command parser, registry and dispatcher.</summary>
public sealed class ModCommandHost
{
    private readonly ClientThreadGuard guard;
    private readonly bool enabled;
    private readonly Dictionary<ResourceId, RegisteredModCommand> registrations = [];
    private RegisteredModCommand[] frozen = [];

    internal ModCommandHost(ClientThreadGuard guard, bool enabled)
    {
        this.guard = guard;
        this.enabled = enabled;
    }

    public bool IsFrozen { get; private set; }

    public IReadOnlyList<RegisteredModCommand> Registrations => IsFrozen
        ? Array.AsReadOnly(frozen)
        : Array.AsReadOnly(Ordered());

    internal IModCommandRegistry ForMod(string modId) => new View(this, modId);

    internal void Freeze()
    {
        guard.Ensure();
        if (IsFrozen) return;
        frozen = Ordered();
        IsFrozen = true;
    }

    public ModCommandExecutionResult Execute(
        string commandLine,
        IModCommandOutput output,
        ulong? playerId = null,
        bool isRemote = false)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(output);
        if (!enabled)
        {
            return new ModCommandExecutionResult(
                ModCommandStatus.Disabled, null, ModActionResult.Pass, "Client commands are unavailable.");
        }

        if (!TryTokenize(commandLine, out string[] tokens, out string? error))
        {
            output.Error(error!);
            return new ModCommandExecutionResult(
                ModCommandStatus.ParseError, null, ModActionResult.Denied, error);
        }

        if (tokens.Length == 0 || !TryParseId(tokens[0], out ResourceId commandId))
        {
            const string diagnostic = "Expected a namespaced command such as '/example:command'.";
            output.Error(diagnostic);
            return new ModCommandExecutionResult(
                ModCommandStatus.ParseError, null, ModActionResult.Denied, diagnostic);
        }

        if (!registrations.TryGetValue(commandId, out RegisteredModCommand? registration))
        {
            string[] suggestions = Complete(tokens[0], maximum: 5).ToArray();
            string diagnostic = suggestions.Length == 0
                ? $"Unknown command '{commandId}'."
                : $"Unknown command '{commandId}'. Did you mean {string.Join(", ", suggestions)}?";
            output.Error(diagnostic);
            return new ModCommandExecutionResult(
                ModCommandStatus.UnknownCommand, commandId, ModActionResult.Denied, diagnostic);
        }

        string[] argumentArray = tokens[1..];
        var invocation = new ModCommandInvocation(
            commandId,
            Array.AsReadOnly(argumentArray),
            playerId,
            isRemote);
        ModActionResult result;
        try
        {
            result = registration.Handler.Execute(invocation, output);
        }
        catch (Exception exception)
        {
            throw new ModCommandCallbackException(registration.ModId, commandId, exception);
        }

        return new ModCommandExecutionResult(
            result == ModActionResult.Pass ? ModCommandStatus.NotHandled : ModCommandStatus.Executed,
            commandId,
            result,
            result == ModActionResult.Pass ? $"Command '{commandId}' was not handled." : null);
    }

    public IReadOnlyList<string> Complete(string input, int maximum = 16)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(input);
        if (maximum <= 0 || !enabled) return [];

        string prefix = input.TrimStart();
        if (prefix.StartsWith("/", StringComparison.Ordinal)) prefix = prefix[1..];
        int whitespace = prefix.IndexOfAny([' ', '\t', '\r', '\n']);
        if (whitespace >= 0) prefix = prefix[..whitespace];

        return Array.AsReadOnly((IsFrozen ? frozen : Ordered())
            .Where(command => command.Id.Value.StartsWith(prefix, StringComparison.Ordinal))
            .Take(maximum)
            .Select(command => "/" + command.Id.Value)
            .ToArray());
    }

    public string? Usage(ResourceId commandId)
    {
        guard.Ensure();
        return registrations.TryGetValue(commandId, out RegisteredModCommand? registration)
            ? registration.Usage
            : null;
    }

    private void Register(string modId, ResourceId id, string usage, int priority, IModCommandHandler handler)
    {
        guard.Ensure();
        ArgumentNullException.ThrowIfNull(handler);
        if (!enabled) return;
        if (IsFrozen) throw new InvalidOperationException("Mod command registration is frozen.");
        ClientRegistration.EnsureOwned(modId, id, "command");
        ArgumentException.ThrowIfNullOrWhiteSpace(usage);
        string normalizedUsage = usage.Trim();
        if (!registrations.TryAdd(id, new RegisteredModCommand(modId, id, normalizedUsage, priority, handler)))
            throw new InvalidOperationException($"Command '{id}' is already registered.");
    }

    private RegisteredModCommand[] Ordered() => registrations.Values
        .OrderBy(value => value.Priority)
        .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
        .ToArray();

    private static bool TryParseId(string token, out ResourceId id)
    {
        try
        {
            string normalized = token.StartsWith("/", StringComparison.Ordinal) ? token[1..] : token;
            id = new ResourceId(normalized);
            return true;
        }
        catch (ArgumentException)
        {
            id = default;
            return false;
        }
    }

    internal static bool TryTokenize(string? commandLine, out string[] tokens, out string? error)
    {
        tokens = [];
        error = null;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return true;
        }

        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        bool escaping = false;
        bool tokenStarted = false;
        foreach (char character in commandLine.Trim())
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
                tokenStarted = true;
                continue;
            }

            if (character == '\\')
            {
                escaping = true;
                tokenStarted = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (tokenStarted)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            current.Append(character);
            tokenStarted = true;
        }

        if (escaping)
        {
            error = "A command cannot end with an escape character.";
            return false;
        }

        if (quoted)
        {
            error = "A quoted command argument is not terminated.";
            return false;
        }

        if (tokenStarted) result.Add(current.ToString());
        tokens = result.ToArray();
        return true;
    }

    private sealed class View : IModCommandRegistry
    {
        private readonly ModCommandHost host;
        private readonly string modId;
        public View(ModCommandHost host, string modId) { this.host = host; this.modId = modId; }
        public void Register(ResourceId id, string usage, int priority, IModCommandHandler handler) =>
            host.Register(modId, id, usage, priority, handler);
    }
}
