using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;
using Tesseris.Game.Player;

namespace Tesseris.Game.World;

/// <summary>
/// Kde se dá ve voxelovém světě stát a kudy se dá jít.
/// </summary>
/// <remarks>
/// <para><b>Proč to je samostatná třída.</b> Tohle je jediné místo v projektu, které řeší
/// pochůznost voxelového terénu — najde podpůrnou plochu, spočítá přesnou výšku nohou
/// a ověří, že se tvor do prostoru nad ní vejde. Bylo to zamčené uvnitř
/// <c>AnimalPopulation</c>, kde to sloužilo náhodné chůzi zvířat. Pathfinding kolonistů
/// (úkol T2) potřebuje přesně tenhle predikát jako <b>generátor sousedů</b> pro A*, takže
/// musí být dostupný samostatně a testovatelný bez zvířat.</para>
///
/// <para><b>Co se při vytažení opravilo.</b> Otesaný blok se dřív bral jako plná krychle
/// a tvor se postavil na jeho horní hranu, i když byl blok otesaný na půlku — v kódu k tomu
/// byla poznámka „otesaný mikroblok zatím nemá veřejný seznam kvádrů". Ten seznam ale
/// existuje: <c>VoxelWorld.MicroShapes</c> drží sloučené kvádry, proti kterým se testuje
/// i kolize hráče. Teď se z nich bere skutečná horní hrana pod nohama. Kolonisté chodící
/// po rozkopaném terénu jsou jádro hry, takže vznášet se nad vytesaným schodem nejde.</para>
///
/// <para><b>Alokace.</b> <see cref="PieceMask.Colliders"/> je <c>yield return</c> iterátor,
/// takže každé volání vyrobí objekt. Pro dnešní použití (zvířata se stropem 48) to nevadí;
/// pro 200 kolonistů hledajících cestu to bude potřeba nahradit variantou plnící
/// <c>Span&lt;Aabb&gt;</c>. Neřeší se to tady, protože bez měření v T2 není proti čemu
/// tu změnu obhájit (pravidlo „neoptimalizovat bez měření").</para>
/// </remarks>
public static class Walkability
{
    /// <summary>Odsazení nohou nad plochu, aby tvor neuvízl v geometrii.</summary>
    private const float SurfaceEpsilon = 0.001f;

    /// <summary>O kolik se smí vystoupit nahoru při hledání země.</summary>
    public const int StepUp = 1;

    /// <summary>O kolik se smí sestoupit dolů při hledání země.</summary>
    public const int StepDown = 2;

    /// <summary>
    /// Najde plochu, na které se dá v daném sloupci stát, a ověří, že se nad ni tvor vejde.
    /// </summary>
    /// <param name="fromY">Výška, ze které se hledá. Prohledá se <see cref="StepUp"/> nahoru
    /// a <see cref="StepDown"/> dolů.</param>
    /// <param name="height">Výška tvora.</param>
    /// <param name="width">Šířka tvora; obal je čtvercový.</param>
    /// <param name="feet">Poloha nohou na nalezené ploše.</param>
    public static bool TryFindGround(
        VoxelWorld world,
        BlockRegistry blocks,
        ushort water,
        Vector3 horizontal,
        float fromY,
        float height,
        float width,
        out Vector3 feet)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        int x = (int)MathF.Floor(horizontal.X);
        int z = (int)MathF.Floor(horizontal.Z);
        int centreY = (int)MathF.Floor(fromY);
        float halfWidth = width * 0.5f;

        // Shora dolů: když jde vystoupit na schod i sejít, má přednost ten vyšší povrch.
        for (int groundY = centreY + StepUp; groundY >= centreY - StepDown; groundY--)
        {
            if (groundY < 0 || groundY >= TerrainGenerator.WorldHeight - 2)
            {
                continue;
            }

            // Nenačtený chunk není totéž co prázdno. Bez téhle podmínky by tvor propadl
            // světem, který se teprve streamuje.
            Vector3i chunk = VoxelWorld.ToChunkPosition(x, groundY, z);
            if (!world.HasChunk(chunk))
            {
                continue;
            }

            if (!TrySupportSurface(world, blocks, water, x, groundY, z, horizontal.X, horizontal.Z, out float feetY))
            {
                continue;
            }

            Vector3 candidate = new(horizontal.X, feetY, horizontal.Z);
            var bounds = new Aabb(
                candidate + new Vector3(-halfWidth, 0f, -halfWidth),
                candidate + new Vector3(halfWidth, height, halfWidth));

            if (PlayerController.IsFree(world, bounds))
            {
                feet = candidate;
                return true;
            }
        }

