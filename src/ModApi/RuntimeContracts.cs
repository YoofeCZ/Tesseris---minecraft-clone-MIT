namespace Tesseris.ModApi;

/// <summary>A renderer-independent position used by the public mod API.</summary>
public readonly record struct ModVector3(float X, float Y, float Z);

/// <summary>
/// Live access to the running game. The object itself remains stable for the lifetime of the
/// mod host while <see cref="World"/> changes as saves are opened and closed.
/// </summary>
public interface IModGame
{
    bool IsWorldLoaded { get; }

    IModWorld? World { get; }

    IModPlayer Player { get; }

    IModInventory Inventory { get; }
}

/// <summary>Stable-ID access to loaded world data.</summary>
public interface IModWorld
{
    /// <summary>Returns the stable ID at a loaded position, or air outside the loaded world.</summary>
    ResourceId GetBlockId(int x, int y, int z);

    /// <summary>
    /// Changes a block only when the coordinate, stable ID and target chunk are valid and loaded.
    /// This never creates a chunk merely because a mod wrote into an unloaded area.
    /// </summary>
    bool TrySetBlockId(int x, int y, int z, ResourceId blockId);

    bool IsChunkLoaded(int chunkX, int chunkY, int chunkZ);
}

public interface IModPlayer
{
    ModVector3 Position { get; }

    float Health { get; }

    bool IsDead { get; }

    /// <summary>Teleports the attached player, returning false while no player is available.</summary>
    bool Teleport(ModVector3 position);
}

public interface IModInventory
{
    int SlotCount => 0;

    int SelectedSlot => -1;

    int CountOf(ResourceId itemId);

    /// <summary>
    /// Adds items and returns the count that did not fit. An unknown ID or unavailable inventory
    /// leaves the complete requested count over.
    /// </summary>
    int Add(ResourceId itemId, int count);

    /// <summary>Removes all requested items atomically, or returns false without changing inventory.</summary>
    bool Remove(ResourceId itemId, int count);

    /// <summary>Reads one immutable player-inventory stack including arbitrary serialized components.</summary>
    bool TryGetSlot(int slot, out ModInventoryStackSnapshot? stack)
    {
        stack = null;
        return false;
    }

    /// <summary>
    /// Atomically replaces a slot when it still equals <paramref name="expected"/>. A null replacement clears
    /// the slot. This is the general data-component boundary used by stateful tools and total conversions.
    /// </summary>
    bool TryReplaceSlot(
        int slot,
        ModInventoryStackSnapshot? expected,
        ModInventoryStackSnapshot? replacement) => false;
}

public sealed record ModInventoryStackSnapshot(
    int Slot,
    ResourceId ItemId,
    int Count,
    int Damage,
    IReadOnlyList<ModStackComponentValue> Components);
