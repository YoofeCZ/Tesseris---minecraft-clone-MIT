using Silk.NET.Vulkan;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Buffer i s pamětí, do které se dá zapisovat z CPU.
///
/// <para>
/// Klasický Vulkan na PC by geometrii nahrával přes staging buffer: zapsat do paměti
/// viditelné z CPU, pak zkopírovat do rychlé paměti GPU. Na Apple Silicon je ale paměť
/// <b>sdílená</b> — měřením se potvrdilo, že existuje paměťový typ, který je zároveň
/// <c>DEVICE_LOCAL</c> i <c>HOST_VISIBLE</c> a <c>HOST_COHERENT</c>. Zápis jde tedy rovnou
/// a mezikrok by byl jen zbytečná kopie.
/// </para>
/// <para>
/// Kód se drží obecné cesty: řekne si o vlastnosti, které chce, a nechá
/// <see cref="VulkanContext.FindMemoryType"/> vybrat. Na stroji bez sdílené paměti vyjde
/// pomalejší typ, ale pořád správný.
/// </para>
/// </summary>
public sealed unsafe class VulkanBuffer : IDisposable
{
    private static int _liveCount;
    private static int _peakCount;
    private static long _liveBytes;

    /// <summary>
    /// Kolik bufferů zrovna žije a nejvíc, kolik jich žilo naráz.
    ///
    /// Sleduje se to proto, že tahle třída alokuje <b>vlastní blok paměti pro každý buffer</b>.
    /// Vulkan má strop na počet alokací (<c>maxMemoryAllocationCount</c>, běžně 4096) a při
    /// jeho překročení <c>vkAllocateMemory</c> selže. Chunk má šest bufferů, takže se strop
    /// dá při velkém dohledu potkat — a bez tohohle počítadla by se to poznalo až pádem.
    /// </summary>
    public static (int Live, int Peak) AllocationCount => (Volatile.Read(ref _liveCount), Volatile.Read(ref _peakCount));

    /// <summary>Kolik bajtů paměti zařízení drží živé buffery. Do overlaye jako „VRAM".</summary>
    public static long LiveBytes => Volatile.Read(ref _liveBytes);

    private readonly VulkanContext _context;
    private VulkanMemoryPool.Allocation _allocation;
    private void* _mapped;

    private VulkanBuffer(VulkanContext context, ulong capacity)
    {
        _context = context;
        Capacity = capacity;
    }

    public Silk.NET.Vulkan.Buffer Handle { get; private set; }

    public DeviceMemory Memory { get; private set; }

    /// <summary>Kolik bajtů se do bufferu vejde.</summary>
    public ulong Capacity { get; }

