using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.Lua;
using Tesseris.Game.Player;
using Tesseris.Game.World;

namespace Tesseris.Game.Entities;

public enum AnimalKind : byte
{
    Sheep = 1,
    Deer = 2,
    Wolf = 3,
    Bee = 4,
    Bunny = 5,
    Chicken = 6,
    Cow = 7,
    Kitten = 8,
    Panda = 9,
    Penguin = 10,
    Rat = 11,
    Warthog = 12,
    SheepBlack = 13,
    SheepBlue = 14,
    SheepBrown = 15,
    SheepCyan = 16,
    SheepDarkGreen = 17,
    SheepDarkGrey = 18,
    SheepGreen = 19,
    SheepGrey = 20,
    SheepMagenta = 21,
    SheepOrange = 22,
    SheepPink = 23,
    SheepRed = 24,
    SheepViolet = 25,
    SheepYellow = 26,
    DirtMonster = 32,
    FireSpirit = 34,
    LavaFlan = 36,
    SandMonster = 40,
    Spider = 41,
    StoneMonster = 42,

    // ODSTRANĚNÉ DRUHY. Čísla zůstávají zabraná schválně: ve starých světech jsou uložená
    // jako bajt, a kdyby je dostal jiný druh, změnila by se zvířata v uložené hře v jiná.
    // Načítání takové zvíře přeskočí — viz Load().
    DungeonMaster = 33,
    LandGuard = 35,
    ObsidianFlan = 37,
    MeseMonster = 38,
    Oerkki = 39,
    TreeMonster = 43,

    // Animalia (MIT, ElCeejo). Druhy, které v mobs_animal ani mobs_monster nejsou.
    Bat = 44,
    GrizzlyBear = 45,
    Cat = 46,
    Fox = 47,
    Horse = 48,
    Opossum = 49,
    Owl = 50,
    Pig = 51,
    SongBird = 52,
    Turkey = 53,
}

public enum AnimalActivity : byte
{
    Idle,
    Graze,
    Wander,
    Flee,
    Hunt,
    Attack,
}

public readonly record struct AnimalTextureLayers(
    float SheepSkin,
    float DeerSkin,
    float WolfSkin = 0f,
    float WolfSkin2 = 0f,
    float WolfSkin3 = 0f,
    float WolfSkin4 = 0f)
{
    public float WolfFor(long entityId) => (entityId & 3L) switch
    {
        1 => WolfSkin2,
        2 => WolfSkin3,
        3 => WolfSkin4,
        _ => WolfSkin,
    };
}

/// <summary>A lightweight vanilla animal. Position is at the centre of its feet.</summary>
public sealed class AnimalEntity
{
    public long Id { get; internal set; }
    public AnimalKind Kind { get; internal set; }
    public Vector3 Position { get; internal set; }
    public float Yaw { get; internal set; }
    public AnimalActivity Activity { get; internal set; }
    public float WalkPhase { get; internal set; }
    public bool Moving { get; internal set; }

    /// <summary>Zvíře z přehlídky <c>/mobs</c> — stojí na místě, aby šlo prohlédnout.</summary>
    public bool Frozen { get; internal set; }
    public Vector3 RenderPosition { get; internal set; }
    public float RenderYaw { get; internal set; }
    public float HeadLowering { get; internal set; }
    public float AttackPose { get; internal set; }
    public float VisualTime { get; internal set; }
    public float Health { get; internal set; }
    public float MaximumHealth => MobDefinitions.For(this).MaximumHealth;
    public bool IsHostile => MobDefinitions.For(this).AttacksPlayerOnSight;
    public string DefinitionId { get; internal set; } = string.Empty;
    public string LuaStaticData { get; internal set; } = string.Empty;
    internal float DecisionSeconds { get; set; }
    internal uint RandomState { get; set; }
    internal float VerticalVelocity { get; set; }
    internal float AvoidanceSeconds { get; set; }
    internal float AlertSeconds { get; set; }
    internal float AttackCooldownSeconds { get; set; }
    internal Vector3 LastKnownPlayerPosition { get; set; }
    internal bool VisualInitialized { get; set; }
    internal Vector3 ScriptVelocity { get; set; }
    internal Vector3 ScriptAcceleration { get; set; }
    internal bool LuaControlsMovement { get; set; }
    internal bool RemovalRequested { get; set; }
}

/// <summary>
/// Vanilla passive fauna. Decisions run at a fixed rate, while rendering interpolates the
/// resulting position every frame. Animals never read unloaded terrain and are capped globally.
/// </summary>
public sealed class AnimalPopulation
{
    public const string FileName = "animals.dat";
    public const int MaximumAnimals = 48;

    private const float FixedStep = 0.1f;
    private const float DespawnRadius = 128f;
    private const float SpawnMinimumRadius = 24f;
    private const float SpawnMaximumRadius = 72f;
    private const uint LegacyMagic = 0x314D4E41; // ANM1
    private const uint CombatMagic = 0x324D4E41; // ANM2
    private const uint LuaMagic = 0x334D4E41; // ANM3: Lua definition and static data
    private const uint Magic = 0x344D4E41; // ANM4: definition ID is canonical

    private readonly List<AnimalEntity> animals = [];
    private readonly BlockRegistry blocks;
    private readonly TerrainGenerator terrain;
    private readonly int worldSeed;
    private readonly ushort water;
    private readonly LuantiLuaRuntime? lua;
    private float stepAccumulator;
    private float spawnAccumulator;
    private uint spawnSequence;

