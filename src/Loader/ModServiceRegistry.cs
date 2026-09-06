using System.Reflection;
using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;

namespace Tesseris.Loader;

/// <summary>
/// Owner-scoped inter-mod service directory. It isolates ownership and lifetime but does not sandbox service
/// implementations. The host must call <see cref="UnregisterOwner"/> before unloading a provider context.
/// </summary>
public sealed class ModServiceRegistry
{
    private readonly int ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ResourceId, Registration> registrations = new();
    private readonly HashSet<string> approvedContractAssemblies;

    public ModServiceRegistry(IEnumerable<Assembly>? sharedContractAssemblies = null)
    {
        approvedContractAssemblies = new HashSet<string>(StringComparer.Ordinal)
        {
            typeof(IModServiceRegistry).Assembly.GetName().Name!,
            typeof(IPreLaunchMod).Assembly.GetName().Name!,
        };
        if (sharedContractAssemblies is not null)
        {
            foreach (Assembly assembly in sharedContractAssemblies)
            {
                string? name = assembly.GetName().Name;
                if (!string.IsNullOrWhiteSpace(name)) approvedContractAssemblies.Add(name);
            }
        }
    }

    public IReadOnlyList<ModServiceDescriptor> Published
    {
        get
        {
            EnsureOwnerThread();
            return registrations.Values
                .Select(registration => registration.Descriptor)
                .OrderBy(descriptor => descriptor.Id.Value, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IModServiceRegistry ForOwner(string modId)
    {
        EnsureOwnerThread();
        ValidateModId(modId);
        return new OwnerView(this, modId);
    }

    public int UnregisterOwner(string modId)
    {
        EnsureOwnerThread();
        ValidateModId(modId);
        ResourceId[] owned = registrations.Values
            .Where(registration => string.Equals(registration.Descriptor.OwnerModId, modId, StringComparison.Ordinal))
            .Select(registration => registration.Descriptor.Id)
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToArray();
        foreach (ResourceId id in owned) registrations.Remove(id);
        return owned.Length;
    }

    private void Publish<TContract>(string owner, ResourceId id, string version, TContract service)
        where TContract : class
    {
        EnsureOwnerThread();
        ArgumentNullException.ThrowIfNull(service);
        EnsureOwned(owner, id);
        string normalizedVersion = SemanticVersion.Parse(version).ToString();
        Type contract = typeof(TContract);
        string contractAssembly = contract.Assembly.GetName().Name ?? string.Empty;
        if (!approvedContractAssemblies.Contains(contractAssembly))
        {
            throw new LoaderException(
                $"Mod '{owner}' cannot publish service '{id}' through contract '{contract.FullName}' from "
                + $"unapproved assembly '{contractAssembly}'. Declare and hash-negotiate it as a shared contract first.");
        }
        if (!contract.IsInstanceOfType(service))
            throw new LoaderException($"Mod '{owner}' service '{id}' does not implement '{contract.FullName}'.");
        var descriptor = new ModServiceDescriptor(id, normalizedVersion, owner, contract);
        if (!registrations.TryAdd(id, new Registration(descriptor, service)))
        {
            ModServiceDescriptor existing = registrations[id].Descriptor;
            throw new LoaderException(
                $"Service '{id}' from mod '{owner}' duplicates service owned by '{existing.OwnerModId}'.");
        }
    }

    private bool TryGet<TContract>(ResourceId id, string versionConstraint, out TContract? service)
        where TContract : class
    {
        EnsureOwnerThread();
        VersionConstraint constraint = VersionConstraint.Parse(versionConstraint);
        if (registrations.TryGetValue(id, out Registration? registration)
            && constraint.Allows(registration.Descriptor.Version)
            && registration.Service is TContract typed)
        {
            service = typed;
            return true;
        }
        service = null;
        return false;
    }

    private bool Remove(string owner, ResourceId id)
    {
        EnsureOwnerThread();
        return registrations.TryGetValue(id, out Registration? registration)
            && string.Equals(registration.Descriptor.OwnerModId, owner, StringComparison.Ordinal)
            && registrations.Remove(id);
    }

    private static void EnsureOwned(string owner, ResourceId id)
    {
        if (!id.Value.StartsWith(owner + ":", StringComparison.Ordinal))
            throw new LoaderException($"Mod '{owner}' may only publish services in its own namespace, not '{id}'.");
    }

    private static void ValidateModId(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        if (modId.Any(character =>
                !((character >= 'a' && character <= 'z') || char.IsDigit(character) || character is '_' or '-' or '.')))
            throw new ArgumentException($"Invalid normalized mod ID '{modId}'.", nameof(modId));
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException("Inter-mod services must be accessed on the loader/game owner thread.");
    }

    private sealed record Registration(ModServiceDescriptor Descriptor, object Service);

    private sealed class OwnerView : IModServiceRegistry
    {
        private readonly ModServiceRegistry registry;
        private readonly string owner;

        public OwnerView(ModServiceRegistry registry, string owner)
        {
            this.registry = registry;
            this.owner = owner;
        }

        public IReadOnlyList<ModServiceDescriptor> Published => registry.Published;

        public void Publish<TContract>(ResourceId id, string version, TContract service) where TContract : class =>
            registry.Publish(owner, id, version, service);

        public bool TryGet<TContract>(ResourceId id, string versionConstraint, out TContract? service)
            where TContract : class => registry.TryGet(id, versionConstraint, out service);

        public bool Remove(ResourceId id) => registry.Remove(owner, id);
    }
}
