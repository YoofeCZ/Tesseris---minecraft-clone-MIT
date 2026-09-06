using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;

namespace Tesseris.Game.Micro;

/// <summary>Co nástroj s otesaným blokem udělá.</summary>
public enum ChiselMode
{
    /// <summary>Odebere mikrovoxely v zásahové krychli.</summary>
    Remove,

    /// <summary>Přidá mikrovoxely před zasaženou stěnou.</summary>
    Add,

    /// <summary>Otočí obsah celého bloku o devadesát stupňů.</summary>
    Rotate,

    /// <summary>Překlopí obsah celého bloku podle osy zasažené stěny.</summary>
    Mirror,

    /// <summary>Poprvé tvar zapamatuje, podruhé ho vloží do jiného bloku.</summary>
    CopyShape,

    /// <summary>Zaoblí hranu odebráním osamocených mikrovoxelů.</summary>
    Smooth,
}

/// <summary>
/// Tesání bloku na mikrovoxely.
///
/// První seknutí do obyčejného bloku ho převede na mikro reprezentaci vyplněnou celou;
/// teprve pak se z ní ubírá. Když v bloku nezbyde jediný mikrovoxel, blok zmizí — o to se
/// stará <see cref="VoxelWorld.SetMicro"/>.
///
/// Nástroj sám nic nemešuje ani nekreslí. Vrátí souřadnice bloku, který se změnil, a volající
/// se postará o přemeshování.
/// </summary>
public sealed class ChiselTool
{
    /// <summary>Povolené velikosti hrany zásahové krychle v mikrovoxelech.</summary>
    public static readonly int[] Sizes = [1, 2, 4, 8];

    private int _size = 2;

    public ChiselMode Mode { get; set; } = ChiselMode.Remove;

    /// <summary>Hrana zásahové krychle. Přijímá jen hodnoty z <see cref="Sizes"/>.</summary>
    public int Size
    {
        get => _size;
        set
        {
            if (Array.IndexOf(Sizes, value) >= 0)
            {
                _size = value;
            }
        }
    }

    /// <summary>Materiál, kterým se přidává.</summary>
    public ushort Material { get; set; }

    /// <summary>Zapamatovaný tvar pro režim kopírování.</summary>
    public MicroBlock? Clipboard { get; private set; }

    /// <summary>Posune velikost na další z povolených hodnot.</summary>
    public void CycleSize()
    {
        int index = Array.IndexOf(Sizes, _size);
        _size = Sizes[(index + 1) % Sizes.Length];
    }

    /// <summary>Posune režim na další.</summary>
    public void CycleMode()
    {
        ChiselMode[] modes = Enum.GetValues<ChiselMode>();
        Mode = modes[(Array.IndexOf(modes, Mode) + 1) % modes.Length];
    }

    /// <summary>
    /// Použije nástroj na zásah paprsku.
    /// </summary>
    /// <param name="affected">Blok, jehož obsah se změnil. Platí jen při úspěchu.</param>
    /// <returns>true, když se něco opravdu změnilo.</returns>
    public bool Apply(VoxelWorld world, in MicroHit hit, out Vector3i affected)
    {
        ArgumentNullException.ThrowIfNull(world);

        affected = hit.Block;

        return Mode switch
        {
            ChiselMode.Remove => ApplyRemove(world, hit, ref affected),
            ChiselMode.Add => ApplyAdd(world, hit, ref affected),
            ChiselMode.Rotate => ApplyTransform(world, hit, RotateAroundY),
            ChiselMode.Mirror => ApplyTransform(world, hit, MirrorAlong(hit.Normal)),
            ChiselMode.CopyShape => ApplyCopyShape(world, hit),
            ChiselMode.Smooth => ApplySmooth(world, hit),
            _ => false,
        };
    }

