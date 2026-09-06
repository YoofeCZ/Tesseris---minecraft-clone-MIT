using OpenTK.Mathematics;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.World;

/// <summary>
/// Deterministic built-in structures. Every structure is authored in local coordinates and
/// rotated as a whole, so doors, stairs and asymmetric rooms remain coherent in all directions.
/// </summary>
public sealed class StructureGenerator
{
    public enum StructureKind
    {
        Ruin,
        Camp,
        UndergroundRoom,
        Cabin,
        Watchtower,
    }

    private const int RegionChunks = 8;
    private readonly int seed;
    private readonly BlockRegistry blocks;
    private readonly IReadOnlyList<StructureTemplate> customStructures;

    private readonly ushort air;
    private readonly ushort glass;
    private readonly ushort lootChest;
    private readonly ushort woodenDoor;
    private readonly ushort woodenDoorTop;
    private readonly ushort trapdoor;
    private readonly ushort ladder;
    private readonly ushort stoneFurnace;
    private readonly ushort torch;

    private readonly ushort cobblestone;
    private readonly ushort cobbleSlab;
    private readonly ushort cobbleStairs;
    private readonly ushort stoneBricks;
    private readonly ushort stoneBrickSlab;
    private readonly ushort stoneBrickStairs;
    private readonly ushort deepslateBricks;
    private readonly ushort deepslateTiles;
    private readonly ushort polishedLimestone;
    private readonly ushort polishedMarble;
    private readonly ushort bricks;

    private readonly ushort sandstoneBricks;
    private readonly ushort sandstoneSlab;
    private readonly ushort sandstoneStairs;
    private readonly ushort chiseledSandstone;
    private readonly ushort redSandstoneBricks;
    private readonly ushort redSandstoneSlab;
    private readonly ushort redSandstoneStairs;
    private readonly ushort chiseledRedSandstone;

    private readonly ushort oakPlanks;
    private readonly ushort oakSlab;
    private readonly ushort oakStairs;
    private readonly ushort oakBeam;
    private readonly ushort sprucePlanks;
    private readonly ushort spruceSlab;
    private readonly ushort spruceStairs;
    private readonly ushort spruceBeam;
    private readonly ushort acaciaPlanks;
    private readonly ushort acaciaSlab;
    private readonly ushort acaciaStairs;
    private readonly ushort acaciaBeam;
    private readonly ushort maplePlanks;
    private readonly ushort mapleSlab;
    private readonly ushort mapleStairs;
    private readonly ushort mapleBeam;
    private readonly ushort redRoofTiles;
    private readonly ushort redRoofSlab;
    private readonly ushort redRoofStairs;
    private readonly ushort slateShingles;
    private readonly ushort slateSlab;
    private readonly ushort slateStairs;

    private readonly ushort machineFrame;
    private readonly ushort reinforcedFrame;
    private readonly ushort powerRelay;
    private readonly ushort smallBattery;
    private readonly ushort largeBattery;
    private readonly ushort ancientPanel;
    private readonly ushort corrugatedMetal;
    private readonly ushort copperBlock;

    private readonly record struct WoodPalette(
        ushort Planks, ushort Slab, ushort Stairs, ushort Beam);

    private readonly record struct MasonryPalette(
        ushort Wall, ushort Accent, ushort Slab, ushort Stairs);

    public StructureGenerator(BlockRegistry blocks, int seed)
    {
        this.blocks = blocks;
        this.seed = seed;
        customStructures = StructureTemplateStore.LoadAll();

        ushort B(string id) => blocks.IndexOf("tesseris:" + id);

        air = BlockRegistry.Air;
        glass = B("glass");
        lootChest = B("loot_chest");
        woodenDoor = B("wooden_door");
        woodenDoorTop = B("wooden_door_top");
        trapdoor = B("wooden_trapdoor");
        ladder = B("ladder");
        stoneFurnace = B("stone_furnace");
        torch = B("torch");

        cobblestone = B("cobblestone");
        cobbleSlab = B("cobblestone_slab");
        cobbleStairs = B("cobblestone_stairs");
        stoneBricks = B("stone_bricks");
        stoneBrickSlab = B("stone_bricks_slab");
        stoneBrickStairs = B("stone_bricks_stairs");
        deepslateBricks = B("deepslate_bricks");
        deepslateTiles = B("deepslate_tiles");
        polishedLimestone = B("polished_limestone");
        polishedMarble = B("polished_marble");
        bricks = B("bricks");

        sandstoneBricks = B("sandstone_bricks");
        sandstoneSlab = B("sandstone_bricks_slab");
        sandstoneStairs = B("sandstone_bricks_stairs");
        chiseledSandstone = B("chiseled_sandstone");
        redSandstoneBricks = B("red_sandstone_bricks");
        redSandstoneSlab = B("red_sandstone_bricks_slab");
        redSandstoneStairs = B("red_sandstone_bricks_stairs");
        chiseledRedSandstone = B("chiseled_red_sandstone");

        oakPlanks = B("oak_planks");
        oakSlab = B("oak_planks_slab");
        oakStairs = B("oak_planks_stairs");
        oakBeam = B("oak_beam");
        sprucePlanks = B("spruce_planks");
        spruceSlab = B("spruce_planks_slab");
        spruceStairs = B("spruce_planks_stairs");
        spruceBeam = B("spruce_beam");
        acaciaPlanks = B("acacia_planks");
        acaciaSlab = B("acacia_planks_slab");
        acaciaStairs = B("acacia_planks_stairs");
        acaciaBeam = B("acacia_beam");
        maplePlanks = B("maple_planks");
        mapleSlab = B("maple_planks_slab");
        mapleStairs = B("maple_planks_stairs");
        mapleBeam = B("maple_beam");
        redRoofTiles = B("red_roof_tiles");
        redRoofSlab = B("red_roof_tiles_slab");
        redRoofStairs = B("red_roof_tiles_stairs");
        slateShingles = B("slate_shingles");
        slateSlab = B("slate_shingles_slab");
        slateStairs = B("slate_shingles_stairs");

        machineFrame = B("machine_frame");
        reinforcedFrame = B("reinforced_machine_frame");
        powerRelay = B("power_relay");
        smallBattery = B("battery_rack_small");
        largeBattery = B("battery_rack_large");
        ancientPanel = B("ancient_tech_panel");
        corrugatedMetal = B("corrugated_metal");
        copperBlock = B("copper_block");
    }

