using Silk.NET.Vulkan;
using Tesseris.Engine.Core;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Zásobník paměti zařízení, ze kterého se buffery vykrajují jako úseky.
///
/// <para><b>Proč to je potřeba.</b> Původně měl každý buffer vlastní <c>vkAllocateMemory</c>.
/// Naměřeno: <b>5477 alokací a přes gigabajt paměti zařízení</b>, tedy zhruba 190 kB na
/// buffer, přestože skutečná data jsou zlomek — režie alokace a zarovnání spolkly zbytek.
/// Vulkan má navíc na počet alokací tvrdý strop (<c>maxMemoryAllocationCount</c>, běžně
/// 4096), takže tudy vedla i cesta k pádu při větším dohledu.</para>
///
/// <para><b>Jak to funguje.</b> Alokují se velké bloky (<see cref="BlockSize"/>) a v každém
/// se drží seznam volných úseků. Přidělení je first-fit, uvolnění vrací úsek zpět a slučuje
/// ho se sousedy. Blok se namapuje jednou a mapování se nikdy neruší — Vulkan to výslovně
/// dovoluje a mapovat kolem každého zápisu je zbytečná režie.</para>
///
/// <para><b>Souběh.</b> Buffery se zakládají výhradně z hlavního vlákna (nahrávání na GPU),
/// ale zámek tu je pro jistotu — cena je zanedbatelná proti volání do ovladače.</para>
/// </summary>
public sealed unsafe class VulkanMemoryPool : IDisposable
{
    /// <summary>
    /// Velikost jednoho bloku. 64 MB je kompromis: větší blok znamená míň alokací, ale
    /// hůř se vrací operačnímu systému, když se dohled zmenší.
    /// </summary>
    // Zkoušeno i 256 MB, aby bylo alokací čtvrtina. Zhoršilo to: p99 vyskočilo z 17-25
    // na 26-30 ms a nejdelší čekání na grafiku ze 30 na 64 ms. Větší blok znamená delší
    // jednotlivé zastavení, a těch pár alokací navíc se proti tomu ztratí.
    private const ulong BlockSize = 64UL * 1024 * 1024;

    private readonly VulkanContext _context;
    private readonly Dictionary<uint, List<Block>> _blocksByType = [];
    private readonly object _lock = new();

    private long _usedBytes;
    private long _reservedBytes;

    /// <summary>
    /// Blok připravený dopředu na vedlejším vlákně, ještě než ho někdo potřebuje.
    /// </summary>
    /// <remarks>
    /// <para><b>Tohle je oprava zbývajících záseků.</b> Naměřeno, že <c>vkAllocateMemory</c>
    /// spolu s <c>vkMapMemory</c> umí zastavit hlavní vlákno na <b>35 ms</b> — tedy jeden
    /// snímek za osmadvacet za vteřinu, uprostřed běhu, kde jinak jede přes šest set.
    /// Zbytek alokací trvá dvě desetiny milisekundy, takže je to nepravidelné a projevuje
    /// se to jako náhodné cuknutí.</para>
    ///
    /// <para>Zvětšit blok nepomůže — už to bylo zkoušeno a zhoršilo to (viz
    /// <see cref="BlockSize"/>). Jediná cesta je nechat alokaci proběhnout <b>mimo</b>
    /// kritickou cestu: jakmile se jeden blok spotřebuje, začne se na pozadí připravovat
    /// další, a až na něj dojde řada, je hotový a jen se převezme.</para>
    ///
    /// <para>Cena je jeden blok navíc v paměti, tedy 64 MB.</para>
    /// </remarks>
    private Block? _spare;
    private uint _spareType;
    private bool _sparePending;

