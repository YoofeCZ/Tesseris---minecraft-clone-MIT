using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Entities;
using Tesseris.Game.Lua;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

public sealed class LuantiLuaRuntimeTests
{
    private const int Seed = 20260812;

    private const string TestEntity = """
        minetest.register_entity("test:wolf", {
            initial_properties = { hp_max = 20, physical = true },
            on_activate = function(self, staticdata, dtime_s)
                self.age = tonumber(staticdata) or 0
            end,
            on_step = function(self, dtime, moveresult)
                self.age = self.age + dtime
                self.object:set_yaw(1.25)
                self.object:set_velocity({ x = 0, y = 0, z = 2 })
            end,
            on_punch = function(self, puncher, elapsed, toolcaps, direction, damage)
                self.last_damage = damage
            end,
            on_death = function(self, killer)
                self.dead = true
            end,
            get_staticdata = function(self)
                return string.format("%.2f", self.age)
            end,
        })
        """;

    [Fact]
    public void Luanti_registrace_a_ObjectRef_ridi_skutecnou_CSharp_entitu()
    {
        using var lua = new LuantiLuaRuntime();
        lua.ExecuteString(TestEntity);
        var wolf = new AnimalEntity
        {
            Id = 41,
            Kind = AnimalKind.Wolf,
            DefinitionId = "test:wolf",
            Health = 16,
        };

        Assert.True(lua.Attach(wolf, "2.5"));
        lua.Step(wolf, 0.25f);
        lua.Punch(wolf, 3f, Vector3.UnitX);

        Assert.Contains("test:wolf", lua.RegisteredEntityNames);
        Assert.Equal(1.25f, wolf.Yaw, 3);
        Assert.Equal(new Vector3(0, 0, 2), wolf.ScriptVelocity);
        Assert.True(wolf.LuaControlsMovement);
        Assert.Equal(16f, wolf.Health, 3);
        Assert.Equal("2.75", lua.GetStaticData(wolf));
    }

    [Fact]
    public void Lua_staticdata_se_zachova_v_ANM3_save()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tesseris-lua-anm3-{Guid.NewGuid():N}.dat");
        try
        {
            BlockRegistry registry = Registry();
            using var firstLua = new LuantiLuaRuntime();
            firstLua.ExecuteString(TestEntity);
            var original = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed, firstLua);
            AnimalEntity wolf = original.AddForTest(AnimalKind.Wolf, new Vector3(4.5f, 1.001f, 4.5f));
            wolf.DefinitionId = "test:wolf";
            Assert.True(firstLua.Attach(wolf, "4.0"));
            firstLua.Step(wolf, 0.5f);
            original.Save(path);

            using var secondLua = new LuantiLuaRuntime();
            secondLua.ExecuteString(TestEntity);
            var restored = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed, secondLua);

            Assert.Equal(1, restored.Load(path));
            AnimalEntity loaded = Assert.Single(restored.Animals);
            Assert.Equal("test:wolf", loaded.DefinitionId);
            Assert.Equal("4.50", secondLua.GetStaticData(loaded));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Nezaregistrovana_entita_bezi_bez_Lua_callbacku()
    {
        using var lua = new LuantiLuaRuntime();
        var sheep = new AnimalEntity { Id = 1, Kind = AnimalKind.Sheep, Health = 10 };

        Assert.False(lua.Attach(sheep));
        lua.Step(sheep, 1f);

        Assert.Equal(10f, sheep.Health);
    }

    [Fact]
    public void Vestavene_Lua_prototypy_se_nactou_z_distribucnich_assetu()
    {
        using LuantiLuaRuntime lua = LuantiLuaRuntime.Load(
            Path.Combine(AppContext.BaseDirectory, "assets"),
            Path.Combine(Path.GetTempPath(), $"tesseris-no-mods-{Guid.NewGuid():N}"));

        Assert.Contains("tesseris:sheep", lua.RegisteredEntityNames);
        Assert.Contains("tesseris:deer", lua.RegisteredEntityNames);
        Assert.Contains("tesseris:wolf", lua.RegisteredEntityNames);
    }

    [Fact]
    public void Lua_velocity_se_pouzije_pri_fyzickem_tiku_populace()
    {
        using var lua = new LuantiLuaRuntime();
        lua.ExecuteString(TestEntity.Replace("test:wolf", "tesseris:wolf", StringComparison.Ordinal));
        BlockRegistry registry = Registry();
        var population = new AnimalPopulation(registry, new TerrainGenerator(registry, Seed), Seed, lua);
        AnimalEntity wolf = population.AddForTest(
            AnimalKind.Wolf,
            new Vector3(4.5f, 1.001f, 4.5f));
        VoxelWorld world = FlatWorld(registry);

        population.Update(world, new Vector3(50, 1, 50), 0.11f, ignorePlayer: true);

        Assert.True(wolf.Position.Z > 4.65f);
        Assert.True(wolf.Moving);
    }

    private static BlockRegistry Registry() => BlockRegistry.LoadFromDirectory(
        Path.Combine(AppContext.BaseDirectory, "assets", "blocks"));

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