    public void Place(Chunk chunk, Vector3i chunkPosition, TerrainGenerator terrain)
    {
        int regionX = FloorDiv(chunkPosition.X, RegionChunks);
        int regionZ = FloorDiv(chunkPosition.Z, RegionChunks);

        for (int rz = regionZ - 1; rz <= regionZ + 1; rz++)
        for (int rx = regionX - 1; rx <= regionX + 1; rx++)
        {
            ulong hash = Hash(seed, rx, rz, 0);
            if ((hash & 7UL) >= 5UL) continue;

            int regionBlocks = RegionChunks * Chunk.Size;
            int originX = (rx * regionBlocks) + 48
                + (int)((hash >> 8) % (ulong)(regionBlocks - 96));
            int originZ = (rz * regionBlocks) + 48
                + (int)((hash >> 24) % (ulong)(regionBlocks - 96));
            int surface = terrain.SurfaceHeight(originX, originZ);
            Biome biome = terrain.BiomeAt(originX, originZ);
            int rotation = (int)((hash >> 56) & 3UL);
            StructureKind kind = SelectKind(hash);

            (int radius, int top) = kind switch
            {
                StructureKind.Cabin => (8, 12),
                StructureKind.Ruin => (8, 8),
                StructureKind.Camp => (8, 7),
                StructureKind.Watchtower => (7, 16),
                _ => (11, 7),
            };

            if (!OverlapsHorizontal(
                    chunkPosition,
                    originX - radius, originX + radius,
                    originZ - radius, originZ + radius))
            {
                continue;
            }

            int maximumRelief = kind switch
            {
                StructureKind.Cabin or StructureKind.Watchtower => 2,
                StructureKind.Camp => 3,
                _ => 4,
            };
            if (kind != StructureKind.UndergroundRoom
                && !SuitableSurface(terrain, originX, originZ, radius - 2, surface, maximumRelief))
            {
                continue;
            }

            if (kind == StructureKind.UndergroundRoom)
            {
                int roomY = Math.Clamp(
                    surface - 20 - (int)((hash >> 52) & 15UL),
                    18,
                    TerrainGenerator.WorldHeight - 10);
                if (OverlapsVertical(chunkPosition, roomY - 2, roomY + top))
                {
                    UndergroundVault(chunk, chunkPosition, originX, roomY, originZ, rotation, hash);
                }
                continue;
            }

            int originY = surface + 1;
            if (!OverlapsVertical(chunkPosition, originY - 5, originY + top)) continue;

            switch (kind)
            {
                case StructureKind.Cabin:
                    Cabin(chunk, chunkPosition, terrain, originX, originY, originZ, rotation, biome, hash);
                    break;
                case StructureKind.Ruin:
                    Ruin(chunk, chunkPosition, terrain, originX, originY, originZ, rotation, biome, hash);
                    break;
                case StructureKind.Camp:
                    Camp(chunk, chunkPosition, terrain, originX, originY, originZ, rotation, biome, hash);
                    break;
                case StructureKind.Watchtower:
                    Watchtower(chunk, chunkPosition, terrain, originX, originY, originZ, rotation, biome, hash);
                    break;
            }
        }

        PlaceCustomStructures(chunk, chunkPosition, terrain);
    }

