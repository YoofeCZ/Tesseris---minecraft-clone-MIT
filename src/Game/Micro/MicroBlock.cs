using Tesseris.Engine.MathLib;
namespace Tesseris.Game.Micro;

/// <summary>
/// Obsah jednoho otesaného bloku: mřížka 16×16×16 mikrovoxelů.
///
/// Nula znamená prázdno, jiná hodnota je index do materiálové palety tohohle bloku.
/// Paleta drží identifikátory bloků, takže jeden otesaný blok může kombinovat víc materiálů —
/// vznikne to při kopírování tvaru z jiného bloku.
///
/// <para><b>Řídkost.</b> Objekt existuje jen pro bloky, do kterých někdo sekl. Netesaný blok
/// nemá v mikro vrstvě vůbec žádný záznam a nestojí ani bajt navíc.</para>
///
/// <para><b>Neměnnost po zveřejnění.</b> Stejně jako chunk se ani MicroBlock po vložení do
/// světa nemění — úprava vyrobí kopii přes <see cref="Clone"/>. Bez toho by meshovací úloha
/// četla mřížku, která se jí mění pod rukama.</para>
///
/// <para><b>Obsahový hash.</b> Slouží k deduplikaci: dva bloky otesané do stejného tvaru
/// ze stejného materiálu sdílejí jednu vygenerovanou mesh. Hash se počítá líně a po každé
/// úpravě se zahodí.</para>
/// </summary>
public sealed class MicroBlock
{
    /// <summary>Hrana mřížky uvnitř bloku.</summary>
    public const int Size = 16;

    public const int SizeShift = 4;
    public const int SizeMask = Size - 1;
    public const int Volume = Size * Size * Size;

    /// <summary>Nejvíc materiálů v jednom otesaném bloku, dané jedním bajtem na mikrovoxel.</summary>
    public const int MaxMaterials = 255;

    private readonly byte[] _voxels;
    private ushort[] _palette;
    private int _paletteCount;
    private int _solidCount;
    private long _hash;
    private bool _hashValid;

    private MicroBlock(byte[] voxels, ushort[] palette, int paletteCount, int solidCount)
    {
        _voxels = voxels;
        _palette = palette;
        _paletteCount = paletteCount;
        _solidCount = solidCount;
    }

    /// <summary>Kolik mikrovoxelů je vyplněných. Nula znamená, že blok zmizel.</summary>
    public int SolidCount => _solidCount;

    /// <summary>Je blok úplně vytesaný, tedy prázdný?</summary>
    public bool IsEmpty => _solidCount == 0;

    /// <summary>Je mřížka celá plná? Takový blok se dá vrátit zpátky na obyčejný blok.</summary>
    public bool IsFull => _solidCount == Volume;

    /// <summary>Počet materiálů v paletě.</summary>
    public int MaterialCount => _paletteCount;

    /// <summary>Lineární index mikrovoxelu. X se mění nejrychleji.</summary>
    public static int LocalIndex(int x, int y, int z) => x | (z << SizeShift) | (y << (SizeShift * 2));

    /// <summary>Vytvoří plně vyplněný blok z obyčejného bloku — první krok každého tesání.</summary>
    public static MicroBlock FromSolid(ushort material)
    {
        byte[] voxels = new byte[Volume];
        Array.Fill(voxels, (byte)1);

        return new MicroBlock([.. voxels], [0, material], paletteCount: 1, solidCount: Volume);
    }