    /// <summary>
    /// Zajistí, že blok má mikro reprezentaci. Obyčejný blok se převede na plnou mřížku,
    /// aby z ní šlo ubírat.
    /// </summary>
    private static MicroBlock? EnsureMicro(VoxelWorld world, Vector3i block)
    {
        MicroBlock? existing = world.GetMicro(block.X, block.Y, block.Z);
        if (existing is not null)
        {
            return existing;
        }

        ushort material = world.GetBlock(block.X, block.Y, block.Z);
        if (world.Registry.IsAir(material) || !world.Registry.Definition(material).Chiselable)
        {
            return null;
        }

        // Blok rozdělený na dílky se musí do mřížky přenést tak, jak vypadá. Jinak by kmen
        // prvním seknutím skokem ztloustl na celý blok — viz MicroBlock.FromShape.
        byte mask = world.GetPieces(block.X, block.Y, block.Z);
        BlockShape shape = world.Registry.ShapeOf(material);

        return mask == PieceMask.Full && shape != BlockShape.Post
            ? MicroBlock.FromSolid(material)
            : MicroBlock.FromShape(material, PieceMask.Colliders(shape, mask));
    }

    private bool ApplyRemove(VoxelWorld world, in MicroHit hit, ref Vector3i affected)
    {
        MicroBlock? source = EnsureMicro(world, hit.Block);
        if (source is null)
        {
            return false;
        }

        MicroBlock edited = source.Clone();
        (int x0, int y0, int z0) = AlignedOrigin(hit);

        bool changed = false;
        for (int y = y0; y < y0 + _size && y < MicroBlock.Size; y++)
        {
            for (int z = z0; z < z0 + _size && z < MicroBlock.Size; z++)
            {
                for (int x = x0; x < x0 + _size && x < MicroBlock.Size; x++)
                {
                    if (!edited.IsSolid(x, y, z))
                    {
                        continue;
                    }

                    edited.SetMaterial(x, y, z, 0);
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        affected = hit.Block;
        world.SetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z, edited);
        return true;
    }

    /// <summary>
    /// Přidá hmotu před zasaženou stěnu. Když zásahová krychle vyjde ven z bloku,
    /// pokračuje se do souseda — jinak by na hranici bloku nešlo přistavět.
    /// </summary>
    private bool ApplyAdd(VoxelWorld world, in MicroHit hit, ref Vector3i affected)
    {
        ushort material = Material != 0 ? Material : hit.Material;
        if (material == 0)
        {
            return false;
        }

        (int x0, int y0, int z0) = AlignedOrigin(hit);

        // Posun o velikost nástroje ve směru normály, aby hmota přibyla před stěnou.
        x0 += hit.Normal.X * _size;
        y0 += hit.Normal.Y * _size;
        z0 += hit.Normal.Z * _size;

        Vector3i target = hit.Block;

        // Vyjetí z mřížky znamená sousední blok.
        (target.X, x0) = Wrap(target.X, x0);
        (target.Y, y0) = Wrap(target.Y, y0);
        (target.Z, z0) = Wrap(target.Z, z0);

        MicroBlock? edited = world.GetMicro(target.X, target.Y, target.Z)?.Clone();

        if (edited is null)
        {
            // Do vzduchu se přistavuje od nuly, do plného bloku se nejdřív převede na mikro.
            edited = world.Registry.IsAir(world.GetBlock(target.X, target.Y, target.Z))
                ? MicroBlock.Empty()
                : EnsureMicro(world, target)?.Clone();
        }

        if (edited is null)
        {
            return false;
        }

        bool changed = false;
        for (int y = y0; y < y0 + _size && y < MicroBlock.Size; y++)
        {
            for (int z = z0; z < z0 + _size && z < MicroBlock.Size; z++)
            {
                for (int x = x0; x < x0 + _size && x < MicroBlock.Size; x++)
                {
                    if (x < 0 || y < 0 || z < 0 || edited.IsSolid(x, y, z))
                    {
                        continue;
                    }

                    edited.SetMaterial(x, y, z, material);
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        affected = target;
        world.SetMicro(target.X, target.Y, target.Z, edited);
        return true;
    }

    private static bool ApplyTransform(VoxelWorld world, in MicroHit hit, Func<int, int, int, (int X, int Y, int Z)> map)
    {
        MicroBlock? source = EnsureMicro(world, hit.Block);
        if (source is null)
        {
            return false;
        }

        MicroBlock result = MicroBlock.Empty();

        for (int y = 0; y < MicroBlock.Size; y++)
        {
            for (int z = 0; z < MicroBlock.Size; z++)
            {
                for (int x = 0; x < MicroBlock.Size; x++)
                {
                    ushort material = source.GetMaterial(x, y, z);
                    if (material == 0)
                    {
                        continue;
                    }

                    (int nx, int ny, int nz) = map(x, y, z);
                    result.SetMaterial(nx, ny, nz, material);
                }
            }
        }

        world.SetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z, result);
        return true;
    }

    /// <summary>Poprvé si tvar zapamatuje, podruhé ho vloží.</summary>
    private bool ApplyCopyShape(VoxelWorld world, in MicroHit hit)
    {
        if (Clipboard is null)
        {
            MicroBlock? source = EnsureMicro(world, hit.Block);
            if (source is null)
            {
                return false;
            }

            Clipboard = source.Clone();
            return false;
        }

        world.SetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z, Clipboard.Clone());
        return true;
    }

    /// <summary>Zapomene zapamatovaný tvar.</summary>
    public void ClearClipboard() => Clipboard = null;

    /// <summary>
    /// Zaoblí hranu: odebere mikrovoxely, které mají málo sousedů ve stěnách.
    /// Osamocené výčnělky zmizí, souvislá plocha zůstane.
    /// </summary>
    private bool ApplySmooth(VoxelWorld world, in MicroHit hit)
    {
        MicroBlock? source = EnsureMicro(world, hit.Block);
        if (source is null)
        {
            return false;
        }

        MicroBlock edited = source.Clone();
        (int x0, int y0, int z0) = AlignedOrigin(hit);
        bool changed = false;

        for (int y = y0; y < y0 + _size && y < MicroBlock.Size; y++)
        {
            for (int z = z0; z < z0 + _size && z < MicroBlock.Size; z++)
            {
                for (int x = x0; x < x0 + _size && x < MicroBlock.Size; x++)
                {
                    if (!source.IsSolid(x, y, z))
                    {
                        continue;
                    }

                    int neighbours = 0;
                    if (source.IsSolidSafe(x - 1, y, z)) { neighbours++; }
                    if (source.IsSolidSafe(x + 1, y, z)) { neighbours++; }
                    if (source.IsSolidSafe(x, y - 1, z)) { neighbours++; }
                    if (source.IsSolidSafe(x, y + 1, z)) { neighbours++; }
                    if (source.IsSolidSafe(x, y, z - 1)) { neighbours++; }
                    if (source.IsSolidSafe(x, y, z + 1)) { neighbours++; }

                    // Tři a méně sousedů znamená roh nebo hrana — ta se ubrousí.
                    if (neighbours > 3)
                    {
                        continue;
                    }

                    edited.SetMaterial(x, y, z, 0);
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        world.SetMicro(hit.Block.X, hit.Block.Y, hit.Block.Z, edited);
        return true;
    }

    /// <summary>Zarovná zásah na mřížku podle velikosti nástroje, aby řezy navazovaly.</summary>
    private (int X, int Y, int Z) AlignedOrigin(in MicroHit hit)
    {
        int x = Math.Max(0, hit.Micro.X);
        int y = Math.Max(0, hit.Micro.Y);
        int z = Math.Max(0, hit.Micro.Z);

        return (x / _size * _size, y / _size * _size, z / _size * _size);
    }

    /// <summary>Převede souřadnici mimo mřížku na sousední blok.</summary>
    private static (int Block, int Local) Wrap(int block, int local)
    {
        if (local < 0)
        {
            return (block - 1, local + MicroBlock.Size);
        }

        if (local >= MicroBlock.Size)
        {
            return (block + 1, local - MicroBlock.Size);
        }

        return (block, local);
    }

    private static (int X, int Y, int Z) RotateAroundY(int x, int y, int z) =>
        (z, y, MicroBlock.Size - 1 - x);

    private static Func<int, int, int, (int X, int Y, int Z)> MirrorAlong(Vector3i normal)
    {
        if (normal.X != 0)
        {
            return static (x, y, z) => (MicroBlock.Size - 1 - x, y, z);
        }

        if (normal.Y != 0)
        {
            return static (x, y, z) => (x, MicroBlock.Size - 1 - y, z);
        }

        return static (x, y, z) => (x, y, MicroBlock.Size - 1 - z);
    }
}
