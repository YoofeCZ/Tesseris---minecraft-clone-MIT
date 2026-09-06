using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.World;

/// <summary>
/// Rozsazuje stromy a kaktusy do hotového terénu.
///
/// <para><b>Strom přesahuje přes hranici chunku, a to je celý problém.</b> Dub v rohu
/// chunku má půlku koruny u souseda. Chunky se přitom generují nezávisle na sobě, na
/// worker vláknech a v libovolném pořadí — jeden nemůže druhému nic „dopsat", protože
/// ten druhý ještě nemusí existovat, nebo už může být zameshovaný.</para>
///
/// <para><b>Řešení je deterministické razítkování.</b> Když se generuje chunk, projdou se
/// všechny sloupce v okolí širokém tak, aby pokrylo každý strom, který do chunku může
/// zasáhnout, a z pozice sloupce se <b>spočítá</b>, jestli tam strom stojí a jaký. Části,
/// které padnou dovnitř chunku, se zapíšou; zbytek se zahodí. Soused si tentýž strom
/// spočítá znovu a zapíše si svou část. Nikdo si nic nepředává a výsledek nezávisí
/// na pořadí.</para>
///
/// <para><b>Nesmí se sahat na cache sloupců sousedních chunků</b> jinak než přes
/// <see cref="TerrainGenerator.ColumnAt"/> — ta je bezzámková a deterministická. Kdyby
/// razítkování potřebovalo hotový sousední chunk, vznikla by kruhová závislost.</para>
/// </summary>
public sealed class TreePlanter
{
    /// <summary>Poslední fáze růstu: současný plně velký strom.</summary>
    public const int MatureGrowthStage = 3;

    /// <summary>
    /// Jak daleko od chunku se hledají kmeny. Musí pokrýt nejširší korunu i nejvyšší
    /// strom, jinak by se u hranice ořezávaly koruny.
    ///
    /// <para>Drží se o blok nad nejširší korunou (akácie, poloměr 5). Kdyby zůstal na
    /// čtyřech jako dřív, kmen o pět bloků za hranicí chunku by se nenašel a jeho listí
    /// by v tomhle chunku chybělo - na stěně chunku by byl vidět svislý řez korunou.</para>
    /// </summary>
    public const int Reach = 6;

    /// <summary>
    /// Nejvyšší strom. Určuje, kolik chunků pod sebou se musí prohledat.
    ///
    /// <para><b>Musí sedět na nejvyšší hodnotu ve výpočtu výšky</b>, jinak se koruna na
    /// hranici chunku usekne — streamer podle tohohle čísla ví, jak daleko dopředu hledat
    /// stromy, které do chunku můžou dosáhnout. Nejvyšší je smrk: 13 + 6 = 19, plus koruna.</para>
    /// </summary>
    private const int MaxHeight = 22;

    /// <summary>Kmen a listí pro každý druh. Velikost drží <see cref="TreeKind"/>.</summary>
    private readonly ushort[] _log = new ushort[TreeKindCount];
    private readonly ushort[] _leaves = new ushort[TreeKindCount];

    /// <summary>Sazenice každého druhu. Podle ní se pozná, co má na daném místě vyrůst.</summary>
    private readonly ushort[] _sapling = new ushort[TreeKindCount];

    private readonly ushort _cactus;

    private readonly ushort _shortGrass;
    private readonly ushort _tallGrass;
    private readonly ushort _dryGrass;
    private readonly ushort _fern;
    private readonly ushort _deadBush;
    private readonly ushort[] _flowers;
    private readonly ushort _stick;
    private readonly ushort _smallStone;
    private readonly ushort _smallStoneMid;
    private readonly ushort _smallStoneBig;
    private readonly ushort _flint;
    private readonly ushort _flintMid;

    // Podvodní porost. Roste na TOMTÉŽ podkladu jako suchozemský (písek, hlína, štěrk),
    // rozhoduje jen to, jestli je sloupec pod hladinou.
    private readonly ushort _seagrass;
    private readonly ushort _kelp;
    private readonly ushort _redAlgae;
    private readonly ushort _paleWeed;
    private readonly ushort _seaFern;

    // Pod hladinou stoji na miste rostliny voda, ne vzduch. Bez tohohle by se podvodni
    // porost nemel kam zapsat — test volneho mista hleda vzduch.
    private readonly ushort _water;

    // Podklad rozhoduje, co na něm vyroste: na sněhu ani kameni netráví nic.
    private readonly ushort _grass;
    private readonly ushort _dryGrassBlock;
    private readonly ushort _snow;
    private readonly ushort _sand;
    private readonly ushort _dirt;
    private readonly ushort _gravel;
    private readonly ushort _stone;

    private readonly int _seed;

    /// <summary>
    /// Kde je hustý les a kde mýtina.
    ///
    /// <para>Bez tohohle byla hustota v celém biomu stejná a stromy vyšly rozeseté
    /// rovnoměrně jako v sadu. Se šumem vzniknou háje a mezi nimi volná krajina — a hlavně
    /// je podle čeho obarvit vzdálený terén, takže lesy jsou vidět až k obzoru.</para>
    ///
    /// <para>Vlnová délka kolem 500 bloků: háj se dá přejít, ale je z něj co vidět i z dálky.</para>
    /// </summary>
    private readonly SimplexNoise _forest;

    public TreePlanter(BlockRegistry registry, int seed)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _seed = seed;
        _forest = new SimplexNoise(seed + 8623);

        _log[(int)TreeKind.Oak] = registry.IndexOf("tesseris:oak_log");
        _leaves[(int)TreeKind.Oak] = registry.IndexOf("tesseris:oak_leaves");
        _log[(int)TreeKind.Spruce] = registry.IndexOf("tesseris:spruce_log");
        _leaves[(int)TreeKind.Spruce] = registry.IndexOf("tesseris:spruce_leaves");
        _log[(int)TreeKind.Acacia] = registry.IndexOf("tesseris:acacia_log");
        _leaves[(int)TreeKind.Acacia] = registry.IndexOf("tesseris:acacia_leaves");
        _log[(int)TreeKind.Birch] = registry.IndexOf("tesseris:birch_log");
        _leaves[(int)TreeKind.Birch] = registry.IndexOf("tesseris:birch_leaves");
        _log[(int)TreeKind.Maple] = registry.IndexOf("tesseris:maple_log");
        _leaves[(int)TreeKind.Maple] = registry.IndexOf("tesseris:maple_leaves");

        // Sazenice se páruje na druh podle jména, ne podle pořadí v enumu. Nová sazenice
        // bez odpovídajícího druhu vyjde jako vzduch a prostě nevyroste — nespadne to.
        for (int kind = 0; kind < TreeKindCount; kind++)
        {
            string species = Enum.GetName((TreeKind)kind)!.ToLowerInvariant();
            _sapling[kind] = registry.IndexOf($"tesseris:{species}_sapling");
        }

        _cactus = registry.IndexOf("tesseris:cactus");

        _shortGrass = registry.IndexOf("tesseris:short_grass");
        _tallGrass = registry.IndexOf("tesseris:tall_grass");
        _dryGrass = registry.IndexOf("tesseris:dry_grass_tuft");
        _fern = registry.IndexOf("tesseris:fern");
        _deadBush = registry.IndexOf("tesseris:dead_bush");
        _stick = registry.IndexOf("tesseris:stick");
        _smallStone = registry.IndexOf("tesseris:small_stone");
        _smallStoneMid = registry.IndexOf("tesseris:small_stone_mid");
        _smallStoneBig = registry.IndexOf("tesseris:small_stone_big");
        _flint = registry.IndexOf("tesseris:flint");
        _flintMid = registry.IndexOf("tesseris:flint_mid");

        _seagrass = registry.IndexOf("tesseris:seagrass");
        _kelp = registry.IndexOf("tesseris:kelp");
        _redAlgae = registry.IndexOf("tesseris:red_algae");
        _paleWeed = registry.IndexOf("tesseris:pale_weed");
        _seaFern = registry.IndexOf("tesseris:sea_fern");
        _water = registry.IndexOf("tesseris:water");

