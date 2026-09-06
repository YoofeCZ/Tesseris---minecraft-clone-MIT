using OpenTK.Mathematics;
using Tesseris.Engine.Core;

namespace Tesseris.Game.Colony;

/// <summary>Odkud a kam vkládač bere.</summary>
public enum InserterEnd : byte
{
    /// <summary>Nic — vkládač na téhle straně nemá co obsluhovat.</summary>
    None,

    /// <summary>Pás.</summary>
    Belt,

    /// <summary>Stroj.</summary>
    Machine,
}

/// <summary>
/// Vkládače: berou z pásu do stroje a ze stroje na pás.
/// </summary>
/// <remarks>
/// <para><b>Proč to je zvlášť od stroje.</b> Stroj v automatickém režimu si z pásu bere sám,
/// ale to platí jen pro pás napojený přímo na něj. Vkládač je obecnější: umí obsloužit
/// i <b>ruční</b> stroj, umí přendat mezi dvěma pásy a umí vzít hotový výrobek a poslat ho
/// dál. Bez něj se z jednotlivých strojů nedá udělat linka.</para>
///
/// <para><b>Struct-of-arrays a spánek</b> (pravidla 6.3 a 6.4). Vkládač bez práce se
/// odregistruje; probudí ho příjezd itemu nebo hotový výrobek.</para>
///
/// <para><b>Jeden item za dobu cyklu.</b> Rychlost je vlastnost vkládače, ne konstanta —
/// na tom stojí, že se propustnost dá zlepšovat lepším vybavením.</para>
/// </remarks>
public sealed class InserterBank
{
    private readonly int _capacity;
    private readonly Vector3i[] _cell;
    private readonly InserterEnd[] _sourceKind;
    private readonly InserterEnd[] _targetKind;
    private readonly int[] _sourceIndex;
    private readonly int[] _targetIndex;
    private readonly BeltSegment?[] _sourceBelt;
    private readonly BeltSegment?[] _targetBelt;
    private readonly int[] _ticksPerMove;
    private readonly int[] _timer;

    /// <summary>Zbouraný vkládač. Místo po něm se nezaplňuje, stejně jako u strojů.</summary>
    private readonly bool[] _removed;

    private readonly UpdateList _active = new();

    private int _count;

    public InserterBank(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _capacity = capacity;
        _cell = new Vector3i[capacity];
        _sourceKind = new InserterEnd[capacity];
        _targetKind = new InserterEnd[capacity];
        _sourceIndex = new int[capacity];
        _targetIndex = new int[capacity];
        _sourceBelt = new BeltSegment?[capacity];
        _targetBelt = new BeltSegment?[capacity];
        _ticksPerMove = new int[capacity];
        _timer = new int[capacity];
        _removed = new bool[capacity];
    }

    public int Count => _count;

    /// <summary>Kolik vkládačů se právě tiká.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>Kolik itemů vkládače celkem přendaly.</summary>
    public int MovedTotal { get; private set; }

    public Vector3i CellOf(int inserter) => _cell[inserter];

    /// <summary>Odkud vkládač bere. Pro ukládání.</summary>
    public InserterEnd SourceKindOf(int inserter) => _sourceKind[inserter];

    /// <summary>Kam vkládač dává. Pro ukládání.</summary>
    public InserterEnd TargetKindOf(int inserter) => _targetKind[inserter];

    /// <summary>Index zdrojového stroje, nebo −1. Pro ukládání.</summary>
    public int SourceIndexOf(int inserter) => _sourceIndex[inserter];

    /// <summary>Index cílového stroje, nebo −1. Pro ukládání.</summary>
    public int TargetIndexOf(int inserter) => _targetIndex[inserter];

    /// <summary>Zdrojový pás, nebo null. Pro ukládání.</summary>
    public BeltSegment? SourceBeltOf(int inserter) => _sourceBelt[inserter];

    /// <summary>Cílový pás, nebo null. Pro ukládání.</summary>
    public BeltSegment? TargetBeltOf(int inserter) => _targetBelt[inserter];

    /// <summary>Jak dlouho trvá jedno přendání. Pro ukládání.</summary>
    public int TicksPerMoveOf(int inserter) => _ticksPerMove[inserter];

    /// <summary>Je vkládač zbouraný?</summary>
    public bool IsRemoved(int inserter) => _removed[inserter];

