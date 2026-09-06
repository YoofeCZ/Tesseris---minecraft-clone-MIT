using OpenTK.Mathematics;

namespace Tesseris.Game.Colony;

/// <summary>
/// Označení kvádru k vykopání ve velitelském režimu.
/// </summary>
/// <remarks>
/// <para>Dva body: první klik zapíchne roh, druhý ho uzavře. Mezi tím se dá tahat a výběr
/// se ukazuje jako kvádr — proto se drží obojí a normalizuje se až na konci.</para>
///
/// <para><b>Strop na velikost je nutnost, ne pohodlí.</b> Jeden voxel je jeden úkol, takže
/// omylem přetažený výběr přes půl mapy by vyrobil miliony úkolů a fronta by se s tím
/// vláčela. Rozumný strop řekne ne dřív, než se to stane.</para>
/// </remarks>
public sealed class AreaSelection
{
    /// <summary>Nejvíc voxelů, které jde označit najednou.</summary>
    public const int MaximumVolume = 64_000;

    private Vector3i _anchor;

    /// <summary>Je výběr rozdělaný?</summary>
    public bool Active { get; private set; }

    /// <summary>Kde se právě táhne druhý roh.</summary>
    public Vector3i Cursor { get; private set; }

    /// <summary>První roh výběru.</summary>
    public Vector3i Anchor => _anchor;

    /// <summary>Menší roh kvádru.</summary>
    public Vector3i Minimum => new(
        Math.Min(_anchor.X, Cursor.X),
        Math.Min(_anchor.Y, Cursor.Y),
        Math.Min(_anchor.Z, Cursor.Z));

    /// <summary>Větší roh kvádru.</summary>
    public Vector3i Maximum => new(
        Math.Max(_anchor.X, Cursor.X),
        Math.Max(_anchor.Y, Cursor.Y),
        Math.Max(_anchor.Z, Cursor.Z));

    /// <summary>Kolik voxelů výběr obsahuje.</summary>
    public long Volume
    {
        get
        {
            if (!Active)
            {
                return 0;
            }

            Vector3i min = Minimum;
            Vector3i max = Maximum;
            return (long)(max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1);
        }
    }

    /// <summary>Vejde se výběr do stropu?</summary>
    public bool WithinLimit => Volume <= MaximumVolume;

    /// <summary>Zapíchne první roh.</summary>
    public void Begin(Vector3i cell)
    {
        _anchor = cell;
        Cursor = cell;
        Active = true;
    }

    /// <summary>Táhne druhý roh.</summary>
    public void DragTo(Vector3i cell)
    {
        if (Active)
        {
            Cursor = cell;
        }
    }

    /// <summary>
    /// Uzavře výběr.
    /// </summary>
    /// <returns>false, když nic rozdělaného nebylo nebo je výběr nad stropem.</returns>
    public bool TryComplete(out Vector3i minimum, out Vector3i maximum)
    {
        minimum = Minimum;
        maximum = Maximum;

        if (!Active || !WithinLimit)
        {
            return false;
        }

        Active = false;
        return true;
    }

    /// <summary>Zahodí rozdělaný výběr.</summary>
    public void Cancel() => Active = false;
}
