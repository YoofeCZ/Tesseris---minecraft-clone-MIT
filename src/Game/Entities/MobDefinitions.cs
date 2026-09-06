using OpenTK.Mathematics;
using Tesseris.Game.World;

namespace Tesseris.Game.Entities;

public enum MobTemperament : byte
{
    Passive,
    Skittish,
    Defensive,
    Predator,
    Hostile,
}

public enum MobMovement : byte
{
    Ground,
    Flying,
    Swimming,
    Lava,
    Climbing,
}

public enum MobAttackStyle : byte
{
    None,
    Melee,
    Ranged,
}

[Flags]
public enum MobHabitat : ushort
{
    None = 0,
    Plains = 1 << 0,
    Savanna = 1 << 1,
    Desert = 1 << 2,
    Badlands = 1 << 3,
    Tundra = 1 << 4,
    Highlands = 1 << 5,
    Peaks = 1 << 6,
    AllLand = Plains | Savanna | Desert | Badlands | Tundra | Highlands | Peaks,
}

/// <summary>Immutable gameplay and source linkage for one built-in creature archetype.</summary>
public sealed record MobDefinition(
    AnimalKind Kind,
    string Id,
    MobTemperament Temperament,
    float Width,
    float Height,
    float MaximumHealth,
    float WanderSpeed,
    float AlertSpeed,
    float NoticeRadius,
    float AttackRange = 0f,
    float AttackDamage = 0f,
    float AttackCooldown = 0f,
    string LootItem = "",
    int MinimumLoot = 0,
    int MaximumLoot = 0,
    MobMovement Movement = MobMovement.Ground,
    MobAttackStyle AttackStyle = MobAttackStyle.None,
    MobHabitat Habitat = MobHabitat.AllLand,
    bool HostileSpawn = false,
    string SourceId = "",
    string VisualId = "")
{
    public bool AttacksPlayerOnSight => Temperament == MobTemperament.Hostile;
    public bool Retaliates => Temperament is MobTemperament.Defensive or MobTemperament.Hostile;
    public bool FleesPlayer => Temperament is MobTemperament.Passive or MobTemperament.Skittish or MobTemperament.Predator;
}

/// <summary>
/// ID-keyed built-in catalog. Byte kinds are stable persistence/renderer keys; namespaced IDs are
/// the canonical extensibility boundary. The upstream creeper is intentionally absent.
/// </summary>
public static class MobDefinitions
{
    private const MobHabitat Temperate = MobHabitat.Plains | MobHabitat.Savanna | MobHabitat.Highlands;
    private const MobHabitat Cold = MobHabitat.Tundra | MobHabitat.Highlands | MobHabitat.Peaks;
    private const MobHabitat Dry = MobHabitat.Savanna | MobHabitat.Desert | MobHabitat.Badlands;

    public static MobDefinition Sheep { get; } = M(AnimalKind.Sheep, "tesseris:sheep", MobTemperament.Skittish,
        .78f, 1.35f, 15, 1, 2, 8, loot: "tesseris:raw_mutton", min: 1, max: 2,
        habitat: Temperate | MobHabitat.Tundra, source: "mobs_animal:sheep_white", visual: "mobs_sheep");
    public static MobDefinition Deer { get; } = M(AnimalKind.Deer, "tesseris:deer", MobTemperament.Skittish,
        .72f, 1.75f, 15, 1.4f, 3, 12, loot: "tesseris:raw_venison", min: 1, max: 3,
        habitat: Temperate, source: "animalia:reindeer", visual: "animalia_reindeer");
    public static MobDefinition Wolf { get; } = M(AnimalKind.Wolf, "tesseris:wolf", MobTemperament.Hostile,
        .62f, 1.1f, 20, 1.5f, 4, 24, 1.45f, 4, 1.1f, "tesseris:wolf_pelt", 0, 1,
        attack: MobAttackStyle.Melee, habitat: Cold | MobHabitat.Plains, hostile: true,
        source: "animalia:wolf", visual: "animalia_wolf");

    public static IReadOnlyList<MobDefinition> All { get; } = Build();

