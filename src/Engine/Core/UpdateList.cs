namespace Tesseris.Engine.Core;

/// <summary>
/// Seznam entit, které se mají tikat. Kdo nemá co dělat, v něm není.
/// </summary>
/// <remarks>
/// <para><b>Proč to je nutné.</b> Pravidlo 6.4 zakazuje <c>foreach (var m in allMachines) m.Tick()</c>
/// a projekt přesně to dělal u pecí: <c>foreach (Furnace f in _furnaces.Values) Step(f, dt)</c>
/// každý snímek, s jediným <c>if (!furnace.Running) return;</c> jako úsporou. Ve zralé kolonii
/// čeká většina strojů na vstup a nesmí stát ani takt.</para>
///
/// <para><b>Sparse set.</b> Dvě pole: husté s aktivními identifikátory a řídké, které z každého
/// identifikátoru udělá index do hustého. Probuzení i uspání jsou O(1) a procházení aktivních
/// je souvislá paměť, ne skákání po haldě. Odebírá se prohozením s posledním prvkem, takže se
/// nic neposouvá.</para>
///
/// <para><b>Nealokuje.</b> Za běhu se pole zvětší jen tehdy, když přijde vyšší identifikátor,
/// než na jaký je místo — v ustáleném stavu je alokací nula (pravidlo 6.5).</para>
///
/// <para><b>Procházet se musí odzadu.</b> Viz <see cref="Active"/>.</para>
/// </remarks>
public sealed class UpdateList
{
    private const int NotPresent = -1;

    private int[] _dense;
    private int[] _sparse;
    private int _count;

    public UpdateList(int initialCapacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);

        _dense = new int[initialCapacity];
        _sparse = new int[initialCapacity];
        Array.Fill(_sparse, NotPresent);
    }

    /// <summary>Kolik entit je právě vzhůru.</summary>
    public int Count => _count;

    /// <summary>
    /// Aktivní identifikátory v souvislé paměti.
    /// </summary>
    /// <remarks>
    /// <para><b>Procházej odzadu</b> (<c>for (int i = list.Count - 1; i >= 0; i--)</c>). Uspání
    /// prohodí poslední prvek na uvolněné místo, takže při průchodu odzadu se přesune prvek,
    /// který už byl na řadě — a nic se nevynechá. Při průchodu odpředu by se jeden prvek přeskočil.</para>
    ///
    /// <para>Entita probuzená během tiku se zařadí na konec, takže přijde na řadu až příští tik.
    /// To je záměr — jinak by šlo jedním tikem probudit řetěz strojů přes celou továrnu.</para>
    /// </remarks>
    public ReadOnlySpan<int> Active => _dense.AsSpan(0, _count);

    public bool IsAwake(int id)
    {
        if (id < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        return id < _sparse.Length && _sparse[id] != NotPresent;
    }

    /// <summary>Zařadí entitu mezi tikané. Opakované probuzení už vzhůru nic neudělá.</summary>
    /// <returns>true, pokud entita předtím spala.</returns>
    public bool Wake(int id)
    {
        if (id < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        EnsureSparse(id);
        if (_sparse[id] != NotPresent)
        {
            return false;
        }

        if (_count == _dense.Length)
        {
            Array.Resize(ref _dense, _dense.Length * 2);
        }

        _dense[_count] = id;
        _sparse[id] = _count;
        _count++;
        return true;
    }

    /// <summary>Vyřadí entitu z tikaných. Uspání spící nic neudělá.</summary>
    /// <returns>true, pokud entita předtím byla vzhůru.</returns>
    public bool Sleep(int id)
    {
        if (id < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        if (id >= _sparse.Length)
        {
            return false;
        }

        int index = _sparse[id];
        if (index == NotPresent)
        {
            return false;
        }

        // Prohození s posledním: uvolněné místo dostane poslední prvek a jeho záznam
        // v řídkém poli se opraví. Nic se neposouvá.
        int last = _dense[--_count];
        _dense[index] = last;
        _sparse[last] = index;
        _sparse[id] = NotPresent;
        return true;
    }

    /// <summary>Uspí všechny. Volá se při načtení nebo zahození světa.</summary>
    public void Clear()
    {
        for (int i = 0; i < _count; i++)
        {
            _sparse[_dense[i]] = NotPresent;
        }

        _count = 0;
    }

    private void EnsureSparse(int id)
    {
        if (id < _sparse.Length)
        {
            return;
        }

        int capacity = _sparse.Length;
        while (capacity <= id)
        {
            capacity *= 2;
        }

        int previous = _sparse.Length;
        Array.Resize(ref _sparse, capacity);
        Array.Fill(_sparse, NotPresent, previous, capacity - previous);
    }
}