    public AnimalPopulation(
        BlockRegistry blocks,
        TerrainGenerator terrain,
        int worldSeed,
        LuantiLuaRuntime? lua = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        this.blocks = blocks;
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.worldSeed = worldSeed;
        this.lua = lua;
        water = blocks.TryIndexOf("tesseris:water", out ushort value) ? value : BlockRegistry.Air;
    }

    public IReadOnlyList<AnimalEntity> Animals => animals;
    public int Count => animals.Count;

    public void Update(
        VoxelWorld world,
        Vector3 playerPosition,
        float deltaSeconds,
        bool ignorePlayer = false,
        bool allowHostileSpawns = true,
        Action<float>? damagePlayer = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f)
            return;

        float elapsed = Math.Min(deltaSeconds, 0.25f);
        spawnAccumulator += elapsed;
        if (spawnAccumulator >= 1f)
        {
            spawnAccumulator -= 1f;
            TrySpawn(world, playerPosition, allowHostileSpawns);
        }

        stepAccumulator += elapsed;
        int steps = 0;
        while (stepAccumulator >= FixedStep && steps++ < 3)
        {
            stepAccumulator -= FixedStep;
            Step(world, playerPosition, FixedStep, ignorePlayer, damagePlayer);
        }

        UpdateVisuals(elapsed);
        SeparateRenderedMobsFromPlayer(playerPosition);
    }

    private void Step(
        VoxelWorld world,
        Vector3 playerPosition,
        float deltaSeconds,
        bool ignorePlayer,
        Action<float>? damagePlayer)
    {
        float despawnSquared = DespawnRadius * DespawnRadius;
        for (int i = animals.Count - 1; i >= 0; i--)
        {
            AnimalEntity animal = animals[i];
            MobDefinition definition = MobDefinitions.For(animal);
            animal.AvoidanceSeconds = Math.Max(0f, animal.AvoidanceSeconds - deltaSeconds);
            animal.AlertSeconds = Math.Max(0f, animal.AlertSeconds - deltaSeconds);
            animal.AttackCooldownSeconds = Math.Max(0f, animal.AttackCooldownSeconds - deltaSeconds);
            Vector3 toPlayer = playerPosition - animal.Position;
            float horizontalDistanceSquared = (toPlayer.X * toPlayer.X) + (toPlayer.Z * toPlayer.Z);
            if (horizontalDistanceSquared > despawnSquared)
            {
                lua?.Detach(animal, removal: false);
                animals.RemoveAt(i);
                continue;
            }

            lua?.Step(animal, deltaSeconds);
            if (animal.RemovalRequested || animal.Health <= 0f)
            {
                if (animal.Health <= 0f)
                    lua?.Death(animal);
                else
                    lua?.Detach(animal, removal: true);
                animals.RemoveAt(i);
                continue;
            }

            // ZMRAZENÁ ZVÍŘATA STOJÍ. Přehlídka z `/mobs` má sloužit k prohlédnutí modelů,
            // takže se nesmí rozejít po krajině dřív, než k ní hráč dojde. Vizuální čas jim
            // běží dál, aby se hýbala klidová animace.
            if (animal.Frozen)
            {
                animal.Moving = false;
                animal.Activity = AnimalActivity.Idle;
                animal.DecisionSeconds = 3600f;
                continue;
            }

            float noticeSquared = definition.NoticeRadius * definition.NoticeRadius;
            bool canSeePlayer = !ignorePlayer
                && horizontalDistanceSquared <= noticeSquared
                && MathF.Abs(toPlayer.Y) <= 4f
                && HasLineOfSight(world, animal, playerPosition);
            AnimalEntity? prey = definition.Temperament == MobTemperament.Predator
                ? FindPrey(world, animal, definition.NoticeRadius)
                : null;

            bool retaliating = definition.Retaliates && animal.AlertSeconds > 0f;
            if (definition.AttacksPlayerOnSight || retaliating)
            {
                UpdateHostileDecision(
                    animal,
                    definition,
                    playerPosition,
                    horizontalDistanceSquared,
                    canSeePlayer,
                    ignorePlayer,
                    damagePlayer);
            }
            else if (definition.FleesPlayer && (canSeePlayer || animal.AlertSeconds > 0f))
            {
                if (canSeePlayer)
                {
                    animal.LastKnownPlayerPosition = playerPosition;
                    animal.AlertSeconds = 2.5f;
                }

                animal.Activity = AnimalActivity.Flee;
                animal.DecisionSeconds = 0.8f;
                Vector3 threat = animal.LastKnownPlayerPosition - animal.Position;
                var away = new Vector2(-threat.X, -threat.Z);
                if (away.LengthSquared > 0.001f && animal.AvoidanceSeconds <= 0f)
                {
                    away.Normalize();
                    float desiredYaw = MathF.Atan2(away.X, away.Y);
                    animal.Yaw = TurnTowards(animal.Yaw, desiredYaw, 0.42f);
                }
            }
            else if (prey is not null)
            {
                UpdatePredatorDecision(animal, definition, prey);
            }
            else
            {
                if (ignorePlayer && animal.Activity is AnimalActivity.Flee or AnimalActivity.Hunt or AnimalActivity.Attack)
                {
                    animal.Activity = AnimalActivity.Idle;
                    animal.DecisionSeconds = 0.35f;
                    animal.AvoidanceSeconds = 0f;
                }
                animal.DecisionSeconds -= deltaSeconds;
                if (animal.DecisionSeconds <= 0f)
                    ChooseActivity(animal);
            }

            float speed = animal.Activity switch
            {
                AnimalActivity.Wander => definition.WanderSpeed,
                AnimalActivity.Flee or AnimalActivity.Hunt => definition.AlertSpeed,
                _ => 0f,
            };

            if (animal.LuaControlsMovement)
            {
                animal.ScriptVelocity += animal.ScriptAcceleration * deltaSeconds;
                var horizontal = new Vector2(animal.ScriptVelocity.X, animal.ScriptVelocity.Z);
                speed = horizontal.Length;
                if (speed > 0.001f)
                    animal.Yaw = MathF.Atan2(horizontal.X, horizontal.Y);
            }

            MaintainGrounding(world, animal, deltaSeconds);
            animal.Moving = speed > 0f
                && MathF.Abs(animal.VerticalVelocity) < 0.001f
                && TryMove(world, animal, speed * deltaSeconds);
            if (animal.Moving)
                animal.WalkPhase += deltaSeconds * speed * 7f;
        }
    }

    private static void UpdateHostileDecision(
        AnimalEntity animal,
        MobDefinition definition,
        Vector3 playerPosition,
        float horizontalDistanceSquared,
        bool canSeePlayer,
        bool ignorePlayer,
        Action<float>? damagePlayer)
    {
        if (ignorePlayer)
        {
            animal.AlertSeconds = 0f;
            if (animal.Activity is AnimalActivity.Hunt or AnimalActivity.Attack)
            {
                animal.Activity = AnimalActivity.Idle;
                animal.DecisionSeconds = 0.4f;
            }
            return;
        }

        if (canSeePlayer)
        {
            animal.LastKnownPlayerPosition = playerPosition;
            animal.AlertSeconds = 5f;
        }

        if (animal.AlertSeconds <= 0f)
        {
            animal.DecisionSeconds -= FixedStep;
            if (animal.DecisionSeconds <= 0f)
                ChooseActivity(animal);
            return;
        }

        Vector3 target = canSeePlayer ? playerPosition : animal.LastKnownPlayerPosition;
        Vector3 direction = target - animal.Position;
        var horizontal = new Vector2(direction.X, direction.Z);
        if (horizontal.LengthSquared > 0.001f && animal.AvoidanceSeconds <= 0f)
        {
            horizontal.Normalize();
            float desiredYaw = MathF.Atan2(horizontal.X, horizontal.Y);
            animal.Yaw = TurnTowards(animal.Yaw, desiredYaw, 0.32f);
        }

        float attackRangeSquared = definition.AttackRange * definition.AttackRange;
        if (canSeePlayer
            && horizontalDistanceSquared <= attackRangeSquared
            && MathF.Abs(direction.Y) <= 1.35f)
        {
            animal.Activity = AnimalActivity.Attack;
            if (animal.AttackCooldownSeconds <= 0f)
            {
                damagePlayer?.Invoke(definition.AttackDamage);
                animal.AttackCooldownSeconds = definition.AttackCooldown;
            }
            return;
        }

        animal.Activity = AnimalActivity.Hunt;
        animal.DecisionSeconds = 0.4f;
    }

    private AnimalEntity? FindPrey(VoxelWorld world, AnimalEntity predator, float radius)
    {
        AnimalEntity? nearest = null;
        float best = radius * radius;
        foreach (AnimalEntity candidate in animals)
        {
            if (ReferenceEquals(candidate, predator) || !IsNaturalPrey(predator.Kind, candidate.Kind))
                continue;
            float distance = Vector3.DistanceSquared(predator.Position, candidate.Position);
            if (distance >= best || !HasLineOfSight(world, predator, candidate.Position))
                continue;
            nearest = candidate;
            best = distance;
        }
        return nearest;
    }

    private static bool IsNaturalPrey(AnimalKind predator, AnimalKind candidate) => predator switch
    {
        AnimalKind.Kitten => candidate is AnimalKind.Rat or AnimalKind.Chicken,
        _ => false,
    };

    private static void UpdatePredatorDecision(AnimalEntity predator, MobDefinition definition, AnimalEntity prey)
    {
        Vector3 direction = prey.Position - predator.Position;
        var horizontal = new Vector2(direction.X, direction.Z);
        if (horizontal.LengthSquared > 0.001f && predator.AvoidanceSeconds <= 0f)
        {
            horizontal.Normalize();
            predator.Yaw = TurnTowards(predator.Yaw, MathF.Atan2(horizontal.X, horizontal.Y), 0.36f);
        }

        float range = Math.Max(.75f, definition.AttackRange);
        if ((direction.X * direction.X) + (direction.Z * direction.Z) <= range * range
            && MathF.Abs(direction.Y) <= 1f)
        {
            predator.Activity = AnimalActivity.Attack;
            if (predator.AttackCooldownSeconds <= 0f)
            {
                prey.Health -= Math.Max(1f, definition.AttackDamage);
                predator.AttackCooldownSeconds = Math.Max(.5f, definition.AttackCooldown);
            }
        }
        else
        {
            predator.Activity = AnimalActivity.Hunt;
            predator.DecisionSeconds = .4f;
        }
    }

    private void UpdateVisuals(float deltaSeconds)
    {
        float positionBlend = 1f - MathF.Exp(-14f * deltaSeconds);
        float rotationBlend = 1f - MathF.Exp(-11f * deltaSeconds);
        float poseBlend = 1f - MathF.Exp(-8f * deltaSeconds);

        foreach (AnimalEntity animal in animals)
        {
            animal.VisualTime += deltaSeconds;
            if (!animal.VisualInitialized)
            {
                animal.RenderPosition = animal.Position;
                animal.RenderYaw = animal.Yaw;
                animal.HeadLowering = animal.Activity == AnimalActivity.Graze ? 1f : 0f;
                animal.AttackPose = animal.Activity == AnimalActivity.Attack ? 1f : 0f;
                animal.VisualInitialized = true;
                continue;
            }

            animal.RenderPosition = Vector3.Lerp(animal.RenderPosition, animal.Position, positionBlend);
            float yawDelta = MathF.Atan2(
                MathF.Sin(animal.Yaw - animal.RenderYaw),
                MathF.Cos(animal.Yaw - animal.RenderYaw));
            animal.RenderYaw += yawDelta * rotationBlend;
            float targetHead = animal.Activity == AnimalActivity.Graze ? 1f : 0f;
            animal.HeadLowering += (targetHead - animal.HeadLowering) * poseBlend;
            float targetAttack = animal.Activity == AnimalActivity.Attack ? 1f : 0f;
            animal.AttackPose += (targetAttack - animal.AttackPose) * poseBlend;
        }
    }

    /// <summary>
    /// Keeps the first-person camera outside mob meshes. Gameplay positions remain governed by AI
    /// and terrain collision; only the interpolated render pose receives the small contact correction.
    /// This prevents the camera near plane from cutting a rectangular hole through large animals.
    /// </summary>
    private void SeparateRenderedMobsFromPlayer(Vector3 playerPosition)
    {
        foreach (AnimalEntity animal in animals)
        {
            MobDefinition definition = MobDefinitions.For(animal);
            animal.RenderPosition = SeparatedRenderPosition(
                animal.RenderPosition, animal.RenderYaw, definition, playerPosition);
        }
    }

    internal static Vector3 SeparatedRenderPosition(
        Vector3 renderPosition,
        float renderYaw,
        MobDefinition definition,
        Vector3 playerPosition)
    {
        const float playerRadius = PlayerController.Width * 0.5f;
        const float cameraClearance = 0.16f;
        if (playerPosition.Y + PlayerController.Height <= renderPosition.Y
            || playerPosition.Y >= renderPosition.Y + definition.Height)
            return renderPosition;

        var fromPlayer = new Vector2(
            renderPosition.X - playerPosition.X,
            renderPosition.Z - playerPosition.Z);
        float required = playerRadius + (definition.Width * 0.5f) + cameraClearance;
        float distanceSquared = fromPlayer.LengthSquared;
        if (distanceSquared >= required * required)
            return renderPosition;

        Vector2 direction;
        if (distanceSquared > 1e-8f)
        {
            direction = fromPlayer / MathF.Sqrt(distanceSquared);
        }
        else
        {
            direction = new Vector2(MathF.Sin(renderYaw), MathF.Cos(renderYaw));
            if (direction.LengthSquared <= 1e-8f)
                direction = Vector2.UnitY;
        }

        return new Vector3(
            playerPosition.X + (direction.X * required),
            renderPosition.Y,
            playerPosition.Z + (direction.Y * required));
    }

    private static bool HasLineOfSight(VoxelWorld world, AnimalEntity animal, Vector3 playerPosition)
    {
        MobDefinition definition = MobDefinitions.For(animal);
        Vector3 start = animal.Position + new Vector3(0f, definition.Height * 0.72f, 0f);
        Vector3 end = playerPosition + new Vector3(0f, 1.45f, 0f);
        Vector3 delta = end - start;
        float distance = delta.Length;
        if (distance <= 0.001f)
            return true;

        Vector3 step = delta / distance * 0.25f;
        Vector3 sample = start + step;
        int samples = Math.Max(0, (int)(distance / 0.25f) - 2);
        for (int i = 0; i < samples; i++, sample += step)
        {
            int x = (int)MathF.Floor(sample.X);
            int y = (int)MathF.Floor(sample.Y);
            int z = (int)MathF.Floor(sample.Z);
            if (world.IsSolid(x, y, z))
                return false;
        }

        return true;
    }

    private void MaintainGrounding(VoxelWorld world, AnimalEntity animal, float deltaSeconds)
    {
        if (TryFindGround(world, animal, animal.Position, out Vector3 ground)
            && MathF.Abs(ground.Y - animal.Position.Y) <= 0.08f)
        {
            animal.Position = new Vector3(animal.Position.X, ground.Y, animal.Position.Z);
            animal.VerticalVelocity = 0f;
            return;
        }

        float previousY = animal.Position.Y;
        animal.VerticalVelocity = Math.Max(animal.VerticalVelocity - (20f * deltaSeconds), -18f);
        float proposedY = previousY + (animal.VerticalVelocity * deltaSeconds);

        if (TryFindLanding(world, animal, proposedY, out float landingY))
        {
            animal.Position = new Vector3(animal.Position.X, landingY, animal.Position.Z);
            animal.VerticalVelocity = 0f;
            return;
        }

        Vector3 proposed = new(animal.Position.X, proposedY, animal.Position.Z);
        float halfWidth = WidthOf(animal) * 0.5f;
        var bounds = new Aabb(
            proposed + new Vector3(-halfWidth, 0f, -halfWidth),
            proposed + new Vector3(halfWidth, HeightOf(animal), halfWidth));
        if (PlayerController.IsFree(world, bounds))
            animal.Position = proposed;
        else
            animal.VerticalVelocity = 0f;
    }

    private bool TryFindLanding(VoxelWorld world, AnimalEntity animal, float proposedY, out float landingY)
    {
        int x = (int)MathF.Floor(animal.Position.X);
        int z = (int)MathF.Floor(animal.Position.Z);
        int highest = (int)MathF.Floor(animal.Position.Y - 0.001f);
        int lowest = (int)MathF.Floor(proposedY - 0.001f);
        float halfWidth = WidthOf(animal) * 0.5f;

        for (int blockY = highest; blockY >= lowest; blockY--)
        {
            if (blockY < 0 || blockY >= TerrainGenerator.WorldHeight - 2)
                continue;
            if (!world.HasChunk(VoxelWorld.ToChunkPosition(x, blockY, z)))
                continue;

            if (!TrySupportSurface(world, x, blockY, z, animal.Position.X, animal.Position.Z, out float feetY))
                continue;
            if (feetY > animal.Position.Y + 0.01f || feetY < proposedY - 0.01f)
                continue;

            Vector3 feet = new(animal.Position.X, feetY, animal.Position.Z);
            var bounds = new Aabb(
                feet + new Vector3(-halfWidth, 0f, -halfWidth),
                feet + new Vector3(halfWidth, HeightOf(animal), halfWidth));
            if (PlayerController.IsFree(world, bounds))
            {
                landingY = feetY;
                return true;
            }
        }

        landingY = proposedY;
        return false;
    }

    private static void ChooseActivity(AnimalEntity animal)
    {
        uint roll = Next(animal);
        int choice = (int)(roll % 10u);
        if (animal.IsHostile)
        {
            animal.Activity = choice < 6 ? AnimalActivity.Wander : AnimalActivity.Idle;
            animal.DecisionSeconds = animal.Activity == AnimalActivity.Wander
                ? 1.4f + ((roll >> 8) & 255u) / 90f
                : 0.8f + ((roll >> 8) & 255u) / 160f;
            if (animal.Activity == AnimalActivity.Wander)
                animal.Yaw += ((((roll >> 16) & 1023u) / 1023f) - 0.5f) * MathF.PI * 1.4f;
        }
        else if (choice < 4)
        {
            animal.Activity = AnimalActivity.Graze;
            animal.DecisionSeconds = 2.5f + ((roll >> 8) & 255u) / 80f;
        }
        else if (choice < 7)
        {
            animal.Activity = AnimalActivity.Idle;
            animal.DecisionSeconds = 1.2f + ((roll >> 8) & 255u) / 150f;
        }
        else
        {
            animal.Activity = AnimalActivity.Wander;
            animal.DecisionSeconds = 2f + ((roll >> 8) & 255u) / 70f;
            animal.Yaw += ((((roll >> 16) & 1023u) / 1023f) - 0.5f) * MathF.PI * 1.6f;
        }
    }

    private bool TryMove(VoxelWorld world, AnimalEntity animal, float distance)
    {
        if (TryMoveAtYaw(world, animal, animal.Yaw, distance, out Vector3 target))
        {
            animal.Position = target;
            return true;
        }

        float side = (Next(animal) & 1u) == 0u ? 1f : -1f;
        ReadOnlySpan<float> offsets = stackalloc float[] { 0.45f, -0.45f, 0.85f, -0.85f, 1.25f, -1.25f, 1.65f };
        foreach (float rawOffset in offsets)
        {
            float candidateYaw = animal.Yaw + (rawOffset * side);
            if (!TryMoveAtYaw(world, animal, candidateYaw, distance, out target))
                continue;

            animal.Yaw = candidateYaw;
            animal.AvoidanceSeconds = 0.65f;
            animal.Position = target;
            return true;
        }

        animal.Yaw += side * (0.55f + ((Next(animal) & 255u) / 255f * 0.35f));
        animal.AvoidanceSeconds = 0.8f;
        animal.DecisionSeconds = Math.Min(animal.DecisionSeconds, 0.5f);
        return false;
    }

    private bool TryMoveAtYaw(
        VoxelWorld world, AnimalEntity animal, float yaw, float distance, out Vector3 target)
    {
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        return TryFindGround(world, animal, animal.Position + (forward * distance), out target);
    }

    // POCHŮZNOST SE PŘESTĚHOVALA DO Walkability. Byla to jediná kopie v projektu a potřebuje
    // ji i pathfinding kolonistů (T2) — držet ji zamčenou uvnitř zvířat by znamenalo ji
    // pro kolonisty napsat podruhé, s vlastní sadou chyb.
    private bool TryFindGround(VoxelWorld world, AnimalEntity animal, Vector3 horizontal, out Vector3 target) =>
        Walkability.TryFindGround(
            world,
            blocks,
            water,
            horizontal,
            animal.Position.Y,
            HeightOf(animal),
            WidthOf(animal),
            out target);

    private bool TrySupportSurface(
        VoxelWorld world, int blockX, int blockY, int blockZ,
        float worldX, float worldZ, out float feetY)
    {
        return Walkability.TrySupportSurface(
            world, blocks, water, blockX, blockY, blockZ, worldX, worldZ, out feetY);
    }

    private void TrySpawn(VoxelWorld world, Vector3 playerPosition, bool allowHostileSpawns)
    {
        if (animals.Count >= MaximumAnimals)
            return;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint sequence = ++spawnSequence;
            uint hash = Hash(worldSeed, sequence, attempt);
            float angle = (hash & 0xFFFFu) / 65535f * MathF.Tau;
            float radius = SpawnMinimumRadius
                + (((hash >> 16) & 0xFFFFu) / 65535f * (SpawnMaximumRadius - SpawnMinimumRadius));
            int x = (int)MathF.Floor(playerPosition.X + MathF.Cos(angle) * radius);
            int z = (int)MathF.Floor(playerPosition.Z + MathF.Sin(angle) * radius);
            Column column = terrain.ColumnAt(x, z);
            if (!SupportsAnimals(column.Biome) || column.Surface <= TerrainGenerator.SeaLevel - 2)
                continue;

            Vector3i chunk = VoxelWorld.ToChunkPosition(x, column.Surface, z);
            if (!world.HasChunk(chunk))
                continue;

            IReadOnlyList<MobDefinition> candidates = MobDefinitions.SpawnCandidates(column.Biome, allowHostileSpawns);
            if (candidates.Count == 0)
                continue;
            MobDefinition definition = candidates[(int)((hash >> 5) % (uint)candidates.Count)];
            AnimalKind kind = definition.Kind;
            var candidate = new AnimalEntity
            {
                Id = ((long)(uint)worldSeed << 32) ^ sequence,
                Kind = kind,
                Position = new Vector3(x + 0.5f, column.Surface + 1.001f, z + 0.5f),
                Yaw = ((hash >> 8) & 0xFFFFu) / 65535f * MathF.Tau,
                Activity = AnimalActivity.Idle,
                DecisionSeconds = 0.5f,
                RandomState = hash == 0 ? 0xA341316Cu : hash,
                Health = definition.MaximumHealth,
                DefinitionId = definition.Id,
            };

            if (animals.Any(existing => Vector3.DistanceSquared(existing.Position, candidate.Position) < 64f)
                || !TryFindGround(world, candidate, candidate.Position, out Vector3 grounded))
                continue;

            candidate.Position = grounded;
            InitializeVisual(candidate);
            animals.Add(candidate);
            lua?.Attach(candidate);
            return;
        }
    }

    internal static bool SupportsAnimals(Biome biome) => biome is
        Biome.Plains or Biome.Savanna or Biome.Tundra or Biome.Highlands;

    internal static AnimalKind KindFor(Biome biome, uint hash) => biome switch
    {
        Biome.Tundra => AnimalKind.Sheep,
        Biome.Highlands => (hash & 3u) == 0u ? AnimalKind.Sheep : AnimalKind.Deer,
        Biome.Savanna => (hash & 1u) == 0u ? AnimalKind.Deer : AnimalKind.Sheep,
        _ => (hash & 3u) == 0u ? AnimalKind.Deer : AnimalKind.Sheep,
    };

    internal static AnimalKind KindForSpawn(Biome biome, uint hash, bool allowHostileSpawns)
    {
        // Hostiles are deliberately uncommon: a night should be dangerous without replacing all wildlife.
        if (allowHostileSpawns && (hash % 5u) == 0u && MobDefinitions.Supports(MobDefinitions.Wolf, biome))
            return AnimalKind.Wolf;

        return KindFor(biome, hash);
    }

    internal AnimalEntity AddForTest(AnimalKind kind, Vector3 position, uint randomState = 1)
    {
        MobDefinition definition = MobDefinitions.For(kind);
        var animal = new AnimalEntity
        {
            Id = animals.Count + 1,
            Kind = kind,
            Position = position,
            RandomState = randomState == 0 ? 1u : randomState,
            DecisionSeconds = 1f,
            Health = definition.MaximumHealth,
            DefinitionId = definition.Id,
        };
        InitializeVisual(animal);
        animals.Add(animal);
        lua?.Attach(animal);
        return animal;
    }

    /// <summary>
    /// Odstraní všechna živá zvířata. Slouží ladicímu příkazu <c>/mobs</c>, aby šlo přehlídku
    /// vytvořit znovu a nezůstalo po ní stádo.
    /// </summary>
    internal int RemoveAll()
    {
        int removed = animals.Count;
        for (int i = animals.Count - 1; i >= 0; i--)
            lua?.Detach(animals[i], removal: false);
        animals.Clear();
        return removed;
    }

    internal AnimalEntity AddForTest(string definitionId, Vector3 position, uint randomState = 1, bool frozen = false)
    {
        if (!MobDefinitions.TryFor(definitionId, out MobDefinition? definition) || definition is null)
            throw new ArgumentException($"Unknown mob definition '{definitionId}'.", nameof(definitionId));
        AnimalEntity animal = AddForTest(definition.Kind, position, randomState);
        animal.DefinitionId = definition.Id;
        animal.Frozen = frozen;
        return animal;
    }

    /// <summary>
    /// Hits the closest visible mob along a ray. Block occlusion is supplied by the caller through
    /// <paramref name="maxDistance"/>, so combat and block selection agree about what is in front.
    /// </summary>
    public bool HasTarget(Vector3 origin, Vector3 direction, float maxDistance)
    {
        if (!float.IsFinite(maxDistance) || maxDistance <= 0f || direction.LengthSquared < 1e-10f)
            return false;

        Vector3 ray = Vector3.Normalize(direction);
        foreach (AnimalEntity animal in animals)
        {
            if (RayIntersects(BoundsAt(animal), origin, ray, maxDistance, out _))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Nejbližší zvíře na paprsku, bez zásahu. Slouží popisku „na co se dívám“, takže na rozdíl
    /// od <see cref="TryHit"/> nic nepoškozuje a nezajímá ho dosah ruky, ale dohled hráče.
    /// </summary>
    public bool TryLookAt(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out AnimalEntity? animal,
        out float distance)
    {
        animal = null;
        distance = 0f;
        if (!float.IsFinite(maxDistance) || maxDistance <= 0f || direction.LengthSquared < 1e-10f)
            return false;

        Vector3 ray = Vector3.Normalize(direction);
        float best = maxDistance;
        foreach (AnimalEntity candidate in animals)
        {
            if (candidate.Health <= 0f) continue;
            if (!RayIntersects(BoundsAt(candidate), origin, ray, best, out float hit)) continue;
            best = hit;
            animal = candidate;
            distance = hit;
        }

        return animal is not null;
    }

    public bool TryHit(
        VoxelWorld world,
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        float damage,
        out MobHit hit)
    {
        ArgumentNullException.ThrowIfNull(world);
        hit = default;
        if (!float.IsFinite(maxDistance) || maxDistance <= 0f
            || !float.IsFinite(damage) || damage <= 0f
            || direction.LengthSquared < 1e-10f)
        {
            return false;
        }

        Vector3 ray = Vector3.Normalize(direction);
        int bestIndex = -1;
        float bestDistance = maxDistance;
        for (int i = 0; i < animals.Count; i++)
        {
            AnimalEntity candidate = animals[i];
            if (RayIntersects(BoundsAt(candidate), origin, ray, bestDistance, out float distance))
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
            return false;

        AnimalEntity animal = animals[bestIndex];
        MobDefinition definition = MobDefinitions.For(animal);
        if (animal.Health <= 0f || !float.IsFinite(animal.Health))
            animal.Health = definition.MaximumHealth;

        float applied = Math.Min(animal.Health, damage);
        animal.Health -= applied;
        // Luanti invokes on_punch after engine damage. Lua may inspect or override the resulting HP.
        lua?.Punch(animal, applied, ray);
        bool killed = animal.Health <= 0f;
        int lootCount = 0;
        if (killed && definition.MaximumLoot > 0)
        {
            uint range = (uint)(definition.MaximumLoot - definition.MinimumLoot + 1);
            lootCount = definition.MinimumLoot + (int)(Next(animal) % range);
            lua?.Death(animal);
            animals.RemoveAt(bestIndex);
        }
        else if (killed)
        {
            lua?.Death(animal);
            animals.RemoveAt(bestIndex);
        }
        else
        {
            animal.LastKnownPlayerPosition = origin;
            animal.AlertSeconds = definition.Retaliates ? 8f : 3.5f;
            animal.Activity = definition.Retaliates
                ? AnimalActivity.Hunt
                : AnimalActivity.Flee;
            animal.DecisionSeconds = 0.6f;

            Vector3 away = animal.Position - origin;
            away.Y = 0f;
            if (away.LengthSquared > 0.001f)
            {
                away.Normalize();
                if (TryFindGround(world, animal, animal.Position + (away * 0.32f), out Vector3 knocked))
                    animal.Position = knocked;
            }
        }

        hit = new MobHit(
            animal.Id,
            animal.Kind,
            animal.Position + new Vector3(0f, definition.Height * 0.5f, 0f),
            bestDistance,
            applied,
            killed,
            definition.LootItem,
            lootCount);
        return true;
    }

    private static Aabb BoundsAt(AnimalEntity animal)
    {
        MobDefinition definition = MobDefinitions.For(animal);
        float halfWidth = definition.Width * 0.5f;
        return new Aabb(
            animal.Position + new Vector3(-halfWidth, 0f, -halfWidth),
            animal.Position + new Vector3(halfWidth, definition.Height, halfWidth));
    }

    private static bool RayIntersects(
        Aabb bounds,
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out float distance)
    {
        float near = 0f;
        float far = maxDistance;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float min = axis == 0 ? bounds.Min.X : axis == 1 ? bounds.Min.Y : bounds.Min.Z;
            float max = axis == 0 ? bounds.Max.X : axis == 1 ? bounds.Max.Y : bounds.Max.Z;
            if (MathF.Abs(d) < 1e-7f)
            {
                if (o < min || o > max)
                {
                    distance = 0f;
                    return false;
                }
                continue;
            }

            float first = (min - o) / d;
            float second = (max - o) / d;
            if (first > second)
                (first, second) = (second, first);
            near = Math.Max(near, first);
            far = Math.Min(far, second);
            if (near > far)
            {
                distance = 0f;
                return false;
            }
        }

        distance = near;
        return near <= maxDistance && far >= 0f;
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(animals.Count);
            foreach (AnimalEntity animal in animals.OrderBy(value => value.Id))
            {
                writer.Write(animal.Id);
                writer.Write((byte)animal.Kind);
                writer.Write(animal.Position.X);
                writer.Write(animal.Position.Y);
                writer.Write(animal.Position.Z);
                writer.Write(animal.Yaw);
                writer.Write((byte)animal.Activity);
                writer.Write(animal.DecisionSeconds);
                writer.Write(animal.RandomState);
                writer.Write(animal.Health);
                writer.Write(animal.AlertSeconds);
                writer.Write(animal.AttackCooldownSeconds);
                writer.Write(animal.LastKnownPlayerPosition.X);
                writer.Write(animal.LastKnownPlayerPosition.Y);
                writer.Write(animal.LastKnownPlayerPosition.Z);
                writer.Write(string.IsNullOrWhiteSpace(animal.DefinitionId)
                    ? MobDefinitions.For(animal).Id
                    : animal.DefinitionId);
                writer.Write(lua?.GetStaticData(animal) ?? animal.LuaStaticData ?? string.Empty);
            }
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, fullPath, overwrite: true);
    }

    public int Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        uint magic = reader.ReadUInt32();
        if (magic is not (Magic or LuaMagic or CombatMagic or LegacyMagic))
            throw new InvalidDataException("Animal save has an unsupported format.");
        bool legacy = magic == LegacyMagic;
        bool hasLuaState = magic is Magic or LuaMagic;

        int count = reader.ReadInt32();
        if (count is < 0 or > MaximumAnimals)
            throw new InvalidDataException($"Animal save contains invalid count {count}.");

        var restored = new List<AnimalEntity>(count);
        int skipped = 0;
        for (int i = 0; i < count; i++)
        {
            long id = reader.ReadInt64();
            AnimalKind kind = (AnimalKind)reader.ReadByte();
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            float yaw = reader.ReadSingle();
            AnimalActivity activity = (AnimalActivity)reader.ReadByte();
            float decision = reader.ReadSingle();
            uint random = reader.ReadUInt32();
            MobDefinition definition;
            if (!Enum.IsDefined(kind) || !Enum.IsDefined(activity)
                || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new InvalidDataException($"Animal save entry {i} is invalid.");

            // ODSTRANĚNÝ DRUH SE PŘESKOČÍ, ne aby kvůli němu spadl celý svět. Zbytek záznamu
            // se ale musí dočíst, jinak by se rozjel proud a rozsypaly se všechny další.
            bool known = MobDefinitions.TryFor(kind, out MobDefinition? found);
            definition = found ?? MobDefinitions.Sheep;
            float health = legacy ? definition.MaximumHealth : reader.ReadSingle();
            float alert = legacy ? 0f : reader.ReadSingle();
            float attackCooldown = legacy ? 0f : reader.ReadSingle();
            Vector3 lastKnown = legacy
                ? position
                : new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            string definitionId = hasLuaState ? reader.ReadString() : definition.Id;
            string luaStaticData = hasLuaState ? reader.ReadString() : string.Empty;
            if (magic == Magic && string.IsNullOrWhiteSpace(definitionId))
                throw new InvalidDataException($"Animal save entry {i} has an empty definition ID.");
            if (!known)
            {
                skipped++;
                continue;
            }

            if (!float.IsFinite(health) || health <= 0f || health > definition.MaximumHealth
                || !float.IsFinite(alert) || alert < 0f
                || !float.IsFinite(attackCooldown) || attackCooldown < 0f
                || !float.IsFinite(lastKnown.X) || !float.IsFinite(lastKnown.Y) || !float.IsFinite(lastKnown.Z))
            {
                throw new InvalidDataException($"Animal save entry {i} has invalid combat state.");
            }

            var animal = new AnimalEntity
            {
                Id = id,
                Kind = kind,
                Position = position,
                Yaw = float.IsFinite(yaw) ? yaw : 0f,
                Activity = activity,
                DecisionSeconds = float.IsFinite(decision) ? Math.Max(0f, decision) : 0f,
                RandomState = random == 0 ? 1u : random,
                Health = health,
                AlertSeconds = alert,
                AttackCooldownSeconds = attackCooldown,
                LastKnownPlayerPosition = lastKnown,
                DefinitionId = string.IsNullOrWhiteSpace(definitionId) ? definition.Id : definitionId,
                LuaStaticData = luaStaticData,
            };
            InitializeVisual(animal);
            restored.Add(animal);
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("Animal save contains trailing data.");

        foreach (AnimalEntity existing in animals)
            lua?.Detach(existing, removal: false);
        animals.Clear();
        animals.AddRange(restored);
        foreach (AnimalEntity animal in restored)
            lua?.Attach(animal, animal.LuaStaticData);
        return animals.Count;
    }

    private static float WidthOf(AnimalEntity animal) => MobDefinitions.For(animal).Width;
    private static float HeightOf(AnimalEntity animal) => MobDefinitions.For(animal).Height;

    private static void InitializeVisual(AnimalEntity animal)
    {
        animal.RenderPosition = animal.Position;
        animal.RenderYaw = animal.Yaw;
        animal.HeadLowering = animal.Activity == AnimalActivity.Graze ? 1f : 0f;
        animal.AttackPose = animal.Activity == AnimalActivity.Attack ? 1f : 0f;
        if (!float.IsFinite(animal.Health) || animal.Health <= 0f)
            animal.Health = MobDefinitions.For(animal).MaximumHealth;
        animal.VisualInitialized = true;
    }

    private static float TurnTowards(float current, float target, float maximumStep)
    {
        float difference = MathF.Atan2(MathF.Sin(target - current), MathF.Cos(target - current));
        return current + Math.Clamp(difference, -maximumStep, maximumStep);
    }

    private static uint Next(AnimalEntity animal)
    {
        uint value = animal.RandomState;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        animal.RandomState = value == 0 ? 0x9E3779B9u : value;
        return animal.RandomState;
    }

    private static uint Hash(int seed, uint sequence, int attempt)
    {
        uint value = unchecked((uint)seed) ^ (sequence * 0x9E3779B9u) ^ ((uint)attempt * 0x85EBCA6Bu);
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        return value ^ (value >> 16);
    }
}
