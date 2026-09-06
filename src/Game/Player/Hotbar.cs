using Tesseris.Game.Blocks;

namespace Tesseris.Game.Player;

/// <summary>
/// Výběr bloku, který hráč pokládá.
///
/// Sloty se plní ze všech typů v registry kromě vzduchu, v pořadí, v jakém je registry
/// očísloval — tedy abecedně podle identifikátoru. Nejde o inventář, jen o výběr materiálu.
///
/// <para><b>Kbelík je slot navíc, ne blok.</b> Vodu nejde vzít ani položit rukou, protože
/// kapalina není materiál, který by šel nést v dlani — a hlavně proto, že s tekoucí vodou
/// by holá ruka byla nástroj na okamžité vysušení jezera. Kbelík je jediná cesta, jak vodu
/// přenést, a nese jeden bit stavu: prázdný, nebo plný.</para>
/// </summary>
public sealed class Hotbar
{
    private readonly ushort[] _slots;

    public Hotbar(BlockRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // Index 0 je vzduch a do výběru nepatří — pokládání vzduchu je těžení.
        _slots = new ushort[registry.Count - 1];
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = (ushort)(i + 1);
        }

        Registry = registry;
    }

    public BlockRegistry Registry { get; }

    /// <summary>Bloky plus kbelík.</summary>
    public int SlotCount => _slots.Length + 1;

    /// <summary>Slot, na kterém stojí kbelík. Je první, aby byl na klávese jedna.</summary>
    public const int BucketSlot = 0;

    /// <summary>Je vybraný kbelík místo bloku?</summary>
    public bool BucketSelected => SelectedIndex == BucketSlot;

    /// <summary>Má kbelík v sobě vodu? Mimo vybraný kbelík nemá význam.</summary>
    public bool BucketFull { get; private set; }

    /// <summary>Naplní kbelík. Volá se, když hráč nabere vodu ze světa.</summary>
    public void FillBucket() => BucketFull = true;

    /// <summary>Vyprázdní kbelík. Volá se, když hráč vodu vylije.</summary>
    public void EmptyBucket() => BucketFull = false;

    public int SelectedIndex { get; private set; }

    /// <summary>
    /// Vybraný blok. U kbelíku vrací vzduch - kbelík se nepokládá jako blok a volající
    /// se musí nejdřív zeptat na <see cref="BucketSelected"/>.
    /// </summary>
    public ushort SelectedBlock => BlockAt(SelectedIndex);

    /// <summary>Identifikátor vybraného bloku bez jmenného prostoru, pro vypsání na obrazovku.</summary>
    public string SelectedName
    {
        get
        {
            if (BucketSelected)
            {
                return BucketFull ? "kbelík s vodou" : "prázdný kbelík";
            }

            string id = Registry.Definition(SelectedBlock).Id;
            int colon = id.LastIndexOf(':');
            return colon >= 0 ? id[(colon + 1)..] : id;
        }
    }

    /// <summary>Vybere slot podle pořadí. Mimo rozsah se nestane nic.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < SlotCount)
        {
            SelectedIndex = index;
        }
    }

    /// <summary>Posune výběr o zadaný počet slotů. Na konci se přetočí na začátek.</summary>
    public void Scroll(int delta)
    {
        if (SlotCount == 0)
        {
            return;
        }

        int next = (SelectedIndex + delta) % SlotCount;
        SelectedIndex = next < 0 ? next + SlotCount : next;
    }

    /// <summary>Blok v zadaném slotu.</summary>
    public ushort BlockAt(int index) =>
        index > BucketSlot && index <= _slots.Length ? _slots[index - 1] : BlockRegistry.Air;
}