    private void PlaceCustomStructures(
        Chunk chunk, Vector3i chunkPosition, TerrainGenerator terrain)
    {
        for (int templateIndex = 0; templateIndex < customStructures.Count; templateIndex++)
        {
            StructureTemplate template = customStructures[templateIndex];
            StructureSpawnRules rules = template.Spawn;
            if (!rules.Enabled || template.Blocks.Count == 0) continue;
            int spacing = Math.Clamp(rules.SpacingChunks, 4, 64);
            int regionX = FloorDiv(chunkPosition.X, spacing);
            int regionZ = FloorDiv(chunkPosition.Z, spacing);

            for (int rz = regionZ - 1; rz <= regionZ + 1; rz++)
            for (int rx = regionX - 1; rx <= regionX + 1; rx++)
            {
                ulong hash = Hash(seed, rx, rz, 1000 + templateIndex);
                if ((int)(hash % 100UL) >= Math.Clamp(rules.ChancePercent, 0, 100)) continue;
                int span = spacing * Chunk.Size;
                int originX = (rx * span) + (int)((hash >> 8) % (ulong)span);
                int originZ = (rz * span) + (int)((hash >> 28) % (ulong)span);
                int surface = terrain.SurfaceHeight(originX, originZ);
                int originY = surface + rules.SurfaceOffset;
                if (originY < rules.MinY || originY > rules.MaxY) continue;
                Biome biome = terrain.BiomeAt(originX, originZ);
                if (rules.Biomes.Length > 0
                    && !rules.Biomes.Any(value => string.Equals(
                        value, biome.ToString(), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                int radius = Math.Max(template.SizeX, template.SizeZ);
                if (!OverlapsHorizontal(chunkPosition,
                        originX - radius, originX + radius,
                        originZ - radius, originZ + radius)
                    || !OverlapsVertical(chunkPosition,
                        originY - template.AnchorY,
                        originY + template.SizeY - template.AnchorY))
                {
                    continue;
                }

                template.Place(chunk, chunkPosition, blocks,
                    originX, originY, originZ, (int)((hash >> 58) & 3UL));
            }
        }
    }

    internal static StructureKind SelectKind(ulong hash)
    {
        // Keep the original three-way split for world/test compatibility. Surface variants
        // are selected with higher bits, while roll 2 remains the underground structure.
        return ((hash >> 40) % 3UL) switch
        {
            0UL => ((hash >> 48) & 1UL) == 0 ? StructureKind.Ruin : StructureKind.Cabin,
            1UL => ((hash >> 49) & 1UL) == 0 ? StructureKind.Camp : StructureKind.Watchtower,
            _ => StructureKind.UndergroundRoom,
        };
    }

    private static bool SuitableSurface(
        TerrainGenerator terrain, int originX, int originZ, int spread, int centre,
        int maximumRelief)
    {
        if (centre <= TerrainGenerator.SeaLevel + 2) return false;

        int min = centre;
        int max = centre;
        for (int z = -spread; z <= spread; z += spread)
        for (int x = -spread; x <= spread; x += spread)
        {
            int height = terrain.SurfaceHeight(originX + x, originZ + z);
            min = Math.Min(min, height);
            max = Math.Max(max, height);
        }
        return max - min <= maximumRelief;
    }

    private WoodPalette WoodFor(Biome biome, ulong hash) => biome switch
    {
        Biome.Savanna or Biome.Badlands => new(acaciaPlanks, acaciaSlab, acaciaStairs, acaciaBeam),
        Biome.Tundra or Biome.Highlands or Biome.SnowyPeaks or Biome.FrozenPeaks =>
            new(sprucePlanks, spruceSlab, spruceStairs, spruceBeam),
        _ when ((hash >> 51) & 1UL) != 0 =>
            new(maplePlanks, mapleSlab, mapleStairs, mapleBeam),
        _ => new(oakPlanks, oakSlab, oakStairs, oakBeam),
    };

    private MasonryPalette MasonryFor(Biome biome) => biome switch
    {
        Biome.Desert => new(sandstoneBricks, chiseledSandstone, sandstoneSlab, sandstoneStairs),
        Biome.Badlands => new(redSandstoneBricks, chiseledRedSandstone, redSandstoneSlab, redSandstoneStairs),
        Biome.Tundra or Biome.SnowyPeaks or Biome.FrozenPeaks =>
            new(deepslateBricks, polishedMarble, slateSlab, slateStairs),
        _ => new(stoneBricks, polishedLimestone, stoneBrickSlab, stoneBrickStairs),
    };

    private void Cabin(
        Chunk chunk, Vector3i cp, TerrainGenerator terrain,
        int ox, int oy, int oz, int rotation, Biome biome, ulong hash)
    {
        WoodPalette wood = WoodFor(biome, hash);
        MasonryPalette stone = MasonryFor(biome);
        ushort roof = biome is Biome.Tundra or Biome.Highlands or Biome.SnowyPeaks or Biome.FrozenPeaks
            ? slateShingles : redRoofTiles;
        ushort roofSlab = roof == slateShingles ? slateSlab : redRoofSlab;
        ushort roofStairs = roof == slateShingles ? slateStairs : redRoofStairs;

        ClearBox(chunk, cp, ox, oy, oz, rotation, -7, 7, 0, 12, -6, 6);
        Foundation(chunk, cp, terrain, ox, oy, oz, rotation, -5, 5, -4, 4, stone.Wall);

        // Podlaha, sokl i práh leží ve stejné výšce. Dřív byla podlaha o blok níž,
        // takže se z obytné místnosti stal nechtěný zapuštěný bazén.
        for (int z = -4; z <= 4; z++)
        for (int x = -5; x <= 5; x++)
        {
            ushort floor = ((x + z) & 3) == 0 ? stone.Accent : wood.Planks;
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 0, z, floor);
            if (Math.Abs(x) == 5 || Math.Abs(z) == 4)
                SetLocal(chunk, cp, ox, oy, oz, rotation, x, 0, z, stone.Wall);
        }

        for (int y = 1; y <= 4; y++)
        for (int z = -4; z <= 4; z++)
        for (int x = -5; x <= 5; x++)
        {
            bool wall = Math.Abs(x) == 5 || Math.Abs(z) == 4;
            if (!wall) continue;

            bool doorway = z == -4 && x == 0 && y <= 2;
            bool window = y is 2 or 3 && (
                (Math.Abs(x) == 5 && z is -1 or 0 or 1)
                || (Math.Abs(z) == 4 && x is -3 or 3));
            ushort material = window ? glass : wood.Planks;
            if (!doorway) SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, material);
        }

        // Corner posts, wall beams and ceiling beams make the silhouette read as timber framing.
        foreach (int x in new[] { -5, 5 })
        foreach (int z in new[] { -4, 4 })
        for (int y = 0; y <= 5; y++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, wood.Planks);

        for (int x = -4; x <= 4; x++)
        {
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 4, -4, wood.Planks);
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 4, 4, wood.Planks);
        }
        for (int z = -3; z <= 3; z++)
        {
            SetLocal(chunk, cp, ox, oy, oz, rotation, -5, 4, z, wood.Planks);
            SetLocal(chunk, cp, ox, oy, oz, rotation, 5, 4, z, wood.Planks);
        }

