using System.Text.Json;
using OpenTK.Mathematics;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.World;

/// <summary>Řídká dlouhodobá simulace lesa a nízké vegetace.</summary>
/// <remarks>
/// Netiká každý blok. Jednou za 1,5 vteřiny ochutná několik deterministicky vybraných
/// sloupců kolem hráče. Cena je pevná bez ohledu na dohled a velikost uloženého světa.
/// Ekologické hodiny a kořeny známých stromů se ukládají zvlášť od regionů.
/// </remarks>
public sealed class LivingVegetation
{
    public const string FileName = "vegetace.json";
    private const int StateVersion = 1;
    private const float PulseSeconds = 1.5f;
    private const int CandidateAttempts = 12;
    private const int NearPlantRadius = 56;
    private const int FarPlantRadius = 224;
    private const int DiscoveryColumnsPerUpdate = 2;
    private const float NearTreeRadius = 96f;
    private const double MinTreeLife = DayCycle.DayLength * 2.0;
    private const double MaxTreeLife = DayCycle.DayLength * 4.0;
    private const double ReproductiveAge = DayCycle.DayLength * 0.5;
    private const double MinSeedInterval = DayCycle.DayLength * 0.5;
    private const double MaxSeedInterval = DayCycle.DayLength * 0.9;
    private const int ParentAttempts = 24;
    private const int SeedAttempts = 48;
    private const int MinSeedDistance = 14;
    private const int MaxSeedDistance = 32;
    private const int MinTreeSpacing = 7;
    private const int LocalForestRadius = 32;
    private const int LocalForestCapacity = 48;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly BlockRegistry _blocks;
    private readonly TerrainGenerator _generator;
    private readonly TreePlanter _planter;
    private readonly int _seed;
    private readonly HashSet<ushort> _logs = [];
    private readonly HashSet<ushort> _saplingBlocks = [];
    private readonly Dictionary<Vector3i, TreeLife> _trees = [];
    private readonly Queue<Vector2i> _columnsToDiscover = [];
    private readonly HashSet<Vector2i> _queuedColumns = [];

    private readonly ushort _shortGrass;
    private readonly ushort _tallGrass;
    private readonly ushort _dryGrass;
    private readonly ushort _fern;
    private readonly ushort _deadBush;
    private readonly ushort[] _flowers;
    private readonly ushort _grass;
    private readonly ushort _dryGround;
    private readonly ushort _sand;
    private readonly ushort _snow;

    private float _pulseClock;
    private long _pulse;
    private double _worldSeconds;
    private double _nextSeedAt = MinSeedInterval;
    private float _naturalDeathCooldown;
    private float _seedWallCooldown;

    public LivingVegetation(BlockRegistry blocks, TerrainGenerator generator, TreePlanter planter, int seed)
    {
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _planter = planter ?? throw new ArgumentNullException(nameof(planter));
        _seed = seed;

        _shortGrass = blocks.IndexOf("tesseris:short_grass");
        _tallGrass = blocks.IndexOf("tesseris:tall_grass");
        _dryGrass = blocks.IndexOf("tesseris:dry_grass_tuft");
        _fern = blocks.IndexOf("tesseris:fern");
        _deadBush = blocks.IndexOf("tesseris:dead_bush");
        _flowers =
        [
            blocks.IndexOf("tesseris:flower_red"),
            blocks.IndexOf("tesseris:flower_yellow"),
            blocks.IndexOf("tesseris:flower_white"),
        ];
        _grass = blocks.IndexOf("tesseris:grass");
        _dryGround = blocks.IndexOf("tesseris:dry_grass");
        _sand = blocks.IndexOf("tesseris:sand");
        _snow = blocks.IndexOf("tesseris:snow");

        for (ushort block = 1; block < blocks.Count; block++)
        {
            string id = blocks.Definition(block).Id;
            if (id.EndsWith("_log", StringComparison.Ordinal))
            {
                _logs.Add(block);
            }
            else if (id.EndsWith("_sapling", StringComparison.Ordinal))
            {
                _saplingBlocks.Add(block);
            }
        }
    }

