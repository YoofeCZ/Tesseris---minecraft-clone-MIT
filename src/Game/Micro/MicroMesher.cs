using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Engine.Rendering;
using Tesseris.Game.Blocks;
using Tesseris.Game.World;

namespace Tesseris.Game.Micro;

/// <summary>
/// Převod otesaného bloku na trojúhelníky a na kolizní kvádry.
///
/// <para><b>Bez kontextu okolí.</b> Mesh se počítá jen z obsahu mřížky 16³ a stěny na okraji
/// bloku se kreslí vždycky, i když za nimi stojí plný soused. Je to schválně: kdyby výsledek
/// závisel na okolí, nešel by sdílet mezi bloky se stejným tvarem, a deduplikace je přesně to,
/// kvůli čemu tahle cesta existuje. Pár skrytých stěn je levnější než tisíc samostatných mesh.</para>
///
/// <para><b>Vlastní cesta.</b> Mikrogeometrie se nikdy nemíchá do chunk meshe. Jde do
/// samostatného bufferu a vlastního průchodu kreslením.</para>
/// </summary>
public static class MicroMesher
{
    /// <summary>Velikost jednoho mikrovoxelu vůči bloku.</summary>
    public const float VoxelScale = 1f / MicroBlock.Size;

    /// <summary>
    /// Poskládá mikrogeometrii celého chunku ze sdílených tvarů.
    ///
    /// Pro každý otesaný blok se sáhne do zásoby a hotové vrcholy se jen zkopírují
    /// s posunem podle polohy bloku uvnitř chunku.
    /// </summary>
    public static void BuildChunkMicro(Chunk chunk, MicroShapeCache cache, MeshBuffer target)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(target);

        target.Clear();

        if (!chunk.HasMicro)
        {
            return;
        }