    /// <summary>
    /// Založí buffer v paměti zapisovatelné z CPU a rovnou ji trvale namapuje.
    ///
    /// Mapování se nikdy neruší: <c>vkMapMemory</c> a <c>vkUnmapMemory</c> kolem každého
    /// zápisu jsou zbytečná režie a Vulkan trvalé mapování výslovně dovoluje.
    /// </summary>
    /// <param name="forReading">
    /// Bude se z bufferu <b>číst na CPU</b>? Pak se paměť grafiky naopak vyloučí.
    ///
    /// <para>Paměť za PCIe je write-combined: zápis do ní jde rychle, ale čtení je
    /// katastrofa. Naměřeno na čtení snímku zpátky v selftestu — po přesunu geometrie do
    /// paměti grafiky vyskočil jeden frame na <b>1954 ms</b> (dřív 385 ms), protože se
    /// tři megabajty framebufferu čtou po pár stovkách kilobajtů za vteřinu. Pro čtení
    /// se proto bere systémová paměť, a hlavně <c>HOST_CACHED</c>.</para>
    /// </param>
    public static VulkanBuffer CreateHostVisible(
        VulkanContext context, ulong sizeBytes, BufferUsageFlags usage, bool forReading = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (sizeBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Buffer nulové velikosti Vulkan odmítne.");
        }

        var buffer = new VulkanBuffer(context, sizeBytes);
        Vk vk = context.Vk;

        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = sizeBytes,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        VulkanContext.Check(
            vk.CreateBuffer(context.Device, &info, null, out Silk.NET.Vulkan.Buffer handle),
            "vkCreateBuffer");

        buffer.Handle = handle;

        vk.GetBufferMemoryRequirements(context.Device, handle, out MemoryRequirements requirements);

        // HOST_COHERENT znamena, ze se po zapisu nemusi rucne splachovat cache.
        //
        // Pamet uz se NEALOKUJE na buffer. Bere se jako usek z velkeho bloku: kazdy buffer
        // mel driv vlastni vkAllocateMemory a naslo se tak 5477 alokaci a pres gigabajt
        // pameti zarizeni. Vulkan ma na pocet alokaci navic tvrdy strop.
        //
        // DEVICE_LOCAL se zada jako PREFEROVANE, a je to zdaleka nejdulezitejsi radek
        // v tomhle souboru. Vulkan vraci prvni vyhovujici typ, takze pouhe
        // HOST_VISIBLE | HOST_COHERENT na samostatne grafice trefi SYSTEMOVOU RAM.
        // Zmereno na RTX 4070 SUPER: typ 2 je HOST_VISIBLE|HOST_COHERENT na halde
        // o 32 GB (systemova pamet), typ 4 tyz plus DEVICE_LOCAL na halde o 12 GB
        // (pamet grafiky). Bez teto preference lezela veskera geometrie v systemove
        // pameti a grafika si ji kazdy snimek tahala pres PCIe.
        const MemoryPropertyFlags Required =
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

        uint fallbackType = context.FindMemoryType(requirements.MemoryTypeBits, Required);

        // Pro čtení se hledá cache, pro kreslení paměť grafiky. Když ani jedno není,
        // zbývá obyčejná systémová paměť.
        MemoryPropertyFlags wanted = forReading
            ? MemoryPropertyFlags.HostCachedBit
            : MemoryPropertyFlags.DeviceLocalBit;

        uint preferredType =
            context.TryFindMemoryType(requirements.MemoryTypeBits, Required | wanted, out uint better)
                ? better
                : fallbackType;

        VulkanMemoryPool.Allocation allocation = context.Memory.Allocate(
            preferredType, fallbackType, requirements.Size, requirements.Alignment);

        buffer._allocation = allocation;
        buffer.Memory = allocation.Owner!.Memory;

        VulkanContext.Check(
            vk.BindBufferMemory(context.Device, handle, buffer.Memory, allocation.Offset),
            "vkBindBufferMemory");

        buffer._mapped = (byte*)allocation.Owner.Mapped + allocation.Offset;

        int live = Interlocked.Increment(ref _liveCount);
        Interlocked.Add(ref _liveBytes, (long)sizeBytes);
        if (live > Volatile.Read(ref _peakCount))
        {
            Volatile.Write(ref _peakCount, live);
        }

        return buffer;
    }

    /// <summary>Zapíše data od začátku bufferu.</summary>
    public void Write<T>(ReadOnlySpan<T> data)
        where T : unmanaged =>
        Write(data, 0);

    /// <summary>
    /// Zapíše data od zadaného místa v bufferu.
    /// </summary>
    /// <remarks>
    /// Slouží dávkám, které do jednoho bufferu zapisují po částech — druhá dávka spritů
    /// musí navázat za tu první, ne ji přepsat.
    /// </remarks>
    public void Write<T>(ReadOnlySpan<T> data, int byteOffset)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);

        int bytes = data.Length * sizeof(T);

        if ((ulong)(bytes + byteOffset) > Capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                $"Data mají {bytes} B na offsetu {byteOffset}, ale buffer pojme jen {Capacity} B.");
        }

        var destination = new Span<byte>(_mapped, (int)Capacity)[byteOffset..];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(data).CopyTo(destination);
    }

    /// <summary>Přečte obsah bufferu. Používá se při čtení snímku pro selftest.</summary>
    public ReadOnlySpan<byte> Read(int byteCount) => new(_mapped, byteCount);

    public void Dispose()
    {
        Vk vk = _context.Vk;

        if (Memory.Handle != 0)
        {
            // Blok se neuvolnuje, jen se vrati usek. Mapovani bloku zustava.
            _context.Memory.Free(_allocation);
            _allocation = default;
            Memory = default;
            _mapped = null;
            Interlocked.Decrement(ref _liveCount);
            Interlocked.Add(ref _liveBytes, -(long)Capacity);
        }

        if (Handle.Handle != 0)
        {
            vk.DestroyBuffer(_context.Device, Handle, null);
            Handle = default;
        }
    }
}
