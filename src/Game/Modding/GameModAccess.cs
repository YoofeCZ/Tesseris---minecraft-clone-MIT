using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.Player;
using Tesseris.Game.World;
using Tesseris.ModApi;
using ModResourceId = Tesseris.ModApi.ResourceId;

namespace Tesseris.Game.Modding;

/// <summary>
/// Dynamic facade between managed mods and the running game. It deliberately implements only
/// ModApi interfaces so game and OpenTK types never cross the public assembly boundary.
/// </summary>
public sealed class GameModAccess : IModGame, IModWorld, IModPlayer, IModInventory
{
    private static readonly ModResourceId AirId = new("tesseris:air");

    private readonly int mainThreadId = Environment.CurrentManagedThreadId;
    private BlockRegistry? blocks;
    private ItemRegistry? items;
    private Inventory? inventory;
    private PlayerController? player;
    private Vitals? vitals;
    private VoxelWorld? world;
    private ChunkStreamer? streamer;

    /// <summary>Raised after a successful mod-initiated block replacement on the game thread.</summary>
    public event Action<ModBlockPosition, ModResourceId, ModResourceId>? BlockChanged;

    public bool IsWorldLoaded => world is not null;

    public IModWorld? World => world is null ? null : this;

    public IModPlayer Player => this;

    public IModInventory Inventory => this;

    public int SlotCount => inventory is null ? 0 : Tesseris.Game.Items.Inventory.AllSlots;

    public int SelectedSlot => inventory?.Selected ?? -1;

    public ModVector3 Position
    {
        get
        {
            Vector3 value = player?.Position ?? Vector3.Zero;
            return new ModVector3(value.X, value.Y, value.Z);
        }
    }

    public float Health => vitals?.Health ?? 0f;

    public bool IsDead => vitals?.Dead ?? true;

    /// <summary>Attaches registries and the player state that survive save changes.</summary>
    public void AttachStaticContent(
        BlockRegistry blockRegistry,
        ItemRegistry itemRegistry,
        Inventory playerInventory,
        PlayerController playerController,
        Vitals playerVitals)
    {
        EnsureMainThread();
        blocks = blockRegistry ?? throw new ArgumentNullException(nameof(blockRegistry));
        items = itemRegistry ?? throw new ArgumentNullException(nameof(itemRegistry));
        inventory = playerInventory ?? throw new ArgumentNullException(nameof(playerInventory));
        player = playerController ?? throw new ArgumentNullException(nameof(playerController));
        vitals = playerVitals ?? throw new ArgumentNullException(nameof(playerVitals));
    }

    /// <summary>Attaches the world for the currently open save.</summary>
    public void AttachWorld(VoxelWorld voxelWorld, ChunkStreamer? chunkStreamer = null)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(voxelWorld);
        if (blocks is not null && !ReferenceEquals(voxelWorld.Registry, blocks))
        {
            throw new ArgumentException(
                "The world must use the block registry attached to this mod facade.",
                nameof(voxelWorld));
        }