        foreach ((int index, MicroBlock micro) in chunk.MicroBlocks)
        {
            MicroShape shape = cache.Get(micro);
            if (shape.IsEmpty)
            {
                continue;
            }

            var offset = new Vector3(
                index & Chunk.SizeMask,
                (index >> (Chunk.SizeShift * 2)) & Chunk.SizeMask,
                (index >> Chunk.SizeShift) & Chunk.SizeMask);

            target.AppendTranslated(shape.Vertices, shape.Indices, offset);
        }
    }

    /// <summary>Sestaví geometrii i kolizní kvádry pro zadaný tvar.</summary>
    public static MicroShape Build(MicroBlock block, BlockRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(registry);

        var mesh = new MeshBuffer();

        if (!block.IsEmpty)
        {
            BuildMesh(block, registry, mesh);
        }

        return new MicroShape(
            mesh.Vertices.ToArray(),
            mesh.Indices.ToArray(),
            BuildColliders(block),
            block.ContentHash);
    }

    private static void BuildMesh(MicroBlock block, BlockRegistry registry, MeshBuffer mesh)
    {
        Span<MaskCell> mask = stackalloc MaskCell[MicroBlock.Size * MicroBlock.Size];

        for (int axis = 0; axis < 3; axis++)
        {
            for (int slice = -1; slice < MicroBlock.Size; slice++)
            {
                for (int sign = 0; sign < 2; sign++)
                {
                    bool positive = sign == 0;
                    if (!BuildMask(block, registry, axis, slice, positive, mask))
                    {
                        continue;
                    }

                    EmitMask(mask, axis, slice + 1, positive, mesh);
                }
            }
        }
    }

    /// <summary>Naplní masku stěn pro jeden řez a jeden směr. Vrací, jestli v ní něco je.</summary>
    private static bool BuildMask(MicroBlock block, BlockRegistry registry, int axis, int slice, bool positive, Span<MaskCell> mask)
    {
        mask.Clear();
        bool any = false;

        for (int v = 0; v < MicroBlock.Size; v++)
        {
            for (int u = 0; u < MicroBlock.Size; u++)
            {
                // Blok, kterému stěna patří, a jeho protějšek.
                int ownerSlice = positive ? slice : slice + 1;
                int otherSlice = positive ? slice + 1 : slice;

                (int ox, int oy, int oz) = MakeCoord(axis, ownerSlice, u, v);
                (int nx, int ny, int nz) = MakeCoord(axis, otherSlice, u, v);

                if (!block.IsSolidSafe(ox, oy, oz) || block.IsSolidSafe(nx, ny, nz))
                {
                    continue;
                }

                ushort material = block.GetMaterial(ox, oy, oz);
                var face = (BlockFace)((axis * 2) + (positive ? 1 : 0));

                mask[u + (v * MicroBlock.Size)] = new MaskCell(
                    material,
                    registry.FaceLayer(material, face),
                    CornerAo(block, axis, otherSlice, u, v, -1, -1),
                    CornerAo(block, axis, otherSlice, u, v, +1, -1),
                    CornerAo(block, axis, otherSlice, u, v, +1, +1),
                    CornerAo(block, axis, otherSlice, u, v, -1, +1));

                any = true;
            }
        }

        return any;
    }

    /// <summary>
    /// Stínění rohu, počítané jen z obsahu bloku. Za hranicí mřížky se předpokládá prázdno,
    /// což je zase kvůli tomu, aby výsledek nezávisel na okolí.
    /// </summary>
    private static byte CornerAo(MicroBlock block, int axis, int outsideSlice, int u, int v, int du, int dv)
    {
        bool side1 = IsSolidAt(block, axis, outsideSlice, u + du, v);
        bool side2 = IsSolidAt(block, axis, outsideSlice, u, v + dv);

        if (side1 && side2)
        {
            return 0;
        }

        bool corner = IsSolidAt(block, axis, outsideSlice, u + du, v + dv);
        return (byte)(3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (corner ? 1 : 0));
    }

    private static bool IsSolidAt(MicroBlock block, int axis, int slice, int u, int v)
    {
        (int x, int y, int z) = MakeCoord(axis, slice, u, v);
        return block.IsSolidSafe(x, y, z);
    }

    private static void EmitMask(Span<MaskCell> mask, int axis, int planeCoord, bool positive, MeshBuffer mesh)
    {
        for (int v = 0; v < MicroBlock.Size; v++)
        {
            int u = 0;
            while (u < MicroBlock.Size)
            {
                MaskCell cell = mask[u + (v * MicroBlock.Size)];
                if (cell.Material == 0)
                {
                    u++;
                    continue;
                }

                int width = 1;
                while (u + width < MicroBlock.Size && mask[u + width + (v * MicroBlock.Size)] == cell)
                {
                    width++;
                }

                int height = 1;
                bool rowMatches = true;
                while (v + height < MicroBlock.Size && rowMatches)
                {
                    for (int k = 0; k < width; k++)
                    {
                        if (mask[u + k + ((v + height) * MicroBlock.Size)] != cell)
                        {
                            rowMatches = false;
                            break;
                        }
                    }

                    if (rowMatches)
                    {
                        height++;
                    }
                }

                EmitQuad(cell, axis, planeCoord, u, v, width, height, positive, mesh);

                for (int dv = 0; dv < height; dv++)
                {
                    mask.Slice(u + ((v + dv) * MicroBlock.Size), width).Clear();
                }

                u += width;
            }
        }
    }

    private static void EmitQuad(in MaskCell cell, int axis, int planeCoord, int u, int v, int width, int height, bool positive, MeshBuffer mesh)
    {
        Vector3 p00 = MakeVertex(axis, planeCoord, u, v);
        Vector3 p10 = MakeVertex(axis, planeCoord, u + width, v);
        Vector3 p11 = MakeVertex(axis, planeCoord, u + width, v + height);
        Vector3 p01 = MakeVertex(axis, planeCoord, u, v + height);

        // UV se počítá v podílech bloku, ne mikrovoxelů — textura tak na otesaném povrchu
        // navazuje na sousední neotesané bloky místo aby se šestnáctkrát zopakovala.
        float uvWidth = width * VoxelScale;
        float uvHeight = height * VoxelScale;

        Vector2 uv00 = new(0f, 0f);
        Vector2 uv10 = new(uvWidth, 0f);
        Vector2 uv11 = new(uvWidth, uvHeight);
        Vector2 uv01 = new(0f, uvHeight);

        // Stejná tabulka jako u obyčejných bloků, jinak by otesaná plocha svítila jinak
        // než neotesaná vedle ní.
        float brightness = FaceShading.ForAxis(axis, positive);
        float ao0 = FaceShading.Combine(cell.Ao00, brightness);
        float ao1 = FaceShading.Combine(cell.Ao10, brightness);
        float ao2 = FaceShading.Combine(cell.Ao11, brightness);
        float ao3 = FaceShading.Combine(cell.Ao01, brightness);

        bool flip = cell.Ao00 + cell.Ao11 < cell.Ao10 + cell.Ao01;

        if (positive)
        {
            mesh.AddQuad(p00, p10, p11, p01, uv00, uv10, uv11, uv01, cell.Layer, ao0, ao1, ao2, ao3, flip);
        }
        else
        {
            mesh.AddQuad(p00, p01, p11, p10, uv00, uv01, uv11, uv10, cell.Layer, ao0, ao3, ao2, ao1, flip);
        }
    }

    /// <summary>
    /// Sloučí vyplněné mikrovoxely do co nejmenšího počtu kvádrů.
    ///
    /// Roste se postupně po ose X, pak Z, pak Y — vždycky jen dokud je celá vrstva plná.
    /// Z rovné desky tak vyjde jediný kvádr místo dvou set padesáti šesti.
    /// </summary>
    private static Aabb[] BuildColliders(MicroBlock block)
    {
        if (block.IsEmpty)
        {
            return [];
        }

        var boxes = new List<Aabb>();
        bool[] used = new bool[MicroBlock.Volume];

        for (int y = 0; y < MicroBlock.Size; y++)
        {
            for (int z = 0; z < MicroBlock.Size; z++)
            {
                for (int x = 0; x < MicroBlock.Size; x++)
                {
                    if (used[MicroBlock.LocalIndex(x, y, z)] || !block.IsSolid(x, y, z))
                    {
                        continue;
                    }

                    int width = 1;
                    while (x + width < MicroBlock.Size && Available(block, used, x + width, y, z))
                    {
                        width++;
                    }

                    int depth = 1;
                    while (z + depth < MicroBlock.Size && RowAvailable(block, used, x, y, z + depth, width))
                    {
                        depth++;
                    }

                    int height = 1;
                    while (y + height < MicroBlock.Size && SlabAvailable(block, used, x, y + height, z, width, depth))
                    {
                        height++;
                    }

                    for (int dy = 0; dy < height; dy++)
                    {
                        for (int dz = 0; dz < depth; dz++)
                        {
                            for (int dx = 0; dx < width; dx++)
                            {
                                used[MicroBlock.LocalIndex(x + dx, y + dy, z + dz)] = true;
                            }
                        }
                    }

                    boxes.Add(new Aabb(
                        new Vector3(x, y, z) * VoxelScale,
                        new Vector3(x + width, y + height, z + depth) * VoxelScale));
                }
            }
        }

        return [.. boxes];
    }

    private static bool Available(MicroBlock block, bool[] used, int x, int y, int z) =>
        block.IsSolid(x, y, z) && !used[MicroBlock.LocalIndex(x, y, z)];

    private static bool RowAvailable(MicroBlock block, bool[] used, int x, int y, int z, int width)
    {
        for (int dx = 0; dx < width; dx++)
        {
            if (!Available(block, used, x + dx, y, z))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SlabAvailable(MicroBlock block, bool[] used, int x, int y, int z, int width, int depth)
    {
        for (int dz = 0; dz < depth; dz++)
        {
            if (!RowAvailable(block, used, x, y, z + dz, width))
            {
                return false;
            }
        }

        return true;
    }

    private static (int X, int Y, int Z) MakeCoord(int axis, int slice, int u, int v) => axis switch
    {
        0 => (slice, u, v),
        1 => (v, slice, u),
        _ => (u, v, slice),
    };

    private static Vector3 MakeVertex(int axis, float slice, float u, float v) => axis switch
    {
        0 => new Vector3(slice, u, v) * VoxelScale,
        1 => new Vector3(v, slice, u) * VoxelScale,
        _ => new Vector3(u, v, slice) * VoxelScale,
    };

    private readonly record struct MaskCell(ushort Material, int Layer, byte Ao00, byte Ao10, byte Ao11, byte Ao01);
}