    private static readonly IReadOnlyDictionary<AnimalKind, MobDefinition> ByKind =
        All.ToDictionary(value => value.Kind);
    private static readonly IReadOnlyDictionary<string, MobDefinition> ById = BuildIdIndex(All);

    /// <summary>Existuje pro tenhle druh ještě definice? Odstraněné druhy vrací false.</summary>
    public static bool TryFor(AnimalKind kind, out MobDefinition? definition) =>
        ByKind.TryGetValue(kind, out definition);

    public static MobDefinition For(AnimalKind kind) => ByKind.TryGetValue(kind, out MobDefinition? value)
        ? value
        : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown built-in mob kind.");

    public static MobDefinition For(AnimalEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return !string.IsNullOrWhiteSpace(entity.DefinitionId) && TryFor(entity.DefinitionId, out MobDefinition? value)
            ? value
            : For(entity.Kind);
    }

    public static bool TryFor(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MobDefinition? definition) =>
        ById.TryGetValue(id, out definition);

    public static bool Supports(MobDefinition definition, Biome biome) =>
        (definition.Habitat & HabitatFor(biome)) != 0;

    public static IReadOnlyList<MobDefinition> SpawnCandidates(Biome biome, bool allowHostiles) => All
        .Where(value => (allowHostiles || !value.HostileSpawn) && Supports(value, biome))
        .ToArray();

    private static IReadOnlyList<MobDefinition> Build()
    {
        var values = new List<MobDefinition> { Sheep, Deer, Wolf,
            M(AnimalKind.Bee, "mobs_animal:bee", MobTemperament.Passive, .4f, .5f, 2, 1, 1.5f, 8,
                loot: "mobs:honey", min: 0, max: 1, movement: MobMovement.Flying, habitat: Temperate, visual: "mobs_bee"),
            M(AnimalKind.Bunny, "mobs_animal:bunny", MobTemperament.Skittish, .54f, .67f, 4, 1, 2, 8,
                loot: "mobs:rabbit_raw", min: 0, max: 1, habitat: Temperate | Cold, visual: "mobs_bunny"),
            M(AnimalKind.Chicken, "mobs_animal:chicken", MobTemperament.Skittish, .6f, .85f, 10, 1, 3, 5,
                loot: "mobs:chicken_raw", min: 1, max: 1, habitat: Temperate | MobHabitat.Savanna, visual: "mobs_chicken"),
            M(AnimalKind.Cow, "mobs_animal:cow", MobTemperament.Defensive, .8f, 1.21f, 20, 1, 2, 8,
                2, 4, 1.2f, "mobs:meat_raw", 1, 3, attack: MobAttackStyle.Melee, habitat: Temperate, visual: "mobs_cow"),
            M(AnimalKind.Kitten, "mobs_animal:kitten", MobTemperament.Predator, .6f, .4f, 10, .6f, 2, 8,
                1, 1, 1, attack: MobAttackStyle.Melee, habitat: Temperate, visual: "mobs_kitten"),
            M(AnimalKind.Panda, "mobs_animal:panda", MobTemperament.Defensive, .8f, .9f, 24, .5f, 1.5f, 8,
                2, 3, 1.4f, attack: MobAttackStyle.Melee, habitat: Temperate | MobHabitat.Highlands, visual: "mobs_panda"),
            M(AnimalKind.Penguin, "mobs_animal:penguin", MobTemperament.Passive, .4f, .5f, 10, 1, 2, 5,
                loot: "mobs:chicken_raw", min: 0, max: 1, movement: MobMovement.Swimming, habitat: Cold, visual: "mobs_penguin"),
            M(AnimalKind.Rat, "mobs_animal:rat", MobTemperament.Skittish, .4f, .2f, 4, 1, 2, 6,
                loot: "mobs:rat_cooked", min: 0, max: 1, habitat: MobHabitat.AllLand, visual: "mobs_rat"),
            M(AnimalKind.Warthog, "mobs_animal:pumba", MobTemperament.Defensive, .8f, .96f, 15, 2, 3, 10,
                2, 2, 1.1f, "mobs:pork_raw", 1, 2, attack: MobAttackStyle.Melee, habitat: Dry | MobHabitat.Plains, visual: "mobs_pumba")
        };

        AddColouredSheep(values);
        values.AddRange([
            Monster(AnimalKind.DirtMonster, "dirt_monster", .6f, 1.7f, 27, 1, 3, 15, 2, 2, "default:dirt", Dry | Temperate, "mobs_stone_monster"),
            Monster(AnimalKind.FireSpirit, "fire_spirit", .4f, .4f, 45, 2, 3, 14, 2, 4, "fire:flint_and_steel", Dry, "mobs_fire_spirit", movement: MobMovement.Flying),
            Monster(AnimalKind.LavaFlan, "lava_flan", 1, 1.7f, 35, .5f, 2, 10, 2.5f, 3, "default:lava_source", Dry, "zmobs_lava_flan", movement: MobMovement.Lava),
            Monster(AnimalKind.SandMonster, "sand_monster", .6f, 1.8f, 20, 1.5f, 4, 8, 2, 1, "default:sand", Dry, "mobs_sand_monster"),
            Monster(AnimalKind.Spider, "spider", 1.4f, .5f, 30, 1, 3, 15, 2, 3, "farming:string", MobHabitat.AllLand, "mobs_spider", movement: MobMovement.Climbing),
            Monster(AnimalKind.StoneMonster, "stone_monster", .6f, 1.7f, 35, 1, 2, 10, 2, 3, "default:cobble", MobHabitat.AllLand, "mobs_stone_monster")
        ]);
        AddAnimalia(values);
        return values.AsReadOnly();
    }

