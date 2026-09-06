using Silk.NET.Vulkan;

namespace Tesseris.Engine.Rendering.Vulkan;

/// <summary>
/// Měří, kolik času na grafice spolkne který průchod.
/// </summary>
/// <remarks>
/// <para><b>Proč to nejde měřit z hlavního vlákna.</b> <see cref="Core.PhaseProfiler"/> měří
/// čas CPU, ale kreslení je jen zápis příkazů do bufferu — ten trvá mikrosekundy bez ohledu
/// na to, jestli grafika pak počítá dvě milisekundy nebo dvacet. Když snímková frekvence
/// klesne a hlavní vlákno má volno, čas se ztrácí na grafice a odsud se nedá zjistit kde.</para>
///
/// <para><b>Jak to funguje.</b> Do příkazového bufferu se mezi průchody zapisují časové
/// značky. Grafika je vyplní, až na ně dojde řada, takže se čtou <b>se zpožděním</b> —
/// výsledky patří snímku spracovanému před <c>FramesInFlight</c> snímky. Pro profilování to
/// nevadí, čísla se čtou stejně průběžně.</para>
///
/// <para>Úseky jsou <b>sekvenční</b>: N značek ohraničuje N−1 úseků, kde každý končí tam,
/// kde další začíná. Je to levnější než dvě značky na úsek a odpovídá to tomu, že průchody
/// jdou za sebou.</para>
/// </remarks>
public sealed unsafe class VulkanGpuProfiler : IDisposable
{
    private readonly VulkanContext _context;
    private readonly string[] _names;
    private readonly int _slots;
    private readonly double[] _last;
    private readonly ulong[] _scratch;

    private QueryPool _pool;
    private int _written;
    private int _frame;

    // Které sady už aspoň jednou proběhly. V prvních snímcích ještě žádná neexistuje
    // a čtení nevyplněných dotazů je podle specifikace chyba, ne jen prázdný výsledek.
    private readonly bool[] _primed = new bool[VulkanRenderer.FramesInFlight];

    /// <param name="names">
    /// Jména úseků. Značek se zapisuje o jednu víc — poslední uzavírá ten poslední úsek.
    /// </param>
    public VulkanGpuProfiler(VulkanContext context, params string[] names)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(names);

        _names = names;
        _slots = names.Length + 1;
        _last = new double[names.Length];
        _scratch = new ulong[_slots];

        Available = context.TimestampPeriod > 0f;

        if (!Available)
        {
            return;
        }

        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,

            // Vlastní sada značek pro každý rozpracovaný snímek. Se sdílenou by se čtení
            // starého snímku potkalo se zápisem nového.
            QueryCount = (uint)(_slots * VulkanRenderer.FramesInFlight),
        };

        VulkanContext.Check(
            context.Vk.CreateQueryPool(context.Device, &info, null, out _pool), "vkCreateQueryPool");
    }

    /// <summary>Umí zařízení časové značky? Když ne, profiler jen tiše nic nedělá.</summary>
    public bool Available { get; }

    /// <summary>
    /// Měřit? <b>Výchozí je vypnuto a je to nutnost, ne opatrnost.</b>
    /// </summary>
    /// <remarks>
    /// <para>Značka se zapisuje na <c>BottomOfPipe</c>, tedy „počkej, až všechno doběhne".
    /// Deset značek na snímek tím rozseká práci na deset kusů, které se nesmějí překrývat —
    /// a překrývání je přesně to, čím grafika drží výkon. Naměřeno: se zapnutým měřením
    /// spadly snímky z 650 na 120 za vteřinu.</para>
    ///
    /// <para>Měření tedy mění to, co měří. Čísla jsou pořád použitelná na <b>poměry</b> mezi
    /// průchody — který z nich žere nejvíc — ale ne jako absolutní časy, a rozhodně se
    /// nesmí nechat zapnuté při hraní.</para>
    /// </remarks>
    public bool Enabled { get; set; }

    public int SectionCount => _names.Length;

    public string Name(int section) => _names[section];

    /// <summary>Kolik milisekund grafika strávila v úseku. Hodnota je stará pár snímků.</summary>
    public double Milliseconds(int section) => _last[section];

    /// <summary>Součet všech úseků. Slouží k porovnání s časem celého snímku.</summary>
    public double Total
    {
        get
        {
            double sum = 0.0;

            for (int i = 0; i < _last.Length; i++)
            {
                sum += _last[i];
            }

            return sum;
        }
    }

    /// <summary>
    /// Vyzvedne výsledky předchozího snímku a vynuluje sadu značek.
    /// </summary>
    /// <remarks>
    /// <b>Musí se volat MIMO běžící rendering.</b> Vulkan zakazuje nulovat sadu uvnitř
    /// <c>vkCmdBeginRendering</c> a validační vrstva to hlásí jako chybu. Volá se proto
    /// z <see cref="VulkanRenderer.BeginFrame"/> ještě předtím, než se rendering otevře.
    /// </remarks>
    public void Reset(CommandBuffer commandBuffer, int frameSlot)
    {
        if (!Available || !Enabled)
        {
            return;
        }

        _frame = frameSlot;
        _written = 0;

        if (_primed[frameSlot])
        {
            ReadPrevious(frameSlot);
        }

        // Bez vynulování by dotazy zůstaly v nedostupném stavu z minula a čtení by
        // hlásilo, že výsledek není k dispozici.
        _context.Vk.CmdResetQueryPool(commandBuffer, _pool, (uint)(frameSlot * _slots), (uint)_slots);

        _primed[frameSlot] = true;
    }

    /// <summary>
    /// Zapíše úvodní značku. Volá se už uvnitř renderingu, na začátku kreslení.
    /// </summary>
    public void BeginFrame(CommandBuffer commandBuffer) => Mark(commandBuffer);

    /// <summary>
    /// Zapíše značku. Ukončuje předchozí úsek a začíná další.
    /// </summary>
    /// <remarks>
    /// Značka se zapisuje na <c>BottomOfPipe</c>, tedy až všechna práce před ní doběhne.
    /// S <c>TopOfPipe</c> by měřila jen to, kdy se příkaz dostal na řadu, ne kdy skončil.
    /// </remarks>
    public void Mark(CommandBuffer commandBuffer)
    {
        if (!Available || !Enabled || _written >= _slots)
        {
            return;
        }

        _context.Vk.CmdWriteTimestamp(
            commandBuffer, PipelineStageFlags.BottomOfPipeBit, _pool, (uint)((_frame * _slots) + _written));

        _written++;
    }

    private void ReadPrevious(int frameSlot)
    {
        fixed (ulong* results = _scratch)
        {
            Result read = _context.Vk.GetQueryPoolResults(
                _context.Device,
                _pool,
                (uint)(frameSlot * _slots),
                (uint)_slots,
                (nuint)(_slots * sizeof(ulong)),
                results,
                sizeof(ulong),
                QueryResultFlags.Result64Bit);

            // NotReady je běžný stav, ne chyba: v prvních snímcích ještě žádná sada
            // nedoběhla. Stará čísla se v tom případě nechají být.
            if (read != Result.Success)
            {
                return;
            }

            double toMilliseconds = _context.TimestampPeriod / 1_000_000.0;

            for (int i = 0; i < _names.Length; i++)
            {
                ulong start = _scratch[i];
                ulong end = _scratch[i + 1];

                _last[i] = end > start ? (end - start) * toMilliseconds : 0.0;
            }
        }
    }

    public void Dispose()
    {
        if (_pool.Handle != 0)
        {
            _context.Vk.DestroyQueryPool(_context.Device, _pool, null);
            _pool = default;
        }
    }
}