    public VulkanMemoryPool(VulkanContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>Kolik bajtů je skutečně přiděleno bufferům.</summary>
    public long UsedBytes => Interlocked.Read(ref _usedBytes);

    /// <summary>Kolik bajtů drží bloky. Rozdíl proti <see cref="UsedBytes"/> je nevyužitá rezerva.</summary>
    public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

    /// <summary>Nejdelší založení bloku. Kandidát na příčinu hitchů, proto se to měří.</summary>
    public double WorstBlockMs { get; private set; }

    /// <summary>
    /// Zadá přípravu dalšího bloku na vedlejším vlákně, pokud už jedna neběží.
    /// </summary>
    /// <remarks>
    /// <para>Volá se <b>uvnitř zámku</b>, ale samotná alokace běží mimo něj — jinak by
    /// vedlejší vlákno drželo zámek těch pětatřicet milisekund a hlavní vlákno by na něm
    /// čekalo přesně tak dlouho, jako kdyby alokovalo samo.</para>
    ///
    /// <para><c>vkAllocateMemory</c> a <c>vkMapMemory</c> smí Vulkan volat z libovolného
    /// vlákna; vnější synchronizaci vyžadují jen operace nad jedním konkrétním objektem,
    /// a tady vzniká pokaždé nový.</para>
    /// </remarks>
    private void EnsureSpare(uint memoryType)
    {
        if (_sparePending || (_spare is not null && _spareType == memoryType))
        {
            return;
        }

        _sparePending = true;

        _ = Task.Run(() =>
        {
            Block? prepared = Block.TryCreate(_context, memoryType, BlockSize);

            lock (_lock)
            {
                _sparePending = false;

                // Mezitím mohla vzniknout jiná rezerva, nebo se příprava nezdařila.
                // V obou případech se ta nová zahodí — držet dvě je zbytečná paměť.
                if (prepared is null)
                {
                    return;
                }

                if (_spare is null)
                {
                    _spare = prepared;
                    _spareType = memoryType;
                }
                else
                {
                    prepared.Dispose(_context);
                }
            }
        });
    }

    /// <summary>Kolik bloků paměti zařízení existuje. Tohle je počet skutečných alokací.</summary>
    public int BlockCount
    {
        get
        {
            lock (_lock)
            {
                int total = 0;
                foreach (List<Block> blocks in _blocksByType.Values)
                {
                    total += blocks.Count;
                }

                return total;
            }
        }
    }

    /// <summary>
    /// Přidělí úsek. Velikost i zarovnání přicházejí z požadavků bufferu.
    /// </summary>
    /// <param name="preferredType">
    /// Paměťový typ, který se má použít přednostně — typicky ten, který je zároveň
    /// <c>DEVICE_LOCAL</c>, tedy skutečně v paměti grafiky.
    /// </param>
    /// <param name="fallbackType">
    /// Náhrada pro případ, že se do preferované haldy už nevejde. Bez resizable BAR má
    /// halda <c>DEVICE_LOCAL | HOST_VISIBLE</c> jen 256 MB a geometrie se do ní nevejde
    /// celá; pak je pomalejší paměť pořád lepší než pád.
    /// </param>
    public Allocation Allocate(uint preferredType, uint fallbackType, ulong size, ulong alignment)
    {
        lock (_lock)
        {
            if (TryTakeFrom(preferredType, size, alignment, out Allocation existing)
                || (fallbackType != preferredType && TryTakeFrom(fallbackType, size, alignment, out existing)))
            {
                return existing;
            }

            // Nic se nevešlo. Blok musí pojmout i buffer větší, než je běžná velikost bloku.
            ulong blockSize = Math.Max(BlockSize, RoundUp(size + alignment, BlockSize));

            // Doba alokace se měří schválně: vkAllocateMemory a vkMapMemory umí zastavit
            // hlavní vlákno na jednotky až desítky milisekund a je to jeden z podezřelých
            // u zbývajících hitchů. Bez čísla se to nedá tvrdit ani vyvrátit.
            long allocationStart = System.Diagnostics.Stopwatch.GetTimestamp();

            uint usedType = preferredType;
            Block? fresh = null;

            // PŘIPRAVENÝ BLOK SE VEZME MÍSTO NOVÉ ALOKACE.
            //
            // Tohle je celý smysl přípravy dopředu: převzetí je jen přiřazení ukazatele,
            // zatímco vkAllocateMemory na tomtéž místě umělo stát pětatřicet milisekund.
            //
            // Bere se jen když sedí typ paměti a velikost stačí — u nadměrného bufferu
            // (blockSize vyšlo větší než BlockSize) se rezerva nepoužije a alokuje se
            // rovnou, protože takový případ je vzácný a připravovat na něj nejde.
            if (_spare is not null && _spareType == preferredType && blockSize <= BlockSize)
            {
                fresh = _spare;
                blockSize = BlockSize;
                _spare = null;
            }

            fresh ??= Block.TryCreate(_context, preferredType, blockSize);

            if (fresh is null && fallbackType != preferredType)
            {
                Log.Warn(
                    $"Paměť grafiky (typ {preferredType}) je plná, další blok jde do systémové paměti "
                    + $"(typ {fallbackType}). Geometrie odtud jde přes PCIe a je znatelně pomalejší.");

                usedType = fallbackType;
                fresh = Block.TryCreate(_context, fallbackType, blockSize);
            }

            if (fresh is null)
            {
                throw new InvalidOperationException(
                    $"vkAllocateMemory selhalo pro blok {blockSize / (1024 * 1024)} MB "
                    + $"(typy {preferredType} i {fallbackType}).");
            }

            if (!_blocksByType.TryGetValue(usedType, out List<Block>? blocks))
            {
                blocks = [];
                _blocksByType[usedType] = blocks;
            }

            blocks.Add(fresh);
            Interlocked.Add(ref _reservedBytes, (long)blockSize);

            // Jakmile se jeden blok spotřebuje, začne se na pozadí připravovat další.
            // Až na něj dojde řada, bude hotový a jen se převezme.
            EnsureSpare(usedType);

            if (!fresh.TryTake(size, alignment, out ulong freshOffset))
            {
                throw new InvalidOperationException(
                    $"Do čerstvého bloku {blockSize} B se nevešlo {size} B se zarovnáním {alignment} B.");
            }

            Interlocked.Add(ref _usedBytes, (long)size);

            double allocationMs =
                (System.Diagnostics.Stopwatch.GetTimestamp() - allocationStart) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;

            WorstBlockMs = Math.Max(WorstBlockMs, allocationMs);

            Log.Info(
                $"Paměťový blok #{BlockCountUnlocked()} o {blockSize / (1024 * 1024)} MB, "
                + $"typ {usedType}, alokace {allocationMs:F2} ms.");

            return new Allocation(fresh, freshOffset, size);
        }
    }

    private bool TryTakeFrom(uint memoryTypeIndex, ulong size, ulong alignment, out Allocation allocation)
    {
        if (_blocksByType.TryGetValue(memoryTypeIndex, out List<Block>? blocks))
        {
            foreach (Block block in blocks)
            {
                if (block.TryTake(size, alignment, out ulong offset))
                {
                    Interlocked.Add(ref _usedBytes, (long)size);
                    allocation = new Allocation(block, offset, size);
                    return true;
                }
            }
        }

        allocation = default;
        return false;
    }

    public void Free(Allocation allocation)
    {
        if (allocation.Owner is null)
        {
            return;
        }

        lock (_lock)
        {
            allocation.Owner.Release(allocation.Offset, allocation.Size);
            Interlocked.Add(ref _usedBytes, -(long)allocation.Size);

            // A suballocator is useful only while its blocks contain live allocations or while
            // one warm block is kept for reuse. Keeping every historical high-water block made
            // rapid exploration permanently reserve gigabytes on 4 GB GPUs and on unified-memory
            // Macs. Once a second block of the same type exists, a completely empty one can be
            // returned to Vulkan immediately.
            foreach ((uint _, List<Block> blocks) in _blocksByType)
            {
                if (blocks.Count <= 1 || !blocks.Contains(allocation.Owner)
                    || !allocation.Owner.IsCompletelyFree)
                {
                    continue;
                }

                blocks.Remove(allocation.Owner);
                Interlocked.Add(ref _reservedBytes, -(long)allocation.Owner.Size);
                allocation.Owner.Dispose(_context);
                break;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (List<Block> blocks in _blocksByType.Values)
            {
                foreach (Block block in blocks)
                {
                    block.Dispose(_context);
                }
            }

            // Připravená rezerva se zatím nikam nepřidala, takže by se jinak neuvolnila.
            //
            // Příprava, která zrovna běží, dopadne bez následků: doběhne, zjistí, že už
            // je rezerva obsazená nebo pool zavřený, a svůj blok zahodí sama.
            _spare?.Dispose(_context);
            _spare = null;

            _blocksByType.Clear();
            Interlocked.Exchange(ref _usedBytes, 0);
            Interlocked.Exchange(ref _reservedBytes, 0);
        }
    }

    private int BlockCountUnlocked()
    {
        int total = 0;
        foreach (List<Block> blocks in _blocksByType.Values)
        {
            total += blocks.Count;
        }

        return total;
    }

    private static ulong RoundUp(ulong value, ulong multiple) => ((value + multiple - 1) / multiple) * multiple;

    /// <summary>Jeden blok paměti zařízení se seznamem volných úseků.</summary>
    public sealed class Block
    {
        private readonly List<(ulong Offset, ulong Size)> _free = [];

        private Block(DeviceMemory memory, void* mapped, ulong size)
        {
            Memory = memory;
            Mapped = mapped;
            Size = size;
            _free.Add((0, size));
        }

        public DeviceMemory Memory { get; }

        public void* Mapped { get; }

        public ulong Size { get; }

        public bool IsCompletelyFree =>
            _free.Count == 1 && _free[0].Offset == 0 && _free[0].Size == Size;

        /// <summary>
        /// Založí blok, nebo vrátí <c>null</c>, když se do haldy nevejde.
        ///
        /// <para>Neúspěch tu není chyba: halda <c>DEVICE_LOCAL | HOST_VISIBLE</c> může být
        /// malá (bez resizable BAR jen 256 MB) a volající má připravenou náhradu.</para>
        /// </summary>
        public static Block? TryCreate(VulkanContext context, uint memoryTypeIndex, ulong size)
        {
            var allocate = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = size,
                MemoryTypeIndex = memoryTypeIndex,
            };

            Result result = context.Vk.AllocateMemory(context.Device, &allocate, null, out DeviceMemory memory);

            if (result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory)
            {
                return null;
            }

            VulkanContext.Check(result, "vkAllocateMemory (blok)");

            void* mapped;
            VulkanContext.Check(
                context.Vk.MapMemory(context.Device, memory, 0, size, 0, &mapped),
                "vkMapMemory (blok)");

            return new Block(memory, mapped, size);
        }

        /// <summary>First-fit se zarovnáním. Nevyužitý začátek úseku se vrací zpátky do seznamu.</summary>
        public bool TryTake(ulong size, ulong alignment, out ulong offset)
        {
            for (int i = 0; i < _free.Count; i++)
            {
                (ulong freeOffset, ulong freeSize) = _free[i];
                ulong aligned = alignment == 0 ? freeOffset : RoundUp(freeOffset, alignment);
                ulong padding = aligned - freeOffset;

                if (padding + size > freeSize)
                {
                    continue;
                }

                _free.RemoveAt(i);

                if (padding > 0)
                {
                    _free.Insert(i, (freeOffset, padding));
                    i++;
                }

                ulong tail = freeSize - padding - size;
                if (tail > 0)
                {
                    _free.Insert(i, (aligned + size, tail));
                }

                offset = aligned;
                return true;
            }

            offset = 0;
            return false;
        }

        /// <summary>Vrátí úsek a slouží ho se sousedy, aby se seznam netříštil.</summary>
        public void Release(ulong offset, ulong size)
        {
            int index = 0;
            while (index < _free.Count && _free[index].Offset < offset)
            {
                index++;
            }

            _free.Insert(index, (offset, size));

            // Sloučení s následujícím a pak s předchozím.
            if (index + 1 < _free.Count && _free[index].Offset + _free[index].Size == _free[index + 1].Offset)
            {
                _free[index] = (_free[index].Offset, _free[index].Size + _free[index + 1].Size);
                _free.RemoveAt(index + 1);
            }

            if (index > 0 && _free[index - 1].Offset + _free[index - 1].Size == _free[index].Offset)
            {
                _free[index - 1] = (_free[index - 1].Offset, _free[index - 1].Size + _free[index].Size);
                _free.RemoveAt(index);
            }
        }

        public void Dispose(VulkanContext context)
        {
            context.Vk.UnmapMemory(context.Device, Memory);
            context.Vk.FreeMemory(context.Device, Memory, null);
        }

        private static ulong RoundUp(ulong value, ulong multiple) => ((value + multiple - 1) / multiple) * multiple;
    }

    /// <summary>Přidělený úsek: ve kterém bloku leží, kde a jak velký je.</summary>
    public readonly record struct Allocation(Block? Owner, ulong Offset, ulong Size);
}
