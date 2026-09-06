using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;

namespace Tesseris.Game.Colony;

/// <summary>Stav jednoho úkolu.</summary>
public enum JobState : byte
{
    /// <summary>Volný, čeká na kolonistu.</summary>
    Open,

    /// <summary>Někdo si ho vzal.</summary>
    Claimed,

    /// <summary>Hotový.</summary>
    Done,

    /// <summary>Zrušený — blok mezitím zmizel.</summary>
    Cancelled,

    /// <summary>
    /// Odložený: teď k němu nevede cesta. Vrátí se mezi volné, až se svět změní.
    /// </summary>
    /// <remarks>
    /// Bez tohohle stavu se kolonista zacyklí. Vezme si nejbližší úkol, zjistí, že k němu
    /// nevede cesta, vrátí ho mezi volné — a protože je pořád nejbližší, vezme si ho zas.
    /// Naměřeno na zdi vysoké dva bloky: vrchní řada je z podlahy nedosažitelná, protože
    /// jediná sousední pochůzná buňka je nahoře na zdi a to je krok o dva bloky.
    /// </remarks>
    Deferred,
}

/// <summary>
/// Fronta úkolů k vykopání. Jeden voxel = jeden úkol.
/// </summary>
/// <remarks>
/// <para><b>Struct-of-arrays, ne seznam objektů</b> (pravidlo 6.3). Označená stěna 20×20×3
/// je 1 200 úkolů a označit se dá i o řád víc; objekt na úkol by z toho udělal hromadu
/// odkazů rozházených po haldě.</para>
///
/// <para><b>Hotové úkoly se nemažou, jen značí.</b> Mazání ze středu pole by posunulo
/// indexy a kolonisté drží index svého úkolu — musel by se přepisovat každému. Místo toho
/// se drží počítadlo volných a pole se uklidí, až bude všechno hotové.</para>
///
/// <para><b>Nejbližší úkol se hledá lineárně.</b> Je to O(počet úkolů) na jedno přiřazení.
/// Vědomě: prostorový index má smysl přidat, až měření ukáže, že to vadí — do té doby by
/// to bylo řešení neexistujícího problému (viz aréna geometrie, kde se přesně tohle stalo).</para>
/// </remarks>
public sealed class DigJobQueue
{
    private Vector3i[] _target = new Vector3i[256];
    private JobState[] _state = new JobState[256];
    private int[] _claimedBy = new int[256];
    private int _count;

    /// <summary>Kolik úkolů fronta zná, včetně hotových.</summary>
    public int Count => _count;

    /// <summary>Kolik úkolů čeká na kolonistu.</summary>
    public int OpenCount { get; private set; }

    /// <summary>Kolik úkolů někdo právě dělá.</summary>
    public int ClaimedCount { get; private set; }

    /// <summary>Kolik úkolů je hotových.</summary>
    public int DoneCount { get; private set; }

    public Vector3i TargetOf(int job) => _target[job];

    public JobState StateOf(int job) => _state[job];

    public int ClaimedBy(int job) => _claimedBy[job];