    /// <summary>
    /// Vytvoří mřížku ve tvaru bloku — první krok tesání do něčeho, co není plná kostka.
    ///
    /// <para><b>Bez tohohle kmen prvním seknutím skokem ztloustne.</b> Tesání začíná tím,
    /// že se blok převede na plnou mřížku 16³; jenže sloupek kmene zabírá jen prostřední
    /// polovinu bloku a listí jen některé dílky. Hráč by tedy sekl do štíhlého kmene a ten
    /// by se v tom okamžiku roztáhl na celou kostku, ze které by se teprve ubíralo.</para>
    ///
    /// <para>Kvádry se berou ze stejného výpočtu, podle kterého se tvar kreslí, takže mřížka
    /// vyjde přesně jako to, na co se hráč díval.</para>
    /// </summary>
    public static MicroBlock FromShape(ushort material, IEnumerable<Aabb> colliders)
    {
        ArgumentNullException.ThrowIfNull(colliders);

        byte[] voxels = new byte[Volume];
        int solid = 0;

        foreach (Aabb collider in colliders)
        {
            // Zaokrouhlení dovnitř: mikrovoxel patří tvaru, jen když v něm celý leží.
            // Jinak by kvádr o hraně, která nesedí na mřížku, o kousek povyrostl.
            int minX = (int)MathF.Round(collider.Min.X * Size);
            int minY = (int)MathF.Round(collider.Min.Y * Size);
            int minZ = (int)MathF.Round(collider.Min.Z * Size);
            int maxX = (int)MathF.Round(collider.Max.X * Size);
            int maxY = (int)MathF.Round(collider.Max.Y * Size);
            int maxZ = (int)MathF.Round(collider.Max.Z * Size);

            for (int y = Math.Max(minY, 0); y < Math.Min(maxY, Size); y++)
            {
                for (int z = Math.Max(minZ, 0); z < Math.Min(maxZ, Size); z++)
                {
                    for (int x = Math.Max(minX, 0); x < Math.Min(maxX, Size); x++)
                    {
                        int index = LocalIndex(x, y, z);

                        if (voxels[index] == 0)
                        {
                            voxels[index] = 1;
                            solid++;
                        }
                    }
                }
            }
        }

        return solid == 0
            ? Empty()
            : new MicroBlock(voxels, [0, material], paletteCount: 1, solidCount: solid);
    }

    /// <summary>Vytvoří prázdný blok bez jediného mikrovoxelu.</summary>
    public static MicroBlock Empty() => new(new byte[Volume], [0], paletteCount: 0, solidCount: 0);

    /// <summary>
    /// Poskládá blok z uložených dat. Používá se při načítání světa z disku.
    ///
    /// <para>Počet vyplněných mikrovoxelů se dopočítá, ne načte: je to odvozená hodnota
    /// a ukládat ji znamená riskovat, že se rozejde s mřížkou.</para>
    /// </summary>
    internal static MicroBlock FromRaw(byte[] voxels, ushort[] palette, int paletteCount)
    {
        int solid = 0;

        foreach (byte voxel in voxels)
        {
            if (voxel != 0)
            {
                solid++;
            }
        }

        return new MicroBlock(voxels, palette, paletteCount, solid);
    }

    /// <summary>Materiál na dané položce palety. Položka 0 je vždycky prázdno.</summary>
    internal ushort RawPaletteAt(int index) => _palette[index];

    /// <summary>Mřížka indexů do palety, jeden bajt na mikrovoxel.</summary>
    internal ReadOnlySpan<byte> RawVoxels => _voxels;

    /// <summary>Kopie pro úpravu. Zveřejněný blok se nikdy nemění na místě.</summary>
    public MicroBlock Clone() =>
        new((byte[])_voxels.Clone(), (ushort[])_palette.Clone(), _paletteCount, _solidCount);

    /// <summary>Identifikátor bloku v daném mikrovoxelu, nebo nula pro prázdno.</summary>
    public ushort GetMaterial(int x, int y, int z)
    {
        byte index = _voxels[LocalIndex(x, y, z)];
        return index == 0 ? (ushort)0 : _palette[index];
    }

    /// <summary>Je mikrovoxel vyplněný?</summary>
    public bool IsSolid(int x, int y, int z) => _voxels[LocalIndex(x, y, z)] != 0;

