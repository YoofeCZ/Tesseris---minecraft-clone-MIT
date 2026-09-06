using OpenTK.Mathematics;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.World.Navigation;

/// <summary>
/// Které buňky jednoho chunku jsou pochůzné.
/// </summary>
/// <remarks>
/// <para><b>Proč mřížka a ne hledání přímo ve světě.</b> A* se ptá „kdo sousedí s touhle
/// buňkou" desetitisíckrát za cestu. Kdyby se to pokaždé počítalo z voxelů — podpůrná plocha,
/// kvádry dílků, mikrovoxely, obal tvora — stálo by hledání cesty násobně víc než samotný
/// průchod grafem. Mřížka se spočítá jednou při načtení chunku a přepočítá se jen tehdy, když
/// se chunk změní (pravidlo 6.8 přenesené na navigaci).</para>
///
/// <para><b>Bitová mapa, ne pole objektů.</b> 32³ buněk je 32 768 bitů, tedy 4 kB na chunk
/// (512 × <c>ulong</c>). Uzel grafu není objekt ani záznam — je to index do téhle mapy.
/// Pravidlo 6.3.</para>
///
/// <para><b>Pochůzná znamená „dá se tu stát".</b> Buňka je pochůzná, když pod ní je nosná
/// plocha a tvor se do prostoru nad ní vejde. Odpověď dává <see cref="Walkability"/>, takže
/// existuje jediná definice pochůznosti — sdílená se zvířaty.</para>
/// </remarks>
public sealed class NavGrid
{
    /// <summary>Hrana chunku v buňkách. Buňka je jeden voxel.</summary>
    public const int Size = Chunk.Size;

    public const int Volume = Size * Size * Size;

    private const int Words = Volume / 64;

    private readonly ulong[] _standable = new ulong[Words];

    /// <summary>Poloha chunku, ke kterému mřížka patří.</summary>
    public Vector3i ChunkPosition { get; private set; }

    /// <summary>Kolik buněk je pochůzných. Nula znamená chunk, kterým se nedá projít.</summary>
    public int StandableCount { get; private set; }

    /// <summary>Výška tvora, pro kterou byla mřížka spočítaná.</summary>
    public float AgentHeight { get; private set; }

    /// <summary>Šířka tvora, pro kterou byla mřížka spočítaná.</summary>
    public float AgentWidth { get; private set; }

    /// <summary>Index buňky uvnitř chunku. Pořadí x → z → y drží sloupec pohromadě.</summary>
    public static int Index(int x, int y, int z) => x + (Size * (z + (Size * y)));

    public static void FromIndex(int index, out int x, out int y, out int z)
    {
        x = index & Chunk.SizeMask;
        z = (index >> Chunk.SizeShift) & Chunk.SizeMask;
        y = index >> (Chunk.SizeShift * 2);
    }

    public static bool InBounds(int x, int y, int z) =>
        (uint)x < Size && (uint)y < Size && (uint)z < Size;

    public bool IsStandable(int index) => (_standable[index >> 6] & (1UL << (index & 63))) != 0;

    public bool IsStandable(int x, int y, int z) =>
        InBounds(x, y, z) && IsStandable(Index(x, y, z));

    /// <summary>
    /// Spočítá pochůznost celého chunku.
    /// </summary>
    /// <remarks>
    /// Prochází se všech 32 768 buněk. Je to drahé a proto se to dělá mimo tick — mřížka
    /// vzniká při načtení chunku a při jeho změně, ne při hledání cesty. Kolik to stojí
    /// ve skutečnosti, musí ukázat benchmark; dokud neexistuje, nedá se to tvrdit.
    /// </remarks>
    public static NavGrid Build(
        VoxelWorld world,
        BlockRegistry blocks,
        ushort water,
        Vector3i chunkPosition,
        float agentHeight = 1.8f,
        float agentWidth = 0.6f)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        var grid = new NavGrid
        {
            ChunkPosition = chunkPosition,
            AgentHeight = agentHeight,
            AgentWidth = agentWidth,
        };

        int baseX = chunkPosition.X * Size;
        int baseY = chunkPosition.Y * Size;
        int baseZ = chunkPosition.Z * Size;

