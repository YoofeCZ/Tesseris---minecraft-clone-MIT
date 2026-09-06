namespace Tesseris.ModApi;

public readonly record struct ModContainerId(ulong High, ulong Low);

public readonly record struct ModMenuSessionId(ulong High, ulong Low);

public sealed record ModContainerSlotSnapshot(
    int Index,
    ModItemStackSnapshot? Stack,
    bool CanInsert,
    bool CanExtract);

/// <summary>Immutable, revisioned container snapshot with slots ordered by index.</summary>
public sealed record ModContainerSnapshot(
    ModContainerId Id,
    ResourceId TypeId,
    uint Revision,
    IReadOnlyList<ModContainerSlotSnapshot> Slots,
    IReadOnlyList<ModComponentValue> Components);

public sealed record ModContainerTypeDefinition(
    ResourceId Id,
    int SlotCount,
    ResourceId? StateSerializerId = null);

public sealed record ModMenuDefinition(
    ResourceId Id,
    ResourceId ContainerTypeId,
    ResourceId ScreenId);

public sealed record ModMenuSessionSnapshot(
    ModMenuSessionId Id,
    ResourceId MenuId,
    ModContainerSnapshot Container,
    uint Revision,
    bool IsOpen);

public sealed record ModMenuAction(
    ResourceId Id,
    uint ExpectedSessionRevision,
    ModSerializedValue? Payload = null);

public interface IModContainerCommandBuffer
{
    void SetSlot(ModContainerId container, uint expectedRevision, int slot, ModItemStackSnapshot? stack);

    void SetState(ModContainerId container, uint expectedRevision, ModComponentValue state);

    void Close(ModMenuSessionId session, uint expectedRevision);
}

public interface IModMenuHandler
{
    ModActionResult Handle(ModMenuSessionSnapshot session, ModMenuAction action, IModContainerCommandBuffer commands);
}

/// <summary>
/// Generic container and menu-session platform. Definitions freeze before play. Session actions execute on
/// the game thread in receive order; revision mismatch rejects the complete action. Networked and loopback
/// sessions use the same serialized action contract.
/// </summary>
public interface IModContainerPlatform
{
    void RegisterContainerType(ModContainerTypeDefinition definition);

    void RegisterMenu(ModMenuDefinition definition, IModMenuHandler handler);

    bool TryOpen(ResourceId menuId, ModContainerId container, out ModMenuSessionSnapshot? session);

    bool TryGetSession(ModMenuSessionId id, out ModMenuSessionSnapshot? session);
}