        world = voxelWorld;
        streamer = chunkStreamer;
    }

    /// <summary>Makes all previously handed-out mod contexts observe that no save is open.</summary>
    public void DetachWorld()
    {
        EnsureMainThread();
        streamer = null;
        world = null;
    }

    public ModResourceId GetBlockId(int x, int y, int z)
    {
        VoxelWorld? currentWorld = world;
        BlockRegistry? currentBlocks = blocks;
        if (currentWorld is null || currentBlocks is null || !IsWorldY(y))
        {
            return AirId;
        }

        Vector3i chunk = VoxelWorld.ToChunkPosition(x, y, z);
        if (!currentWorld.HasChunk(chunk))
        {
            return AirId;
        }

        ushort block = currentWorld.GetBlock(x, y, z);
        if (block >= currentBlocks.Count)
        {
            return AirId;
        }

        return new ModResourceId(currentBlocks.Definition(block).Id);
    }

    public bool TrySetBlockId(int x, int y, int z, ModResourceId blockId)
    {
        EnsureMainThread();
        VoxelWorld? currentWorld = world;
        BlockRegistry? currentBlocks = blocks;
        string? stableId = blockId.Value;
        if (currentWorld is null || currentBlocks is null || !IsWorldY(y)
            || string.IsNullOrWhiteSpace(stableId)
            || !currentBlocks.TryIndexOf(stableId, out ushort block))
        {
            return false;
        }

        Vector3i chunk = VoxelWorld.ToChunkPosition(x, y, z);
        if (!currentWorld.HasChunk(chunk))
        {
            return false;
        }

        if (currentWorld.GetBlock(x, y, z) == block)
        {
            return true;
        }

        ushort previous = currentWorld.GetBlock(x, y, z);
        currentWorld.SetBlock(x, y, z, block);
        streamer?.InvalidateBlock(x, y, z);

        ushort actual = currentWorld.GetBlock(x, y, z);
        if (actual != previous)
        {
            BlockChanged?.Invoke(
                new ModBlockPosition(x, y, z),
                new ModResourceId(currentBlocks.Definition(previous).Id),
                new ModResourceId(currentBlocks.Definition(actual).Id));
        }

        return true;
    }

    public bool IsChunkLoaded(int chunkX, int chunkY, int chunkZ)
    {
        VoxelWorld? currentWorld = world;
        return currentWorld is not null
            && chunkY >= 0
            && chunkY < TerrainGenerator.WorldHeightChunks
            && currentWorld.HasChunk(new Vector3i(chunkX, chunkY, chunkZ));
    }

    public bool Teleport(ModVector3 position)
    {
        EnsureMainThread();
        PlayerController? currentPlayer = player;
        if (currentPlayer is null || !IsFinite(position))
        {
            return false;
        }

        currentPlayer.Teleport(new Vector3(position.X, position.Y, position.Z));
        return true;
    }

    public int CountOf(ModResourceId itemId)
    {
        ItemRegistry? currentItems = items;
        Inventory? currentInventory = inventory;
        string? stableId = itemId.Value;
        if (currentItems is null || currentInventory is null || string.IsNullOrWhiteSpace(stableId))
        {
            return 0;
        }

        int item = currentItems.IndexOf(stableId);
        return item == ItemRegistry.Nothing ? 0 : currentInventory.CountOf(item);
    }

    public int Add(ModResourceId itemId, int count)
    {
        EnsureMainThread();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return 0;
        }

        ItemRegistry? currentItems = items;
        Inventory? currentInventory = inventory;
        string? stableId = itemId.Value;
        if (currentItems is null || currentInventory is null || string.IsNullOrWhiteSpace(stableId))
        {
            return count;
        }

        int item = currentItems.IndexOf(stableId);
        if (item == ItemRegistry.Nothing)
        {
            return count;
        }

        return currentInventory.Add(new ItemStack(item, count, Damage: 0)).Count;
    }

    public bool Remove(ModResourceId itemId, int count)
    {
        EnsureMainThread();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ItemRegistry? currentItems = items;
        Inventory? currentInventory = inventory;
        string? stableId = itemId.Value;
        if (currentItems is null || currentInventory is null || string.IsNullOrWhiteSpace(stableId))
        {
            return false;
        }

        int item = currentItems.IndexOf(stableId);
        return item != ItemRegistry.Nothing && currentInventory.Remove(item, count);
    }

    public bool TryGetSlot(int slot, out ModInventoryStackSnapshot? stack)
    {
        Inventory? currentInventory = inventory;
        ItemRegistry? currentItems = items;
        if (currentInventory is null || currentItems is null
            || slot < 0 || slot >= Tesseris.Game.Items.Inventory.AllSlots)
        {
            stack = null;
            return false;
        }

        ItemStack current = currentInventory[slot];
        if (current.IsEmpty)
        {
            stack = null;
            return false;
        }

        stack = Snapshot(slot, current, currentItems);
        return true;
    }

    public bool TryReplaceSlot(
        int slot,
        ModInventoryStackSnapshot? expected,
        ModInventoryStackSnapshot? replacement)
    {
        EnsureMainThread();
        Inventory? currentInventory = inventory;
        ItemRegistry? currentItems = items;
        if (currentInventory is null || currentItems is null
            || slot < 0 || slot >= Tesseris.Game.Items.Inventory.AllSlots)
        {
            return false;
        }

        ItemStack current = currentInventory[slot];
        ModInventoryStackSnapshot? actual = current.IsEmpty ? null : Snapshot(slot, current, currentItems);
        if (!SnapshotEquals(actual, expected))
        {
            return false;
        }

        if (replacement is null)
        {
            currentInventory[slot] = ItemStack.Empty;
            return true;
        }

        if (replacement.Slot != slot || replacement.Count <= 0 || replacement.Damage < 0
            || replacement.Components is null || replacement.Components.Count > 1024)
        {
            return false;
        }

        int item = currentItems.IndexOf(replacement.ItemId.Value);
        if (item == ItemRegistry.Nothing || replacement.Count > currentItems.Definition(item).MaxStack)
        {
            return false;
        }

        ItemComponentMap components = ItemComponentMap.Empty;
        var seen = new HashSet<ModResourceId>();
        foreach (ModStackComponentValue component in replacement.Components)
        {
            if (!seen.Add(component.ComponentId))
            {
                return false;
            }

            try
            {
                components = components.Set(component.ComponentId, component.Value);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        var value = new ItemStack(item, replacement.Count, replacement.Damage, components);
        if (slot >= Tesseris.Game.Items.Inventory.FirstArmourSlot
            && !currentInventory.FitsArmourSlot(
                value,
                slot - Tesseris.Game.Items.Inventory.FirstArmourSlot))
        {
            return false;
        }

        currentInventory[slot] = value;
        return true;
    }

    private static ModInventoryStackSnapshot Snapshot(int slot, ItemStack stack, ItemRegistry registry) => new(
        slot,
        new ModResourceId(registry.Definition(stack.Item).Id),
        stack.Count,
        stack.Damage,
        stack.Components.Values()
            .Select(value => new ModStackComponentValue(value.Id, value.Value))
            .ToArray());

    private static bool SnapshotEquals(
        ModInventoryStackSnapshot? left,
        ModInventoryStackSnapshot? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Slot != right.Slot || left.ItemId != right.ItemId
            || left.Count != right.Count || left.Damage != right.Damage
            || left.Components.Count != right.Components.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Components.Count; index++)
        {
            ModStackComponentValue first = left.Components[index];
            ModStackComponentValue second = right.Components[index];
            if (first.ComponentId != second.ComponentId
                || first.Value.SerializerId != second.Value.SerializerId
                || first.Value.SchemaVersion != second.Value.SchemaVersion
                || !first.Value.Payload.Span.SequenceEqual(second.Value.Payload.Span))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWorldY(int y) => y >= 0 && y < TerrainGenerator.WorldHeight;

    private static bool IsFinite(ModVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != mainThreadId)
        {
            throw new InvalidOperationException("Mod game mutations must run on the main game thread.");
        }
    }
}