    public double WorldSeconds => _worldSeconds;
    public long Pulses => _pulse;
    public int KnownTrees => _trees.Count;
    public int SeededTrees { get; private set; }
    public int ExpandedTrees { get; private set; }
    public int FallenTrees { get; private set; }
    public int PlantChanges { get; private set; }

    public void NoticeTree(Vector3i root)
    {
        if (!_trees.ContainsKey(root))
        {
            _trees[root] = new TreeLife(
                root, _worldSeconds, _worldSeconds + Lifetime(root), HasSpread: false);
        }
    }

    public void ForgetTree(Vector3i block)
    {
        // Zásah nemusí mířit do paty. Uložený je kořen, takže po stejné ose hledáme dolů
        // přes bezpečně větší rozsah, než má nejvyšší strom. Jinak po creative rozbití
        // horní klády zůstával v ekologii neexistující „duch“ stromu.
        int bottom = Math.Max(0, block.Y - 32);
        for (int y = block.Y; y >= bottom; y--)
        {
            if (_trees.Remove(new Vector3i(block.X, y, block.Z)))
            {
                return;
            }
        }
    }

    /// <summary>Zařadí právě načtený chunkový sloupec k úplné registraci přírodních stromů.</summary>
    public void NoticeLoadedColumn(Vector2i column)
    {
        if (_queuedColumns.Add(column))
        {
            _columnsToDiscover.Enqueue(column);
        }
    }

    public void Update(
        VoxelWorld world,
        Vector3 player,
        float seconds,
        SaplingGrowth saplings,
        TreeFelling felling,
        Action<Vector3i>? invalidate = null,
        float realSeconds = float.NaN,
        bool growthAllowed = true,
        float wallSeconds = float.NaN)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(saplings);
        ArgumentNullException.ThrowIfNull(felling);

        DiscoverQueuedColumns(world);

        if (!float.IsFinite(seconds) || seconds <= 0f)
        {
            return;
        }

        _worldSeconds += seconds;
        _pulseClock += seconds;

        float realDelta = float.IsFinite(realSeconds) && realSeconds >= 0f
            ? realSeconds
            : seconds;
        float wallDelta = float.IsFinite(wallSeconds) && wallSeconds >= 0f
            ? wallSeconds
            : realDelta;
        _naturalDeathCooldown = Math.Max(0f, _naturalDeathCooldown - realDelta);
        _seedWallCooldown = Math.Max(0f, _seedWallCooldown - wallDelta);

        // Biologický věk sleduje zrychlené slunce, ale velké změny světa mají vlastní
        // rozpočet ve SKUTEČNÉM čase. Při 512× tak stromy dospějí rychle, neodumře jich
        // však dvě stě za sekundu a nevznikne synchronizovaná kalamita.
        if (_naturalDeathCooldown <= 0f
            && TryFallOldTree(world, player, saplings, felling, invalidate))
        {
            _naturalDeathCooldown = 8f;
        }

        // Biological time decides when the forest may spread. Large world changes also
        // have a wall-clock budget, so 512x acceleration cannot sow many trees per frame.
        // Missed intervals are deliberately never replayed.
        if (growthAllowed && _worldSeconds >= _nextSeedAt && _seedWallCooldown <= 0f)
        {
            bool expanded = TrySpreadForest(world, saplings, invalidate);
            _nextSeedAt = _worldSeconds + SeedInterval();
            _seedWallCooldown = expanded ? 12f : 3f;
        }

        int duePulses = (int)(_pulseClock / PulseSeconds);
        int pulses = Math.Min(duePulses, 4);
        if (pulses == 0)
        {
            return;
        }