    /// <summary>
    /// Zbourá vkládač.
    /// </summary>
    /// <remarks>
    /// Místo se nezaplňuje ze stejného důvodu jako u strojů: index si drží pás v
    /// <see cref="BeltSegment.InserterTag"/> a stroj ve svém výstupu.
    /// </remarks>
    public void Remove(int inserter)
    {
        if (_removed[inserter])
        {
            return;
        }

        if (_sourceBelt[inserter] is { } source && source.InserterTag == inserter)
        {
            source.InserterTag = -1;
        }

        _sourceKind[inserter] = InserterEnd.None;
        _targetKind[inserter] = InserterEnd.None;
        _sourceBelt[inserter] = null;
        _targetBelt[inserter] = null;
        _sourceIndex[inserter] = -1;
        _targetIndex[inserter] = -1;
        _removed[inserter] = true;
        _active.Sleep(inserter);
    }

    /// <summary>
    /// Zbourá všechny vkládače, kterým zbouraný pás nebo stroj vzal jeden konec.
    /// </summary>
    /// <remarks>
    /// <b>Vkládač s jedním koncem nemá smysl nechat stát.</b> Nemá odkud brát nebo nemá kam
    /// dávat, takže by jen budil sám sebe a v UI by vypadal jako funkční kus linky.
    /// </remarks>
    public int ForgetBelt(BeltSegment belt)
    {
        ArgumentNullException.ThrowIfNull(belt);

        int removed = 0;
        for (int id = 0; id < _count; id++)
        {
            if (_removed[id])
            {
                continue;
            }

            if (ReferenceEquals(_sourceBelt[id], belt) || ReferenceEquals(_targetBelt[id], belt))
            {
                Remove(id);
                removed++;
            }
        }

        return removed;
    }

