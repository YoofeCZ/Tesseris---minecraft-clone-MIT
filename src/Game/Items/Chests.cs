using OpenTK.Mathematics;

namespace Tesseris.Game.Items;

public sealed class Chest
{
    public const int SlotCount = 27;
    public ItemStack[] Slots { get; } = new ItemStack[SlotCount];
    public bool IsEmpty => Array.TrueForAll(Slots, stack => stack.IsEmpty);
    public bool LootInitialized { get; internal set; }
}

/// <summary>
/// Jeden otevřený panel truhly. Jednoduchá truhla zpřístupní 27 slotů, dvojtruhla
/// spojí dvě samostatně uložené poloviny do jednoho 54slotového pohledu.
/// </summary>
public sealed class ChestContainer
{
    public ChestContainer(Chest first, Chest? second = null)
    {
        First = first ?? throw new ArgumentNullException(nameof(first));
        if (ReferenceEquals(first, second)) throw new ArgumentException("Obě poloviny truhly musí být různé.", nameof(second));
        Second = second;
    }

    public Chest First { get; }
    public Chest? Second { get; }
    public bool IsDouble => Second is not null;
    public int SlotCount => IsDouble ? Chest.SlotCount * 2 : Chest.SlotCount;

    public ItemStack this[int index]
    {
        get
        {
            Chest chest = Resolve(index, out int local);
            return chest.Slots[local];
        }
        set
        {
            Chest chest = Resolve(index, out int local);
            chest.Slots[local] = value;
        }
    }

    private Chest Resolve(int index, out int local)
    {
        if ((uint)index >= (uint)SlotCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (index < Chest.SlotCount)
        {
            local = index;
            return First;
        }

        local = index - Chest.SlotCount;
        return Second!;
    }
}

/// <summary>Perzistentni uloziste truhel, klicovane pozici bloku.</summary>
public sealed class Chests
{
    public const string FileName = "chests.dat";
    private const uint LegacyMagic = 0x31534843; // CHS1
    private const uint Magic = 0x32534843; // CHS2
    private const int MaximumSavedChests = 1_000_000;
    private readonly Dictionary<Vector3i, Chest> chests = [];
    private readonly ItemRegistry items;

    public Chests(ItemRegistry items) => this.items = items ?? throw new ArgumentNullException(nameof(items));
    public int Count => chests.Count;

    public Chest At(Vector3i block)
    {
        if (!chests.TryGetValue(block, out Chest? chest)) chests.Add(block, chest = new Chest());
        return chest;
    }

    public Chest AtLoot(Vector3i block, int worldSeed)
    {
        Chest chest = At(block);
        if (chest.LootInitialized) return chest;

        chest.LootInitialized = true;
        ulong random = World.StructureGenerator.Hash(worldSeed, block.X, block.Y, block.Z);
        AddLoot(chest, "tesseris:apple", 2 + Next(ref random, 5), ref random);
        AddLoot(chest, "tesseris:coal", 4 + Next(ref random, 9), ref random);

        string[] common = [
            "tesseris:flint", "tesseris:iron_ingot", "tesseris:copper_ingot",
            "tesseris:redstone_dust", "tesseris:lapis_lazuli", "tesseris:gold_ingot",
            "tesseris:flint_pickaxe", "tesseris:flint_axe"
        ];
        int rolls = 2 + Next(ref random, 4);
        for (int roll = 0; roll < rolls; roll++)
        {
            string id = common[Next(ref random, common.Length)];
            int count = id.EndsWith("pickaxe", StringComparison.Ordinal)
                || id.EndsWith("_axe", StringComparison.Ordinal) ? 1 : 1 + Next(ref random, 5);
            AddLoot(chest, id, count, ref random);
        }

        if (Next(ref random, 8) == 0) AddLoot(chest, "tesseris:diamond", 1, ref random);
        if (Next(ref random, 10) == 0) AddLoot(chest, "tesseris:emerald", 1 + Next(ref random, 2), ref random);
        return chest;
    }

    private void AddLoot(Chest chest, string id, int count, ref ulong random)
    {
        int item = items.IndexOf(id);
        if (item == ItemRegistry.Nothing) return;
        int start = Next(ref random, Chest.SlotCount);
        for (int offset = 0; offset < Chest.SlotCount; offset++)
        {
            int slot = (start + offset) % Chest.SlotCount;
            if (chest.Slots[slot].IsEmpty)
            {
                chest.Slots[slot] = new ItemStack(item, Math.Min(count, items.Definition(item).MaxStack), 0);
                return;
            }
        }
    }

    private static int Next(ref ulong state, int maximum)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong value = state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return (int)(value % (uint)maximum);
    }

    public IEnumerable<ItemStack> Remove(Vector3i block) =>
        chests.Remove(block, out Chest? chest) ? chest.Slots : [];

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            List<KeyValuePair<Vector3i, Chest>> full = [.. chests.Where(pair => !pair.Value.IsEmpty || pair.Value.LootInitialized)
                .OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.Z)];
            if (full.Count == 0) { File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(full.Count);
                foreach ((Vector3i position, Chest chest) in full)
                {
                    writer.Write(position.X); writer.Write(position.Y); writer.Write(position.Z);
                    writer.Write(chest.LootInitialized);
                    foreach (ItemStack stack in chest.Slots) ItemStackCodec.Write(writer, stack, items);
                }
            }
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Engine.Core.Log.Warn($"Truhly se nepodarilo ulozit: {error.Message}"); }
    }

    public int Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) return 0;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            uint magic = reader.ReadUInt32();
            if (magic is not (Magic or LegacyMagic)) throw new InvalidDataException("Neznamy format truhel.");
            int count = reader.ReadInt32();
            if (count is < 0 or > MaximumSavedChests) throw new InvalidDataException("Neplatny pocet truhel.");
            chests.Clear();
            for (int i = 0; i < count; i++)
            {
                var position = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                var chest = new Chest();
                if (magic == Magic) chest.LootInitialized = reader.ReadBoolean();
                for (int slot = 0; slot < Chest.SlotCount; slot++) chest.Slots[slot] = ItemStackCodec.Read(reader, items);
                if (!chest.IsEmpty || chest.LootInitialized) chests[position] = chest;
            }
            return chests.Count;
        }
        catch (Exception error) when (error is IOException or EndOfStreamException or InvalidDataException)
        { Engine.Core.Log.Warn($"Truhly se nepodarilo nacist: {error.Message}"); chests.Clear(); return 0; }
    }
}