        _flowers =
        [
            registry.IndexOf("tesseris:flower_red"),
            registry.IndexOf("tesseris:flower_yellow"),
            registry.IndexOf("tesseris:flower_white"),
        ];

        _grass = registry.IndexOf("tesseris:grass");
        _dryGrassBlock = registry.IndexOf("tesseris:dry_grass");
        _snow = registry.IndexOf("tesseris:snow");
        _sand = registry.IndexOf("tesseris:sand");
        _dirt = registry.IndexOf("tesseris:dirt");
        _gravel = registry.IndexOf("tesseris:gravel");
        _stone = registry.IndexOf("tesseris:stone");
    }

    private enum TreeKind
    {
        Oak,
        Spruce,
        Acacia,
        Birch,
        Maple,
    }

    /// <summary>
    /// Kolik druhů stromů existuje.
    /// </summary>
    /// <remarks>
    /// Pole kmenů a listí se podle toho zvětšují sama. Dřív tu byla trojka napevno, takže
    /// přidání druhu znamenalo tichý pád na indexu — a to až za běhu, při sázení.
    /// </remarks>
    private static readonly int TreeKindCount = Enum.GetValues<TreeKind>().Length;

    /// <summary>Vrátí sazenici druhu, který do daného sloupce patří.</summary>
    /// <remarks>
    /// Živá vegetace používá stejné rozhodnutí jako prvotní generování. Kdyby si druh
    /// losovala sama, mohl by uprostřed smrkového lesa samovolně vznikat dubový háj.
    /// </remarks>
    public ushort SaplingAt(int worldX, int worldZ, TerrainGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);

        Column column = generator.ColumnAt(worldX, worldZ);
        uint hash = Hash(worldX, worldZ, _seed);
        (int chance, TreeKind kind, bool cactus) = Profile(column.Biome, hash);

        return chance == 0 || cactus ? BlockRegistry.Air : _sapling[(int)kind];
    }

    /// <summary>Je na souřadnici kořen stromu z původního deterministického lesa?</summary>
    /// <remarks>
    /// Nečte svět. Ekologie tak bezpečně rozpozná přírodní strom, aniž by za strom
    /// považovala hráčův sloup z klád. Přítomnost kmene musí volající ověřit ve světě.
    /// </remarks>
    public bool TryNaturalTreeAt(
        int worldX, int worldZ, TerrainGenerator generator, out Vector3i root, out ushort log)
    {
        ArgumentNullException.ThrowIfNull(generator);

        if (!TryTreeAt(worldX, worldZ, generator, out Tree tree) || tree.Cactus)
        {
            root = default;
            log = BlockRegistry.Air;
            return false;
        }

        root = new Vector3i(tree.X, tree.BaseY, tree.Z);
        log = _log[(int)tree.Kind];
        return true;
    }

    /// <summary>
    /// Vloží do chunku ty části stromů, které do něj zasahují.
    ///
    /// <para>Volá se až po vyplnění terénu, na worker vlákně, na chunk, který ještě nikdo
    /// jiný nevidí.</para>
    /// </summary>
    public void Plant(Chunk chunk, int baseX, int baseY, int baseZ, TerrainGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(generator);

        // Chunk celý pod zemí ani vysoko nad terénem se nemusí procházet vůbec. Rozsah je
        // schválně velkorysý: falešně zahrnutý chunk stojí jednu smyčku navíc, falešně
        // vynechaný by uřízl korunu.
        int top = baseY + Chunk.Size;

        for (int offsetZ = -Reach; offsetZ < Chunk.Size + Reach; offsetZ++)
        {
            for (int offsetX = -Reach; offsetX < Chunk.Size + Reach; offsetX++)
            {
                int worldX = baseX + offsetX;
                int worldZ = baseZ + offsetZ;

                if (!TryTreeAt(worldX, worldZ, generator, out Tree tree))
                {
                    continue;
                }

                // Strom, který celý leží mimo svislý rozsah chunku, se přeskočí dřív, než
                // se začne razítkovat.
                if (tree.BaseY > top || tree.BaseY + tree.Height + 2 < baseY)
                {
                    continue;
                }

                var sink = new ChunkSink(chunk, baseX, baseY, baseZ);
                Stamp(tree, ref sink);
            }
        }

        PlantUndergrowth(chunk, baseX, baseY, baseZ, generator);
    }

    /// <summary>
    /// Vysází trávu, kapradí a kytky na povrch.
    ///
    /// <para><b>Nepotřebuje lem.</b> Rostlina má jeden blok a nikam nepřesahuje, takže se
    /// stačí projít vlastní sloupce chunku. Tím se to liší od stromů, u kterých se musí
    /// prohledávat i okolí.</para>
    ///
    /// <para>Rozhoduje <b>blok pod rostlinou</b>, ne biom. Je to spolehlivější: na
    /// strmině leží kámen nebo štěrk i uprostřed louky, a z kamene tráva neroste.
    /// Zároveň to zadarmo vyřeší okraje biomů.</para>
    /// </summary>
    private void PlantUndergrowth(Chunk chunk, int baseX, int baseY, int baseZ, TerrainGenerator generator)
    {
        for (int z = 0; z < Chunk.Size; z++)
        {
            for (int x = 0; x < Chunk.Size; x++)
            {
                int worldX = baseX + x;
                int worldZ = baseZ + z;

                int surface = generator.SurfaceHeight(worldX, worldZ);

                // NA DNĚ ROSTE TAKY, jen něco jiného. Dřív se sloupec pod hladinou přeskakoval
                // úplně s odůvodněním, že by trs čněl z vody — to platí jen pro místo TĚSNĚ
                // pod hladinou, kde rostlina prorazí povrch. Hlouběji je dno holé zbytečně.
                bool underwater = surface < TerrainGenerator.SeaLevel;

                // Rostlina stojí blok nad povrchem, takže mělčina o jeden blok by ji vystrčila
                // nad hladinu. Ostrá podmínka, ne tolerance: jediný trs koukající z moře je
                // vidět na kilometr.
                if (underwater && surface + 1 >= TerrainGenerator.SeaLevel)
                {
                    continue;
                }

                int y = surface + 1 - baseY;

                // Rostlina stojí jeden blok nad povrchem. Když ten blok do tohohle chunku
                // nespadne, patří sousedovi.
                if (y < 0 || y >= Chunk.Size)
                {
                    continue;
                }

                // Místo musí být volné: pod stromem ani ve skále nic neroste. Pod hladinou
                // je "volno" VODA, ne vzduch — porost ji nahradí a mesher pak vodní stěnu
                // k rostlině nekreslí, takže díra po ní není vidět (viz ChunkMesher).
                ushort occupant = chunk.GetBlock(x, y, z);
                bool free = occupant == BlockRegistry.Air || (underwater && occupant == _water);

                if (!free)
                {
                    continue;
                }

                // Podklad. Leží o blok níž, takže může být i v sousedním chunku — pak se
                // vezme ze světa přes generátor, ne z tohoto chunku.
                int groundY = y - 1;
                ushort ground = groundY >= 0
                    ? chunk.GetBlock(x, groundY, z)
                    : generator.SurfaceBlockAt(worldX, surface, worldZ);

                ushort plant = PickPlant(worldX, worldZ, ground, generator, underwater);

                if (plant == BlockRegistry.Air)
                {
                    continue;
                }

                if (!underwater)
                {
                    chunk.SetBlock(x, y, z, plant);
                    continue;
                }

                // VODNÍ ROSTLINA MŮŽE BÝT VYŠŠÍ NEŽ BLOK.
                //
                // Chaluha roste do pěti bloků, mořská tráva do dvou. Zapisuje se odspodu
                // nahoru a přeruší se na první překážce — kameni, jiné rostlině nebo hladině.
                //
                // Hash musí sedět na ten, kterým se vybíral druh, jinak by týž trs vyšel
                // pokaždé jinak vysoký podle toho, který chunk se zrovna meshuje.
                int height = PlantHeight(plant, Hash(worldX, worldZ, _seed + 4517));

                for (int step = 0; step < height; step++)
                {
                    // Hladina je strop: trs nesmí prorazit povrch, jinak z moře trčí stéblo.
                    if (surface + 1 + step >= TerrainGenerator.SeaLevel)
                    {
                        break;
                    }

                    int py = y + step;

                    // Části, které padnou mimo tenhle chunk, dopíše soused — počítá totéž.
                    if (py < 0 || py >= Chunk.Size)
                    {
                        continue;
                    }

                    ushort there = chunk.GetBlock(x, py, z);

                    if (there != BlockRegistry.Air && there != _water)
                    {
                        break;
                    }

                    chunk.SetBlock(x, py, z, plant);
                }
            }
        }
    }

    /// <summary>Co vyroste na daném podkladu, nebo vzduch.</summary>
    /// <summary>
    /// Rostlina na daném sloupci, nebo vzduch.
    /// </summary>
    /// <remarks>
    /// Vystaveno kvůli vzdálenému terénu, který si trávu sází sám, ale musí ji dostat na
    /// <b>táž místa</b> jako chunky — jinak by při přechodu hranice porost přeskočil.
    /// </remarks>
    public ushort PlantAt(int worldX, int worldZ, ushort ground, TerrainGenerator generator) =>
        PickPlant(worldX, worldZ, ground, generator, underwater: false);

    /// <summary>Kolik bloků nad sebou daná vodní rostlina zabírá.</summary>
    /// <remarks>
    /// <para><b>Výška je vlastnost druhu, ne náhoda.</b> Kdyby se losovala zvlášť, stál by
    /// vedle sebe jednoblokový a čtyřblokový trs téhož druhu a vypadalo by to jako chyba.
    /// Rozptyl uvnitř druhu dělá <paramref name="hash"/>, ale jen o blok.</para>
    /// </remarks>
    private int PlantHeight(ushort plant, uint hash)
    {
        uint jitter = (hash >> 17) % 100;

        // Chaluha je ta vysoká — má tvořit les, kterým se dá proplavat.
        if (plant == _kelp)
        {
            return jitter < 30 ? 3 : (jitter < 75 ? 4 : 5);
        }

        // MOŘSKÁ TRÁVA JE VŽDYCKY JEN JEDEN BLOK.
        //
        // Dřív měla občas dva a vypadalo to špatně: nemá segmentové varianty jako chaluha,
        // takže se nad sebou octly dva TÉŽE kresby — dva plné trsy na sobě, ne jedna vyšší
        // rostlina. Vícepatrové smí být jen to, co má kořen, tělo a vrcholek zvlášť.
        if (plant == _seagrass || plant == _paleWeed)
        {
            return 1;
        }

        // Kapradí a řasy jsou mezi tím.
        if (plant == _seaFern)
        {
            return jitter < 55 ? 2 : 3;
        }

        // Červená řasa segmenty má, takže vícepatrová být smí.
        if (plant == _redAlgae)
        {
            return jitter < 55 ? 2 : 3;
        }

        return 1;
    }

    /// <summary>
    /// Co vyroste na mořském dně.
    ///
    /// <para><b>Podle čeho se to dělí.</b> Generátor nezná řeky ani jezera — voda je prostě
    /// všude, kde terén klesne pod hladinu, a biomy jsou jen suchozemské. Rozdělit porost
    /// na „mořský" a „říční" proto nejde, ta informace nikde není.</para>
    ///
    /// <para>Co se rozlišit dá a co dělá stejnou službu: <b>hloubka</b> a <b>biom pobřeží</b>.
    /// Mělčina u břehu dostane jiný porost než hluboké dno a studené moře jiný než teplé —
    /// výsledkem je, že zátoka v tundře vypadá jinak než laguna v savaně, i když o tom, že
    /// je jedna zátoka a druhá laguna, generátor nic neví.</para>
    /// </summary>
    private ushort PickWaterPlant(
        int worldX, int worldZ, ushort ground, TerrainGenerator generator, uint hash, uint sample)
    {
        // Z kamene neroste nic ani pod vodou; sráz zůstane holý.
        if (ground != _sand && ground != _grass && ground != _dryGrassBlock)
        {
            return BlockRegistry.Air;
        }

        int depth = TerrainGenerator.SeaLevel - generator.SurfaceHeight(worldX, worldZ);

        // HUSTOTA ROSTE S HLOUBKOU, ale zdaleka ne do koberce.
        //
        // Prvni verze mela plosnych 34 % a dno bylo souvisle zarostle — porost prestal byt
        // porostem a stal se z nej travnik. Melcina je ted skoro holá s obcasnym trsem
        // a plne zarostla je az hlubina, kde je stejne skoro tma.
        float density = depth < 4 ? 0.05f : (depth < 10 ? 0.11f : 0.17f);

        if (sample >= HashSteps * density)
        {
            return BlockRegistry.Air;
        }

        Biome biome = generator.BiomeAt(worldX, worldZ);
        uint pick = (hash >> 12) % 100;

        // STUDENÁ VODA: bledý porost, žádné řasy. Tundra a vrcholky.
        if (biome is Biome.Tundra or Biome.SnowyPeaks or Biome.FrozenPeaks)
        {
            return pick < 64 ? _paleWeed : _kelp;
        }

        // TEPLÁ VODA: červené řasy patří k tropům a k pouštnímu pobřeží.
        if (biome is Biome.Desert or Biome.Badlands or Biome.Savanna)
        {
            return pick switch
            {
                < 42 => _seagrass,
                < 70 => _redAlgae,
                < 88 => _seaFern,
                _ => _kelp,
            };
        }

        // MĚLČINA v mírném pásmu je trávnatá, hlubina patří chaluhám.
        if (depth < 6)
        {
            return pick < 72 ? _seagrass : _seaFern;
        }

        return pick switch
        {
            < 34 => _seagrass,
            < 58 => _seaFern,
            < 82 => _kelp,
            _ => _paleWeed,
        };
    }

    private ushort PickPlant(
        int worldX, int worldZ, ushort ground, TerrainGenerator generator, bool underwater)
    {
        // Vlastní hash, jinak by rostliny stály přesně tam co stromy.
        uint hash = Hash(worldX, worldZ, _seed + 4517);
        uint sample = hash % HashSteps;

        // PODVODNÍ POROST SE ROZHODUJE PRVNÍ a přebíjí podklad.
        //
        // Na dně leží písek, hlína i štěrk — tytéž bloky jako na souši. Kdyby se rozhodovalo
        // podle podkladu, vyrostla by na mořském dně obyčejná louka.
        if (underwater)
        {
            return PickWaterPlant(worldX, worldZ, ground, generator, hash, sample);
        }

        ushort clutter = GroundClutterAt(worldX, worldZ, ground, generator);
        if (clutter != BlockRegistry.Air)
        {
            return clutter;
        }

        if (ground == _grass)
        {
            // V lese je podrostu víc. Sahá se na tentýž lesní šum jako u stromů, takže
            // hustý porost a hustý les jsou na stejném místě.
            //
            // Naměřeno, proč zrovna tolik: při 0,35 až 0,80 vyskočil meshing chunku
            // z 0,76 na 1,61 ms (cíl je 2) a trojúhelníků přibylo o polovinu. Zředěním
            // se z toho stalo 1,0 ms a louka pořád vypadá jako louka.
            float density = 0.18f + (Forest(worldX, worldZ) * 0.30f);

            if (sample >= HashSteps * density)
            {
                return BlockRegistry.Air;
            }

            uint pick = (hash >> 12) % 100;

            return pick switch
            {
                < 62 => _shortGrass,
                < 82 => _tallGrass,
                < 90 => _fern,
                _ => _flowers[(hash >> 20) % (uint)_flowers.Length],
            };
        }

        if (ground == _dryGrassBlock)
        {
            return sample < HashSteps * 0.30f ? _dryGrass : BlockRegistry.Air;
        }

        if (ground == _sand)
        {
            // V poušti jen občas suchý keř. Rozhoduje biom, aby keře nerostly na pláži.
            return sample < HashSteps * 0.012f && generator.BiomeAt(worldX, worldZ) == Biome.Desert
                ? _deadBush
                : BlockRegistry.Air;
        }

        if (ground == _snow)
        {
            return sample < HashSteps * 0.04f ? _dryGrass : BlockRegistry.Air;
        }

        return BlockRegistry.Air;
    }

    /// <summary>Deterministický klacík, kamínek nebo pazourek pro nový i starší svět.</summary>
    public ushort GroundClutterAt(
        int worldX, int worldZ, ushort ground, TerrainGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);

        uint hash = Hash(worldX, worldZ, _seed + 9731);
        float sample = (hash % HashSteps) / (float)HashSteps;

        if (ground == _grass)
        {
            float sticks = 0.012f + (Forest(worldX, worldZ) * 0.012f);
            if (sample < sticks) { return _stick; }
            if (sample < sticks + 0.007f) { return StoneClutter(hash); }
            return sample < sticks + 0.0095f ? FlintClutter(hash) : BlockRegistry.Air;
        }

        if (ground == _dryGrassBlock)
        {
            if (sample < 0.008f) { return _stick; }
            if (sample < 0.020f) { return StoneClutter(hash); }
            return sample < 0.023f ? FlintClutter(hash) : BlockRegistry.Air;
        }

        if (ground == _sand)
        {
            if (sample < 0.012f) { return StoneClutter(hash); }
            return sample < 0.017f ? FlintClutter(hash) : BlockRegistry.Air;
        }

        if (ground == _gravel)
        {
            if (sample < 0.025f) { return StoneClutter(hash); }
            return sample < 0.037f ? FlintClutter(hash) : BlockRegistry.Air;
        }

        if (ground == _stone)
        {
            if (sample < 0.012f) { return StoneClutter(hash); }
            return sample < 0.015f ? FlintClutter(hash) : BlockRegistry.Air;
        }

        if (ground == _dirt)
        {
            if (sample < 0.006f) { return _stick; }
            return sample < 0.014f ? StoneClutter(hash) : BlockRegistry.Air;
        }

        return ground == _snow && sample < 0.006f ? StoneClutter(hash) : BlockRegistry.Air;
    }

    private ushort StoneClutter(uint hash) => ((hash >> 16) % 20u) switch
    {
        < 11u => _smallStone,
        < 17u => _smallStoneMid,
        _ => _smallStoneBig,
    };

    private ushort FlintClutter(uint hash) => ((hash >> 21) % 10u) < 7u
        ? _flint
        : _flintMid;

    /// <summary>
    /// Nejhustší možná varianta: jeden strom na tolik sloupců. Slouží k levnému odmítnutí.
    /// Musí být menší nebo rovno nejmenší hodnotě v tabulce hustot níž, jinak by se
    /// v nejhustším háji stromy ztrácely.
    /// </summary>
    private const int DensestChance = 32;

    /// <summary>Jemnost, na kterou se hash převádí. Mocnina dvou kvůli levnému zbytku.</summary>
    private const uint HashSteps = 4096;

    /// <summary>Stojí na téhle souřadnici strom? Čistě z pozice a seedu, bez stavu.</summary>
    private bool TryTreeAt(int worldX, int worldZ, TerrainGenerator generator, out Tree tree)
    {
        tree = default;

        // POŘADÍ KONTROL JE PODLE CENY, NE PODLE LOGIKY.
        //
        // Hash je zadarmo, lesní šum stojí tři oktávy, ColumnAt přes dvacet. Kdyby se
        // ptalo na biom hned, stálo by rozsazení stromů dvacetioktávový vzorek na každý
        // sloupec světa — a vzdálený terén, který prochází desítky tisíc sloupců
        // na dlaždici, by se stavěl několikanásobně dýl.
        //
        // Rovnoměrné číslo místo zbytku po dělení: díky němu jde předfiltrovat prahem
        // nejhustší možné varianty a přitom zůstane přesně rozdělené.
        uint hash = Hash(worldX, worldZ, _seed);
        uint sample = hash % HashSteps;

        if (sample >= HashSteps / DensestChance)
        {
            return false;
        }

        Column column = generator.ColumnAt(worldX, worldZ);

        // Pod hladinou ani na samém břehu strom neroste — vyrůstal by z vody.
        if (column.Surface < TerrainGenerator.SeaLevel + 2)
        {
            return false;
        }

        (int chance, TreeKind kind, bool cactus) = Profile(column.Biome, hash);

        if (chance == 0)
        {
            return false;
        }

        // Hustota se násobí lesním šumem: v háji roste hustě, na mýtině vůbec. Základní
        // šance je proto menší než dřív a v hájích ji tenhle násobek dožene.
        float density = cactus ? 1f : Forest(worldX, worldZ);
        if (density <= 0f)
        {
            return false;
        }

        if (sample >= HashSteps * density / chance)
        {
            return false;
        }

        // Na strmině strom nestojí — vypadal by, jako když visí ze stěny.
        if (Slope(generator, worldX, worldZ) > 1.6f)
        {
            return false;
        }

        int height = cactus
            ? 2 + (int)((hash >> 8) % 3)
            : kind switch
            {
                TreeKind.Oak => 9 + (int)((hash >> 8) % 5),
                TreeKind.Spruce => 13 + (int)((hash >> 8) % 7),
                TreeKind.Birch => 11 + (int)((hash >> 8) % 6),
                TreeKind.Maple => 10 + (int)((hash >> 8) % 4),
                _ => 9 + (int)((hash >> 8) % 3),
            };

        tree = new Tree(worldX, column.Surface + 1, worldZ, kind, height, cactus, hash);
        return true;
    }

    /// <summary>Druh a základní hustota stromu v biomu.</summary>
    private static (int Chance, TreeKind Kind, bool Cactus) Profile(Biome biome, uint hash) =>
        biome switch
        {
            // Čísla jsou „jeden strom na tolik sloupců" v nejhustším háji. U dubu vychází
            // strom zhruba každých šest bloků, což je souvislý les — a zároveň mez, kde
            // se to ještě vyplatí: naměřeno, že při jednom na 26 sloupců vyskočilo p95
            // z 12 na 20,7 ms, protože geometrie chunku narostla o čtvrtinu.
            // V MÍRNÉM PÁSMU ROSTOU DVA DRUHY, ne jeden.
            //
            // Les z jediného druhu vypadá jako tapeta — všechny koruny mají tutéž barvu
            // i obrys. Druh se proto losuje z hashe sloupce, takže je stálý (týž strom
            // vyjde po znovunačtení chunku stejně) a přitom se druhy promíchají strom po
            // stromu, ne po velkých plochách.
            //
            // Bříza k dubu do nížin, javor mezi smrky do kopců — červená koruna proti
            // jehličí je vidět a dělá to v krajině orientační bod.
            // JAVOR ROSTE I V NÍŽINÁCH, JEN VZÁCNĚ.
            //
            // Byl původně jen v kopcích, a to ho v praxi znamenalo nemít: naměřeno na ploše
            // 1400×1400 bloků kolem počátku vyšlo 2521 dubů, 1948 bříz — a 10 javorů.
            // Nebylo to špatným poměrem druhů, ale tím, že je Highlands u počátku vzácný
            // biom; javor z něj dědil jeho vzácnost a ještě se dělil se smrkem.
            //
            // Osm procent nížin z něj dělá nález, ne nedostupnost: v lese jich je pár,
            // takže si červené koruny pořád drží roli orientačního bodu.
            Biome.Plains => (38, Mix(hash, TreeKind.Oak, TreeKind.Birch, TreeKind.Maple, 0.42f, 0.08f), false),
            Biome.Savanna => (110, TreeKind.Acacia, false),
            Biome.Highlands => (48, Mix(hash, TreeKind.Spruce, TreeKind.Maple, 0.30f), false),
            Biome.Tundra => (130, TreeKind.Spruce, false),
            Biome.Desert => (900, TreeKind.Oak, true),
            _ => (0, TreeKind.Oak, false),
        };

    /// <summary>
    /// Vybere z hashe jeden ze dvou druhů. <paramref name="share"/> je podíl toho druhého.
    /// </summary>
    /// <remarks>
    /// Bere se <b>jiná část hashe</b> než na hustotu a výšku (ty sahají po spodním bajtu
    /// a po posunu o osm). Kdyby se braly tytéž bity, druh by korespondoval s výškou
    /// a v lese by byly všechny břízy nízké.
    /// </remarks>
    private static TreeKind Mix(uint hash, TreeKind common, TreeKind rare, float share) =>
        ((hash >> 19) & 0xFFu) < share * 256f ? rare : common;

    /// <summary>
    /// Vybere z hashe jeden ze tří druhů. Podíly platí pro druhý a třetí, zbytek je první.
    /// </summary>
    /// <remarks>
    /// Bere <b>tytéž bity</b> jako dvoudruhová varianta, jen je dělí na tři pásma. Kdyby si
    /// sáhla jinam, změnil by se druh každého stromu v už rozehraném světě.
    /// </remarks>
    private static TreeKind Mix(
        uint hash, TreeKind common, TreeKind second, TreeKind third, float secondShare, float thirdShare)
    {
        uint pick = (hash >> 19) & 0xFFu;

        if (pick < thirdShare * 256f)
        {
            return third;
        }

        return pick < (thirdShare + secondShare) * 256f ? second : common;
    }

    /// <summary>
    /// Kam se zapisují bloky stromu.
    /// </summary>
    /// <remarks>
    /// <para><b>Strom se staví dvakrát v životě světa</b> — jednou při generování chunku
    /// a podruhé, když vyroste ze zasazené sazenice. Obě cesty musí dát <b>týž tvar</b>,
    /// jinak by se les rozpadl na dva druhy stromů podle toho, jak vznikly.</para>
    ///
    /// <para>Rozhraní tvar odděluje od cíle: výpočet je jeden, zapisovat se dá do chunku
    /// (s ořezem podle hranic) i do živého světa. Je generické přes <c>struct</c>, aby se
    /// při generování světa nealokoval na každý strom jeden objekt navíc — sází se jich
    /// při streamingu tisíce.</para>
    /// </remarks>
    private interface ITreeSink
    {
        void Put(int worldX, int worldY, int worldZ, ushort block, bool onlyIntoAir);
    }

    /// <summary>Zápis do jednoho chunku. Co je mimo, si dopíše soused sám.</summary>
    private readonly struct ChunkSink(Chunk chunk, int baseX, int baseY, int baseZ) : ITreeSink
    {
        public void Put(int worldX, int worldY, int worldZ, ushort block, bool onlyIntoAir) =>
            TreePlanter.Put(chunk, worldX, worldY, worldZ, block, baseX, baseY, baseZ, onlyIntoAir);
    }

    /// <summary>
    /// Zapíše do chunku, respektive do zvoleného cíle, ty části stromu, které do něj padnou.
    ///
    /// <para><b>Kmen stojí uprostřed svého bloku</b> a je tenký po celé výšce — sází se jako
    /// celý blok tvaru sloupku. Uvnitř koruny ho obklopuje listí ze všech čtyř stran, takže
    /// rozdíl proti plnému bloku není vidět.</para>
    /// </summary>
    private void Stamp<TSink>(Tree tree, ref TSink sink)
        where TSink : struct, ITreeSink
    {
        ushort log = tree.Cactus ? _cactus : _log[(int)tree.Kind];
        ushort leaves = _leaves[(int)tree.Kind];

        // Kaktus zůstává z celých bloků: je tlustý schválně a v poušti stojí sám, takže není
        // k čemu ho přizpůsobovat.
        if (tree.Cactus)
        {
            for (int i = 0; i < tree.Height; i++)
            {
                sink.Put(tree.X, tree.BaseY + i, tree.Z, log, onlyIntoAir: false);
            }

            return;
        }

        int crown = tree.BaseY + tree.Height;

        (int crownBottom, int radius) = CrownShape(tree.Kind, crown, tree.Hash);

        for (int y = tree.BaseY; y <= crown; y++)
        {
            sink.Put(tree.X, y, tree.Z, log, onlyIntoAir: false);
        }

        StampCrown(tree, leaves, crown, crownBottom, radius, ref sink);
    }

    /// <summary>Kde koruna začíná a jak je široká. Musí sedět na <see cref="TryCanopyAt"/>.</summary>
    /// <remarks>
    /// Koruny jsou schválně statné. S poloměrem 2 a korunou vysokou čtyři patra vycházel
    /// z chomáčů listí spíš keřík na tyčce - listí je teď kreslené jako prostupující se
    /// karty, takže objem musí dát počet bloků, ne hustota jednoho z nich.
    /// </remarks>
    private static (int Bottom, int Radius) CrownShape(TreeKind kind, int crown, uint hash)
    {
        // KAŽDÝ STROM JINÝ. Dokud tady stály pevné hodnoty, měl každý dub přesně tutéž
        // korunu a les vypadal jako pole razítek. Rozsah je vždycky odvozený od druhu,
        // takže smrk zůstane štíhlý a vysoký a akácie placatá a široká - mění se jen
        // konkrétní kus uvnitř toho rozsahu.
        //
        // Bity se berou z různých míst hashe než výška stromu (ta bere 8..10), jinak by
        // vysoký strom měl vždycky i největší korunu.
        uint r = (hash >> 12) & 0xFFu;
        uint b = (hash >> 20) & 0xFFu;

        return kind switch
        {
            // Smrk: kužel, koruna sahá hluboko po kmeni.
            //
            // Poloměr byl dva až čtyři bloky a kužel z něj vycházel tak řídký, že se dal
            // prokouknout skrz - z dálky z něj zbyla tyčka s pár chomáči. Tři až šest
            // bloků z něj udělá hutný jehličnan; štíhlý zůstane, protože se pořád měří
            // proti délce koruny, která je u smrku nejdelší ze všech druhů.
            TreeKind.Spruce => (crown - (8 + (int)(b % 5u)), 3 + (int)(r % 4u)),

            // Akácie: placka nahoře, zato nejširší ze všech.
            TreeKind.Acacia => (crown - (1 + (int)(b % 3u)), 4 + (int)(r % 3u)),

            // Bříza: úzká a dlouhá koruna. Bříza má štíhlý habitus a poznávacím znamením
            // je právě to, že je vidět kmen — koruna proto sahá po kmeni níž než u dubu,
            // ale je o blok užší.
            TreeKind.Birch => (crown - (5 + (int)(b % 4u)), 2 + (int)(r % 3u)),

            // Javor: široká klenba na krátkém kmeni. Nejkratší koruna ze všech listnáčů,
            // zato se roztahuje do stran.
            TreeKind.Maple => (crown - (3 + (int)(b % 3u)), 4 + (int)(r % 3u)),

            // Dub: koule, nejčastější strom - proto nejširší rozptyl.
            _ => (crown - (4 + (int)(b % 5u)), 3 + (int)(r % 3u)),
        };
    }

    /// <summary>
    /// Koruna z dílků.
    ///
    /// <para>Pracuje se v dílcích, ne v blocích, takže obrys je dvakrát jemnější než dřív
    /// a listí navazuje na kmen. Tvar dělá poloměr, který se s výškou mění — kužel u smrku,
    /// useknutá koule u dubu, placka u akácie.</para>
    ///
    /// <para>Okrajové dílky se podle hashe vynechávají, aby koruna nebyla hladké těleso.
    /// Hashuje se souřadnice dílku, takže se na ní shodnou i dva sousední chunky.</para>
    /// </summary>
    private void StampCrown<TSink>(
        Tree tree, ushort leaves, int crown, int crownBottom, int radius, ref TSink sink)
        where TSink : struct, ITreeSink
    {
        // KORUNA JE Z CELÝCH BLOKŮ, NE Z DÍLKŮ.
        //
        // Dílek je půlka bloku, takže z koruny byla drobenka půlbloků — a co hůř, listí
        // se vešlo i do bloku, kde stojí kmen, jako druhá vrstva materiálu. Kmen tím byl
        // obalený listím a při kopání šlo obojí naráz.
        //
        // Teď je list celý blok a do bloku s kmenem se nedostane vůbec (Put sází jen do
        // vzduchu). Dojem spojité koruny nedělá jemnější mřížka, ale PŘESAH: list se kreslí
        // o kus větší, než je, takže se sousedi zanoří do sebe. Viz BlockDefinition.Overhang.
        int centreX = tree.X;
        int centreZ = tree.Z;

        int bottomPiece = crownBottom;
        // KORUNA SAHÁ NAD VRCHOL KMENE, ne jen k němu.
        //
        // Dokud koruna končila přesně na `crown`, byl nejvyšší blok kmene holý - kmen
        // vykukoval z listí jako tyčka a shora byl strom useknutý. Dvě patra navíc kmen
        // přikryjí a zároveň dají koruně vršek, který se dá obejít.
        const int AboveTrunk = 2;

        int topPiece = crown + AboveTrunk;
        int height = topPiece - bottomPiece;

        int maxRadius = radius;

        // Prořídnutí okraje. Počítá se jednou na strom, ne na každý blok.
        uint thinning = 8u + ((tree.Hash >> 4) % 25u);

        for (int py = bottomPiece; py <= topPiece; py++)
        {
            // Podíl výšky v koruně: 0 dole, 1 nahoře.
            float t = height <= 0 ? 0f : (py - bottomPiece) / (float)height;

            int levelRadius = tree.Kind switch
            {
                // Jehličnan se zužuje rovnoměrně vzhůru.
                TreeKind.Spruce => (int)MathF.Round(maxRadius * (1f - (0.75f * t))),

                // Akácie je placka: široká a skoro bez zúžení.
                TreeKind.Acacia => (int)MathF.Round(maxRadius * (1f - (0.30f * t))),

                // Bříza se zužuje vzhůru jako vejce postavené na špičku — dole úzká,
                // nejširší nad polovinou. Tím se liší od dubu, který je nejširší uprostřed.
                TreeKind.Birch => (int)MathF.Round(maxRadius * MathF.Sin((0.25f + (0.6f * t)) * MathF.PI)),

                // Javor je klenba: nejširší hned nad nasazením a nahoře se zaobluje.
                TreeKind.Maple => (int)MathF.Round(maxRadius * (1f - (0.55f * t * t))),

                // Dub je koule useknutá zdola: nejširší uprostřed. Mocnina je nižší než
                // dřív (2,1 místo 3,1), aby vršek nebyl špička - koruna má nad kmenem
                // ještě dvě patra a ta by jinak vyšla prázdná.
                _ => (int)MathF.Round(maxRadius * MathF.Sqrt(MathF.Max(0f, 1f - ((t - 0.45f) * (t - 0.45f) * 2.1f)))),
            };

            if (levelRadius <= 0)
            {
                continue;
            }

            for (int pz = centreZ - levelRadius; pz <= centreZ + levelRadius; pz++)
            {
                for (int px = centreX - levelRadius; px <= centreX + levelRadius; px++)
                {
                    int dx = px - centreX;
                    int dz = pz - centreZ;
                    int distance = (dx * dx) + (dz * dz);

                    if (distance > levelRadius * levelRadius)
                    {
                        continue;
                    }

                    // Okraj se prokousává, vnitřek zůstává celý — jinak by koruna byla hladké
                    // těleso a z jemné mřížky by nebylo nic vidět.
                    //
                    // NAD KMENEM SE NEPROKOUSÁVÁ. Vršek má málo bloků, takže by z něj
                    // po vynechání skoro poloviny zbyly osamocené kusy a kmen by koukal ven.
                    bool aboveTrunk = py > crown;

                    // HUSTOTA JE TAKY U KAŽDÉHO STROMU JINÁ: 8 až 32 % okraje se vynechá.
                    // Jeden strom je tak prořídlý a druhý hustý, i když mají stejný poloměr.
                    if (!aboveTrunk
                        && distance > (levelRadius - 1) * (levelRadius - 1)
                        && PieceHash(px, py, pz) % 100u < thinning)
                    {
                        continue;
                    }

                    sink.Put(px, py, pz, leaves, onlyIntoAir: true);
                }
            }
        }
    }

    /// <summary>Hash souřadnice bloku koruny. Musí být stabilní napříč chunky.</summary>
    private uint PieceHash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(_seed + 991) * 2166136261u;
            h = (h ^ (uint)x) * 16777619u;
            h = (h ^ (uint)y) * 16777619u;
            h = (h ^ (uint)z) * 16777619u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            return h ^ (h >> 15);
        }
    }

    /// <summary>
    /// Zapíše blok, pokud světová souřadnice padne dovnitř chunku.
    ///
    /// <para>Ořez je jediné místo, kde se řeší, že strom přesahuje. Části mimo chunk se
    /// tiše zahodí — soused si je zapíše sám.</para>
    /// </summary>
    private static void Put(
        Chunk chunk, int worldX, int worldY, int worldZ, ushort block,
        int baseX, int baseY, int baseZ, bool onlyIntoAir = false)
    {
        int x = worldX - baseX;
        int y = worldY - baseY;
        int z = worldZ - baseZ;

        if (x < 0 || y < 0 || z < 0 || x >= Chunk.Size || y >= Chunk.Size || z >= Chunk.Size)
        {
            return;
        }

        // Listí nepřepisuje kmen ani terén, jinak by strom stojící u skály koruně ukrojil
        // kámen a vznikly by v ní díry.
        if (onlyIntoAir && chunk.GetBlock(x, y, z) != BlockRegistry.Air)
        {
            return;
        }

        chunk.SetBlock(x, y, z, block);

        // Plný blok nesmí zdědit masku po dílcích, které tu stály předtím — zůstala by v něm díra.
        chunk.SetPieces(x, y, z, PieceMask.Full);
    }

    /// <summary>
    /// Zápis do živého světa. Sbírá bloky do dávky, sám nic nezapisuje.
    /// </summary>
    /// <remarks>
    /// <para><b>Do ničeho se nezařezává.</b> Při generování světa razítkuje kmen přes terén,
    /// protože terén je v tu chvíli čerstvý a nic v něm nestojí. Vyrostlý strom je jiný
    /// případ: kolem něj může být barák, který hráč postavil, a strom by mu do něj vyrostl.
    /// Proto se tady zapisuje výhradně do vzduchu — jedinou výjimkou je blok samotné
    /// sazenice, na jehož místo se staví pata kmene.</para>
    ///
    /// <para><b>Rozepsané místo se už nepřepíše.</b> Do chunku se zapisuje rovnou, takže
    /// listí kolem kmene narazí na hotovou kládu a přeskočí ji. Tady se ale sbírá dávka
    /// a svět se do posledního okamžiku nemění — kdyby se tahle úvaha vynechala, listí by
    /// se do dávky přidalo za kmen a při zápisu by ho přebilo. Ve hře to vypadalo tak, že
    /// vyrostlý dub měl pět klád místo dvanácti; zbytek kmene sežrala vlastní koruna.</para>
    /// </remarks>
    private readonly struct CollectSink(Dictionary<Vector3i, ushort> blocks) : ITreeSink
    {
        public void Put(int worldX, int worldY, int worldZ, ushort block, bool onlyIntoAir)
        {
            if (worldY < 0 || worldY >= TerrainGenerator.WorldHeight)
            {
                return;
            }

            var at = new Vector3i(worldX, worldY, worldZ);
            _ = onlyIntoAir;

            // Kmen se zapisuje před korunou. První zápis proto musí vyhrát, aby listí
            // uprostřed koruny nepřepsalo kmen.
            blocks.TryAdd(at, block);
        }
    }

    /// <summary>
    /// Vypěstuje strom ze sazenice, která na daném místě stojí.
    /// </summary>
    /// <param name="world">Svět, do kterého se zapisuje.</param>
    /// <param name="at">Souřadnice bloku sazenice.</param>
    /// <param name="touched">Chunky, které se změnily a je potřeba je přemeshovat.</param>
    /// <returns>Kolik bloků strom zabral. Nula znamená, že nevyrostl.</returns>
    /// <remarks>
    /// <para><b>Tvar je týž jako u generovaných stromů.</b> Sází to <see cref="Stamp"/>,
    /// jen s jiným cílem zápisu — kdyby si vyrostlý strom počítal korunu po svém, byly by
    /// v lese dva druhy dubů podle toho, jestli vyrostl, nebo se vygeneroval.</para>
    ///
    /// <para><b>Pod stropem nevyroste vůbec.</b> Kdyby se zapsalo jen to, co se vejde,
    /// zůstal by ve sklepě uříznutý pahýl. Když kmen nemá kam, sazenice počká — a až
    /// hráč strop odklidí, vyroste.</para>
    ///
    /// <para>Podoba stromu se losuje z <b>polohy</b>, takže je stálá: dvě sazenice vedle
    /// sebe dají dva různé stromy, ale táž sazenice na témž místě vždycky tentýž.</para>
    /// </remarks>
    public int Grow(VoxelWorld world, Vector3i at, out IReadOnlyCollection<Vector3i> touched)
        => GrowStage(world, at, MatureGrowthStage, out touched);

    /// <summary>
    /// Posune strom do jedné ze tří viditelných fází růstu.
    /// </summary>
    /// <remarks>
    /// Fáze jedna má tři až čtyři klády a kompaktní mladou korunu, fáze dvě zhruba dvě třetiny
    /// konečné výšky a řídkou korunu. Fáze tři používá původní plné razítko druhu. Předchozí
    /// koruna se odstraní pouze tam, kde pořád leží očekávané dřevo či listí; hráčova
    /// stavba se proto při růstu nikdy nesmaže.
    /// </remarks>
    public int GrowStage(
        VoxelWorld world,
        Vector3i at,
        int stage,
        out IReadOnlyCollection<Vector3i> touched)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfLessThan(stage, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stage, MatureGrowthStage);

        touched = [];

        ushort root = world.GetBlock(at.X, at.Y, at.Z);
        int kind = Array.IndexOf(_sapling, root);
        int previousStage = 0;

        if (kind < 0)
        {
            kind = Array.IndexOf(_log, root);
            previousStage = stage - 1;

            if (kind < 0 || previousStage < 1)
            {
                return 0;
            }
        }

        uint hash = Hash(at.X, at.Z, _seed + 3301) ^ ((uint)at.Y * 2654435761u);
        int height = GrowthHeight((TreeKind)kind, hash);
        var tree = new Tree(at.X, at.Y, at.Z, (TreeKind)kind, height, Cactus: false, hash);

        Dictionary<Vector3i, ushort> oldShape = previousStage == 0
            ? []
            : CollectGrowthShape(tree, previousStage);
        Dictionary<Vector3i, ushort> newShape = CollectGrowthShape(tree, stage);

        // Celý nový kmen musí mít místo. Koruna se smí kolem staveb oříznout, kmen ne:
        // napůl vyrostlý strom by vypadal jako poškozený save.
        foreach ((Vector3i position, ushort block) in newShape)
        {
            if (block != _log[kind])
            {
                continue;
            }

            ushort current = world.GetBlock(position.X, position.Y, position.Z);
            bool belongsToOldTree = oldShape.TryGetValue(position, out ushort old)
                && current == old;

            if (position != at && current != BlockRegistry.Air && !belongsToOldTree)
            {
                return 0;
            }
        }

        var writes = new List<(Vector3i At, ushort Block)>(oldShape.Count + newShape.Count);

        // Co z malé koruny v nové fázi není, musí zmizet. Maže se jen očekávaný blok
        // starého stromu — pokud ho hráč mezitím nahradil, jeho změna vyhrává.
        foreach ((Vector3i position, ushort oldBlock) in oldShape)
        {
            if (!newShape.ContainsKey(position)
                && world.GetBlock(position.X, position.Y, position.Z) == oldBlock)
            {
                writes.Add((position, BlockRegistry.Air));
            }
        }

        foreach ((Vector3i position, ushort block) in newShape)
        {
            ushort current = world.GetBlock(position.X, position.Y, position.Z);
            bool belongsToOldTree = oldShape.TryGetValue(position, out ushort old)
                && current == old;
            bool replaceableRoot = position == at && (IsSapling(current) || current == _log[kind]);

            if (current == BlockRegistry.Air || belongsToOldTree || replaceableRoot)
            {
                writes.Add((position, block));
            }
        }

        if (writes.Count == 0)
        {
            return 0;
        }

        touched = world.SetBlocks(writes);
        return writes.Count;
    }

    /// <summary>Je blok jedním z kmenů, které může vytvořit růst sazenice?</summary>
    public bool IsTreeLog(ushort block) =>
        block != BlockRegistry.Air && Array.IndexOf(_log, block) >= 0;

    /// <summary>Vrátí konečnou výšku centrálního kmene pro sazenici na daném místě.</summary>
    public int MatureTrunkHeight(Vector3i at, ushort sapling)
    {
        int kind = Array.IndexOf(_sapling, sapling);
        return kind < 0 ? 0 : GrowthTree(at, kind).Height;
    }

    /// <summary>Rozpozná osiřelou malou či střední růstovou fázi podle jejího přesného tvaru.</summary>
    /// <remarks>
    /// Starší save mohl obsahovat mladý strom bez uloženého odpočtu. Kontroluje se pata kmene,
    /// jeho přesná výška a většina očekávané koruny, takže se za rozpracovaný růst nepovažuje
    /// dospělý přírodní strom ani hráčův sloup z klád.
    /// </remarks>
    public bool TryGrowthStage(VoxelWorld world, Vector3i at, out int stage)
    {
        ArgumentNullException.ThrowIfNull(world);
        stage = 0;

        ushort root = world.GetBlock(at.X, at.Y, at.Z);
        int kind = Array.IndexOf(_log, root);
        if (kind < 0 || world.GetBlock(at.X, at.Y - 1, at.Z) == root)
        {
            return false;
        }

        Tree tree = GrowthTree(at, kind);

        for (int candidate = MatureGrowthStage - 1; candidate >= 1; candidate--)
        {
            Dictionary<Vector3i, ushort> shape = CollectGrowthShape(tree, candidate);
            int expectedLogs = shape.Count(pair => pair.Value == root);
            int actualLogs = 0;

            while (actualLogs < MaxTreeHeight
                   && world.GetBlock(at.X, at.Y + actualLogs, at.Z) == root)
            {
                actualLogs++;
            }

            if (actualLogs != expectedLogs)
            {
                continue;
            }

            int expectedLeaves = 0;
            int matchingLeaves = 0;
            foreach ((Vector3i position, ushort block) in shape)
            {
                if (block == root)
                {
                    continue;
                }

                expectedLeaves++;
                if (world.GetBlock(position.X, position.Y, position.Z) == block)
                {
                    matchingLeaves++;
                }
            }

            // Původní první fáze měla jen pět až šest listů. Přijmout u krátkého kmene
            // tři shody je záměrná migrace těchto starých řídkých stromků; u střední fáze
            // zůstává přísná dvoutřetinová shoda, aby se nechytil běžný přírodní strom.
            bool recognizable = candidate == 1
                ? matchingLeaves >= 3
                : expectedLeaves > 0 && matchingLeaves * 3 >= expectedLeaves * 2;

            if (recognizable)
            {
                stage = candidate;
                return true;
            }
        }

        return false;
    }

    private static int GrowthHeight(TreeKind kind, uint hash) => kind switch
    {
        TreeKind.Oak => 9 + (int)((hash >> 8) % 5),
        TreeKind.Spruce => 13 + (int)((hash >> 8) % 7),
        TreeKind.Birch => 11 + (int)((hash >> 8) % 6),
        TreeKind.Maple => 10 + (int)((hash >> 8) % 4),
        _ => 9 + (int)((hash >> 8) % 3),
    };

    private Tree GrowthTree(Vector3i at, int kind)
    {
        uint hash = Hash(at.X, at.Z, _seed + 3301) ^ ((uint)at.Y * 2654435761u);
        int height = GrowthHeight((TreeKind)kind, hash);
        return new Tree(at.X, at.Y, at.Z, (TreeKind)kind, height, Cactus: false, hash);
    }

    private Dictionary<Vector3i, ushort> CollectGrowthShape(Tree tree, int stage)
    {
        var blocks = new Dictionary<Vector3i, ushort>();
        var sink = new CollectSink(blocks);

        if (stage >= MatureGrowthStage)
        {
            Stamp(tree, ref sink);
            return blocks;
        }

        ushort log = _log[(int)tree.Kind];
        ushort leaves = _leaves[(int)tree.Kind];
        int height = stage == 1
            ? 3 + (int)((tree.Hash >> 4) & 1u)
            : Math.Max(6, (int)MathF.Round(tree.Height * 0.64f));

        for (int y = 0; y <= height; y++)
        {
            sink.Put(tree.X, tree.BaseY + y, tree.Z, log, onlyIntoAir: false);
        }

        int crown = tree.BaseY + height;
        if (stage == 1)
        {
            // Mladý strom má tři kompaktní vrstvy. Horní část kmene je ze všech stran
            // zakrytá, ale koruna je pořád výrazně menší než u střední a dospělé fáze.
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0 && dy <= 0)
                        {
                            continue;
                        }

                        bool corner = Math.Abs(dx) == 1 && Math.Abs(dz) == 1;
                        if (dy == 1 && corner
                            && ((PieceHash(tree.X + dx, crown + dy, tree.Z + dz) ^ tree.Hash) & 1u) == 0u)
                        {
                            continue;
                        }

                        sink.Put(tree.X + dx, crown + dy, tree.Z + dz, leaves, onlyIntoAir: true);
                    }
                }
            }

            sink.Put(tree.X, crown + 2, tree.Z, leaves, onlyIntoAir: true);

            return blocks;
        }

        // Střední strom: tři řídká patra, poloměr nejvýš dva. Okraj se probírá hashem,
        // takže koruna má méně listí než dospělá a nepůsobí jako hotový strom na krátké noze.
        for (int dy = -2; dy <= 2; dy++)
        {
            int radius = Math.Abs(dy) == 2 ? 1 : 2;
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0 && dy <= 0)
                    {
                        continue;
                    }

                    int distance = Math.Abs(dx) + Math.Abs(dz);
                    if (distance > radius + 1)
                    {
                        continue;
                    }

                    uint leafHash = PieceHash(tree.X + dx, crown + dy, tree.Z + dz) ^ tree.Hash;
                    if (distance >= radius && leafHash % 100u < 34u)
                    {
                        continue;
                    }

                    sink.Put(tree.X + dx, crown + dy, tree.Z + dz, leaves, onlyIntoAir: true);
                }
            }
        }

        return blocks;
    }

    /// <summary>Je ten blok sazenice?</summary>
    public bool IsSapling(ushort block) =>
        block != BlockRegistry.Air && Array.IndexOf(_sapling, block) >= 0;

    /// <summary>
    /// Popis koruny pro vzdálený terén: kde začíná, kde končí a jak je široká.
    /// </summary>
    public readonly record struct Canopy(int BottomY, int TopY, int Radius, ushort Leaves);

    /// <summary>
    /// Stojí na téhle souřadnici strom, a jak vypadá jeho koruna?
    ///
    /// <para>Používá vzdálený terén, který kreslí korunu jako jednoduchý kvádr. Ptá se
    /// <b>tímtéž výpočtem</b>, jakým se sázejí skutečné stromy, takže na hranici dohledu
    /// strom nepřeskočí jinam — jen se z kvádru stane koruna z bloků.</para>
    ///
    /// <para>Kaktusy se vracejí taky: v poušti jsou jediné, co ční nad zem.</para>
    /// </summary>
    public bool TryCanopyAt(int worldX, int worldZ, TerrainGenerator generator, out Canopy canopy)
    {
        canopy = default;

        if (!TryTreeAt(worldX, worldZ, generator, out Tree tree))
        {
            return false;
        }

        int crown = tree.BaseY + tree.Height;

        if (tree.Cactus)
        {
            canopy = new Canopy(tree.BaseY, crown, 0, _cactus);
            return true;
        }

        // Týž výpočet, jaký razítkuje Stamp — jinak by koruna na hranici dohledu změnila tvar.
        (int bottom, int radius) = CrownShape(tree.Kind, crown, tree.Hash);

        canopy = new Canopy(bottom, crown, radius, _leaves[(int)tree.Kind]);
        return true;
    }

    /// <summary>
    /// Zapojení lesa na dané souřadnici: 0 = mýtina, 1 = hustý háj.
    ///
    /// <para>Je veřejná schválně: <b>tímtéž číslem se obarvuje vzdálený terén</b>, takže
    /// les je vidět i tam, kam už jednotlivé stromy nikdo nekreslí. Kdyby si LOD počítal
    /// vlastní, rozešly by se a na hranici dohledu by les buď zmizel, nebo přiskočil.</para>
    /// </summary>
    public float Forest(int worldX, int worldZ)
    {
        // Práh nad nulou: bez něj by mýtina nikdy nebyla úplně prázdná, jen řidší.
        float value = _forest.Fractal(worldX * 0.0021f, worldZ * 0.0021f, octaves: 3);
        return Math.Clamp((value - 0.02f) * 1.45f, 0f, 1f);
    }

    /// <summary>
    /// Blok, kterým se ve vzdáleném terénu obarví zapojený les, nebo vzduch.
    ///
    /// <para>Práh je vysoko: obarví se jen tam, kde je les opravdu hustý. Řídký porost
    /// z dálky splyne se zemí a obarvit ho by znamenalo, že celý biom zezelená.</para>
    /// </summary>
    public ushort FarCanopyBlock(Biome biome, int worldX, int worldZ)
    {
        TreeKind kind;

        // DRUH SE LOSUJE STEJNĚ JAKO PŘI SÁZENÍ, jinak by se barva koruny na hranici LOD
        // změnila — vzdálený les by byl dubový a po přiblížení by z něj byla polovina bříz.
        uint hash = Hash(worldX, worldZ, _seed);

        switch (biome)
        {
            case Biome.Plains:
                // Musí sedět na TryTreeAt, jinak by se barva vzdáleného lesa rozešla s tím,
                // co v něm po přiblížení opravdu stojí.
                kind = Mix(hash, TreeKind.Oak, TreeKind.Birch, TreeKind.Maple, 0.42f, 0.08f);
                break;

            case Biome.Highlands:
                kind = Mix(hash, TreeKind.Spruce, TreeKind.Maple, 0.30f);
                break;

            case Biome.Tundra:
                kind = TreeKind.Spruce;
                break;

            case Biome.Savanna:
                // Savana je i v „lese" řídká, takže se nikdy nezavře do souvislé plochy.
                return BlockRegistry.Air;

            default:
                return BlockRegistry.Air;
        }

        return Forest(worldX, worldZ) > 0.55f ? _leaves[(int)kind] : BlockRegistry.Air;
    }

    /// <summary>Sklon terénu v okolí sloupce. Na strmině stromy nerostou.</summary>
    private static float Slope(TerrainGenerator generator, int worldX, int worldZ)
    {
        int here = generator.ColumnAt(worldX, worldZ).Surface;

        int dx = Math.Abs(generator.ColumnAt(worldX + 1, worldZ).Surface - here);
        int dz = Math.Abs(generator.ColumnAt(worldX, worldZ + 1).Surface - here);

        return Math.Max(dx, dz);
    }

    /// <summary>Hash pozice. Musí být stabilní: na něm stojí, že chunky vidí tentýž strom.</summary>
    private static uint Hash(int x, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed * 2166136261u;
            h = (h ^ (uint)x) * 16777619u;
            h = (h ^ (uint)z) * 16777619u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            return h ^ (h >> 15);
        }
    }

    private readonly record struct Tree(int X, int BaseY, int Z, TreeKind Kind, int Height, bool Cactus, uint Hash);

    /// <summary>Nejvyšší možný strom. Streamer podle toho ví, kam až koruna může sahat.</summary>
    public static int MaxTreeHeight => MaxHeight;
}
