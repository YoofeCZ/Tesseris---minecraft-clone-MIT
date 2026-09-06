using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Items;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>Dlouhodobý život lesa bez procházení všech bloků světa.</summary>
public sealed class LivingVegetationTests
{
    private const int Seed = 20260727;

    private static BlockRegistry Registry() =>
        BlockRegistry.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    private static VoxelWorld Meadow(BlockRegistry blocks, int radius = 64)
    {
        var world = new VoxelWorld(blocks);
        ushort grass = blocks.IndexOf("tesseris:grass");
        var writes = new List<(Vector3i, ushort)>();

        for (int z = -radius; z <= radius; z++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                writes.Add((new Vector3i(x, 320, z), grass));
            }
        }

        world.SetBlocks(writes);
        return world;
    }

    private static (LivingVegetation Ecology, SaplingGrowth Growth, TreeFelling Felling)
        Systems(BlockRegistry blocks, TerrainGenerator generator)
    {
        generator.EnableTrees(blocks);
        TreePlanter planter = generator.Trees!;
        var items = ItemRegistry.Create(blocks, Path.Combine(AppContext.BaseDirectory, "assets", "items"));
        return (
            new LivingVegetation(blocks, generator, planter, Seed),
            new SaplingGrowth(planter, generator),
            new TreeFelling(blocks, items));
    }

    [Fact]
    public void Ekologie_neceka_na_cely_svisly_sloupec_kdyz_je_povrch_nacteny()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        var world = new VoxelWorld(blocks);
        var column = Vector2i.Zero;

        generator.Generate(new Chunk(), new Vector3i(column.X, 0, column.Y));
        Assert.True(generator.TryGetSurfaceRange(column.X, column.Y, out int lowest, out int highest));

        int bottom = (lowest + 1) >> Chunk.SizeShift;
        int top = (highest + 1) >> Chunk.SizeShift;
        for (int y = bottom; y <= top; y++)
        {
            Assert.True(world.TryAddChunk(new Vector3i(column.X, y, column.Y), new Chunk()));
        }

        Assert.True(ChunkStreamer.IsEcologySurfaceLoaded(world, generator, column));
        Assert.True(
            world.ChunkCount < TerrainGenerator.WorldHeightChunks,
            "Test omylem načetl celou výšku světa a nehlídá původní chybu.");

        Assert.True(world.RemoveChunk(new Vector3i(column.X, bottom, column.Y)));
        Assert.False(ChunkStreamer.IsEcologySurfaceLoaded(world, generator, column));
    }

    [Fact]
    public void Sazenice_na_povrchu_se_najde_i_kdyz_hrac_leti_vysoko_nad_ni()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (_, SaplingGrowth growth, _) = Systems(blocks, generator);
        var world = new VoxelWorld(blocks);

        int surface = generator.SurfaceHeight(0, 0);
        var root = new Vector3i(0, surface + 1, 0);
        world.SetBlock(root.X, root.Y - 1, root.Z, blocks.IndexOf("tesseris:grass"));
        world.SetBlock(root.X, root.Y, root.Z, blocks.IndexOf("tesseris:oak_sapling"));

        var observer = new Vector3(0f, 900f, 0f);
        for (int slice = 0; slice < 8; slice++)
        {
            growth.Update(world, observer, seconds: 1.5f, realSeconds: 0.05f);
        }

        Assert.Equal(1, growth.Growing);
    }

    [Fact]
    public void Louka_se_bez_skenovani_vsech_bloku_znovu_zazeleni()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, SaplingGrowth growth, TreeFelling felling) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);

        for (int i = 0; i < 240; i++)
        {
            ecology.Update(world, new Vector3(0f, 321f, 0f), 1.5f, growth, felling);
        }

        int plants = 0;
        for (int z = -56; z <= 56; z++)
        {
            for (int x = -56; x <= 56; x++)
            {
                if (blocks.IsReplaceableVegetation(world.GetBlock(x, 321, z)))
                {
                    plants++;
                }
            }
        }

        Assert.True(plants > 0, "Ekologické pulzy na prázdnou louku nevrátily jedinou rostlinu.");
        Assert.True(ecology.PlantChanges > 0);
    }

    [Fact]
    public void V_noci_na_prazdne_louce_nevyroste_novy_porost()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, SaplingGrowth growth, TreeFelling felling) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);

        for (int i = 0; i < 240; i++)
        {
            ecology.Update(
                world,
                new Vector3(0f, 321f, 0f),
                1.5f,
                growth,
                felling,
                growthAllowed: false);
        }

        for (int z = -56; z <= 56; z++)
        {
            for (int x = -56; x <= 56; x++)
            {
                Assert.False(blocks.IsReplaceableVegetation(world.GetBlock(x, 321, z)));
            }
        }

        Assert.Equal(0, ecology.PlantChanges);
    }

    [Fact]
    public void Maximalni_zrychleni_nezanecha_backlog_ekologickych_pulzu()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, SaplingGrowth growth, TreeFelling felling) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);

        ecology.Update(
            world,
            Vector3.Zero,
            seconds: 3_000f,
            growth,
            felling,
            growthAllowed: false);

        Assert.Equal(4, ecology.Pulses);

        for (int i = 0; i < 10; i++)
        {
            ecology.Update(world, Vector3.Zero, 0.1f, growth, felling, growthAllowed: false);
        }

        Assert.Equal(4, ecology.Pulses);
    }

    [Fact]
    public void Rozbita_horni_klada_odstrani_strom_z_evidence()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, _, _) = Systems(blocks, generator);
        var root = new Vector3i(4, 321, -7);

        ecology.NoticeTree(root);
        ecology.ForgetTree(root + (Vector3i.UnitY * 6));

        Assert.Equal(0, ecology.KnownTrees);
    }

    [Fact]
    public void Dospely_strom_se_pomalu_rozsiri_jen_jednou_a_bez_umrti()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, SaplingGrowth growth, TreeFelling felling) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);

        var root = new Vector3i(0, 321, 0);
        world.SetBlock(root.X, root.Y, root.Z, blocks.IndexOf("tesseris:oak_sapling"));
        Assert.True(generator.Trees!.Grow(world, root, out _) > 0);
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                """
                {
                  "version": 1,
                  "worldSeconds": 1000,
                  "pulse": 0,
                  "nextSeedAt": 1001,
                  "trees": [
                    { "x": 0, "y": 321, "z": 0, "bornAt": 0, "fallAt": 999999 }
                  ],
                  "growingTrees": []
                }
                """);

            Assert.True(ecology.Load(path, growth));
            ecology.Update(world, Vector3.Zero, 0.5f, growth, felling);
            Assert.Equal(0, ecology.ExpandedTrees);

            ecology.Update(world, Vector3.Zero, 0.6f, growth, felling);
            Assert.Equal(1, ecology.ExpandedTrees);
            Assert.Equal(1, ecology.SeededTrees);
            Assert.Equal(0, ecology.FallenTrees);
            Assert.Equal(1, growth.Growing);

            ecology.Save(path, growth);
            (LivingVegetation restored, SaplingGrowth restoredGrowth, TreeFelling restoredFelling) =
                Systems(blocks, generator);
            Assert.True(restored.Load(path, restoredGrowth));

            restored.Update(
                world,
                Vector3.Zero,
                DayCycle.DayLength * 20f,
                restoredGrowth,
                restoredFelling,
                realSeconds: 20f,
                wallSeconds: 20f);
            Assert.Equal(0, restored.ExpandedTrees);
            Assert.Equal(1, restoredGrowth.Growing);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stary_zmeskany_termin_po_nacteni_nevyvola_okamzite_seti()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, SaplingGrowth growth, TreeFelling felling) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);
        var root = new Vector3i(0, 321, 0);
        world.SetBlock(root.X, root.Y, root.Z, blocks.IndexOf("tesseris:oak_sapling"));
        Assert.True(generator.Trees!.Grow(world, root, out _) > 0);

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                """
                {
                  "version": 1,
                  "worldSeconds": 5000,
                  "pulse": 0,
                  "nextSeedAt": 20,
                  "trees": [
                    { "x": 0, "y": 321, "z": 0, "bornAt": 0, "fallAt": 999999 }
                  ],
                  "growingTrees": []
                }
                """);

            Assert.True(ecology.Load(path, growth));
            ecology.Update(world, Vector3.Zero, 1.5f, growth, felling);

            Assert.Equal(0, ecology.ExpandedTrees);
            Assert.Equal(0, growth.Growing);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Vzdaleny_prirodni_strom_se_zaregistruje_bez_rucniho_notice_a_zemre()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, SaplingGrowth growth, TreeFelling felling) = Systems(blocks, generator);
        TreePlanter planter = generator.Trees!;

        Vector3i root = default;
        ushort log = BlockRegistry.Air;
        bool foundNatural = false;
        for (int z = 256; z < 384 && !foundNatural; z++)
        {
            for (int x = 256; x < 384; x++)
            {
                if (planter.TryNaturalTreeAt(x, z, generator, out root, out log))
                {
                    foundNatural = true;
                    break;
                }
            }
        }

        Assert.True(foundNatural, "V testovacím vzdáleném pásmu nebyl jediný přírodní strom.");

        var world = new VoxelWorld(blocks);
        ushort grass = blocks.IndexOf("tesseris:grass");
        var writes = new List<(Vector3i, ushort)>();
        for (int z = root.Z - 24; z <= root.Z + 24; z++)
        {
            for (int x = root.X - 24; x <= root.X + 24; x++)
            {
                writes.Add((new Vector3i(x, root.Y - 1, z), grass));
            }
        }

        for (int y = 0; y < 9; y++)
        {
            writes.Add((new Vector3i(root.X, root.Y + y, root.Z), log));
        }

        world.SetBlocks(writes);
        ecology.NoticeLoadedColumn(new Vector2i(root.X >> Chunk.SizeShift, root.Z >> Chunk.SizeShift));
        ecology.Update(world, Vector3.Zero, 1.5f, growth, felling);

        Assert.Equal(1, ecology.KnownTrees);

        ecology.Update(world, Vector3.Zero, DayCycle.DayLength * 5f, growth, felling);

        Assert.Equal(1, ecology.FallenTrees);
        Assert.Equal(1, ecology.SeededTrees);
        Assert.Equal(BlockRegistry.Air, world.GetBlock(root.X, root.Y, root.Z));
    }

    [Fact]
    public void Prirozena_smrt_nevytvori_radky_spalku()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (_, _, TreeFelling felling) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);

        var root = new Vector3i(0, 321, 0);
        world.SetBlock(root.X, root.Y, root.Z, blocks.IndexOf("tesseris:oak_sapling"));
        Assert.True(generator.Trees!.Grow(world, root, out _) > 0);

        int felled = felling.FallNaturally(
            world, root, new Vector2i(1, 0), out IReadOnlyList<Vector3i> changed);

        Assert.True(felled >= 9);
        Assert.Equal(BlockRegistry.Air, world.GetBlock(root.X, root.Y, root.Z));

        ushort oak = blocks.IndexOf("tesseris:oak_log");
        ushort oakLeaves = blocks.IndexOf("tesseris:oak_leaves");
        for (int x = root.X + 1; x <= root.X + felled; x++)
        {
            Assert.NotEqual(oak, world.GetBlock(x, root.Y, root.Z));
        }

        Assert.DoesNotContain(changed, block => block.X > root.X + 6);
        Assert.DoesNotContain(changed, block => block.Z > root.Z + 6);

        for (int y = root.Y; y < root.Y + 30; y++)
        {
            for (int z = root.Z - 6; z <= root.Z + 6; z++)
            {
                for (int x = root.X - 6; x <= root.X + 6; x++)
                {
                    Assert.NotEqual(oakLeaves, world.GetBlock(x, y, z));
                }
            }
        }

        Assert.Equal(0, felling.Pending);
    }

    [Fact]
    public void Mlady_strom_ma_mene_kmene_i_listi_a_roste_pres_tri_faze()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (_, SaplingGrowth growth, _) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);

        var root = new Vector3i(0, 321, 0);
        ushort sapling = blocks.IndexOf("tesseris:oak_sapling");
        ushort log = blocks.IndexOf("tesseris:oak_log");
        ushort leaves = blocks.IndexOf("tesseris:oak_leaves");
        world.SetBlock(root.X, root.Y, root.Z, sapling);
        growth.Notice(root);

        (int Logs, int Leaves) Count()
        {
            int logs = 0;
            int foliage = 0;

            for (int y = root.Y; y < root.Y + 30; y++)
            {
                for (int z = root.Z - 8; z <= root.Z + 8; z++)
                {
                    for (int x = root.X - 8; x <= root.X + 8; x++)
                    {
                        ushort block = world.GetBlock(x, y, z);
                        if (block == log) { logs++; }
                        if (block == leaves) { foliage++; }
                    }
                }
            }

            return (logs, foliage);
        }

        growth.Update(world, new Vector3(0f, 321f, 0f), 100f);
        (int youngLogs, int youngLeaves) = Count();
        growth.Update(world, new Vector3(0f, 321f, 0f), 100f);
        (int mediumLogs, int mediumLeaves) = Count();
        growth.Update(world, new Vector3(0f, 321f, 0f), 100f);
        (int adultLogs, int adultLeaves) = Count();

        Assert.InRange(youngLogs, 4, 5);
        Assert.InRange(youngLeaves, 19, 25);
        Assert.True(mediumLogs > youngLogs, $"Mladý {youngLogs}, střední {mediumLogs} klád.");
        Assert.True(adultLogs > mediumLogs, $"Střední {mediumLogs}, dospělý {adultLogs} klád.");
        Assert.True(mediumLeaves > youngLeaves, $"Mladý {youngLeaves}, střední {mediumLeaves} listů.");
        Assert.True(adultLeaves > mediumLeaves, $"Střední {mediumLeaves}, dospělý {adultLeaves} listů.");
        Assert.Equal(1, growth.Grown);
        Assert.Equal(3, growth.StagesAdvanced);
    }

    [Fact]
    public void Osirely_mlady_strom_se_znovu_najde_a_doroste()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);
        var root = new Vector3i(0, 321, 0);
        world.SetBlock(root.X, root.Y, root.Z, blocks.IndexOf("tesseris:birch_sapling"));
        Assert.True(generator.Trees!.GrowStage(world, root, 1, out _) > 0);

        // Napodobí starou první fázi ze save: holý krátký kmen a jen kříž listů s vrškem.
        ushort leaves = blocks.IndexOf("tesseris:birch_leaves");
        int crown = root.Y;
        while (world.GetBlock(root.X, crown + 1, root.Z) == blocks.IndexOf("tesseris:birch_log"))
        {
            crown++;
        }

        for (int y = crown - 1; y <= crown + 2; y++)
        {
            for (int z = root.Z - 1; z <= root.Z + 1; z++)
            {
                for (int x = root.X - 1; x <= root.X + 1; x++)
                {
                    if (world.GetBlock(x, y, z) == leaves)
                    {
                        world.SetBlock(x, y, z, BlockRegistry.Air);
                    }
                }
            }
        }

        world.SetBlock(root.X - 1, crown, root.Z, leaves);
        world.SetBlock(root.X + 1, crown, root.Z, leaves);
        world.SetBlock(root.X, crown, root.Z - 1, leaves);
        world.SetBlock(root.X, crown, root.Z + 1, leaves);
        world.SetBlock(root.X, crown + 1, root.Z, leaves);

        var recovered = new SaplingGrowth(generator.Trees);
        for (int i = 0; i < 8 && recovered.StagesAdvanced == 0; i++)
        {
            recovered.Update(world, new Vector3(0f, 321f, 0f), 100f);
        }

        Assert.True(recovered.StagesAdvanced > 0, "Malý strom bez save záznamu sken znovu nezařadil.");
    }

    [Fact]
    public void Pri_zrychlenem_slunci_zustane_kazda_faze_chvili_viditelna()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (_, SaplingGrowth growth, _) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);

        var root = new Vector3i(0, 321, 0);
        ushort sapling = blocks.IndexOf("tesseris:oak_sapling");
        world.SetBlock(root.X, root.Y, root.Z, sapling);
        growth.Notice(root);

        growth.Update(world, new Vector3(0f, 321f, 0f), seconds: 0f, realSeconds: 10f);
        Assert.Equal(sapling, world.GetBlock(root.X, root.Y, root.Z));
        Assert.Equal(0, growth.StagesAdvanced);

        growth.Update(world, new Vector3(0f, 321f, 0f), seconds: 512f, realSeconds: 0.1f);
        Assert.Equal(blocks.IndexOf("tesseris:oak_log"), world.GetBlock(root.X, root.Y, root.Z));
        Assert.Equal(1, growth.StagesAdvanced);

        growth.Update(world, new Vector3(0f, 321f, 0f), seconds: 512f, realSeconds: 0.1f);
        Assert.Equal(1, growth.StagesAdvanced);

        growth.Update(world, new Vector3(0f, 321f, 0f), seconds: 512f, realSeconds: 2.5f);
        Assert.Equal(2, growth.StagesAdvanced);
    }

    [Fact]
    public void Rozpracovany_mlady_strom_pokracuje_po_nacteni()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, SaplingGrowth growth, _) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);
        var root = new Vector3i(0, 321, 0);
        world.SetBlock(root.X, root.Y, root.Z, blocks.IndexOf("tesseris:oak_sapling"));
        growth.Notice(root);
        growth.Update(world, new Vector3(0f, 321f, 0f), 100f);

        string directory = Path.Combine(
            Path.GetTempPath(), "tesseris-growth-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, LivingVegetation.FileName);

        try
        {
            ecology.Save(path, growth);

            var restoredGenerator = new TerrainGenerator(blocks, Seed);
            (LivingVegetation restored, SaplingGrowth restoredGrowth, _) = Systems(blocks, restoredGenerator);
            Assert.True(restored.Load(path, restoredGrowth));
            Assert.Equal(1, restoredGrowth.Growing);

            restoredGrowth.Update(world, new Vector3(0f, 321f, 0f), 100f);
            Assert.Equal(1, restoredGrowth.StagesAdvanced);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Zemrely_strom_zalozi_nahradni_sazenici_o_kus_dal()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, SaplingGrowth growth, TreeFelling felling) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);

        var root = new Vector3i(0, 321, 0);
        world.SetBlock(root.X, root.Y, root.Z, blocks.IndexOf("tesseris:oak_sapling"));
        Assert.True(generator.Trees!.Grow(world, root, out _) > 0);

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                """
                {
                  "version": 1,
                  "worldSeconds": 5000,
                  "pulse": 0,
                  "nextSeedAt": 999999,
                  "trees": [
                    { "x": 0, "y": 321, "z": 0, "bornAt": 0, "fallAt": 1 }
                  ],
                  "growingTrees": []
                }
                """);

            Assert.True(ecology.Load(path, growth));
            ecology.Update(world, new Vector3(0f, 321f, 0f), 1.5f, growth, felling);

            ushort sapling = blocks.IndexOf("tesseris:oak_sapling");
            var found = new List<Vector3i>();
            for (int z = -18; z <= 18; z++)
            {
                for (int x = -18; x <= 18; x++)
                {
                    for (int y = 313; y <= 325; y++)
                    {
                        if (world.GetBlock(x, y, z) == sapling)
                        {
                            found.Add(new Vector3i(x, y, z));
                        }
                    }
                }
            }

            Assert.Equal(1, ecology.FallenTrees);
            Vector3i replacement = Assert.Single(found);
            int distance = Math.Max(
                Math.Abs(replacement.X - root.X),
                Math.Abs(replacement.Z - root.Z));
            Assert.InRange(distance, 6, 18);
            Assert.True(growth.Growing > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Z_prestarlych_stromu_zemre_nejdriv_ten_u_hrace()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, SaplingGrowth growth, TreeFelling felling) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);
        var far = new Vector3i(0, 321, 0);
        var near = new Vector3i(20, 321, 0);
        ushort sapling = blocks.IndexOf("tesseris:oak_sapling");
        ushort log = blocks.IndexOf("tesseris:oak_log");

        world.SetBlock(far.X, far.Y, far.Z, sapling);
        Assert.True(generator.Trees!.Grow(world, far, out _) > 0);
        world.SetBlock(near.X, near.Y, near.Z, sapling);
        Assert.True(generator.Trees.Grow(world, near, out _) > 0);

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                """
                {
                  "version": 1,
                  "worldSeconds": 5000,
                  "pulse": 0,
                  "nextSeedAt": 999999,
                  "trees": [
                    { "x": 0, "y": 321, "z": 0, "bornAt": 0, "fallAt": 1 },
                    { "x": 20, "y": 321, "z": 0, "bornAt": 0, "fallAt": 1 }
                  ],
                  "growingTrees": []
                }
                """);

            Assert.True(ecology.Load(path, growth));
            ecology.Update(world, new Vector3(20f, 321f, 0f), 1.5f, growth, felling);

            Assert.Equal(log, world.GetBlock(far.X, far.Y, far.Z));
            Assert.NotEqual(log, world.GetBlock(near.X, near.Y, near.Z));
            Assert.Equal(1, ecology.FallenTrees);
            Assert.Equal(1, ecology.SeededTrees);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stary_puvodni_strom_ma_prednost_pred_mladsi_nahradou_ve_stejne_casti_lesa()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, SaplingGrowth growth, TreeFelling felling) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);
        var oldTree = new Vector3i(0, 321, 0);
        var youngReplacement = new Vector3i(20, 321, 0);
        ushort sapling = blocks.IndexOf("tesseris:oak_sapling");
        ushort log = blocks.IndexOf("tesseris:oak_log");

        world.SetBlock(oldTree.X, oldTree.Y, oldTree.Z, sapling);
        Assert.True(generator.Trees!.Grow(world, oldTree, out _) > 0);
        world.SetBlock(youngReplacement.X, youngReplacement.Y, youngReplacement.Z, sapling);
        Assert.True(generator.Trees.Grow(world, youngReplacement, out _) > 0);

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                """
                {
                  "version": 1,
                  "worldSeconds": 5000,
                  "pulse": 0,
                  "nextSeedAt": 999999,
                  "trees": [
                    { "x": 0, "y": 321, "z": 0, "bornAt": 0, "fallAt": 1 },
                    { "x": 20, "y": 321, "z": 0, "bornAt": 3000, "fallAt": 4000 }
                  ],
                  "growingTrees": []
                }
                """);

            Assert.True(ecology.Load(path, growth));
            ecology.Update(world, new Vector3(20f, 321f, 0f), 1.5f, growth, felling);

            Assert.NotEqual(log, world.GetBlock(oldTree.X, oldTree.Y, oldTree.Z));
            Assert.Equal(log, world.GetBlock(youngReplacement.X, youngReplacement.Y, youngReplacement.Z));
            Assert.Equal(1, ecology.FallenTrees);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Vek_stromu_prezije_ulozeni_a_nacteni()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation first, SaplingGrowth growth, TreeFelling felling) = Systems(blocks, generator);
        VoxelWorld world = Meadow(blocks);
        var root = new Vector3i(3, 321, -4);
        first.NoticeTree(root);
        first.Update(world, new Vector3(0f, 321f, 0f), 123f, growth, felling);

        string directory = Path.Combine(
            Path.GetTempPath(), "tesseris-ecology-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, LivingVegetation.FileName);

        try
        {
            first.Save(path, growth);

            var secondGenerator = new TerrainGenerator(blocks, Seed);
            (LivingVegetation second, SaplingGrowth secondGrowth, _) = Systems(blocks, secondGenerator);

            Assert.True(second.Load(path, secondGrowth));
            Assert.Equal(first.KnownTrees, second.KnownTrees);
            Assert.Equal(first.WorldSeconds, second.WorldSeconds, precision: 3);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Poskozeny_ekologicky_soubor_nezablokuje_svet()
    {
        BlockRegistry blocks = Registry();
        var generator = new TerrainGenerator(blocks, Seed);
        (LivingVegetation ecology, _, _) = Systems(blocks, generator);

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ tohle neni json");
            Assert.False(ecology.Load(path));
            Assert.Equal(0, ecology.KnownTrees);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
