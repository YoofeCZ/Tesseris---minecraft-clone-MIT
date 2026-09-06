namespace Tesseris.ModApi;

/// <summary>
/// Stable access point for mod-owned data in the current world. All members are game-thread-only.
/// <see cref="IsWorldOpen"/> must be checked again after lifecycle callbacks because containers are
/// scoped to one open world and must not be retained after it closes.
/// </summary>
public interface IModData
{
    bool IsWorldOpen { get; }

    /// <summary>The current world's data container, or null when no world is open.</summary>
    IModDataContainer? World { get; }

    /// <summary>
    /// Returns data attached to a block position in the current world. Returns null when no world is open.
    /// Position data remains opaque to the game and may exist independently of the block at that position.
    /// </summary>
    IModDataContainer? At(ModBlockPosition position);
}

/// <summary>
/// A namespaced opaque byte store. Keys must belong to the owning mod's namespace. Reads return
/// immutable snapshots; writes are persisted with the next world save. Access is game-thread-only
/// and enumeration order is the ordinal order of <see cref="ResourceId.Value"/>.
/// </summary>
public interface IModDataContainer
{
    IReadOnlyCollection<ResourceId> Keys { get; }

    bool TryGet(ResourceId key, out ReadOnlyMemory<byte> value);

    void Set(ResourceId key, ReadOnlySpan<byte> value);

    bool Remove(ResourceId key);
}

/// <summary>A short-lived world lifecycle context valid only during its game-thread callback.</summary>
public interface IModWorldLifecycleContext
{
    IModData Data { get; }
}
