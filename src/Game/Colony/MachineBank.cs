using OpenTK.Mathematics;
using Tesseris.Engine.Core;

namespace Tesseris.Game.Colony;

/// <summary>Jak stroj běží.</summary>
public enum MachineMode : byte
{
    /// <summary>Stojí u něj člověk a drtí ručně. Ten člověk je po tu dobu zaneprázdněný.</summary>
    Manual,

    /// <summary>Napojený na pásy a proud. Běží sám a člověka nepotřebuje.</summary>
    Automatic,
}

/// <summary>
/// Stroje kolonie. Zatím jeden druh — drtič.
/// </summary>
/// <remarks>
/// <para><b>Tady je jádro celé hry.</b> „Stroje nekupují propustnost,
/// kupují lidský čas." Drtič běží buď ručně, a pak u něj někdo musí stát, nebo automaticky,
/// a pak je ten člověk volný. Okamžik, kdy se napojí pásy a počítadlo volných lidí skočí
/// z nuly na jedničku, je celá hra v jedné vteřině.</para>
///
/// <para><b>Režim se neukládá, odvozuje se.</b> Stroj je automatický přesně tehdy, když má
/// vstupní pás, výstupní pás a proud. Uložený příznak by se dřív nebo později rozešel se
/// skutečností — takhle se nemá s čím rozejít, a hlavně je tím napevno dané, že automatizace
/// je důsledek postavené infrastruktury, ne přepínač v UI.</para>
///
/// <para><b>Struct-of-arrays a spící stroje</b> (pravidla 6.3 a 6.4). Cíl je 3 000 aktivních
/// strojů; ve zralé kolonii jich většina čeká na vstup a nesmí stát ani takt. Předloha, jak
/// to vypadat nesmí, je <c>Furnaces</c>: <c>Dictionary</c> objektů procházený celý každý snímek.</para>
/// </remarks>
public sealed class MachineBank
{
    /// <summary>Kolik itemů se vejde do vstupního zásobníku.</summary>
    public const int InputCapacity = 8;

    /// <summary>Kolik itemů se vejde do výstupního zásobníku.</summary>
    public const int OutputCapacity = 8;

    private readonly int _capacity;
    private readonly Vector3i[] _cell;
    private readonly ushort[] _inputItem;
    private readonly ushort[] _outputItem;
    private readonly int[] _ticksPerCraft;
    private readonly int[] _progress;
    private readonly int[] _input;
    private readonly int[] _output;
    private readonly int[] _operator;
    private readonly bool[] _powered;
    private readonly BeltSegment?[] _inputBelt;
    private readonly BeltSegment?[] _outputBelt;

    /// <summary>Zbouraný stroj. Místo po něm se nezaplňuje — viz <see cref="Remove"/>.</summary>
    private readonly bool[] _removed;

    /// <summary>Který vkládač odebírá výstup. Index, nebo −1.</summary>
    private readonly int[] _outputInserter;

    /// <summary>Vkládače, které se mají probudit, protože jim dozrál výrobek.</summary>
    /// <remarks>
    /// <b>Fronta místo přímého volání.</b> Stroje o vkládačích nesmí vědět — jinak by se ty
    /// dvě banky zamotaly do sebe. Takhle stroj jen řekne „mám hotovo" a volající to rozdá.
    /// </remarks>
    private readonly Queue<int> _wakeInserters = new();

    /// <summary>Kolonisté, které automatizace pustila od stroje.</summary>
    private readonly Queue<int> _freedColonists = new();

    private readonly UpdateList _active = new();

    private int _count;

    public MachineBank(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _capacity = capacity;
        _cell = new Vector3i[capacity];
        _inputItem = new ushort[capacity];
        _outputItem = new ushort[capacity];
        _ticksPerCraft = new int[capacity];
        _progress = new int[capacity];
        _input = new int[capacity];
        _output = new int[capacity];
        _operator = new int[capacity];
        _powered = new bool[capacity];
        _inputBelt = new BeltSegment?[capacity];
        _outputBelt = new BeltSegment?[capacity];
        _outputInserter = new int[capacity];
        _removed = new bool[capacity];
    }

    public int Count => _count;

    /// <summary>Kolik strojů se právě tiká. Zbytek spí a nestojí nic.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>Kolik dávek se celkem podařilo zpracovat.</summary>
    public int CraftedTotal { get; private set; }

    public Vector3i CellOf(int machine) => _cell[machine];