    /// <summary>Je mikrovoxel vyplněný? Souřadnice mimo mřížku vrací false.</summary>
    public bool IsSolidSafe(int x, int y, int z) =>
        (uint)x < Size && (uint)y < Size && (uint)z < Size && IsSolid(x, y, z);

    /// <summary>Syrový index do palety. Používá mesher, který si paletu přeloží sám.</summary>
    public byte GetPaletteIndex(int index) => _voxels[index];

    /// <summary>Materiál na dané pozici palety.</summary>
    public ushort MaterialAt(byte paletteIndex) => paletteIndex == 0 ? (ushort)0 : _palette[paletteIndex];

    /// <summary>Vyplní mikrovoxel materiálem, nebo ho vyprázdní nulou. Jen na kopii.</summary>
    public void SetMaterial(int x, int y, int z, ushort material)
    {
        int index = LocalIndex(x, y, z);
        byte previous = _voxels[index];

        if (material == 0)
        {
            if (previous == 0)
            {
                return;
            }

            _voxels[index] = 0;
            _solidCount--;
            _hashValid = false;
            return;
        }

        byte paletteIndex = FindOrAddMaterial(material);
        if (previous == paletteIndex)
        {
            return;
        }

        if (previous == 0)
        {
            _solidCount++;
        }

        _voxels[index] = paletteIndex;
        _hashValid = false;
    }

    /// <summary>
    /// Hash obsahu. Dva bloky se stejným hashem mají skoro jistě stejný tvar i materiály;
    /// deduplikační cache si shodu ještě potvrdí porovnáním, takže kolize nevadí.
    /// </summary>
    public long ContentHash
    {
        get
        {
            if (_hashValid)
            {
                return _hash;
            }

            // FNV-1a nad mřížkou i paletou. Paleta musí do hashe vstoupit taky — stejný tvar
            // z kamene a ze dřeva se nesmí sloučit do jedné mesh.
            ulong hash = 14695981039346656037UL;

            foreach (byte voxel in _voxels)
            {
                hash = (hash ^ voxel) * 1099511628211UL;
            }

            for (int i = 1; i <= _paletteCount; i++)
            {
                hash = (hash ^ _palette[i]) * 1099511628211UL;
            }

            _hash = (long)hash;
            _hashValid = true;
            return _hash;
        }
    }

    /// <summary>Mají oba bloky doslova stejný obsah? Používá cache na potvrzení shody hashů.</summary>
    public bool HasSameContent(MicroBlock other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_solidCount != other._solidCount || _paletteCount != other._paletteCount)
        {
            return false;
        }

        for (int i = 1; i <= _paletteCount; i++)
        {
            if (_palette[i] != other._palette[i])
            {
                return false;
            }
        }

        return _voxels.AsSpan().SequenceEqual(other._voxels);
    }

    /// <summary>Materiál, kterého je v bloku nejvíc. Používá se pro zvuk a částice tesání.</summary>
    public ushort DominantMaterial()
    {
        if (_paletteCount == 0)
        {
            return 0;
        }

        Span<int> counts = stackalloc int[_paletteCount + 1];
        foreach (byte voxel in _voxels)
        {
            counts[voxel]++;
        }

        int best = 1;
        for (int i = 2; i <= _paletteCount; i++)
        {
            if (counts[i] > counts[best])
            {
                best = i;
            }
        }

        return _palette[best];
    }

    private byte FindOrAddMaterial(ushort material)
    {
        for (int i = 1; i <= _paletteCount; i++)
        {
            if (_palette[i] == material)
            {
                return (byte)i;
            }
        }

        if (_paletteCount >= MaxMaterials)
        {
            throw new InvalidOperationException(
                $"Otesaný blok už obsahuje {MaxMaterials} materiálů, což je mez jednobajtového indexu.");
        }

        _paletteCount++;
        if (_paletteCount >= _palette.Length)
        {
            Array.Resize(ref _palette, Math.Max(4, _palette.Length * 2));
        }

        _palette[_paletteCount] = material;
        return (byte)_paletteCount;
    }
}
