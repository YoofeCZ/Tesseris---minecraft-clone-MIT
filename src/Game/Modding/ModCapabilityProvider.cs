using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>Immutable owner-specific capability snapshot exposed through <see cref="IModContextV2"/>.</summary>
internal sealed class ModCapabilityProvider : IModCapabilityProvider
{
    private readonly IReadOnlyDictionary<ResourceId, Entry> capabilities;

    public ModCapabilityProvider(IEnumerable<(ModCapabilityDescriptor Descriptor, object Value)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var entries = new Dictionary<ResourceId, Entry>();
        foreach ((ModCapabilityDescriptor descriptor, object value) in values)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(value);
            if (!descriptor.ContractType.IsInstanceOfType(value))
            {
                throw new ArgumentException(
                    $"Capability '{descriptor.Id}' value does not implement '{descriptor.ContractType.FullName}'.",
                    nameof(values));
            }

            if (!entries.TryAdd(descriptor.Id, new Entry(descriptor, value)))
            {
                throw new ArgumentException($"Capability '{descriptor.Id}' is registered twice.", nameof(values));
            }
        }

        capabilities = entries;
        Available = Array.AsReadOnly(entries.Values
            .Select(entry => entry.Descriptor)
            .OrderBy(descriptor => descriptor.Id.Value, StringComparer.Ordinal)
            .ToArray());
    }

    public IReadOnlyList<ModCapabilityDescriptor> Available { get; }

    public bool TryGet<TContract>(ResourceId id, out TContract? capability) where TContract : class
    {
        if (capabilities.TryGetValue(id, out Entry? entry) && entry.Value is TContract typed)
        {
            capability = typed;
            return true;
        }

        capability = null;
        return false;
    }

    private sealed record Entry(ModCapabilityDescriptor Descriptor, object Value);
}