    /// <summary>Je stroj zbouraný? Zbouraný stroj má pořád svůj index, ale nic nedělá.</summary>
    public bool IsRemoved(int machine) => _removed[machine];

    public ushort InputItemOf(int machine) => _inputItem[machine];

    public ushort OutputItemOf(int machine) => _outputItem[machine];

    public int InputOf(int machine) => _input[machine];

    public int OutputOf(int machine) => _output[machine];

    public int ProgressOf(int machine) => _progress[machine];

    public int OperatorOf(int machine) => _operator[machine];

    public bool IsPowered(int machine) => _powered[machine];

    /// <summary>Kolik tiků trvá jedna dávka. Pro ukládání.</summary>
    public int TicksPerCraftOf(int machine) => _ticksPerCraft[machine];

    /// <summary>Vstupní pás stroje, nebo null. Pro ukládání.</summary>
    public BeltSegment? InputBeltOf(int machine) => _inputBelt[machine];

    /// <summary>Výstupní pás stroje, nebo null. Pro ukládání.</summary>
    public BeltSegment? OutputBeltOf(int machine) => _outputBelt[machine];

    /// <summary>
    /// Nastaví rozdělanou práci a zásobníky rovnou. Jen pro načítání uloženého stavu.
    /// </summary>
    /// <remarks>
    /// Přes <see cref="TryInsert"/> to nejde: rozdělaná dávka se tam nedá dosadit a stroj by
    /// se po načtení vrátil na začátek výroby.
    /// </remarks>
    public void Restore(int machine, int input, int output, int progress)
    {
        _input[machine] = Math.Clamp(input, 0, InputCapacity);
        _output[machine] = Math.Clamp(output, 0, OutputCapacity);
        _progress[machine] = Math.Clamp(progress, 0, _ticksPerCraft[machine]);
        Wake(machine);
    }

    /// <summary>
    /// V jakém režimu stroj běží.
    /// </summary>
    /// <remarks>
    /// Odvozeno, ne uložené: automatický je ten, kdo má oba pásy a proud. Kdo nemá, potřebuje
    /// člověka.
    /// </remarks>
    public MachineMode ModeOf(int machine) =>
        _inputBelt[machine] is not null && _outputBelt[machine] is not null && _powered[machine]
            ? MachineMode.Automatic
            : MachineMode.Manual;

