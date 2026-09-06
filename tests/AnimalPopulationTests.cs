using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Entities;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class AnimalPopulationTests
{
    private const int Seed = 20260811;

    private static BlockRegistry Registry() => BlockRegistry.LoadFromDirectory(
        Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

    [Theory]
    [InlineData(Biome.Plains, true)]
    [InlineData(Biome.Savanna, true)]
    [InlineData(Biome.Tundra, true)]
    [InlineData(Biome.Highlands, true)]
    [InlineData(Biome.Desert, false)]
    [InlineData(Biome.Badlands, false)]
    [InlineData(Biome.FrozenPeaks, false)]
    public void Zvirata_se_spawnuji_jen_v_obyvatelnych_biomech(Biome biome, bool expected) =>
        Assert.Equal(expected, AnimalPopulation.SupportsAnimals(biome));

    [Fact]
    public void Druh_je_pro_tentyz_biom_a_hash_deterministicky()
    {
        for (uint hash = 0; hash < 100; hash++)
        {
            Assert.Equal(
                AnimalPopulation.KindFor(Biome.Plains, hash),
                AnimalPopulation.KindFor(Biome.Plains, hash));
        }
        Assert.Equal(AnimalKind.Sheep, AnimalPopulation.KindFor(Biome.Tundra, 42));
    }

    [Fact]
    public void Zvire_pred_hracem_utika_a_neprojde_podlahou()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);
        var floor = new Chunk();
        ushort stone = registry.IndexOf("tesseris:stone");
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
            floor.SetBlock(x, 0, z, stone);
        Assert.True(world.TryAddChunk(Vector3i.Zero, floor));

        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed);
        AnimalEntity sheep = population.AddForTest(
            AnimalKind.Sheep, new Vector3(8.5f, 1.001f, 8.5f), randomState: 7);
        float before = sheep.Position.X;

        population.Update(world, new Vector3(10f, 1.001f, 8.5f), 0.2f);

        Assert.Equal(AnimalActivity.Flee, sheep.Activity);
        Assert.True(sheep.Position.X < before, "Ovce neutekla směrem od hráče.");
        Assert.InRange(sheep.Position.Y, 1f, 1.01f);
    }

    [Fact]
    public void V_kreativu_zvire_hrace_ignoruje()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);
        var floor = new Chunk();
        ushort stone = registry.IndexOf("tesseris:stone");
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
            floor.SetBlock(x, 0, z, stone);
        Assert.True(world.TryAddChunk(Vector3i.Zero, floor));

        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed);
        AnimalEntity sheep = population.AddForTest(
            AnimalKind.Sheep, new Vector3(8.5f, 1.001f, 8.5f), randomState: 7);
        sheep.Activity = AnimalActivity.Flee;
        Vector3 before = sheep.Position;

        population.Update(
            world,
            new Vector3(9f, 1.001f, 8.5f),
            0.2f,
            ignorePlayer: true);

        Assert.NotEqual(AnimalActivity.Flee, sheep.Activity);
        Assert.Equal(before, sheep.Position);
    }

    [Fact]
    public void Save_obnovi_druh_pozici_a_stav_bez_ztraty()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TesserisAnimalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            BlockRegistry registry = Registry();
            var terrain = new TerrainGenerator(registry, Seed);
            var original = new AnimalPopulation(registry, terrain, Seed);
            AnimalEntity deer = original.AddForTest(AnimalKind.Deer, new Vector3(12.5f, 306f, -4.5f), 91);
            deer.Activity = AnimalActivity.Graze;
            deer.Yaw = 1.25f;
            string path = Path.Combine(directory, AnimalPopulation.FileName);

            original.Save(path);
            var restored = new AnimalPopulation(registry, terrain, Seed);
            Assert.Equal(1, restored.Load(path));

            AnimalEntity loaded = Assert.Single(restored.Animals);
            Assert.Equal(AnimalKind.Deer, loaded.Kind);
            Assert.Equal(deer.Position, loaded.Position);
            Assert.Equal(AnimalActivity.Graze, loaded.Activity);
            Assert.Equal(deer.Yaw, loaded.Yaw);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Zvire_po_odstraneni_podlahy_spadne_a_nelevituje()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);
        var chunk = new Chunk();
        ushort stone = registry.IndexOf("tesseris:stone");
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
            chunk.SetBlock(x, 0, z, stone);
        Assert.True(world.TryAddChunk(Vector3i.Zero, chunk));

        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed);
        AnimalEntity sheep = population.AddForTest(
            AnimalKind.Sheep, new Vector3(8.5f, 4.001f, 8.5f), randomState: 7);

        population.Update(world, new Vector3(50f, 1f, 50f), 0.3f);

        Assert.True(sheep.Position.Y < 4f, "Ovce zůstala viset ve vzduchu.");
    }

    [Fact]
    public void Nekolizni_kaminek_se_nepovazuje_za_plny_blok_pod_nohama()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);
        var chunk = new Chunk();
        ushort stone = registry.IndexOf("tesseris:stone");
        ushort pebble = registry.IndexOf("tesseris:small_stone");
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
            chunk.SetBlock(x, 0, z, stone);
        chunk.SetBlock(8, 1, 8, pebble);
        Assert.True(world.TryAddChunk(Vector3i.Zero, chunk));

        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed);
        AnimalEntity sheep = population.AddForTest(
            AnimalKind.Sheep, new Vector3(8.5f, 2.001f, 8.5f), randomState: 7);

        for (int i = 0; i < 6; i++)
            population.Update(world, new Vector3(50f, 1f, 50f), 0.11f);

        Assert.InRange(sheep.Position.Y, 1f, 1.01f);
    }

    [Fact]
    public void Renderovana_poloha_dohani_fyziku_plynule()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);
        var floor = new Chunk();
        ushort stone = registry.IndexOf("tesseris:stone");
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
            floor.SetBlock(x, 0, z, stone);
        Assert.True(world.TryAddChunk(Vector3i.Zero, floor));

        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed);
        AnimalEntity sheep = population.AddForTest(
            AnimalKind.Sheep, new Vector3(8.5f, 1.001f, 8.5f), randomState: 7);
        sheep.Activity = AnimalActivity.Flee;
        sheep.Yaw = MathF.PI * 0.5f;
        Vector3 visualBefore = sheep.RenderPosition;

        population.Update(world, new Vector3(0f, 1f, 8.5f), 0.11f);

        Assert.NotEqual(visualBefore, sheep.RenderPosition);
        Assert.NotEqual(sheep.Position, sheep.RenderPosition);
    }

    [Fact]
    public void Kamera_neprosekne_velke_zvire_pri_tesnem_kontaktu()
    {
        MobDefinition deer = MobDefinitions.Deer;
        Vector3 player = new(8.5f, 1.001f, 8.5f);
        Vector3 separated = AnimalPopulation.SeparatedRenderPosition(
            new Vector3(8.55f, 1.001f, 8.5f), 0f, deer, player);

        float horizontal = new Vector2(separated.X - player.X, separated.Z - player.Z).Length;
        float required = (Tesseris.Game.Player.PlayerController.Width * 0.5f)
            + (deer.Width * 0.5f) + 0.16f;
        Assert.True(horizontal >= required - 0.0001f);
        Assert.Equal(1.001f, separated.Y);
    }

    [Fact]
    public void Utikajici_zvire_obejde_prekkazku_misto_otaceni_na_miste()
    {
        BlockRegistry registry = Registry();
        var world = new VoxelWorld(registry);
        var chunk = new Chunk();
        ushort stone = registry.IndexOf("tesseris:stone");
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
            chunk.SetBlock(x, 0, z, stone);
        for (int y = 1; y <= 3; y++)
            chunk.SetBlock(8, y, 7, stone);
        Assert.True(world.TryAddChunk(Vector3i.Zero, chunk));

        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed);
        AnimalEntity sheep = population.AddForTest(
            AnimalKind.Sheep, new Vector3(8.5f, 1.001f, 8.5f), randomState: 7);
        Vector3 start = sheep.Position;

        for (int i = 0; i < 10; i++)
            population.Update(world, new Vector3(8.5f, 1.001f, 10f), 0.1f);

        Assert.True(
            Vector3.DistanceSquared(start, sheep.Position) > 0.35f,
            "Ovce se u překážky pouze otáčela na místě.");
        Assert.True(MathF.Abs(sheep.Position.X - start.X) > 0.1f, "Ovce se nepokusila překážku obejít.");
    }

    [Fact]
    public void Voxelovy_model_ma_telo_hlavu_a_nohy()
    {
        var animal = new AnimalEntity { Kind = AnimalKind.Sheep };
        var mesh = new MeshBuffer();
        ChunkRenderer.AddAnimal(mesh, animal, new AnimalTextureLayers(1, 2));

        Assert.True(mesh.VertexCount >= 240);
        Assert.True(mesh.IndexCount >= 350);

        var uv = new HashSet<(float U, float V)>();
        ReadOnlySpan<float> vertices = mesh.Vertices;
        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            int offset = vertex * MeshBuffer.FloatsPerVertex;
            uv.Add((vertices[offset + 3], vertices[offset + 4]));
        }

        Assert.True(uv.Count >= 20, "Model musi pouzivat oddelene UV oblasti skin atlasu.");
    }

    [Fact]
    public void Vlk_hrace_uvidi_pron_asleduje_a_priblizi_se()
    {
        BlockRegistry registry = Registry();
        VoxelWorld world = FlatWorld(registry);
        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed);
        AnimalEntity wolf = population.AddForTest(
            AnimalKind.Wolf, new Vector3(8.5f, 1.001f, 8.5f), randomState: 17);
        Vector3 player = new(13.5f, 1.001f, 8.5f);
        float before = Vector3.DistanceSquared(wolf.Position, player);

        population.Update(world, player, 0.21f, allowHostileSpawns: false);

        Assert.Equal(AnimalActivity.Hunt, wolf.Activity);
        Assert.True(Vector3.DistanceSquared(wolf.Position, player) < before);
    }

    [Fact]
    public void Vlk_utoci_s_cooldownem_a_ne_kazdy_simulacni_krok()
    {
        BlockRegistry registry = Registry();
        VoxelWorld world = FlatWorld(registry);
        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed);
        AnimalEntity wolf = population.AddForTest(
            AnimalKind.Wolf, new Vector3(8.5f, 1.001f, 8.5f), randomState: 19);
        float damage = 0f;
        Vector3 player = new(9.4f, 1.001f, 8.5f);

        population.Update(
            world, player, 0.11f, allowHostileSpawns: false, damagePlayer: value => damage += value);
        population.Update(
            world, player, 0.11f, allowHostileSpawns: false, damagePlayer: value => damage += value);

        Assert.Equal(AnimalActivity.Attack, wolf.Activity);
        Assert.Equal(MobDefinitions.Wolf.AttackDamage, damage, 3);
    }

    [Fact]
    public void Vlk_nevidi_a_neutoci_pres_plnou_stenu()
    {
        BlockRegistry registry = Registry();
        VoxelWorld world = FlatWorld(registry);
        ushort stone = registry.IndexOf("tesseris:stone");
        world.SetBlock(9, 1, 8, stone);
        world.SetBlock(9, 2, 8, stone);
        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed);
        AnimalEntity wolf = population.AddForTest(
            AnimalKind.Wolf, new Vector3(8.3f, 1.001f, 8.5f), randomState: 23);
        float damage = 0f;

        population.Update(
            world,
            new Vector3(10.5f, 1.001f, 8.5f),
            0.21f,
            allowHostileSpawns: false,
            damagePlayer: value => damage += value);

        Assert.NotEqual(AnimalActivity.Hunt, wolf.Activity);
        Assert.NotEqual(AnimalActivity.Attack, wolf.Activity);
        Assert.Equal(0f, damage);
    }

    [Fact]
    public void Zasah_paprskem_ubere_zivoty_a_smrt_vrati_deterministicky_loot()
    {
        BlockRegistry registry = Registry();
        VoxelWorld world = FlatWorld(registry);
        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed);
        population.AddForTest(AnimalKind.Sheep, new Vector3(8.5f, 1.001f, 8.5f), randomState: 31);

        bool found = population.TryHit(
            world,
            new Vector3(8.5f, 1.7f, 4f),
            Vector3.UnitZ,
            6f,
            damage: 20f,
            out MobHit hit);

        Assert.True(found);
        Assert.True(hit.Killed);
        Assert.Equal("tesseris:raw_mutton", hit.LootItem);
        Assert.InRange(hit.LootCount, 1, 2);
        Assert.Empty(population.Animals);
    }

    [Fact]
    public void Save_obnovi_i_zivoty_a_bojovy_stav_moba()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TesserisMobStateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            BlockRegistry registry = Registry();
            VoxelWorld world = FlatWorld(registry);
            var terrain = new TerrainGenerator(registry, Seed);
            var original = new AnimalPopulation(registry, terrain, Seed);
            original.AddForTest(AnimalKind.Wolf, new Vector3(8.5f, 1.001f, 8.5f), randomState: 37);
            Assert.True(original.TryHit(
                world,
                new Vector3(8.5f, 1.7f, 4f),
                Vector3.UnitZ,
                6f,
                damage: 5f,
                out _));

            string path = Path.Combine(directory, AnimalPopulation.FileName);
            original.Save(path);
            var restored = new AnimalPopulation(registry, terrain, Seed);
            Assert.Equal(1, restored.Load(path));

            AnimalEntity wolf = Assert.Single(restored.Animals);
            Assert.Equal(MobDefinitions.Wolf.MaximumHealth - 5f, wolf.Health, 3);
            Assert.Equal(AnimalActivity.Hunt, wolf.Activity);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Stary_ANM1_save_se_nacte_s_plnymi_zivoty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tesseris-anm1-{Guid.NewGuid():N}.dat");
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x314D4E41u);
                writer.Write(1);
                writer.Write(42L);
                writer.Write((byte)AnimalKind.Sheep);
                writer.Write(8.5f);
                writer.Write(1.001f);
                writer.Write(8.5f);
                writer.Write(0.75f);
                writer.Write((byte)AnimalActivity.Graze);
                writer.Write(1.5f);
                writer.Write(123u);
            }

            BlockRegistry registry = Registry();
            var population = new AnimalPopulation(
                registry,
                new TerrainGenerator(registry, Seed),
                Seed);

            Assert.Equal(1, population.Load(path));
            AnimalEntity sheep = Assert.Single(population.Animals);
            Assert.Equal(MobDefinitions.Sheep.MaximumHealth, sheep.Health, 3);
            Assert.Equal(AnimalActivity.Graze, sheep.Activity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Vlk_ma_vlastni_ctitelnou_siluetu()
    {
        var wolf = new AnimalEntity { Kind = AnimalKind.Wolf };
        var mesh = new MeshBuffer();

        ChunkRenderer.AddAnimal(mesh, wolf, new AnimalTextureLayers(1, 2, 3));

        Assert.True(mesh.VertexCount >= 260);
        Assert.True(mesh.IndexCount >= 390);
    }

    private static VoxelWorld FlatWorld(BlockRegistry registry)
    {
        var world = new VoxelWorld(registry);
        var floor = new Chunk();
        ushort stone = registry.IndexOf("tesseris:stone");
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
            floor.SetBlock(x, 0, z, stone);
        Assert.True(world.TryAddChunk(Vector3i.Zero, floor));
        return world;
    }
}