    /// <summary>
    /// Druhy z modu Animalia (MIT, ElCeejo), které v mobs_animal ani mobs_monster nejsou.
    /// Rozměry jsou z jejich <c>hitbox</c> (šířka je poloměr, proto dvojnásobek), životy,
    /// rychlost a dohled přímo z definic v <c>references/animalia/mobs</c>.
    /// Kuře, kráva, ovce a krysa se nepřidávají — tentýž druh už máme z mobs_animal.
    /// </summary>
    private static void AddAnimalia(List<MobDefinition> values) => values.AddRange([
        M(AnimalKind.Bat, "animalia:bat", MobTemperament.Skittish, .3f, .3f, 2, 4, 6.4f, 12,
            habitat: MobHabitat.AllLand, movement: MobMovement.Flying, source: "animalia:bat"),
        M(AnimalKind.GrizzlyBear, "animalia:grizzly_bear", MobTemperament.Defensive, 1f, 1f, 20, 4, 6.4f, 10,
            1.6f, 6, 1.2f, attack: MobAttackStyle.Melee, habitat: Temperate | Cold, source: "animalia:grizzly_bear"),
        M(AnimalKind.Cat, "animalia:cat", MobTemperament.Predator, .4f, .4f, 10, 3, 4.8f, 16,
            1f, 1, 1.2f, attack: MobAttackStyle.Melee, habitat: Temperate, source: "animalia:cat"),
        M(AnimalKind.Fox, "animalia:fox", MobTemperament.Predator, .7f, .5f, 10, 4, 6.4f, 16,
            1.3f, 2, 1.2f, attack: MobAttackStyle.Melee, habitat: Temperate | Cold, source: "animalia:fox"),
        M(AnimalKind.Horse, "animalia:horse", MobTemperament.Skittish, 1.3f, 1.95f, 40, 4, 8, 16,
            habitat: Temperate | MobHabitat.Savanna, source: "animalia:horse"),
        M(AnimalKind.Opossum, "animalia:opossum", MobTemperament.Skittish, .5f, .4f, 5, 4, 6.4f, 16,
            habitat: Temperate, source: "animalia:opossum"),
        M(AnimalKind.Owl, "animalia:owl", MobTemperament.Skittish, .3f, .3f, 10, 4, 6.4f, 16,
            habitat: Temperate | Cold, movement: MobMovement.Flying, source: "animalia:owl"),
        M(AnimalKind.Pig, "animalia:pig", MobTemperament.Skittish, .7f, .7f, 20, 3, 4.8f, 12,
            loot: "mobs:pork_raw", min: 1, max: 2, habitat: Temperate, source: "animalia:pig"),
        M(AnimalKind.SongBird, "animalia:song_bird", MobTemperament.Skittish, .4f, .4f, 2, 4, 6.4f, 8,
            habitat: Temperate, movement: MobMovement.Flying, source: "animalia:song_bird"),
        M(AnimalKind.Turkey, "animalia:turkey", MobTemperament.Skittish, .6f, .6f, 8, 2, 3.2f, 8,
            loot: "mobs:chicken_raw", min: 1, max: 1, habitat: Temperate | MobHabitat.Savanna,
            source: "animalia:turkey")]);