    /// <summary>Postaví stroj.</summary>
    public int Add(Vector3i cell, ushort inputItem, ushort outputItem, int ticksPerCraft)
    {
        if (_count == _capacity)
        {
            throw new InvalidOperationException($"Strojů je nejvýš {_capacity}.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerCraft, 1);

        int id = _count++;
        _cell[id] = cell;
        _inputItem[id] = inputItem;
        _outputItem[id] = outputItem;
        _ticksPerCraft[id] = ticksPerCraft;
        _progress[id] = 0;
        _input[id] = 0;
        _output[id] = 0;
        _operator[id] = -1;
        _powered[id] = false;
        _inputBelt[id] = null;
        _outputBelt[id] = null;
        _outputInserter[id] = -1;
        return id;
    }

    /// <summary>
    /// Zbourá stroj.
    /// </summary>
    /// <remarks>
    /// <para><b>Místo po stroji se nezaplňuje.</b> Jeho index si drží pás
    /// (<see cref="BeltSegment.ConsumerTag"/>), vkládač i kolonista, který u něj stojí.
    /// Prohození posledního prvku na uvolněné místo — jinak správný způsob, jak z pole mazat —
    /// by všem třem tiše podstrčilo cizí stroj. Díra v poli stojí pár bajtů a spící stroj
    /// nestojí ani takt.</para>
    ///
    /// <para>Volající musí vrátit operátora mezi volné lidi a uklidit obsah zásobníků; tahle
    /// banka o kolonistech ani o skladu neví.</para>
    /// </remarks>
    /// <returns>Kdo u stroje stál, nebo −1.</returns>
    public int Remove(int machine)
    {
        if (_removed[machine])
        {
            return -1;
        }

        SetInputBelt(machine, null);
        _outputBelt[machine] = null;
        _outputInserter[machine] = -1;
        _powered[machine] = false;
        _input[machine] = 0;
        _output[machine] = 0;
        _progress[machine] = 0;

        int colonist = _operator[machine];
        _operator[machine] = -1;

        _removed[machine] = true;
        Sleep(machine);
        return colonist;
    }

    /// <summary>Odpojí zbouraný pás ode všech strojů, které z něj braly nebo na něj dávaly.</summary>
    public void ForgetBelt(BeltSegment belt)
    {
        ArgumentNullException.ThrowIfNull(belt);

        for (int machine = 0; machine < _count; machine++)
        {
            if (ReferenceEquals(_inputBelt[machine], belt))
            {
                SetInputBelt(machine, null);
            }

            if (ReferenceEquals(_outputBelt[machine], belt))
            {
                SetOutputBelt(machine, null);
            }
        }
    }

    /// <summary>Napojí vstupní pás. Probudí stroj, protože po pásu může něco přijet.</summary>
    public void SetInputBelt(int machine, BeltSegment? belt)
    {
        if (_inputBelt[machine] is { } previous && previous.ConsumerTag == machine)
        {
            previous.ConsumerTag = -1;
        }

        _inputBelt[machine] = belt;

        // Pás si zapamatuje, koho má probudit, až na něj něco přijede.
        if (belt is not null)
        {
            belt.ConsumerTag = machine;
        }

        Wake(machine);
    }

    /// <summary>Napojí výstupní pás.</summary>
    public void SetOutputBelt(int machine, BeltSegment? belt)
    {
        _outputBelt[machine] = belt;
        Wake(machine);
    }

    /// <summary>Zapne nebo vypne proud.</summary>
    public void SetPowered(int machine, bool powered)
    {
        _powered[machine] = powered;
        Wake(machine);
    }

    /// <summary>
    /// Postaví ke stroji člověka.
    /// </summary>
    /// <returns>false, pokud u něj někdo už stojí, nebo ho stroj vůbec nepotřebuje.</returns>
    public bool TryAssignOperator(int machine, int colonist)
    {
        if (_removed[machine] || _operator[machine] >= 0 || ModeOf(machine) == MachineMode.Automatic)
        {
            return false;
        }

        _operator[machine] = colonist;
        Wake(machine);
        return true;
    }

    /// <summary>Pustí člověka od stroje a vrátí, kdo to byl.</summary>
    public int ReleaseOperator(int machine)
    {
        int colonist = _operator[machine];
        _operator[machine] = -1;
        return colonist;
    }

    /// <summary>Vloží surovinu do vstupního zásobníku. Používá ji člověk i vkládač.</summary>
    public bool TryInsert(int machine, ushort item)
    {
        if (_removed[machine] || item != _inputItem[machine] || _input[machine] >= InputCapacity)
        {
            return false;
        }

        _input[machine]++;
        Wake(machine);
        return true;
    }

    /// <summary>Odebere hotový výrobek.</summary>
    public bool TryTake(int machine, out ushort item)
    {
        item = _outputItem[machine];
        if (_removed[machine] || _output[machine] <= 0)
        {
            return false;
        }

        _output[machine]--;
        Wake(machine);
        return true;
    }

    /// <summary>Řekne stroji, který vkládač mu odebírá výstup, aby ho šlo probudit.</summary>
    public void SetOutputInserter(int machine, int inserter) => _outputInserter[machine] = inserter;

    /// <summary>Vybere vkládač, kterému dozrál výrobek. Volající ho má probudit.</summary>
    public bool TryDequeueWake(out int inserter)
    {
        if (_wakeInserters.Count > 0)
        {
            inserter = _wakeInserters.Dequeue();
            return true;
        }

        inserter = -1;
        return false;
    }

    /// <summary>Vybere kolonistu, kterého automatizace pustila. Volající ho má uvolnit.</summary>
    public bool TryDequeueFreed(out int colonist)
    {
        if (_freedColonists.Count > 0)
        {
            colonist = _freedColonists.Dequeue();
            return true;
        }

        colonist = -1;
        return false;
    }

    /// <summary>
    /// Probudí stroj, aby se příští tik zase počítal.
    /// </summary>
    /// <remarks>
    /// <b>Zbouraný stroj se probudit nedá</b> a je to schválně tady, ne u každého volajícího:
    /// probouzí ho pás, vkládač, kolonista i zapnutí proudu. Jedna podmínka na konci trychtýře
    /// je jistota, šest podmínek u vchodů je čekání na to, kdy se na jednu zapomene.
    /// </remarks>
    public void Wake(int machine)
    {
        if (!_removed[machine])
        {
            _active.Wake(machine);
        }
    }

    /// <summary>
    /// Ohlásí, že na pás něco přibylo, a probudí stroj, který z něj bere.
    /// </summary>
    /// <remarks>
    /// Volá ten, kdo item na pás položil — jiný stroj, vkládač nebo kolonista. Bez toho by
    /// spící stroj nikdy nezjistil, že mu dorazila surovina.
    /// </remarks>
    public void NotifyPushed(BeltSegment belt)
    {
        ArgumentNullException.ThrowIfNull(belt);

        if (belt.ConsumerTag >= 0 && belt.ConsumerTag < _count)
        {
            Wake(belt.ConsumerTag);
        }
    }

    /// <summary>
    /// Jeden krok všech pracujících strojů.
    /// </summary>
    /// <remarks>
    /// <b>Prochází se odzadu</b> — uspání prohodí poslední prvek na uvolněné místo, takže
    /// při průchodu odpředu by se jeden stroj přeskočil.
    /// </remarks>
    /// <returns>Kolik operátorů se uvolnilo, protože se jejich stroj zautomatizoval.</returns>
    public int Tick()
    {
        int freed = 0;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            int machine = _active.Active[i];
            freed += Step(machine);
        }

        return freed;
    }

    private int Step(int machine)
    {
        int freed = 0;
        MachineMode mode = ModeOf(machine);

        // AUTOMATIZACE PUSTÍ ČLOVĚKA. Tohle je ten okamžik, kvůli kterému celá hra existuje:
        // jakmile je stroj napojený na pásy a proud, člověk u něj nemá co dělat.
        if (mode == MachineMode.Automatic && _operator[machine] >= 0)
        {
            _freedColonists.Enqueue(_operator[machine]);
            _operator[machine] = -1;
            freed++;
        }

        if (mode == MachineMode.Automatic)
        {
            PullFromBelt(machine);
        }

        // RUČNÍ REŽIM BEZ ČLOVĚKA NEBĚŽÍ. Stroj se uspí a probudí ho až příchod operátora.
        if (mode == MachineMode.Manual && _operator[machine] < 0)
        {
            Sleep(machine);
            return freed;
        }

        Craft(machine);

        if (mode == MachineMode.Automatic)
        {
            PushToBelt(machine);
        }

        // NENÍ CO DĚLAT? SPÁT. Stroj bez suroviny a bez rozdělané práce nemá důvod tikat.
        //
        // Vstupní pás se musí započítat: item po něm teprve jede a stroj ho musí vyhlížet.
        // Jakmile je pás prázdný, spí i tak — probudí ho NotifyPushed, až na něj někdo
        // něco položí.
        bool beltHasWork = _inputBelt[machine] is { Count: > 0 };
        if (_input[machine] == 0 && _progress[machine] == 0 && _output[machine] == 0 && !beltHasWork)
        {
            Sleep(machine);
        }

        return freed;
    }

    private void PullFromBelt(int machine)
    {
        BeltSegment? belt = _inputBelt[machine];
        if (belt is null || _input[machine] >= InputCapacity)
        {
            return;
        }

        if (belt.FrontIsReady && belt.FrontItem == _inputItem[machine] && belt.TryPop(out ushort item))
        {
            _input[machine]++;
            _ = item;
        }
    }

    private void PushToBelt(int machine)
    {
        BeltSegment? belt = _outputBelt[machine];
        if (belt is null || _output[machine] <= 0)
        {
            return;
        }

        if (belt.TryPush(_outputItem[machine]))
        {
            _output[machine]--;

            // Za výstupním pásem může stát další stroj, který zrovna spí.
            NotifyPushed(belt);
        }
    }

    private void Craft(int machine)
    {
        // Plný výstup zastaví práci. Bez toho by stroj drtil do prázdna a itemy by mizely.
        if (_output[machine] >= OutputCapacity)
        {
            return;
        }

        if (_progress[machine] == 0)
        {
            if (_input[machine] <= 0)
            {
                return;
            }

            _input[machine]--;
        }

        _progress[machine]++;
        if (_progress[machine] < _ticksPerCraft[machine])
        {
            return;
        }

        _progress[machine] = 0;
        _output[machine]++;
        CraftedTotal++;

        // Vkládač u výstupu možná spí a čeká přesně na tohle.
        if (_outputInserter[machine] >= 0)
        {
            _wakeInserters.Enqueue(_outputInserter[machine]);
        }
    }

    private void Sleep(int machine) => _active.Sleep(machine);
}