        for (int y = 0; y < Size; y++)
        {
            for (int z = 0; z < Size; z++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int worldX = baseX + x;
                    int worldY = baseY + y;
                    int worldZ = baseZ + z;

                    if (!IsStandableAt(world, blocks, water, worldX, worldY, worldZ, agentHeight, agentWidth))
                    {
                        continue;
                    }

                    int index = Index(x, y, z);
                    grid._standable[index >> 6] |= 1UL << (index & 63);
                    grid.StandableCount++;
                }
            }
        }

        return grid;
    }

    /// <summary>
    /// Kolik pater nad a pod změněným blokem může změna ovlivnit.
    /// </summary>
    /// <remarks>
    /// <para>Odvozeno, ne odhadnuto. Blok na <c>y</c> se do pochůznosti promítá dvěma cestami:
    /// jako <b>nosič</b> pro buňku o patro výš, a jako <b>překážka</b> pro buňky, jejichž obal
    /// tvora ho protíná. Nohy leží kdekoli uvnitř své buňky, tedy až <c>y+1</c>, a tvor je
    /// vysoký 1,8 — obal proto sahá nejvýš do <c>y+2,8</c>. Buňka <c>c</c> tedy blok
    /// protíná pro <c>c ≥ y−2</c>, a nosičem je pro <c>c = y+1</c>.</para>
    ///
    /// <para>Rozsah −3 až +2 má jedno patro rezervy na obou stranách. Šest buněk proti
    /// 32 768 při plné přestavbě.</para>
    /// </remarks>
    public const int AffectedBelow = 3;

    /// <inheritdoc cref="AffectedBelow"/>
    public const int AffectedAbove = 2;

    /// <summary>
    /// Přepočítá pochůznost buněk, které mohla ovlivnit změna jednoho bloku.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč to musí existovat.</b> Naměřeno: plná přestavba mřížky jednoho chunku
    /// stojí 3,34 ms a jedno kopnutí zneplatní až osm chunků — 27 ms, tedy trojnásobek celého
    /// rozpočtu tiku. Při dvou stech kopajících kolonistech je plná přestavba neprůchodná.</para>
    ///
    /// <para><b>Proč stačí jeden sloupec.</b> Obal tvora je 0,6 široký a leží uprostřed buňky,
    /// takže zabírá vodorovně 0,2 až 0,8 uvnitř téhož bloku — do sousedního sloupce nezasahuje.
    /// Nosnou plochu si buňka taky bere jen z bloku přímo pod sebou. Změna bloku proto mění
    /// pochůznost výhradně ve svém vlastním sloupci.</para>
    ///
    /// <para>Změna u stropu nebo podlahy chunku může sahat do sousedního chunku ve svislé ose;
    /// volající musí zavolat totéž i na jeho mřížku. Vodorovně to potřeba není.</para>
    /// </remarks>
    /// <returns>true, pokud se pochůznost některé buňky opravdu změnila.</returns>
    public bool UpdateAfterBlockChange(
        VoxelWorld world,
        BlockRegistry blocks,
        ushort water,
        int worldX,
        int worldY,
        int worldZ)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        int localX = worldX - (ChunkPosition.X * Size);
        int localZ = worldZ - (ChunkPosition.Z * Size);

        if ((uint)localX >= Size || (uint)localZ >= Size)
        {
            return false;
        }

        int baseY = ChunkPosition.Y * Size;
        int first = Math.Max(0, worldY - AffectedBelow - baseY);
        int last = Math.Min(Size - 1, worldY + AffectedAbove - baseY);

        bool changed = false;
        for (int localY = first; localY <= last; localY++)
        {
            changed |= RecomputeCell(world, blocks, water, localX, localY, localZ, baseY);
        }

        return changed;
    }

    /// <summary>Přepočítá jednu buňku a srovná bit i počítadlo. Vrací, jestli se změnila.</summary>
    private bool RecomputeCell(
        VoxelWorld world,
        BlockRegistry blocks,
        ushort water,
        int localX,
        int localY,
        int localZ,
        int baseY)
    {
        bool standable = IsStandableAt(
            world,
            blocks,
            water,
            (ChunkPosition.X * Size) + localX,
            baseY + localY,
            (ChunkPosition.Z * Size) + localZ,
            AgentHeight,
            AgentWidth);

        int index = Index(localX, localY, localZ);
        int word = index >> 6;
        ulong bit = 1UL << (index & 63);
        bool was = (_standable[word] & bit) != 0;

        if (was == standable)
        {
            return false;
        }

        if (standable)
        {
            _standable[word] |= bit;
            StandableCount++;
        }
        else
        {
            _standable[word] &= ~bit;
            StandableCount--;
        }

        return true;
    }

    /// <summary>
    /// Dá se stát ve světových souřadnicích v téhle buňce?
    /// </summary>
    /// <remarks>
    /// Buňka <c>(x, y, z)</c> znamená „nohy někde uvnitř tohohle voxelu". Nosná plocha se
    /// hledá v bloku pod ní; když je nosič vyšší (schod, otesaný blok), musí vyjít výška
    /// nohou pořád uvnitř téhle buňky, jinak patří buňka nad ní a tahle by se počítala dvakrát.
    /// </remarks>
    public static bool IsStandableAt(
        VoxelWorld world,
        BlockRegistry blocks,
        ushort water,
        int worldX,
        int worldY,
        int worldZ,
        float agentHeight,
        float agentWidth)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        // RYCHLÁ ODPOVĚĎ PRO PRÁZDNÝ SLOUPEC. Bez nosiče pod nohama se stát nedá, ať je nad
        // ním cokoli — a ve vzduchu je drtivá většina buněk chunku.
        //
        // Naměřeno ve hře: `NavGrid.Build` stál 69,53 ms na jeden chunk proti 3,34 ms, které
        // má napsané v dokumentaci. Prochází 32 768 buněk a pro každou volal
        // `Walkability.TrySupportSurface`, který se ptá na tvar bloku a prochází jeho kvádry
        // přes `yield return` iterátor — tedy alokace na buňku i tam, kde je pod nohama
        // čirý vzduch. Jedna otázka „je ten blok vůbec pevný" to utne dřív.
        if (!world.IsSolid(worldX, worldY - 1, worldZ) && world.GetMicro(worldX, worldY - 1, worldZ) is null)
        {
            return false;
        }

        float centreX = worldX + 0.5f;
        float centreZ = worldZ + 0.5f;

        if (!Walkability.TrySupportSurface(
                world, blocks, water, worldX, worldY - 1, worldZ, centreX, centreZ, out float feetY))
        {
            return false;
        }

        // Nosič vyšší než jeden blok patří buňce nad touhle.
        if (feetY < worldY - 0.001f || feetY >= worldY + 1f)
        {
            return false;
        }

        float half = agentWidth * 0.5f;
        var bounds = new Engine.MathLib.Aabb(
            new Vector3(centreX - half, feetY, centreZ - half),
            new Vector3(centreX + half, feetY + agentHeight, centreZ + half));

        return Player.PlayerController.IsFree(world, bounds);
    }
}
