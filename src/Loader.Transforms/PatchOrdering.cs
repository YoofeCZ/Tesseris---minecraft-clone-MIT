using Tesseris.Loader.Abstractions;
using Tesseris.ModApi;

namespace Tesseris.Loader.Transforms;

internal static class PatchOrdering
{
    public static IReadOnlyList<ModMethodPatchDescriptor> Order(
        IReadOnlyList<ModMethodPatchDescriptor> patches,
        ModLoadPlan plan)
    {
        var loadIndex = plan.Entries.ToDictionary(entry => entry.Package.Id, entry => entry.LoadIndex, StringComparer.Ordinal);
        var byId = new Dictionary<ResourceId, ModMethodPatchDescriptor>();
        foreach (ModMethodPatchDescriptor patch in patches)
        {
            if (!loadIndex.ContainsKey(patch.OwnerModId))
                throw Error(patch, $"owner is absent from load plan '{plan.Fingerprint}'");
            ModLoadPlanEntry owner = plan.Entries.Single(entry => entry.Package.Id == patch.OwnerModId);
            if (!owner.TrustDecision.Allowed || owner.Package.Trust != ModTrustLevel.CoreMod)
                throw Error(patch, "owner is not an explicitly trusted CoreMod load-plan entry");
            if (!patch.Id.Value.StartsWith(patch.OwnerModId + ":", StringComparison.Ordinal))
                throw Error(patch, "patch ID is outside the owner's namespace");
            if (!byId.TryAdd(patch.Id, patch)) throw Error(patch, "patch ID is duplicated");
        }

        var outgoing = byId.Keys.ToDictionary(id => id, _ => new HashSet<ResourceId>(), EqualityComparer<ResourceId>.Default);
        var indegree = byId.Keys.ToDictionary(id => id, _ => 0, EqualityComparer<ResourceId>.Default);
        foreach (ModMethodPatchDescriptor patch in patches)
        {
            foreach (ResourceId before in patch.Before)
            {
                EnsureKnown(patch, before, byId);
                AddEdge(patch.Id, before, outgoing, indegree);
            }
            foreach (ResourceId after in patch.After)
            {
                EnsureKnown(patch, after, byId);
                AddEdge(after, patch.Id, outgoing, indegree);
            }
        }

        var comparer = Comparer<ResourceId>.Create((left, right) =>
        {
            if (left == right) return 0;
            ModMethodPatchDescriptor a = byId[left];
            ModMethodPatchDescriptor b = byId[right];
            int result = loadIndex[a.OwnerModId].CompareTo(loadIndex[b.OwnerModId]);
            if (result != 0) return result;
            result = a.Priority.CompareTo(b.Priority);
            return result != 0 ? result : string.CompareOrdinal(a.Id.Value, b.Id.Value);
        });
        var ready = new SortedSet<ResourceId>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key), comparer);
        var ordered = new List<ModMethodPatchDescriptor>(patches.Count);
        while (ready.Count > 0)
        {
            ResourceId id = ready.Min;
            ready.Remove(id);
            ordered.Add(byId[id]);
            foreach (ResourceId next in outgoing[id].OrderBy(value => value, comparer))
                if (--indegree[next] == 0) ready.Add(next);
        }
        if (ordered.Count != patches.Count)
        {
            string cycle = string.Join(", ", indegree.Where(pair => pair.Value > 0)
                .Select(pair => pair.Key.Value).Order(StringComparer.Ordinal));
            throw new CoreModTransformationException($"Coremod patch ordering cycle detected: {cycle}.");
        }
        return ordered.ToArray();
    }

    private static void EnsureKnown(
        ModMethodPatchDescriptor owner,
        ResourceId referenced,
        IReadOnlyDictionary<ResourceId, ModMethodPatchDescriptor> known)
    {
        if (!known.ContainsKey(referenced)) throw Error(owner, $"ordering references unknown patch '{referenced}'");
    }

    private static void AddEdge(
        ResourceId before,
        ResourceId after,
        Dictionary<ResourceId, HashSet<ResourceId>> outgoing,
        Dictionary<ResourceId, int> indegree)
    {
        if (outgoing[before].Add(after)) indegree[after]++;
    }

    internal static CoreModTransformationException Error(ModMethodPatchDescriptor patch, string reason) =>
        new($"Coremod '{patch.OwnerModId}' patch '{patch.Id}' is invalid: {reason}.");
}
