using Tesseris.ModApi;

namespace Tesseris.Game.Modding.Client;

internal sealed class ClientThreadGuard
{
    private readonly int threadId = Environment.CurrentManagedThreadId;

    public void Ensure()
    {
        if (Environment.CurrentManagedThreadId != threadId)
        {
            throw new InvalidOperationException("Mod client operations must run on the client game thread.");
        }
    }
}

internal static class ClientRegistration
{
    public static string NormalizeModId(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        string normalized = modId.Trim().ToLowerInvariant();
        _ = new ResourceId(normalized + ":validation");
        return normalized;
    }

    public static void EnsureOwned(string modId, ResourceId id, string kind)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException($"A non-default {kind} ID is required.", nameof(id));
        }

        int separator = id.Value.IndexOf(':');
        if (!id.Value.AsSpan(0, separator).Equals(modId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Mod '{modId}' may only register {kind} IDs in its own namespace.");
        }
    }

    public static int Compare(int leftPriority, ResourceId leftId, int rightPriority, ResourceId rightId)
    {
        int comparison = leftPriority.CompareTo(rightPriority);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(leftId.Value, rightId.Value);
    }

    public static ModSerializedValue? Clone(ModSerializedValue? value) => value is not { } source
        ? null
        : new ModSerializedValue(source.SerializerId, source.SchemaVersion, source.Payload.ToArray());
}

public sealed class ModInputCallbackException : Exception
{
    public ModInputCallbackException(
        string modId,
        ResourceId bindingId,
        ModInputPhase phase,
        Exception innerException)
        : base($"Mod '{modId}' input binding '{bindingId}' failed during {phase}.", innerException)
    {
        ModId = modId;
        BindingId = bindingId;
        Phase = phase;
    }

    public string ModId { get; }
    public ResourceId BindingId { get; }
    public ModInputPhase Phase { get; }
}

public sealed class ModCommandCallbackException : Exception
{
    public ModCommandCallbackException(string modId, ResourceId commandId, Exception innerException)
        : base($"Mod '{modId}' command '{commandId}' failed.", innerException)
    {
        ModId = modId;
        CommandId = commandId;
    }

    public string ModId { get; }
    public ResourceId CommandId { get; }
}

public enum ModClientUiCallbackPhase
{
    Create,
    Opened,
    Closed,
    Input,
    Draw,
    Hud
}

public sealed class ModClientUiCallbackException : Exception
{
    public ModClientUiCallbackException(
        string modId,
        ResourceId registrationId,
        ModClientUiCallbackPhase phase,
        Exception innerException)
        : base($"Mod '{modId}' client UI '{registrationId}' failed during {phase}.", innerException)
    {
        ModId = modId;
        RegistrationId = registrationId;
        Phase = phase;
    }

    public string ModId { get; }
    public ResourceId RegistrationId { get; }
    public ModClientUiCallbackPhase Phase { get; }
}

public sealed class ModRenderCallbackException : Exception
{
    public ModRenderCallbackException(
        string modId,
        ResourceId registrationId,
        ModRenderPhase phase,
        Exception innerException)
        : base($"Mod '{modId}' render callback '{registrationId}' failed during {phase}.", innerException)
    {
        ModId = modId;
        RegistrationId = registrationId;
        Phase = phase;
    }

    public string ModId { get; }
    public ResourceId RegistrationId { get; }
    public ModRenderPhase Phase { get; }
}