        feet = new Vector3(horizontal.X, fromY, horizontal.Z);
        return false;
    }

    /// <summary>
    /// Jak vysoko leží povrch jednoho bloku pod zadaným vodorovným bodem.
    /// </summary>
    /// <remarks>
    /// Bere v úvahu tři různé podoby pevnosti: plný blok, blok složený z dílků
    /// (schody, půlbloky, trámy) a otesaný blok s mikrovoxely. U posledních dvou je horní
    /// hrana závislá na tom, KDE ve čtverci bloku tvor stojí.
    /// </remarks>
    public static bool TrySupportSurface(
        VoxelWorld world,
        BlockRegistry blocks,
        ushort water,
        int blockX,
        int blockY,
        int blockZ,
        float worldX,
        float worldZ,
        out float feetY)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(blocks);

        feetY = 0f;
        if (!world.IsSolid(blockX, blockY, blockZ))
        {
            return false;
        }

        float localX = worldX - blockX;
        float localZ = worldZ - blockZ;

        ushort block = world.GetBlock(blockX, blockY, blockZ);
        if (block == BlockRegistry.Air)
        {
            // OTESANÝ BLOK MÁ V BLOKOVÉ VRSTVĚ VZDUCH. Pevnost se pozná z existence
            // mikro dat — a horní hrana se bere ze sloučených kvádrů, ne z celé krychle.
            MicroBlock? micro = world.GetMicro(blockX, blockY, blockZ);
            if (micro is null)
            {
                return false;
            }

            return TryMicroSurface(world, micro, blockY, localX, localZ, out feetY);
        }

        if (block == water || !blocks.IsSolid(block))
        {
            return false;
        }

        byte pieces = world.GetPieces(blockX, blockY, blockZ);
        BlockShape shape = blocks.ShapeOf(block);

        // PLNÁ KOSTKA NEMUSÍ PROCHÁZET SEZNAM KVÁDRŮ. Jejich povrch je vždycky horní stěna
        // bloku, tedy 1,0 — a plných kostek je v každém chunku drtivá většina.
        // <c>PieceMask.Colliders</c> je `yield return` iterátor, takže každé volání vyrobí
        // objekt; tohle ho pro nejběžnější případ obejde.
        //
        // POZOR, JAKO VÝKONOVÁ OPRAVA SE TO NEPOČÍTÁ. Vypadalo to tak (67,75 → 31,76 ms na
        // chunk), ale to byla dvě jednotlivá měření z opačných konců rozptylu. Tři běhy proti
        // kódu BEZ téhle zkratky daly 32,8 / 40,1 / 47,7 ms, s ní 51,2 / 57,3 / 67,1 ms —
        // rozptyl mezi běhy je větší než rozdíl mezi variantami, takže zlepšení prokázat
        // nejde. Zkratka tu zůstává, protože je správná a levná, ne protože něco zrychlila.
        //
        // Stavba mřížky pořád stojí desítky milisekund proti rozpočtu tiku 8 ms; skutečné
        // řešení je rozložit ji do víc tiků nebo přepsat IsFree na variantu bez alokací.
        if (shape == BlockShape.Cube && pieces == PieceMask.Full)
        {
            feetY = blockY + 1f + SurfaceEpsilon;
            return true;
        }

        float highest = float.NegativeInfinity;
        foreach (Aabb collider in PieceMask.Colliders(shape, pieces))
        {
            if (!CoversColumn(collider, localX, localZ))
            {
                continue;
            }

            highest = Math.Max(highest, collider.Max.Y);
        }

        if (!float.IsFinite(highest))
        {
            return false;
        }

        feetY = blockY + highest + SurfaceEpsilon;
        return true;
    }

    /// <summary>Nejvyšší kvádr otesaného bloku pod zadaným bodem.</summary>
    private static bool TryMicroSurface(
        VoxelWorld world,
        MicroBlock micro,
        int blockY,
        float localX,
        float localZ,
        out float feetY)
    {
        feetY = 0f;
        MicroShape shape = world.MicroShapes.Get(micro);

        float highest = float.NegativeInfinity;
        foreach (Aabb collider in shape.Colliders)
        {
            if (!CoversColumn(collider, localX, localZ))
            {
                continue;
            }

            highest = Math.Max(highest, collider.Max.Y);
        }

        if (!float.IsFinite(highest))
        {
            // Pod nohama je sice otesaný blok, ale v tomhle sloupci z něj nic nezbylo —
            // tvor by stál nad dírou. Dřív se sem dosadila horní hrana celé krychle
            // a tvor se v tom místě vznášel.
            return false;
        }

        feetY = blockY + highest + SurfaceEpsilon;
        return true;
    }

    /// <summary>Leží vodorovný bod nad tímhle kvádrem? Dotyk na hraně se počítá.</summary>
    private static bool CoversColumn(in Aabb collider, float localX, float localZ) =>
        localX >= collider.Min.X - SurfaceEpsilon
        && localX <= collider.Max.X + SurfaceEpsilon
        && localZ >= collider.Min.Z - SurfaceEpsilon
        && localZ <= collider.Max.Z + SurfaceEpsilon;
}