        // Jeden snímek smí změnit svět nanejvýš čtyřikrát. Celé nezpracované pulzy se
        // zahodí místo uložení do skrytého backlogu, který by se po zpomalení dlouho doháněl.
        _pulseClock -= duePulses * PulseSeconds;
        for (int i = 0; i < pulses; i++)
        {
            Pulse(world, player, growthAllowed, invalidate);
        }
    }

    private void Pulse(
        VoxelWorld world,
        Vector3 player,
        bool growthAllowed,
        Action<Vector3i>? invalidate)
    {
        _pulse++;
        bool changedPlant = false;
        for (int attempt = 0; attempt < CandidateAttempts; attempt++)
        {
            int radius = attempt < CandidateAttempts / 2 ? NearPlantRadius : FarPlantRadius;
            Vector2i column = Candidate(player, attempt, radius);
            int surface = _generator.SurfaceHeight(column.X, column.Y);
            var at = new Vector3i(column.X, surface + 1, column.Y);
            if (!Loaded(world, at))
            {
                continue;
            }

            uint hash = Hash(column.X, column.Y, _pulse, attempt);

            if (!changedPlant && TryChangePlant(world, at, hash, growthAllowed))
            {
                changedPlant = true;
                PlantChanges++;
                invalidate?.Invoke(at);
            }

        }
    }

    private void DiscoverQueuedColumns(VoxelWorld world)
    {
        for (int processed = 0;
             processed < DiscoveryColumnsPerUpdate && _columnsToDiscover.Count > 0;
             processed++)
        {
            Vector2i column = _columnsToDiscover.Dequeue();
            _queuedColumns.Remove(column);

            int baseX = column.X * Chunk.Size;
            int baseZ = column.Y * Chunk.Size;

            for (int localZ = 0; localZ < Chunk.Size; localZ++)
            {
                for (int localX = 0; localX < Chunk.Size; localX++)
                {
                    DiscoverNaturalTree(world, baseX + localX, baseZ + localZ);
                }
            }
        }
    }

    private void DiscoverNaturalTree(VoxelWorld world, int x, int z)
    {
        if (!_planter.TryNaturalTreeAt(x, z, _generator, out Vector3i root, out ushort log)
            || _trees.ContainsKey(root))
        {
            return;
        }

        if (world.GetBlock(root.X, root.Y, root.Z) == log
            && world.GetBlock(root.X, root.Y + 1, root.Z) == log)
        {
            NoticeTree(root);
        }
    }

    private bool TryChangePlant(VoxelWorld world, Vector3i at, uint hash, bool growthAllowed)
    {
        ushort current = world.GetBlock(at.X, at.Y, at.Z);
        if (IsLowPlant(current))
        {
            if (hash % 100u < 12u)
            {
                world.SetBlock(at.X, at.Y, at.Z, BlockRegistry.Air);
                return true;
            }

            return false;
        }

        if (!growthAllowed || !world.Registry.IsAir(current))
        {
            return false;
        }

        ushort ground = world.GetBlock(at.X, at.Y - 1, at.Z);
        ushort clutter = _planter.GroundClutterAt(at.X, at.Z, ground, _generator);
        if (clutter != BlockRegistry.Air && world.CanPlace(clutter, at.X, at.Y, at.Z))
        {
            world.SetBlock(at.X, at.Y, at.Z, clutter);
            return true;
        }

        int chance = NearbyLowPlants(world, at, radius: 3) > 0 ? 52 : 18;
        if (hash % 100u >= chance)
        {
            return false;
        }

        ushort plant = PickPlant(at.X, at.Z, ground, hash);
        if (plant == BlockRegistry.Air || !world.CanPlace(plant, at.X, at.Y, at.Z))
        {
            return false;
        }

        world.SetBlock(at.X, at.Y, at.Z, plant);
        return true;
    }

    private bool TryFallOldTree(
        VoxelWorld world,
        Vector3 player,
        SaplingGrowth saplings,
        TreeFelling felling,
        Action<Vector3i>? invalidate)
    {
        // Střídá se blízká a vzdálená část dohledu. UVNITŘ zvolené části ale vždy
        // zemře nejdřív strom s nejstarším termínem smrti. Pouhý výběr nejbližšího
        // stromu způsobil, že jeho náhradní sazenice vyrostla na podobném místě a zemřela
        // znovu, zatímco tisíce původních přestárlých stromů zůstávaly beze změny.
        bool chooseFar = (FallenTrees & 1) != 0;
        float nearDistanceSquared = NearTreeRadius * NearTreeRadius;

        while (true)
        {
            TreeLife? selectedInBand = null;
            float selectedInBandDistance = 0f;
            TreeLife? fallback = null;
            float fallbackDistance = 0f;

            foreach (TreeLife candidate in _trees.Values)
            {
                if (candidate.FallAt > _worldSeconds || !Loaded(world, candidate.Root))
                {
                    continue;
                }

                float distance = HorizontalDistanceSquared(candidate.Root, player);
                if (BetterDeathCandidate(candidate, distance, fallback, fallbackDistance, chooseFar))
                {
                    fallback = candidate;
                    fallbackDistance = distance;
                }

                bool inWantedBand = chooseFar
                    ? distance > nearDistanceSquared
                    : distance <= nearDistanceSquared;
                if (inWantedBand
                    && BetterDeathCandidate(
                        candidate, distance, selectedInBand, selectedInBandDistance, chooseFar))
                {
                    selectedInBand = candidate;
                    selectedInBandDistance = distance;
                }
            }

            TreeLife? selected = selectedInBand ?? fallback;
            if (selected is null)
            {
                return false;
            }

            TreeLife life = selected;
            Vector3i root = life.Root;

            ushort log = world.GetBlock(root.X, root.Y, root.Z);
            if (!_logs.Contains(log) || world.GetBlock(root.X, root.Y + 1, root.Z) != log)
            {
                _trees.Remove(root);
                continue;
            }

            // Strom zemře teprve tehdy, když pro jeho nástupce existuje skutečné místo
            // v dostatečné vzdálenosti. Náhrada se tak nikdy nevrátí na tutéž patu.
            if (!TryFindReplacementSapling(
                    world, root, log, out ushort sapling, out Vector3i replacement))
            {
                _trees[root] = life with { FallAt = _worldSeconds + DayCycle.DayLength };
                return false;
            }

            int felled = felling.FallNaturally(
                world, root, FallDirection(root), out IReadOnlyList<Vector3i> changed);
            _trees.Remove(root);

            if (felled > 0)
            {
                FallenTrees++;
                foreach (Vector3i block in changed)
                {
                    invalidate?.Invoke(block);
                }

                world.SetBlock(replacement.X, replacement.Y, replacement.Z, sapling);
                saplings.Notice(replacement);
                SeededTrees++;
                invalidate?.Invoke(replacement);
            }

            return felled > 0;
        }
    }

    private static bool BetterDeathCandidate(
        TreeLife candidate,
        float distance,
        TreeLife? selected,
        float selectedDistance,
        bool chooseFar)
    {
        if (selected is null || candidate.FallAt < selected.FallAt)
        {
            return true;
        }

        if (candidate.FallAt > selected.FallAt)
        {
            return false;
        }

        // Stejně staré stromy rozsekne vzdálenost, aby blízký/vzdálený rytmus zůstal
        // deterministický a na obraze se opravdu střídaly obě části lesa.
        return chooseFar ? distance > selectedDistance : distance < selectedDistance;
    }

    private static float HorizontalDistanceSquared(Vector3i root, Vector3 player)
    {
        float dx = (root.X + 0.5f) - player.X;
        float dz = (root.Z + 0.5f) - player.Z;
        return (dx * dx) + (dz * dz);
    }

    /// <summary>A mature tree may establish one distant sapling during its lifetime.</summary>
    private bool TrySpreadForest(
        VoxelWorld world,
        SaplingGrowth saplings,
        Action<Vector3i>? invalidate)
    {
        List<TreeLife> candidates = SelectSpreadParents();

        if (candidates.Count == 0)
        {
            return false;
        }

        IReadOnlyList<SaplingGrowth.GrowthSnapshot> growing = saplings.Snapshot();
        var occupied = new HashSet<Vector2i>(_trees.Count + growing.Count);
        foreach (Vector3i root in _trees.Keys)
        {
            occupied.Add(new Vector2i(root.X, root.Z));
        }

        foreach (SaplingGrowth.GrowthSnapshot tree in growing)
        {
            occupied.Add(new Vector2i(tree.X, tree.Z));
        }

        foreach (TreeLife parent in candidates)
        {
            Vector3i root = parent.Root;
            if (!Loaded(world, root))
            {
                continue;
            }

            ushort log = world.GetBlock(root.X, root.Y, root.Z);
            if (!_logs.Contains(log) || world.GetBlock(root.X, root.Y + 1, root.Z) != log)
            {
                _trees.Remove(root);
                continue;
            }

            if (CountTreesNear(occupied, root, LocalForestRadius) >= LocalForestCapacity)
            {
                continue;
            }

            string saplingId = _blocks.Definition(log).Id.Replace(
                "_log", "_sapling", StringComparison.Ordinal);
            if (!_blocks.TryIndexOf(saplingId, out ushort sapling)
                || !TryFindExpansionSapling(world, root, sapling, occupied, out Vector3i planted))
            {
                continue;
            }

            world.SetBlock(planted.X, planted.Y, planted.Z, sapling);
            saplings.Notice(planted);
            _trees[root] = parent with { HasSpread = true };
            SeededTrees++;
            ExpandedTrees++;
            invalidate?.Invoke(planted);
            return true;
        }

        return false;
    }

    /// <summary>Vybere nanejvýš pevný počet reprodukčních rodičů v jednom lineárním průchodu.</summary>
    private List<TreeLife> SelectSpreadParents()
    {
        var selected = new List<(uint Score, TreeLife Tree)>(ParentAttempts + 1);
        long eventStep = (long)Math.Floor(_nextSeedAt);

        foreach (TreeLife tree in _trees.Values)
        {
            if (tree.HasSpread
                || tree.BornAt + ReproductiveAge > _worldSeconds
                || tree.FallAt <= _worldSeconds)
            {
                continue;
            }

            uint score = Hash(tree.Root.X, tree.Root.Z, eventStep, tree.Root.Y);
            selected.Add((score, tree));
            selected.Sort(static (a, b) =>
            {
                int scoreOrder = a.Score.CompareTo(b.Score);
                if (scoreOrder != 0) { return scoreOrder; }
                int xOrder = a.Tree.Root.X.CompareTo(b.Tree.Root.X);
                if (xOrder != 0) { return xOrder; }
                int zOrder = a.Tree.Root.Z.CompareTo(b.Tree.Root.Z);
                return zOrder != 0 ? zOrder : a.Tree.Root.Y.CompareTo(b.Tree.Root.Y);
            });

            if (selected.Count > ParentAttempts)
            {
                selected.RemoveAt(selected.Count - 1);
            }
        }

        return [.. selected.Select(candidate => candidate.Tree)];
    }

    private bool TryFindExpansionSapling(
        VoxelWorld world,
        Vector3i root,
        ushort sapling,
        HashSet<Vector2i> occupied,
        out Vector3i planted)
    {
        int radiusSpan = (MaxSeedDistance - MinSeedDistance) + 1;
        for (int attempt = 0; attempt < SeedAttempts; attempt++)
        {
            uint hash = Hash(
                root.X,
                root.Z,
                (long)Math.Floor(_nextSeedAt),
                root.Y + (attempt * 97));
            int radius = MinSeedDistance + (int)(hash % (uint)radiusSpan);
            int perimeter = radius * 8;
            (int dx, int dz) = RingOffset(radius, (int)((hash >> 8) % (uint)perimeter));
            var approximate = new Vector3i(root.X + dx, root.Y, root.Z + dz);

            if (!Loaded(world, approximate)
                || !TryFindPlantAt(
                    world, approximate.X, root.Y, approximate.Z, sapling, out Vector3i candidate)
                || !Loaded(world, candidate)
                || HasTreeNear(occupied, candidate, MinTreeSpacing)
                || !HasMatureTrunkRoom(world, candidate, sapling))
            {
                continue;
            }

            planted = candidate;
            return true;
        }

        planted = default;
        return false;
    }

    private static int CountTreesNear(HashSet<Vector2i> occupied, Vector3i centre, int radius)
    {
        int count = 0;
        for (int z = centre.Z - radius; z <= centre.Z + radius; z++)
        {
            for (int x = centre.X - radius; x <= centre.X + radius; x++)
            {
                if (occupied.Contains(new Vector2i(x, z)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool HasTreeNear(HashSet<Vector2i> occupied, Vector3i centre, int radius)
    {
        int extent = radius - 1;
        for (int z = centre.Z - extent; z <= centre.Z + extent; z++)
        {
            for (int x = centre.X - extent; x <= centre.X + extent; x++)
            {
                if (occupied.Contains(new Vector2i(x, z)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool HasMatureTrunkRoom(VoxelWorld world, Vector3i root, ushort sapling)
    {
        int height = _planter.MatureTrunkHeight(root, sapling);
        if (height <= 0 || root.Y + height >= TerrainGenerator.WorldHeight)
        {
            return false;
        }

        for (int offset = 1; offset <= height; offset++)
        {
            var at = new Vector3i(root.X, root.Y + offset, root.Z);
            if (!world.HasChunk(VoxelWorld.ToChunkPosition(at.X, at.Y, at.Z))
                || !world.Registry.IsAir(world.GetBlock(at.X, at.Y, at.Z)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Každý přirozeně zemřelý strom po sobě zanechá sazenici stejného druhu.</summary>
    private bool TryFindReplacementSapling(
        VoxelWorld world,
        Vector3i root,
        ushort log,
        out ushort sapling,
        out Vector3i planted)
    {
        string saplingId = _blocks.Definition(log).Id.Replace("_log", "_sapling", StringComparison.Ordinal);
        if (!_blocks.TryIndexOf(saplingId, out sapling))
        {
            planted = default;
            return false;
        }

        uint hash = Hash(root.X, root.Z, _seed + 1777, root.Y);

        // Projde celý obvod každého čtvercového prstence 6–18 bloků od rodiče. Začátek
        // každého obvodu otočí hash, takže les necestuje pravidelně jedním směrem.
        for (int radius = 6; radius <= 18; radius++)
        {
            int perimeter = radius * 8;
            int start = (int)((hash + ((uint)radius * 2654435761u)) % (uint)perimeter);

            for (int attempt = 0; attempt < perimeter; attempt++)
            {
                (int dx, int dz) = RingOffset(radius, (start + attempt) % perimeter);

                if (TryFindPlantAt(world, root.X + dx, root.Y, root.Z + dz, sapling, out planted))
                {
                    return true;
                }
            }
        }

        planted = default;
        return false;
    }

    private static (int X, int Z) RingOffset(int radius, int index)
    {
        int side = radius * 2;
        if (index < side) { return (-radius + index, -radius); }
        index -= side;
        if (index < side) { return (radius, -radius + index); }
        index -= side;
        if (index < side) { return (radius - index, radius); }
        index -= side;
        return (-radius, radius - index);
    }

    private bool TryFindPlantAt(
        VoxelWorld world, int x, int baseY, int z, ushort sapling, out Vector3i planted)
    {
        for (int y = Math.Min(baseY + 4, TerrainGenerator.WorldHeight - 1); y >= Math.Max(1, baseY - 8); y--)
        {
            ushort current = world.GetBlock(x, y, z);
            ushort ground = world.GetBlock(x, y - 1, z);

            if ((world.Registry.IsAir(current) || IsLowPlant(current))
                && world.Registry.CanStandOn(sapling, ground))
            {
                planted = new Vector3i(x, y, z);
                return true;
            }
        }

        planted = default;
        return false;
    }

    private ushort PickPlant(int x, int z, ushort ground, uint hash)
    {
        if (ground == _grass)
        {
            uint pick = (hash >> 8) % 100u;
            if (_generator.ForestDensity(x, z) > 0.60f && pick < 36u)
            {
                return _fern;
            }

            return pick switch
            {
                < 58u => _shortGrass,
                < 82u => _tallGrass,
                < 90u => _fern,
                _ => _flowers[(hash >> 18) % (uint)_flowers.Length],
            };
        }

        if (ground == _dryGround || ground == _snow)
        {
            return _dryGrass;
        }

        return ground == _sand && _generator.BiomeAt(x, z) == Biome.Desert
            ? _deadBush
            : BlockRegistry.Air;
    }

    private bool IsLowPlant(ushort block) =>
        block != BlockRegistry.Air
        && _blocks.ShapeOf(block) == BlockShape.Cross
        && _blocks.IsReplaceableVegetation(block)
        && !_saplingBlocks.Contains(block);

    private int NearbyLowPlants(VoxelWorld world, Vector3i centre, int radius)
    {
        int found = 0;
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int surface = _generator.SurfaceHeight(centre.X + dx, centre.Z + dz);
                if (IsLowPlant(world.GetBlock(centre.X + dx, surface + 1, centre.Z + dz)))
                {
                    found++;
                }
            }
        }

        return found;
    }

    private Vector2i Candidate(Vector3 player, int attempt, int radius)
    {
        uint hash = Hash((int)_pulse, attempt, _seed, attempt + 71);
        int span = (radius * 2) + 1;
        int x = (int)MathF.Floor(player.X) + ((int)(hash % (uint)span) - radius);
        int z = (int)MathF.Floor(player.Z) + ((int)((hash >> 16) % (uint)span) - radius);
        return new Vector2i(x, z);
    }

    private static bool Loaded(VoxelWorld world, Vector3i at) =>
        world.HasChunk(VoxelWorld.ToChunkPosition(at.X, at.Y, at.Z))
        && world.HasChunk(VoxelWorld.ToChunkPosition(at.X, at.Y - 1, at.Z));

    private double Lifetime(Vector3i root)
    {
        uint hash = Hash(root.X, root.Z, _seed, root.Y);
        double t = (hash % 10_000u) / 9_999.0;
        return MinTreeLife + ((MaxTreeLife - MinTreeLife) * t);
    }

    private double SeedInterval()
    {
        uint hash = Hash(
            _seed,
            _trees.Count,
            (long)Math.Floor(_worldSeconds),
            unchecked((int)_pulse));
        double t = (hash % 10_000u) / 9_999.0;
        return MinSeedInterval + ((MaxSeedInterval - MinSeedInterval) * t);
    }

    private Vector2i FallDirection(Vector3i root) => (Hash(root.X, root.Z, _seed + 991, root.Y) & 3u) switch
    {
        0u => new Vector2i(1, 0),
        1u => new Vector2i(-1, 0),
        2u => new Vector2i(0, 1),
        _ => new Vector2i(0, -1),
    };

    private uint Hash(int x, int z, long step, int salt) =>
        Hash(x ^ unchecked((int)step), z ^ unchecked((int)(step >> 32)), _seed + salt, salt);

    private static uint Hash(int x, int z, int seed, int salt)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)x) * 16777619u;
            hash = (hash ^ (uint)z) * 16777619u;
            hash = (hash ^ (uint)seed) * 16777619u;
            hash = (hash ^ (uint)salt) * 16777619u;
            hash ^= hash >> 13;
            hash *= 0x5bd1e995u;
            return hash ^ (hash >> 15);
        }
    }

    public bool Load(string path, SaplingGrowth? growth = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            State? state = JsonSerializer.Deserialize<State>(File.ReadAllText(path), JsonOptions);
            if (state is null || state.Version != StateVersion)
            {
                return false;
            }

            _worldSeconds = Math.Max(0.0, state.WorldSeconds);
            _pulse = Math.Max(0L, state.Pulse);
            _trees.Clear();
            _nextSeedAt = state.NextSeedAt > _worldSeconds
                ? state.NextSeedAt
                : _worldSeconds + SeedInterval();

            foreach (TreeState tree in state.Trees ?? [])
            {
                var root = new Vector3i(tree.X, tree.Y, tree.Z);
                _trees[root] = new TreeLife(
                    root,
                    tree.BornAt,
                    Math.Max(tree.BornAt, tree.FallAt),
                    tree.HasSpread);
            }

            growth?.Restore(state.GrowingTrees);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public void Save(string path, SaplingGrowth? growth = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var state = new State(
            StateVersion,
            _worldSeconds,
            _pulse,
            _nextSeedAt,
            [.. _trees.Values.Select(tree => new TreeState(
                tree.Root.X,
                tree.Root.Y,
                tree.Root.Z,
                tree.BornAt,
                tree.FallAt,
                tree.HasSpread))],
            growth?.Snapshot());

        string temporary = fullPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, fullPath, overwrite: true);
    }

    private sealed record TreeLife(Vector3i Root, double BornAt, double FallAt, bool HasSpread);
    private sealed record State(
        int Version,
        double WorldSeconds,
        long Pulse,
        double NextSeedAt,
        List<TreeState>? Trees,
        IReadOnlyList<SaplingGrowth.GrowthSnapshot>? GrowingTrees);
    private sealed record TreeState(
        int X, int Y, int Z, double BornAt, double FallAt, bool HasSpread = false);
}