        // Uzavřený strop nese střechu a odstraní průhled mezi horní hranou stěn a sklonem.
        for (int z = -4; z <= 4; z++)
        for (int x = -5; x <= 5; x++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 5, z, wood.Planks);

        // Trojúhelníkové štíty na obou čelech vyplní prostor mezi stropem a každou
        // vyšší řadou sedlové střechy. Bez nich byl dům z boků otevřený až do krovu.
        foreach (int z in new[] { -4, 4 })
        for (int x = -5; x <= 5; x++)
        {
            int roofY = 5 + (6 - Math.Abs(x));
            for (int y = 6; y < roofY; y++)
                SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, wood.Planks);
        }

        // Stepped gable roof with overhang. Every row is a real stair, not a solid rectangle.
        for (int z = -5; z <= 5; z++)
        for (int x = -6; x <= 6; x++)
        {
            int distance = Math.Abs(x);
            int rise = Math.Max(0, 6 - distance);
            if (x == 0)
                SetLocal(chunk, cp, ox, oy, oz, rotation, x, 5 + rise, z, roofSlab);
            else
                SetStairLocal(
                    chunk, cp, ox, oy, oz, rotation,
                    x, 5 + rise, z, roofStairs,
                    x < 0 ? PieceMask.DoorEast : PieceMask.DoorWest,
                    upsideDown: false);
        }

        SetDoorLocal(chunk, cp, ox, oy, oz, rotation, 0, 1, -4, PieceMask.DoorSouth);
        SetStairLocal(chunk, cp, ox, oy, oz, rotation, 0, 0, -5, stone.Stairs, PieceMask.DoorSouth, false);

        // Interior: hearth, work bench, loft access and loot beside the back wall.
        SetLocal(chunk, cp, ox, oy, oz, rotation, -3, 1, 2, stone.Wall);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -3, 2, 2, stoneFurnace);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 3, 1, 2, lootChest);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 1, 1, 2, wood.Slab);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 0, 1, 2, wood.Slab);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -1, 1, 2, wood.Slab);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -4, 1, -2, torch);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 4, 1, -2, torch);

        _ = roof; // Root texture block remains part of the palette even when shaped variants build the roof.
    }

    private void Ruin(
        Chunk chunk, Vector3i cp, TerrainGenerator terrain,
        int ox, int oy, int oz, int rotation, Biome biome, ulong hash)
    {
        MasonryPalette stone = MasonryFor(biome);
        ClearBox(chunk, cp, ox, oy, oz, rotation, -8, 8, 0, 8, -7, 7);
        Foundation(chunk, cp, terrain, ox, oy, oz, rotation, -6, 6, -5, 5, stone.Wall);

        for (int z = -5; z <= 5; z++)
        for (int x = -6; x <= 6; x++)
        {
            ushort floor = ((x + z) & 1) == 0 ? stone.Wall : stone.Accent;
            SetLocal(chunk, cp, ox, oy - 1, oz, rotation, x, 0, z, floor);
        }

        // Čitelná kamenná svatyně: souvislý obvod, pravidelné průchody a žádné náhodně
        // plovoucí zbytky patra. Ruina je poškozená otevřenou střechou, ne chaosem bloků.
        for (int y = 0; y <= 3; y++)
        for (int z = -5; z <= 5; z++)
        for (int x = -6; x <= 6; x++)
        {
            bool edge = Math.Abs(x) == 6 || Math.Abs(z) == 5;
            if (!edge) continue;
            bool entrance = z == -5 && Math.Abs(x) <= 1 && y is 1 or 2;
            bool sideWindow = y == 2 && Math.Abs(x) == 6 && Math.Abs(z) <= 1;
            bool rearWindow = y == 2 && z == 5 && Math.Abs(x) is 3 or 4;
            if (entrance || sideWindow || rearWindow) continue;

            ushort material = y == 1 && ((x + z) & 3) == 0 ? stone.Accent : stone.Wall;
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, material);
        }

        // Čtyři stabilní nárožní věžice a souvislá římsa dávají stavbě jasný účel i siluetu.
        foreach (int x in new[] { -6, 6 })
        foreach (int z in new[] { -5, 5 })
        for (int y = 0; y <= 5; y++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z,
                y is 1 or 4 ? stone.Accent : stone.Wall);

        for (int x = -5; x <= 5; x++)
        {
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 4, -5, stone.Slab);
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 4, 5, stone.Slab);
        }
        for (int z = -4; z <= 4; z++)
        {
            SetLocal(chunk, cp, ox, oy, oz, rotation, -6, 4, z, stone.Slab);
            SetLocal(chunk, cp, ox, oy, oz, rotation, 6, 4, z, stone.Slab);
        }

        // Centrální oltář s přístupovými schody a technickým reliktem.
        SetStairLocal(chunk, cp, ox, oy, oz, rotation, 0, 0, -6, stone.Stairs, PieceMask.DoorSouth, false);
        for (int z = 1; z <= 3; z++)
        for (int x = -2; x <= 2; x++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 0, z, stone.Accent);
        for (int x = -1; x <= 1; x++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 1, 3, stone.Wall);
        SetStairLocal(chunk, cp, ox, oy, oz, rotation, 0, 0, 0,
            stone.Stairs, PieceMask.DoorSouth, false);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 0, 2, 3, ancientPanel);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -1, 2, 3, torch);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 1, 2, 3, torch);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 4, 0, 3, lootChest);
        if ((hash & 1UL) != 0)
            SetLocal(chunk, cp, ox, oy, oz, rotation, -4, 0, 3, lootChest);
    }

    private void Camp(
        Chunk chunk, Vector3i cp, TerrainGenerator terrain,
        int ox, int oy, int oz, int rotation, Biome biome, ulong hash)
    {
        WoodPalette wood = WoodFor(biome, hash);
        MasonryPalette stone = MasonryFor(biome);
        ClearBox(chunk, cp, ox, oy, oz, rotation, -8, 8, 0, 7, -7, 7);
        // Dvě menší plošiny pod přístřešky; střed tábora zůstává přirozený terén.
        Foundation(chunk, cp, terrain, ox, oy, oz, rotation, -9, -1, -4, 4, stone.Wall);
        Foundation(chunk, cp, terrain, ox, oy, oz, rotation, 1, 9, -4, 4, stone.Wall);

        // Central fire ring and four low seats.
        for (int z = -1; z <= 1; z++)
        for (int x = -1; x <= 1; x++)
            if (Math.Abs(x) == 1 || Math.Abs(z) == 1)
                SetLocal(chunk, cp, ox, oy, oz, rotation, x, -1, z, cobblestone);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 0, 0, 0, torch);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -3, 0, 0, wood.Slab);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 3, 0, 0, wood.Slab);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 0, 0, -3, wood.Slab);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 0, 0, 3, wood.Slab);

        // Two opposing shelters with proper posts and sloped roofs.
        Shelter(chunk, cp, ox, oy, oz, rotation, -5, 0, wood, redRoofStairs, redRoofSlab);
        Shelter(chunk, cp, ox, oy, oz, rotation, 5, 0, wood, redRoofStairs, redRoofSlab);

        SetLocal(chunk, cp, ox, oy, oz, rotation, -5, 0, 3, lootChest);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 5, 0, -3, lootChest);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -6, 0, -3, stoneFurnace);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 6, 0, 3, wood.Planks);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 6, 1, 3, trapdoor);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -2, 0, 5, wood.Planks);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 2, 0, 5, wood.Planks);
        for (int x = -1; x <= 1; x++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 1, 5, wood.Planks);
    }

    private void Shelter(
        Chunk chunk, Vector3i cp, int ox, int oy, int oz, int rotation,
        int centreX, int centreZ, WoodPalette wood, ushort roofStairs, ushort roofSlab)
    {
        for (int z = -3; z <= 3; z += 6)
        for (int x = -2; x <= 2; x += 4)
        for (int y = 0; y <= 4; y++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, centreX + x, y, centreZ + z, wood.Planks);

        // Pevná podkladová střecha: schody tvoří viditelný sklon, ale pod nimi už není
        // možné vidět oblohu a sloupy mají skutečný blok, kterého se dotýkají.
        for (int z = -3; z <= 3; z++)
        for (int x = -3; x <= 3; x++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, centreX + x, 3, centreZ + z, wood.Planks);

        // Souvislá sedlová střecha: žádné tři oddělené pruhy ani mezery mezi nimi.
        for (int z = -4; z <= 4; z++)
        for (int x = -4; x <= 4; x++)
        {
            int rise = Math.Max(0, 4 - Math.Abs(x));
            if (x == 0)
                SetLocal(chunk, cp, ox, oy, oz, rotation, centreX, 3 + rise, centreZ + z, roofSlab);
            else
                SetStairLocal(
                    chunk, cp, ox, oy, oz, rotation,
                    centreX + x, 3 + rise, centreZ + z, roofStairs,
                    x < 0 ? PieceMask.DoorEast : PieceMask.DoorWest, false);
        }
        for (int z = -3; z <= 3; z++)
        for (int x = -3; x <= 3; x++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, centreX + x, -1, centreZ + z, wood.Planks);
    }

    private void Watchtower(
        Chunk chunk, Vector3i cp, TerrainGenerator terrain,
        int ox, int oy, int oz, int rotation, Biome biome, ulong hash)
    {
        WoodPalette wood = WoodFor(biome, hash);
        MasonryPalette stone = MasonryFor(biome);
        ClearBox(chunk, cp, ox, oy, oz, rotation, -7, 7, 0, 16, -7, 7);
        Foundation(chunk, cp, terrain, ox, oy, oz, rotation, -4, 4, -4, 4, stone.Wall);

        // Solid masonry base with a real entrance and full glass slit windows.
        for (int y = 0; y <= 3; y++)
        for (int z = -3; z <= 3; z++)
        for (int x = -3; x <= 3; x++)
        {
            bool shell = y == 0 || Math.Abs(x) == 3 || Math.Abs(z) == 3;
            bool door = z == -3 && x == 0 && y is 1 or 2;
            bool slit = y == 2 && ((Math.Abs(x) == 3 && z == 0) || (Math.Abs(z) == 3 && x is -2 or 2));
            if (shell && !door) SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, slit ? glass : stone.Wall);
        }
        SetDoorLocal(chunk, cp, ox, oy, oz, rotation, 0, 1, -3, PieceMask.DoorSouth);
        SetStairLocal(chunk, cp, ox, oy, oz, rotation, 0, 0, -4, stone.Stairs, PieceMask.DoorSouth, false);

        // Timber upper floor, balcony and four structural posts.
        for (int z = -4; z <= 4; z++)
        for (int x = -4; x <= 4; x++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 4, z, wood.Planks);
        foreach (int x in new[] { -3, 3 })
        foreach (int z in new[] { -3, 3 })
        for (int y = 4; y <= 9; y++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, wood.Planks);

        for (int y = 5; y <= 8; y++)
        for (int z = -3; z <= 3; z++)
        for (int x = -3; x <= 3; x++)
        {
            bool wall = Math.Abs(x) == 3 || Math.Abs(z) == 3;
            bool window = y is 6 or 7 && (
                (Math.Abs(x) == 3 && z == 0) || (Math.Abs(z) == 3 && x == 0));
            if (wall) SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, window ? glass : wood.Planks);
        }

        // Stěny končí v y=8; souvislý strop v y=9 je uzavře a nese střechu.
        for (int z = -3; z <= 3; z++)
        for (int x = -3; x <= 3; x++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, 9, z, wood.Planks);

        // Pyramida nad obvodem stoupá až od y=12. Obvodové stěny proto musí mít
        // souvislou horní výplň; jinak pod šikminou zůstane dvoubloková vodorovná mezera.
        for (int y = 10; y <= 11; y++)
        for (int z = -3; z <= 3; z++)
        for (int x = -3; x <= 3; x++)
        {
            if (Math.Abs(x) == 3 || Math.Abs(z) == 3)
                SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, wood.Planks);
        }

        // Battlements and a pyramidal stepped roof.
        for (int i = -4; i <= 4; i++)
        {
            if ((i & 1) == 0)
            {
                SetLocal(chunk, cp, ox, oy, oz, rotation, i, 5, -4, stone.Accent);
                SetLocal(chunk, cp, ox, oy, oz, rotation, i, 5, 4, stone.Accent);
                SetLocal(chunk, cp, ox, oy, oz, rotation, -4, 5, i, stone.Accent);
                SetLocal(chunk, cp, ox, oy, oz, rotation, 4, 5, i, stone.Accent);
            }
        }
        for (int ring = 0; ring <= 5; ring++)
        {
            int extent = 5 - ring;
            int y = 10 + ring;
            if (extent == 0)
            {
                SetLocal(chunk, cp, ox, oy, oz, rotation, 0, y, 0, slateSlab);
                continue;
            }
            for (int i = -extent + 1; i < extent; i++)
            {
                SetStairLocal(chunk, cp, ox, oy, oz, rotation, i, y, -extent, slateStairs, PieceMask.DoorSouth, false);
                SetStairLocal(chunk, cp, ox, oy, oz, rotation, i, y, extent, slateStairs, PieceMask.DoorNorth, false);
                SetStairLocal(chunk, cp, ox, oy, oz, rotation, -extent, y, i, slateStairs, PieceMask.DoorEast, false);
                SetStairLocal(chunk, cp, ox, oy, oz, rotation, extent, y, i, slateStairs, PieceMask.DoorWest, false);
            }

            // Rohy se nesmí čtyřikrát přepsat poslední stranou. Vynucené outer rohy
            // drží souvislou pyramidu a správně míří vysokou čtvrtinou dovnitř střechy.
            SetStairLocal(chunk, cp, ox, oy, oz, rotation, -extent, y, -extent,
                slateStairs, PieceMask.DoorSouth, false, StairCornerShape.OuterLeft);
            SetStairLocal(chunk, cp, ox, oy, oz, rotation, extent, y, -extent,
                slateStairs, PieceMask.DoorSouth, false, StairCornerShape.OuterRight);
            SetStairLocal(chunk, cp, ox, oy, oz, rotation, extent, y, extent,
                slateStairs, PieceMask.DoorNorth, false, StairCornerShape.OuterLeft);
            SetStairLocal(chunk, cp, ox, oy, oz, rotation, -extent, y, extent,
                slateStairs, PieceMask.DoorNorth, false, StairCornerShape.OuterRight);
        }

        // Souvislý žebřík z přízemí do horní místnosti; blok podlahy v y=4 se jím
        // nahradí a vytvoří skutečný průlez, ne slepý trapdoor ve stropě.
        for (int y = 1; y <= 8; y++)
            SetLadderLocal(chunk, cp, ox, oy, oz, rotation, 2, y, 2, PieceMask.DoorWest);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 0, 5, 2, lootChest);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -2, 5, 2, torch);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 2, 5, -2, torch);
    }

    private void UndergroundVault(
        Chunk chunk, Vector3i cp, int ox, int oy, int oz, int rotation, ulong hash)
    {
        // Main hall shell.
        for (int y = -2; y <= 6; y++)
        for (int z = -8; z <= 8; z++)
        for (int x = -8; x <= 8; x++)
        {
            bool shell = y is -2 or 6 || Math.Abs(x) == 8 || Math.Abs(z) == 8;
            ushort material = y == -2 ? deepslateTiles
                : ((x + y + z) & 7) == 0 ? reinforcedFrame : deepslateBricks;
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, shell ? material : air);
        }

        // Entrance corridor and two side chambers.
        ClearBox(chunk, cp, ox, oy, oz, rotation, -2, 2, -1, 3, -11, -8);
        for (int z = -12; z <= -8; z++)
        {
            for (int x = -2; x <= 2; x++) SetLocal(chunk, cp, ox, oy, oz, rotation, x, -1, z, deepslateTiles);
            for (int y = 0; y <= 3; y++)
            {
                SetLocal(chunk, cp, ox, oy, oz, rotation, -3, y, z, reinforcedFrame);
                SetLocal(chunk, cp, ox, oy, oz, rotation, 3, y, z, reinforcedFrame);
            }
        }
        for (int side = -1; side <= 1; side += 2)
        {
            int cx = side * 6;
            for (int y = 0; y <= 4; y++)
            for (int z = -3; z <= 3; z++)
            for (int x = cx - 3; x <= cx + 3; x++)
            {
                bool shell = y == 4 || x == cx - 3 || x == cx + 3 || Math.Abs(z) == 3;
                if (shell) SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, deepslateBricks);
                else SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, air);
            }
        }

        // Pillars, cable trench and machinery make this a place rather than an empty box.
        foreach (int x in new[] { -6, -2, 2, 6 })
        foreach (int z in new[] { -6, 6 })
        for (int y = -1; y <= 5; y++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, y is 0 or 4 ? copperBlock : reinforcedFrame);

        for (int z = -6; z <= 6; z++)
        {
            SetLocal(chunk, cp, ox, oy, oz, rotation, 0, -1, z, corrugatedMetal);
            if ((z & 2) == 0) SetLocal(chunk, cp, ox, oy, oz, rotation, 0, 0, z, powerRelay);
        }

        SetDoorLocal(chunk, cp, ox, oy, oz, rotation, 0, 0, -8, PieceMask.DoorSouth);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -6, -1, 0, largeBattery);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -5, -1, 0, smallBattery);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 5, -1, 0, machineFrame);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 6, -1, 0, ancientPanel);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 6, 0, 0, ancientPanel);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 3, -1, 5, lootChest);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -3, -1, 5, lootChest);
        if ((hash & 1UL) != 0) SetLocal(chunk, cp, ox, oy, oz, rotation, 0, -1, 7, lootChest);
        SetLocal(chunk, cp, ox, oy, oz, rotation, -7, 1, 0, ancientPanel);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 7, 1, 0, ancientPanel);
        SetLocal(chunk, cp, ox, oy, oz, rotation, 0, 5, 7, ancientPanel);
    }

    private void Foundation(
        Chunk chunk, Vector3i cp, TerrainGenerator terrain,
        int ox, int oy, int oz, int rotation,
        int minX, int maxX, int minZ, int maxZ, ushort material)
    {
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            (int wx, int wz) = Transform(ox, oz, x, z, rotation);
            int surface = terrain.SurfaceHeight(wx, wz);
            int bottom = Math.Max(surface + 1, oy - 5);
            for (int y = bottom; y < oy; y++) Set(chunk, cp, wx, y, wz, material);
            Set(chunk, cp, wx, oy - 1, wz, material);
        }
    }

    private void ClearBox(
        Chunk chunk, Vector3i cp, int ox, int oy, int oz, int rotation,
        int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
            SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, air);
    }

    private void SetDoorLocal(
        Chunk chunk, Vector3i cp, int ox, int oy, int oz, int rotation,
        int x, int y, int z, int localFacing)
    {
        int facing = RotateFacing(localFacing, rotation);
        byte state = PieceMask.DoorState(facing, hingeRight: false, open: false);
        SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, woodenDoor, state);
        SetLocal(chunk, cp, ox, oy, oz, rotation, x, y + 1, z, woodenDoorTop, state);
    }

    private void SetStairLocal(
        Chunk chunk, Vector3i cp, int ox, int oy, int oz, int rotation,
        int x, int y, int z, ushort block, int localFacing, bool upsideDown,
        StairCornerShape corner = StairCornerShape.Straight)
    {
        byte state = PieceMask.StairState(
            RotateFacing(localFacing, rotation),
            upsideDown,
            corner);
        SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, block, state);
    }

    private void SetLadderLocal(
        Chunk chunk, Vector3i cp, int ox, int oy, int oz, int rotation,
        int x, int y, int z, int localFacing)
    {
        byte state = PieceMask.LadderState(RotateFacing(localFacing, rotation));
        SetLocal(chunk, cp, ox, oy, oz, rotation, x, y, z, ladder, state);
    }

    private void SetLocal(
        Chunk chunk, Vector3i cp, int ox, int oy, int oz, int rotation,
        int x, int y, int z, ushort block, byte? pieces = null)
    {
        (int wx, int wz) = Transform(ox, oz, x, z, rotation);
        Set(chunk, cp, wx, oy + y, wz, block, pieces);
    }

    private static (int X, int Z) Transform(int ox, int oz, int x, int z, int rotation) =>
        (rotation & 3) switch
        {
            1 => (ox + z, oz - x),
            2 => (ox - x, oz - z),
            3 => (ox - z, oz + x),
            _ => (ox + x, oz + z),
        };

    private static int RotateFacing(int facing, int rotation) => (facing + rotation) & 3;

    private void Set(
        Chunk chunk, Vector3i cp, int wx, int wy, int wz, ushort block, byte? pieces = null)
    {
        int lx = wx - (cp.X * Chunk.Size);
        int ly = wy - (cp.Y * Chunk.Size);
        int lz = wz - (cp.Z * Chunk.Size);
        if ((uint)lx >= Chunk.Size || (uint)ly >= Chunk.Size || (uint)lz >= Chunk.Size) return;

        chunk.SetBlock(lx, ly, lz, block);
        chunk.SetPieces(lx, ly, lz, pieces ?? blocks.DefaultPieces(block));
    }

    private static bool OverlapsHorizontal(
        Vector3i cp, int minX, int maxX, int minZ, int maxZ)
    {
        int chunkMinX = cp.X * Chunk.Size;
        int chunkMinZ = cp.Z * Chunk.Size;
        return maxX >= chunkMinX && minX < chunkMinX + Chunk.Size
            && maxZ >= chunkMinZ && minZ < chunkMinZ + Chunk.Size;
    }

    private static bool OverlapsVertical(Vector3i cp, int minY, int maxY)
    {
        int chunkMinY = cp.Y * Chunk.Size;
        return maxY >= chunkMinY && minY < chunkMinY + Chunk.Size;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : ((value + 1) / divisor) - 1;

    internal static ulong Hash(long seed, int x, int y, int z)
    {
        ulong value = unchecked((ulong)seed) ^ 0x9E3779B97F4A7C15UL;
        value ^= unchecked((ulong)(long)x) * 0xBF58476D1CE4E5B9UL;
        value ^= unchecked((ulong)(long)y) * 0x94D049BB133111EBUL;
        value ^= unchecked((ulong)(long)z) * 0xD6E8FEB86659FD93UL;
        value ^= value >> 30; value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27; value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