    /// <summary>
    /// Označí kvádr k vykopání. Úkol vznikne jen pro bloky, které tam opravdu jsou.
    /// </summary>
    /// <returns>Kolik úkolů přibylo.</returns>
    public int MarkArea(VoxelWorld world, BlockRegistry blocks, Vector3i min, Vector3i max)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        int added = 0;
        for (int y = Math.Min(min.Y, max.Y); y <= Math.Max(min.Y, max.Y); y++)
        {
            for (int z = Math.Min(min.Z, max.Z); z <= Math.Max(min.Z, max.Z); z++)
            {
                for (int x = Math.Min(min.X, max.X); x <= Math.Max(min.X, max.X); x++)
                {
                    ushort block = world.GetBlock(x, y, z);
                    if (block == BlockRegistry.Air || !blocks.IsSolid(block))
                    {
                        continue;
                    }

                    Add(new Vector3i(x, y, z));
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>Přidá jeden úkol. Duplicitu neřeší — o to se stará volající.</summary>
    public int Add(Vector3i target)
    {
        if (_count == _target.Length)
        {
            Array.Resize(ref _target, _target.Length * 2);
            Array.Resize(ref _state, _state.Length * 2);
            Array.Resize(ref _claimedBy, _claimedBy.Length * 2);
        }

        _target[_count] = target;
        _state[_count] = JobState.Open;
        _claimedBy[_count] = -1;
        OpenCount++;
        return _count++;
    }

    /// <summary>
    /// Přidělí kolonistovi nejbližší volný úkol.
    /// </summary>
    /// <remarks>
    /// Vzdálenost se měří na druhou, bez odmocniny — pro porovnání je to totéž a odmocnina
    /// v přiřazovací smyčce je zbytečná práce.
    /// </remarks>
    public bool TryClaimNearest(Vector3i from, int colonist, out int job)
    {
        job = -1;
        long best = long.MaxValue;

        for (int i = 0; i < _count; i++)
        {
            if (_state[i] != JobState.Open)
            {
                continue;
            }

            Vector3i delta = _target[i] - from;
            long distance = ((long)delta.X * delta.X) + ((long)delta.Y * delta.Y) + ((long)delta.Z * delta.Z);

            // Při shodě rozhoduje nižší index, aby bylo přiřazení deterministické.
            if (distance >= best)
            {
                continue;
            }

            best = distance;
            job = i;
        }

        if (job < 0)
        {
            return false;
        }

        _state[job] = JobState.Claimed;
        _claimedBy[job] = colonist;
        OpenCount--;
        ClaimedCount++;
        return true;
    }

    /// <summary>Vrátí úkol zpátky mezi volné — kolonista k němu nedošel.</summary>
    public void Release(int job)
    {
        if (_state[job] != JobState.Claimed)
        {
            return;
        }

        _state[job] = JobState.Open;
        _claimedBy[job] = -1;
        ClaimedCount--;
        OpenCount++;
    }

    public void Complete(int job)
    {
        if (_state[job] is JobState.Done or JobState.Cancelled)
        {
            return;
        }

        UpdateCounts(job, JobState.Done);
        DoneCount++;
    }

    /// <summary>Zruší úkol. Používá se, když blok mezitím zmizel.</summary>
    public void Cancel(int job)
    {
        if (_state[job] is JobState.Done or JobState.Cancelled)
        {
            return;
        }

        UpdateCounts(job, JobState.Cancelled);
    }

    /// <summary>Odloží úkol, ke kterému nevede cesta. Nezmizí, jen se přestane nabízet.</summary>
    public void Defer(int job)
    {
        if (_state[job] is JobState.Done or JobState.Cancelled)
        {
            return;
        }

        UpdateCounts(job, JobState.Deferred);
        DeferredCount++;
    }

    /// <summary>Kolik úkolů čeká na to, až se svět změní.</summary>
    public int DeferredCount { get; private set; }

    /// <summary>
    /// Vrátí odložené úkoly mezi volné.
    /// </summary>
    /// <remarks>
    /// Volá se po každé změně světa: vykopáním bloku se mohla otevřít cesta k něčemu, co
    /// bylo předtím za zdí. Bez toho by odložený úkol zůstal odložený navždycky.
    /// </remarks>
    /// <returns>Kolik úkolů se vrátilo.</returns>
    public int ReviveDeferred()
    {
        if (DeferredCount == 0)
        {
            return 0;
        }

        int revived = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_state[i] != JobState.Deferred)
            {
                continue;
            }

            _state[i] = JobState.Open;
            _claimedBy[i] = -1;
            OpenCount++;
            revived++;
        }

        DeferredCount = 0;
        return revived;
    }

    private void UpdateCounts(int job, JobState next)
    {
        if (_state[job] == JobState.Open)
        {
            OpenCount--;
        }
        else if (_state[job] == JobState.Claimed)
        {
            ClaimedCount--;
        }
        else if (_state[job] == JobState.Deferred)
        {
            DeferredCount--;
        }

        _state[job] = next;
        _claimedBy[job] = -1;
    }

    public void Clear()
    {
        _count = 0;
        OpenCount = 0;
        ClaimedCount = 0;
        DoneCount = 0;
        DeferredCount = 0;
    }
}