    private static void AddColouredSheep(List<MobDefinition> values)
    {
        (AnimalKind Kind, string Colour)[] colours = [
            (AnimalKind.SheepBlack, "black"), (AnimalKind.SheepBlue, "blue"),
            (AnimalKind.SheepBrown, "brown"), (AnimalKind.SheepCyan, "cyan"),
            (AnimalKind.SheepDarkGreen, "dark_green"), (AnimalKind.SheepDarkGrey, "dark_grey"),
            (AnimalKind.SheepGreen, "green"), (AnimalKind.SheepGrey, "grey"),
            (AnimalKind.SheepMagenta, "magenta"), (AnimalKind.SheepOrange, "orange"),
            (AnimalKind.SheepPink, "pink"), (AnimalKind.SheepRed, "red"),
            (AnimalKind.SheepViolet, "violet"), (AnimalKind.SheepYellow, "yellow")];
        foreach ((AnimalKind kind, string colour) in colours)
            values.Add(M(kind, $"mobs_animal:sheep_{colour}", MobTemperament.Skittish, .78f, 1.35f,
                12, 1, 2, 8, loot: "mobs:mutton_raw", min: 1, max: 2,
                habitat: Temperate | MobHabitat.Tundra, visual: $"mobs_sheep:{colour}"));
    }

    private static MobDefinition Monster(AnimalKind kind, string name, float width, float height, float hp,
        float walk, float run, float notice, float reach, float damage, string loot, MobHabitat habitat,
        string visual, MobAttackStyle attack = MobAttackStyle.Melee, MobMovement movement = MobMovement.Ground) =>
        M(kind, $"mobs_monster:{name}", MobTemperament.Hostile, width, height, hp, walk, run, notice,
            reach, damage, attack == MobAttackStyle.Ranged ? 1.8f : 1.1f, loot, 0, 1,
            movement, attack, habitat, true, $"mobs_monster:{name}", visual);

    private static MobDefinition M(AnimalKind kind, string id, MobTemperament temperament,
        float width, float height, float hp, float walk, float run, float notice,
        float reach = 0, float damage = 0, float cooldown = 0, string loot = "", int min = 0, int max = 0,
        MobMovement movement = MobMovement.Ground, MobAttackStyle attack = MobAttackStyle.None,
        MobHabitat habitat = MobHabitat.AllLand, bool hostile = false, string source = "", string visual = "") =>
        new(kind, id, temperament, width, height, hp, walk, run, notice, reach, damage, cooldown,
            loot, min, max, movement, attack, habitat, hostile, source, visual);

    private static IReadOnlyDictionary<string, MobDefinition> BuildIdIndex(IEnumerable<MobDefinition> definitions)
    {
        var result = new Dictionary<string, MobDefinition>(StringComparer.Ordinal);
        foreach (MobDefinition definition in definitions)
        {
            result.Add(definition.Id, definition);
            if (!string.IsNullOrWhiteSpace(definition.SourceId))
                result.TryAdd(definition.SourceId, definition);
        }
        return result;
    }

    private static MobHabitat HabitatFor(Biome biome) => biome switch
    {
        Biome.Plains => MobHabitat.Plains,
        Biome.Savanna => MobHabitat.Savanna,
        Biome.Desert => MobHabitat.Desert,
        Biome.Badlands => MobHabitat.Badlands,
        Biome.Tundra => MobHabitat.Tundra,
        Biome.Highlands => MobHabitat.Highlands,
        _ => MobHabitat.Peaks,
    };
}

public readonly record struct MobHit(
    long EntityId,
    AnimalKind Kind,
    Vector3 Position,
    float Distance,
    float Damage,
    bool Killed,
    string LootItem,
    int LootCount);