    /// <inheritdoc cref="ForgetBelt"/>
    public int ForgetMachine(int machine)
    {
        int removed = 0;
        for (int id = 0; id < _count; id++)
        {
            if (_removed[id])
            {
                continue;
            }

            bool touches = (_sourceKind[id] == InserterEnd.Machine && _sourceIndex[id] == machine)
                || (_targetKind[id] == InserterEnd.Machine && _targetIndex[id] == machine);

            if (touches)
            {
                Remove(id);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Postaví vkládač mezi pás a stroj.</summary>
    public int AddBeltToMachine(Vector3i cell, BeltSegment source, int machine, int ticksPerMove = 8)
    {
        ArgumentNullException.ThrowIfNull(source);

        int id = Allocate(cell, ticksPerMove);
        _sourceKind[id] = InserterEnd.Belt;
        _sourceBelt[id] = source;
        source.InserterTag = id;
        _targetKind[id] = InserterEnd.Machine;
        _targetIndex[id] = machine;
        _active.Wake(id);
        return id;
    }

    /// <summary>Postaví vkládač mezi stroj a pás.</summary>
    public int AddMachineToBelt(MachineBank machines, Vector3i cell, int machine, BeltSegment target, int ticksPerMove = 8)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(target);

        int id = Allocate(cell, ticksPerMove);
        _sourceKind[id] = InserterEnd.Machine;
        _sourceIndex[id] = machine;
        _targetKind[id] = InserterEnd.Belt;
        _targetBelt[id] = target;

        // Stroj si zapamatuje, koho probudit, až mu dozraje výrobek.
        machines.SetOutputInserter(machine, id);

        _active.Wake(id);
        return id;
    }

    /// <summary>Postaví vkládač mezi dva pásy.</summary>
    public int AddBeltToBelt(Vector3i cell, BeltSegment source, BeltSegment target, int ticksPerMove = 8)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        int id = Allocate(cell, ticksPerMove);
        _sourceKind[id] = InserterEnd.Belt;
        _sourceBelt[id] = source;
        source.InserterTag = id;
        _targetKind[id] = InserterEnd.Belt;
        _targetBelt[id] = target;
        _active.Wake(id);
        return id;
    }

    private int Allocate(Vector3i cell, int ticksPerMove)
    {
        if (_count == _capacity)
        {
            throw new InvalidOperationException($"Vkládačů je nejvýš {_capacity}.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerMove, 1);

        int id = _count++;
        _cell[id] = cell;
        _ticksPerMove[id] = ticksPerMove;
        _timer[id] = 0;
        _sourceKind[id] = InserterEnd.None;
        _targetKind[id] = InserterEnd.None;
        _sourceBelt[id] = null;
        _targetBelt[id] = null;
        _sourceIndex[id] = -1;
        _targetIndex[id] = -1;
        return id;
    }

    /// <summary>Probudí vkládač, aby se příští tik zase počítal.</summary>
    /// <remarks>Zbouraný se probudit nedá — jedna podmínka na konci trychtýře, viz MachineBank.</remarks>
    public void Wake(int inserter)
    {
        if (!_removed[inserter])
        {
            _active.Wake(inserter);
        }
    }

    /// <summary>Probudí všechny. Volá se, když se svět změní natolik, že se to nedá sledovat.</summary>
    public void WakeAll()
    {
        for (int id = 0; id < _count; id++)
        {
            Wake(id);
        }
    }

    /// <summary>Jeden krok všech pracujících vkládačů.</summary>
    /// <remarks>Odzadu, protože uspání prohodí poslední prvek na uvolněné místo.</remarks>
    public void Tick(MachineBank machines)
    {
        ArgumentNullException.ThrowIfNull(machines);

        // Nejdřív probudit ty, kterým stroj oznámil hotový výrobek.
        while (machines.TryDequeueWake(out int woken))
        {
            Wake(woken);
        }

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            Step(machines, _active.Active[i]);
        }
    }

    private void Step(MachineBank machines, int id)
    {
        if (++_timer[id] < _ticksPerMove[id])
        {
            return;
        }

        _timer[id] = 0;

        if (!TryTake(machines, id, out ushort item))
        {
            // NEUSNOUT, DOKUD JE NA ZDROJI PRÁCE. Item po pásu teprve jede a vkládač ho musí
            // vyhlížet. Přesně na tohle už narazil stroj: usnul dřív, než ruda dojela, a linka
            // stála, přestože pás i stroj fungovaly každý zvlášť.
            if (!SourceHasPendingWork(machines, id))
            {
                _active.Sleep(id);
            }

            return;
        }

        if (TryGive(machines, id, item))
        {
            MovedTotal++;
            return;
        }

        // CÍL JE PLNÝ — ITEM SE MUSÍ VRÁTIT. Bez toho by se ztratil, a mizející itemy jsou
        // nejhorší druh chyby: nikde se to nehlásí a projeví se to až chybějící produkcí.
        GiveBack(machines, id, item);
    }

    /// <summary>Je na zdroji něco, co teprve dojede nebo dozraje?</summary>
    private bool SourceHasPendingWork(MachineBank machines, int id) => _sourceKind[id] switch
    {
        InserterEnd.Belt => _sourceBelt[id] is { Count: > 0 },
        InserterEnd.Machine => machines.OutputOf(_sourceIndex[id]) > 0
            || machines.ProgressOf(_sourceIndex[id]) > 0
            || machines.InputOf(_sourceIndex[id]) > 0,
        _ => false,
    };

    /// <summary>Ohlásí, že na pás něco přibylo, a probudí vkládač, který z něj bere.</summary>
    public void NotifyPushed(BeltSegment belt)
    {
        ArgumentNullException.ThrowIfNull(belt);

        if (belt.InserterTag >= 0 && belt.InserterTag < _count)
        {
            Wake(belt.InserterTag);
        }
    }

    private bool TryTake(MachineBank machines, int id, out ushort item)
    {
        item = 0;

        switch (_sourceKind[id])
        {
            case InserterEnd.Belt:
                BeltSegment belt = _sourceBelt[id]!;
                if (!belt.FrontIsReady)
                {
                    return false;
                }

                return belt.TryPop(out item);

            case InserterEnd.Machine:
                return machines.TryTake(_sourceIndex[id], out item);

            default:
                return false;
        }
    }

    private bool TryGive(MachineBank machines, int id, ushort item)
    {
        switch (_targetKind[id])
        {
            case InserterEnd.Belt:
                BeltSegment belt = _targetBelt[id]!;
                if (!belt.TryPush(item))
                {
                    return false;
                }

                // Za pásem může stát spící stroj.
                machines.NotifyPushed(belt);
                return true;

            case InserterEnd.Machine:
                return machines.TryInsert(_targetIndex[id], item);

            default:
                return false;
        }
    }

    private void GiveBack(MachineBank machines, int id, ushort item)
    {
        switch (_sourceKind[id])
        {
            case InserterEnd.Belt:
                // Pás vrácený item přijme na svůj zadní konec; když ani tam není místo,
                // je celá linka zahlcená a jeden item navíc už nic nezmění.
                _sourceBelt[id]!.TryPush(item);
                break;

            case InserterEnd.Machine:
                machines.TryInsert(_sourceIndex[id], item);
                break;
        }
    }
}
