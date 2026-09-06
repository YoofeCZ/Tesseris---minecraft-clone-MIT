namespace Tesseris.ModApi;

/// <summary>Stable handle for one logical stack, independent of inventory slot moves.</summary>
public readonly record struct ModStackId(ulong High, ulong Low);

public readonly record struct ModStackComponentValue(ResourceId ComponentId, ModSerializedValue Value);

/// <summary>
/// Immutable item-stack snapshot. Component entries are sorted by ID and cannot be modified in place.
/// Stack mutations always create a later revision through a command buffer.
/// </summary>
public sealed record ModItemStackSnapshot(
    ModStackId Id,
    ResourceId ItemId,
    int Count,
    uint Revision,
    IReadOnlyList<ModStackComponentValue> Components);

public sealed record ModStackComponentDescriptor(
    ResourceId Id,
    ResourceId SerializerId,
    bool CopyWhenSplit = true,
    bool RequireEqualToMerge = true);

/// <summary>
/// Deferred, game-thread stack changes. Expected revisions provide optimistic concurrency; a rejected
/// command leaves the stack unchanged. Commands commit in callback registration order and issue order.
/// </summary>
public interface IModStackCommandBuffer
{
    void SetComponent(ModStackId stack, uint expectedRevision, ModStackComponentValue component);

    void RemoveComponent(ModStackId stack, uint expectedRevision, ResourceId componentId);

    void SetCount(ModStackId stack, uint expectedRevision, int count);
}

public interface IModStackPlatform
{
    void RegisterComponent(ModStackComponentDescriptor descriptor);

    bool TryGet(ModStackId id, out ModItemStackSnapshot? stack);

    IModStackCommandBuffer CreateCommandBuffer();
}
