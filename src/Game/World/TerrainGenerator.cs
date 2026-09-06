using System.Collections.Concurrent;
using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Game.Blocks;
using Tesseris.Game.Modding;
using Tesseris.Game.World.Definitions;

namespace Tesseris.Game.World;

/// <summary>
/// Typ krajiny. Určuje <b>jen skladbu materiálů</b>, ne tvar terénu.
///
/// <para>To rozdělení je podstatné a Tesseris ho do 28. 7. 2026 nemělo. Když biom
/// rozhoduje i o tvaru, vyjde z toho „hora" jako biom — a pak je na kopci v poušti kámen,
/// protože kopec přebil poušť. Tvar terénu vzniká z kontinentů, členitosti a hřebenů;
/// biom se do hotového tvaru teprve obarví. Stejně to dělá Minecraft od 1.18.</para>
/// </summary>
public enum Biome
{
    /// <summary>Mírné a vlhké: tráva, pod ní hlína.</summary>
    Plains,

    /// <summary>Teplé a polosuché: vyschlá tráva.</summary>
    Savanna,

    /// <summary>Horké a suché: písek na pískovci.</summary>
    Desert,

    /// <summary>Nejhorší sucho: červený písek. Leží uvnitř pouští jako jejich jádro.</summary>
    Badlands,

    /// <summary>Studené nížiny: tenká vrstva sněhu na hlíně.</summary>
    Tundra,

    /// <summary>Chladné vysočiny pod hranicí lesa: tráva na kameni, mělká ornice.</summary>
    Highlands,

    /// <summary>Studené vrcholky: sníh na skále.</summary>
    SnowyPeaks,

    /// <summary>Vysoké, ale ne dost studené na sníh: holá skála.</summary>
    StonyPeaks,

    /// <summary>Nejvyšší a nejstudenější: ledový příkrov.</summary>
    FrozenPeaks,
}

/// <summary>
/// Výška povrchu a typ krajiny na jednom vodorovném sloupci.
/// </summary>
/// <param name="Surface">Výška v blocích, tedy zaokrouhlená.</param>
/// <param name="Exact">
/// Výška před zaokrouhlením. Slouží <b>jen</b> k výpočtu sklonu.
///
/// <para>Ze zaokrouhlených výšek sklon spočítat nejde: na mírném svahu skáče rozdíl
/// sousedů mezi 1 a 2, takže práh „strmé stěny" se přepíná blok po bloku a povrch vyjde
/// jako zrnitá směs kamene a trávy. Zadavatel to popsal jako „z dálky to vypadá divně,
/// jak se to tam kombinuje".</para>
/// </param>
public readonly record struct Column(int Surface, float Exact, Biome Biome);

/// <summary>
/// Generátor terénu ze simplexového šumu.
///
/// Třída je po vytvoření <b>neměnná a bezpečná pro souběh</b> — všechna pole jsou readonly
/// a šum si nic nepamatuje. Díky tomu ji můžou worker vlákna volat současně bez zámků.
///
/// Svět je vysoký <see cref="WorldHeight"/> bloků a vodorovně neomezený. Výška povrchu je
/// čistá funkce souřadnic, takže se stejný svět vygeneruje pokaždé stejně a nemusí se ukládat.
/// </summary>
public sealed class TerrainGenerator
{
    /// <summary>
    /// Kolik chunků na výšku má svět. 32 chunků po 32 blocích dává **1024 bloků** od
    /// nejspodnější vrstvy po strop oblohy.
    ///
    /// <para>Zadání znělo „1000 kostek". Použito 1024, protože výška musí být násobek
    /// velikosti chunku; 1000 by znamenalo poslední patro nedopočítané.</para>
    /// </summary>
    public const int WorldHeightChunks = 32;

    /// <summary>Výška světa v blocích.</summary>
    public const int WorldHeight = WorldHeightChunks * Chunk.Size;

    /// <summary>Nejnižší a nejvyšší úroveň, kde se prokopávají jeskyně.</summary>
    private const int CaveBottom = 6;
    private const int CaveTop = 300;

    /// <summary>
    /// Kolem téhle hloubky přechází kámen v deepslate. Přesná hranice se rozvlní šumem,
    /// aby to nebyl vodorovný řez.
    /// </summary>
    private const int DeepslateLine = 190;

    /// <summary>Referenční úroveň krajiny. Roli mořské hladiny hraje jen jako záchytný bod.</summary>
    private const int BaseHeight = 320;

    private const int MinSurface = 4;
    private const int MaxSurface = WorldHeight - 8;

    /// <summary>
    /// Mořská hladina. Všechen vzduch pod ní se zaplaví vodou.
    ///
    /// <para><b>Číslo je vybrané měřením, ne od oka.</b> Rozložení výšek povrchu na ploše
    /// 60 × 60 km: p10 = 282, p25 = 305, p50 = 335, p90 = 415. Při hladině 305 leží pod
    /// vodou <b>25 % světa</b> — dost na moře i jezera, ale krajina zůstane krajinou.
    /// Hladina 320 by zatopila 37 %, hladina 280 jen 9 % a byly by z toho louže.</para>
    /// </summary>
    public const int SeaLevel = 305;

    /// <summary>Jak vysoko nad hladinu sahá pláž. Nad tím už roste tráva.</summary>
    private const int BeachHeight = 3;

    /// <summary>
    /// Rozpočet výšky. Součet amplitud musí zůstat pod <see cref="MaxSurface"/>, jinak by se
    /// vrcholky ořezaly na plochu — a oříznutá hora vypadá jako stůl.
    /// 320 + 110 + 70 + 470 = 970, tedy 46 bloků pod stropem 1016.
    ///
    /// <para><b>S amplitudou se musí roztáhnout i vlnová délka.</b> Kdyby se jen zvedla,
    /// zůstane stejná vodorovná vzdálenost a ze svahů budou svislé stěny. Hory jsou proto
    /// zároveň vyšší i širší — půl vlny hřebene měří přes 1100 bloků, takže svah vychází
    /// kolem 24 stupňů.</para>
    /// </summary>
    private const float ContinentAmplitude = 110f;
    private const float HillAmplitude = 70f;
    private const float MountainAmplitude = 470f;

    /// <summary>
    /// Drobný reliéf. Bez něj je povrch hladká funkce zaokrouhlená na bloky a vzniknou
    /// z toho <b>pravidelné vrstevnicové schody jako na vojenské mapě</b> — nejjemnější
    /// detail v terénu měl vlnovou délku 57 bloků, takže se výška měnila tak pomalu,
    /// že každý schod vytvořil dlouhou souvislou linii.
    ///
    /// <para>Tahle vrstva má vlnovou délku kolem 18 bloků a amplitudu pár bloků, takže
    /// vrstevnice roztrhá, aniž by změnila tvar krajiny.</para>
    /// </summary>
    private const float DetailAmplitude = 3.0f;

    /// <summary>O kolik bloků se souřadnice ohne, než se ochutná výška.</summary>
    private const float WarpStrength = 120f;

    /// <summary>
    /// Sklon (převýšení na blok), od kterého se na povrchu neudrží ornice a prosvítá podloží.
    /// 1,1 odpovídá zhruba 48 stupňům.
    /// </summary>
    private const float SteepSlope = 1.1f;

    /// <summary>Nad tímhle sklonem se neudrží ani suť a je vidět holá skála.</summary>
    private const float VerySteepSlope = 2.0f;

    /// <summary>
    /// Šířka pole sloupců včetně jednoho okraje na každé straně. Okraj je potřeba na sklon:
    /// ten se počítá z rozdílu sousedů, a bez lemu by u stěny chunku chyběl.
    /// </summary>
    private const int PaddedSize = Chunk.Size + 2;

    private readonly SimplexNoise _warpX;
    private readonly SimplexNoise _warpZ;
    private readonly SimplexNoise _continents;
    private readonly SimplexNoise _hills;
    private readonly SimplexNoise _detail;
    private readonly SimplexNoise _landform;
    private readonly SimplexNoise _roughness;
    private readonly SimplexNoise _mountainRange;
    private readonly SimplexNoise _ridges;
    private readonly SimplexNoise _temperature;
    private readonly SimplexNoise _humidity;
    private readonly SimplexNoise _weird;
    private readonly SimplexNoise _edge;
    private readonly SimplexNoise _caveA;
    private readonly SimplexNoise _caveB;

    // Volitelné: bez něj se generuje holý terén. Viz EnableTrees.
    private TreePlanter? _trees;

    private readonly ushort _stone;
    private readonly ushort _coalOre;
    private readonly ushort _ironOre;
    private readonly ushort _copperOre;
    private readonly ushort _goldOre;
    private readonly ushort _lapisOre;
    private readonly ushort _redstoneOre;
    private readonly ushort _diamondOre;
    private readonly ushort _emeraldOre;
    private readonly ushort _dirt;
    private readonly ushort _grass;
    private readonly ushort _sand;
    private readonly ushort _dryGrass;
    private readonly ushort _sandstone;
    private readonly ushort _redSand;
    private readonly ushort _gravel;
    private readonly ushort _snow;
    private readonly ushort _deepslate;
    private readonly ushort _ice;
    private readonly ushort _terracotta;
    private readonly ushort _water;

    private readonly ModWorldGenerationPipeline? _modWorldGeneration;
    private readonly ModWorldRuntime? _worldRuntime;
    private readonly BlockRegistry _registry;
    private readonly StructureGenerator _structures;

    private readonly ConcurrentDictionary<Vector2i, ColumnBlock> _columnCache = new();

    public TerrainGenerator(
        BlockRegistry registry,
        int seed,
        ModWorldGenerationPipeline? modWorldGeneration = null,
        ModWorldRuntime? worldRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        Seed = seed;
        _registry = registry;
        _modWorldGeneration = modWorldGeneration;
        _worldRuntime = worldRuntime;
        _structures = new StructureGenerator(registry, seed);

        // Každá vrstva má vlastní seed, jinak by se tvary provázaly a hory by seděly
        // přesně tam, kde je poušť.
        _warpX = new SimplexNoise(seed + 6131);
        _warpZ = new SimplexNoise(seed + 8677);
        _continents = new SimplexNoise(seed);
        _hills = new SimplexNoise(seed + 3301);
        _detail = new SimplexNoise(seed + 4483);
        _landform = new SimplexNoise(seed + 6949);
        _roughness = new SimplexNoise(seed + 5623);
        _mountainRange = new SimplexNoise(seed + 1327);
        _ridges = new SimplexNoise(seed + 7433);
        _temperature = new SimplexNoise(seed + 7919);
        _humidity = new SimplexNoise(seed + 4211);
        _weird = new SimplexNoise(seed + 5779);
        _edge = new SimplexNoise(seed + 2081);
        _caveA = new SimplexNoise(seed + 9137);
        _caveB = new SimplexNoise(seed + 2699);

        _stone = registry.IndexOf("tesseris:stone");
        _coalOre = registry.IndexOf("tesseris:coal_ore");
        _ironOre = registry.IndexOf("tesseris:iron_ore");
        _copperOre = registry.IndexOf("tesseris:copper_ore");
        _goldOre = registry.IndexOf("tesseris:gold_ore");
        _lapisOre = registry.IndexOf("tesseris:lapis_ore");
        _redstoneOre = registry.IndexOf("tesseris:redstone_ore");
        _diamondOre = registry.IndexOf("tesseris:diamond_ore");
        _emeraldOre = registry.IndexOf("tesseris:emerald_ore");
        _dirt = registry.IndexOf("tesseris:dirt");
        _grass = registry.IndexOf("tesseris:grass");
        _sand = registry.IndexOf("tesseris:sand");
        _dryGrass = registry.IndexOf("tesseris:dry_grass");
        _sandstone = registry.IndexOf("tesseris:sandstone");
        _redSand = registry.IndexOf("tesseris:red_sand");
        _gravel = registry.IndexOf("tesseris:gravel");
        _snow = registry.IndexOf("tesseris:snow");
        _deepslate = registry.IndexOf("tesseris:deepslate");
        _ice = registry.IndexOf("tesseris:ice");
        _terracotta = registry.IndexOf("tesseris:terracotta");
        _water = registry.IndexOf("tesseris:water");
    }

    public TerrainGenerator(
        BlockRegistry registry,
        int seed,
        IEnumerable<RegisteredWorldGenerationHook> worldGenerationHooks)
        : this(registry, seed, new ModWorldGenerationPipeline(registry, worldGenerationHooks))
    {
    }

    public int Seed { get; }

    /// <summary>Výška povrchu na dané vodorovné souřadnici.</summary>
    public int SurfaceHeight(int worldX, int worldZ) => ColumnAt(worldX, worldZ).Surface;

    /// <summary>Typ krajiny na dané vodorovné souřadnici.</summary>
    public Biome BiomeAt(int worldX, int worldZ) => ColumnAt(worldX, worldZ).Biome;

    /// <summary>
    /// Výška i biom jedním výpočtem.
    ///
    /// <para>Dohromady schválně: biom závisí na výšce a na tom, jestli sloupec patří hoře.
    /// Když se počítaly zvlášť, spočítala se výška dvakrát — jednou pro terén a podruhé
    /// uvnitř určování biomu.</para>
    ///
    /// <para><b>Z čeho se výška skládá.</b> Čtyři pásma, každé s jiným úkolem:
    /// kontinenty dělají nížiny a vysočiny, členitost říká, kde je krajina placka a kde
    /// rozháraná, kopce dodají střední zvlnění a hřebeny hory. Před vším se souřadnice
    /// ohnou jiným šumem, takže vrstevnice nemají hladký, „narýsovaný" tvar.</para>
    /// </summary>
    public Column ColumnAt(int worldX, int worldZ)
    {
        if (_worldRuntime is not null)
        {
            Tesseris.ModApi.ModWorldColumn sampled = _worldRuntime.SampleColumn(worldX, worldZ);
            Tesseris.ModApi.ResourceId biomeId = _worldRuntime.SampleBiome(
                worldX,
                sampled.SurfaceY,
                worldZ).Id;
            return new Column(sampled.SurfaceY, sampled.SurfaceY, CompatibilityBiome(biomeId));
        }

        if (_modWorldGeneration?.TrySampleColumn(
                Seed,
                worldX,
                worldZ,
                out Tesseris.ModApi.ModWorldColumn modColumn) == true)
        {
            if ((uint)modColumn.SurfaceY >= WorldHeight)
            {
                throw new InvalidDataException(
                    $"A mod world generator returned surface Y {modColumn.SurfaceY} for [{worldX}, {worldZ}]; "
                    + $"the supported range is 0..{WorldHeight - 1}.");
            }

            // Vanilla-only systems still ask for a biome. Replacement generators own actual chunk
            // materials and decoration, so Plains is only a neutral compatibility value here.
            return new Column(modColumn.SurfaceY, modColumn.SurfaceY, Biome.Plains);
        }

        // Ohnutí souřadnic (domain warp). Nejlevnější způsob, jak sundat z terénu
        // matematický vzhled: každý tvar níž se tím pokroutí, jako by ho obrousila eroze.
        const float WarpFrequency = 0.00055f;
        float wx = worldX + (_warpX.Sample(worldX * WarpFrequency, worldZ * WarpFrequency) * WarpStrength);
        float wz = worldZ + (_warpZ.Sample(worldX * WarpFrequency, worldZ * WarpFrequency) * WarpStrength);

        // Kontinenty. Vlnová délka kolem 10 000 bloků — díky tomu existují velké nížiny
        // a vysočiny, ne jen lokální hrbolky.
        float continent = _continents.Fractal(wx * 0.00010f, wz * 0.00010f, octaves: 3);

        // Členitost. Násobí kopce, takže někde je rovina a jinde rozbitý terén.
        // Bez ní má celý svět všude stejnou drsnost a působí uměle.
        float roughness = Saturate((_roughness.Sample(wx * 0.00030f, wz * 0.00030f) * 0.6f) + 0.5f);

        float hills = _hills.Fractal(wx * 0.0022f, wz * 0.0022f, octaves: 4);

        // Drobný reliéf. Sahá se na nezkroucené souřadnice: ohnutí má vlnovou délku přes
        // 1800 bloků a na detailu o 18 blocích by se neprojevilo, jen by stálo výkon.
        float detail = _detail.Fractal(worldX * 0.022f, worldZ * 0.022f, octaves: 2);

        // Kde vůbec hory jsou. Bez téhle masky pokrývají hřebeny rovnoměrně celý svět
        // a vznikne z nich pravidelná síť valů.
        float range = _mountainRange.Fractal(wx * 0.00018f, wz * 0.00018f, octaves: 2);
        float mountain = _ridges.Ridged(wx * 0.00045f, wz * 0.00045f, octaves: 6)
                       * SmoothStep(0.06f, 0.50f, range);

        // Typ krajiny. Mění se s ním POVAHA terénu, ne jen výška — bez toho má celý svět
        // stejnou drsnost, stejnou četnost hřebenů i stejný detail, a působí jednotvárně
        // i s kontinenty a horami. Myšlenka je převzatá z Vintage Story („landforms").
        Landform form = LandformAt(wx, wz);

        float height = BaseHeight
                     + (continent * ContinentAmplitude)
                     + (hills * HillAmplitude * roughness * form.Hills)
                     + (mountain * MountainAmplitude * form.Mountains)
                     + (detail * DetailAmplitude * form.Detail);


        int surface = Math.Clamp((int)height, MinSurface, MaxSurface);

        return new Column(surface, height, ClassifyBiome(worldX, worldZ, wx, wz, surface));
    }

    /// <summary>
    /// Výběr biomu z klimatu. Vstupem je teplota, vlhkost a nadmořská výška — <b>ne</b> tvar
    /// terénu.
    ///
    /// <para>Poušť tedy zůstane pouští i na kopci. Že je kopec kamenný, řeší až povrchová
    /// pravidla podle sklonu (<see cref="SurfaceBlock"/>), a ta se na biom neptají. Do
    /// 28. 7. 2026 to bylo slepené: „hora" byla biom a přebila poušť, takže duny měly
    /// kamenné vrcholky.</para>
    /// </summary>
    private Biome ClassifyBiome(int worldX, int worldZ, float wx, float wz, int surface)
    {
        // ROZTŘESENÍ HRANICE SE DĚLÁ OHNUTÍM SOUŘADNIC, NE PŘIČTENÍM K HODNOTĚ.
        //
        // Dřív se k teplotě i vlhkosti přičítal šum s amplitudou 0,14, zatímco prahy mezi
        // biomy jsou od sebe kolem 0,3. Kolem každého prahu tím vznikl pás široký stovky
        // bloků, kde se biom přepínal blok po bloku — z hranice byl maskáč a uvnitř
        // roztroušené ostrůvky cizího biomu. Přesně tak vznikl ten bílý flek tundry
        // uprostřed zelené nížiny.
        //
        // Ohnutí souřadnic dělá totéž, co se od roztřesení čekalo — hranice se zvlní
        // a přestane být narýsovaná čára — ale zůstane hranicí. Dvě měřítka: hrubé
        // zvlnění v řádu stovek bloků a jemné v řádu desítek.
        float warpX = (_edge.Sample(worldX * 0.0016f, worldZ * 0.0016f) * 170f)
                    + (_edge.Sample((worldX * 0.011f) + 51f, (worldZ * 0.011f) - 23f) * 26f);

        float warpZ = (_edge.Sample((worldX * 0.0016f) + 137f, (worldZ * 0.0016f) + 79f) * 170f)
                    + (_edge.Sample((worldX * 0.011f) - 91f, (worldZ * 0.011f) + 64f) * 26f);

        float cx = wx + warpX;
        float cz = wz + warpZ;

        // Výška ochlazuje. Díky tomu jsou vrcholky studené i uprostřed teplého pásma —
        // proto může nad pouští stát zasněžený štít, což je na pohled ta nejvýraznější
        // kombinace, kterou svět nabízí.
        // Nadmořská výška: 0 na úrovni krajiny, 1 na nejvyšších štítech. Ve světě vysokém
        // 1024 bloků je tenhle rozsah přes 600 bloků, takže výškové pásmo je konečně
        // znát — v původním světě vysokém 128 se všechno mačkalo do třiceti bloků.
        float altitude = Saturate((surface - BaseHeight) / 620f);

        // TEPLOTA MÁ MNOHEM VĚTŠÍ MĚŘÍTKO NEŽ VLHKOST. To je celý trik.
        //
        // Dřív měly obě zhruba stejnou vlnovou délku (kolem 2500 a 3300 bloků) a k tomu
        // jen dvě oktávy. Mapa pak vypadala jako maskáč: všechny biomy stejně velké
        // placky poházené rovnoměrně, a hlavně tundra hned vedle pouště. To v přírodě
        // nenastane, protože teplota se mění po tisících kilometrů, kdežto srážky
        // po stovkách.
        //
        // Teplota má teď vlnovou délku kolem 12 500 bloků, takže vzniknou velké teplé
        // a studené kraje, a vlhkost 4000, takže se uvnitř nich střídá sucho a vlhko.
        // Oktáv je pět místo dvou — hranice tím přestane být hladký ovál a rozstřepí se.
        float temperature = _temperature.Fractal(cx * 0.00008f, cz * 0.00008f, octaves: 5)
                          - (altitude * 1.35f);

        float humidity = _humidity.Fractal(cx * 0.00025f, cz * 0.00025f, octaves: 5);

        // Vysoko nahoře rozhoduje jen teplota. Tři pásma nad sebou: skála, sníh, led.
        if (altitude > 0.46f)
        {
            return temperature switch
            {
                < -1.05f => Biome.FrozenPeaks,
                < -0.55f => Biome.SnowyPeaks,
                _ => Biome.StonyPeaks,
            };
        }

        // Vysočiny: ještě porostlé, ale ornice je mělká a skála blízko.
        if (altitude > 0.24f && temperature > -0.60f)
        {
            return Biome.Highlands;
        }

        // Studený kraj. Teplota má velké měřítko, takže je to souvislé pásmo, ne konfety.
        if (temperature < -0.32f)
        {
            return Biome.Tundra;
        }

        // TABULKA MÍSTO ŽEBŘÍKU.
        //
        // Dřív se z teploty a vlhkosti spočítalo jedno skóre „suchost" a to se prahovalo
        // třikrát za sebou. Jenže prahy na jednom plynulém poli jsou vrstevnice — a proto
        // byla kolem každé pouště přesně soustředná savana a uvnitř přesně soustředné
        // badlands. Z mapy byly terče.
        //
        // Teď rozhodují obě veličiny zvlášť. Poušť je tam, kde je horko A zároveň sucho;
        // savana tam, kde je horko a polosucho, nebo mírno a hodně sucho. Protože se
        // teplota a vlhkost mění v úplně jiném měřítku, jejich průnik dává nepravidelné
        // tvary, ne kroužky. (Původní poznámka tvrdila, že průnik dvou šumů dává drobné
        // skvrny — to platilo jen proto, že obě pole měla tehdy stejné měřítko a jen dvě
        // oktávy.)
        float weird = _weird.Fractal(cx * 0.00038f, cz * 0.00038f, octaves: 3);

        if (temperature > 0.22f)
        {
            // Horko.
            if (humidity < -0.18f)
            {
                // Badlands nejsou „ještě sušší poušť", ale vlastní oblast. Kdyby stačila
                // suchost, ležely by přesně uprostřed každé pouště. Rozhoduje proto třetí,
                // nezávislé pole — a přičítá se, místo aby se prahovalo zvlášť, jinak má
                // okraj rovnou hranu tam, kde ten práh protne poušť.
                return humidity + (weird * 0.55f) < -0.42f ? Biome.Badlands : Biome.Desert;
            }

            return humidity < 0.12f ? Biome.Savanna : Biome.Plains;
        }

        // Mírné pásmo. Sucho tu nedělá poušť, ale step — na tu máme savanu.
        return humidity < -0.34f ? Biome.Savanna : Biome.Plains;
    }

    /// <summary>
    /// Povaha krajiny na daném místě: násobitele jednotlivých pásem terénu.
    ///
    /// <para>Vzniká plynulým mícháním mezi několika předpisy podle pomalého výběrového
    /// šumu, takže jeden kraj je rovina, druhý kopcovitý, třetí divoké hory a čtvrtý
    /// stolové plošiny — a přechody mezi nimi nejsou vidět jako hrana.</para>
    /// </summary>
    private readonly record struct Landform(float Hills, float Mountains, float Detail)
    {
        public static Landform Lerp(Landform a, Landform b, float t) => new(
            a.Hills + ((b.Hills - a.Hills) * t),
            a.Mountains + ((b.Mountains - a.Mountains) * t),
            a.Detail + ((b.Detail - a.Detail) * t));
    }

    /// <summary>
    /// Předpisy krajiny seřazené podle výběrového šumu. Míchají se vždy dva sousední.
    ///
    /// <para><b>Terasy tu byly a jsou pryč.</b> Přitahování výšky k násobkům 26 bloků mělo
    /// udělat stolové hory, ale ve skutečnosti z toho byly <b>osamocené svislé zdi</b>
    /// uprostřed mírné krajiny — a protože strmá stěna znamená kámen, svítily z trávy
    /// šedě. Ověřeno vypnutím: bez nich zdi zmizely. Plošiny se dají udělat líp přes
    /// vlastní tvar šumu, ne dodatečným zaokrouhlením hotové výšky.</para>
    /// </summary>
    private static readonly Landform[] Landforms =
    [
        new(Hills: 0.25f, Mountains: 0.10f, Detail: 0.45f),  // pláně
        new(Hills: 1.00f, Mountains: 0.35f, Detail: 1.00f),  // kopcovitá krajina
        new(Hills: 0.60f, Mountains: 0.90f, Detail: 0.70f),  // podhůří
        new(Hills: 1.30f, Mountains: 1.70f, Detail: 1.40f),  // divoké hory
    ];

    private Landform LandformAt(float wx, float wz)
    {
        // Vlnová délka kolem 2500 bloků: kraj se dá přejít, ale ne v rámci jednoho dohledu.
        float selector = (_landform.Fractal(wx * 0.0004f, wz * 0.0004f, octaves: 2) * 0.5f) + 0.5f;
        float scaled = Saturate(selector) * (Landforms.Length - 1);

        int index = Math.Min((int)scaled, Landforms.Length - 2);
        float blend = SmoothStep(0f, 1f, scaled - index);

        return Landform.Lerp(Landforms[index], Landforms[index + 1], blend);
    }

    private static float Saturate(float value) => Math.Clamp(value, 0f, 1f);

    /// <summary>Hladký přechod mezi dvěma prahy. Ostrý práh by udělal viditelnou hranu.</summary>
    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Saturate((value - edge0) / (edge1 - edge0));
        return t * t * (3f - (2f * t));
    }

    /// <summary>
    /// Vyplní chunk. Volá se z worker vláken, každé na svůj vlastní chunk.
    /// </summary>
    public void Generate(Chunk chunk, Vector3i chunkPosition)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (_worldRuntime is not null)
        {
            _worldRuntime.GenerateChunk(
                chunk,
                _registry,
                chunkPosition.X,
                chunkPosition.Y,
                chunkPosition.Z);
            _modWorldGeneration?.Apply(chunk, chunkPosition, Seed);
            return;
        }

        bool replacedVanilla = _modWorldGeneration?.GenerateBase(chunk, chunkPosition, Seed) == true;
        if (!replacedVanilla)
        {
            GenerateVanilla(chunk, chunkPosition);
            _structures.Place(chunk, chunkPosition, this);
        }

        // Mod hooky musí proběhnout i po rychlé vanilla cestě (homogenní vzduch, voda nebo kámen).
        // Proto jsou až ve wrapperu a všechny vanilla returny zůstávají v privátní metodě.
        _modWorldGeneration?.Apply(chunk, chunkPosition, Seed);
    }

    private void GenerateVanilla(Chunk chunk, Vector3i chunkPosition)
    {
        int baseX = chunkPosition.X * Chunk.Size;
        int baseY = chunkPosition.Y * Chunk.Size;
        int baseZ = chunkPosition.Z * Chunk.Size;
        int topY = baseY + Chunk.Size - 1;

        // Chunky mimo svislý rozsah světa jsou prázdné a nemá cenu na ně sahat.
        if (baseY >= WorldHeight || topY < 0)
        {
            chunk.Fill(BlockRegistry.Air);
            return;
        }

        ColumnBlock block = ColumnsFor(chunkPosition.X, chunkPosition.Z);

        // Celý chunk je nad terénem: samý vzduch, žádné pole indexů.
        //
        // Rozsah se musí zvednout o nejvyšší strom: koruna sahá nad povrch a chunk, který
        // by se jinak odbyl jako prázdný, může být plný listí. Bez toho by stromy mizely
        // ve výšce, kde zrovna začíná nový chunk.
        // Rychlé odbytí platí jen NAD HLADINOU. Pod ní je i „prázdný" chunk plný vody.
        bool aboveSea = baseY > SeaLevel;

        if (aboveSea && baseY > block.Highest + TreePlanter.MaxTreeHeight + 3)
        {
            chunk.Fill(BlockRegistry.Air);
            return;
        }

        if (aboveSea && baseY > block.Highest)
        {
            // Nad terénem, ale v dosahu korun: vzduch a do něj případné stromy.
            chunk.Fill(BlockRegistry.Air);
            _trees?.Plant(chunk, baseX, baseY, baseZ, this);
            return;
        }

        // Celý chunk je nad terénem a zároveň celý pod hladinou: samá voda.
        if (baseY > block.Highest && topY <= SeaLevel)
        {
            chunk.Fill(_water);
            return;
        }

        // Celý chunk je pod terénem a mimo pásmo jeskyní: samé podloží.
        bool outsideCaveBand = baseY > CaveTop || topY < CaveBottom;
        if (topY < block.Lowest && outsideCaveBand)
        {
            chunk.Fill(topY < DeepslateLine - 12 ? _deepslate : _stone);
            PopulateSolidChunkOres(chunk, block.Columns, baseX, baseY, baseZ);
            return;
        }

        FillVoxels(chunk, block.Columns, baseX, baseY, baseZ);

        // Stromy až po terénu: razítkují se do hotového povrchu a listí se zapisuje jen
        // do vzduchu, takže nesmí přijít dřív.
        _trees?.Plant(chunk, baseX, baseY, baseZ, this);
    }

    /// <summary>
    /// Zapne rozsazování stromů.
    ///
    /// <para>Odděleně od konstruktoru schválně: <see cref="TreePlanter"/> potřebuje
    /// generátor a generátor jeho, takže by z toho byla kruhová závislost v konstruktoru.
    /// Testy terénu navíc můžou běžet bez vegetace.</para>
    /// </summary>
    public void EnableTrees(BlockRegistry registry) => _trees = new TreePlanter(registry, Seed);

    /// <summary>
    /// Sazeč stromů, nebo <c>null</c>, dokud se nezavolá <see cref="EnableTrees"/>.
    /// </summary>
    /// <remarks>
    /// Vystaveno kvůli růstu sazenic: vyrostlý strom musí mít <b>týž tvar</b> jako
    /// vygenerovaný, takže si nesmí sáhnout na vlastní kopii sazeče s jiným seedem.
    /// </remarks>
    public TreePlanter? Trees => _trees;

    private static Biome CompatibilityBiome(Tesseris.ModApi.ResourceId id) => id.Value switch
    {
        "tesseris:savanna" => Biome.Savanna,
        "tesseris:desert" => Biome.Desert,
        "tesseris:badlands" => Biome.Badlands,
        "tesseris:tundra" => Biome.Tundra,
        "tesseris:highlands" => Biome.Highlands,
        "tesseris:snowy_peaks" => Biome.SnowyPeaks,
        "tesseris:stony_peaks" => Biome.StonyPeaks,
        "tesseris:frozen_peaks" => Biome.FrozenPeaks,
        _ => Biome.Plains,
    };

    /// <summary>
    /// Sloupce chunku, počítané jednou a sdílené celým svislým sloupcem chunků.
    ///
    /// <para><b>Bez téhle cache to při výšce 1024 nemůže fungovat.</b> Jeden chunk potřebuje
    /// 34×34 sloupců a každý sloupec stojí přes dvacet vzorků šumu. Svislý sloupec má
    /// 32 chunků, takže bez sdílení by se tatáž práce udělala dvaatřicetkrát — přes 700 tisíc
    /// vyhodnocení šumu na jediný sloupec světa. Se sdílením je to jednou.</para>
    /// </summary>
    private ColumnBlock ColumnsFor(int chunkX, int chunkZ) =>
        _columnCache.GetOrAdd(new Vector2i(chunkX, chunkZ), static (key, self) => self.BuildColumns(key), this);

    private ColumnBlock BuildColumns(Vector2i chunkPosition)
    {
        int baseX = chunkPosition.X * Chunk.Size;
        int baseZ = chunkPosition.Y * Chunk.Size;

        // Sloupce i s lemem jednoho bloku kolem dokola. Lem stojí 13 % práce navíc a je
        // za něj sklon, který nikde nechybí — u stěny chunku by se jinak musel hádat.
        var columns = new Column[PaddedSize * PaddedSize];
        int lowest = int.MaxValue;
        int highest = int.MinValue;

        for (int z = 0; z < PaddedSize; z++)
        {
            for (int x = 0; x < PaddedSize; x++)
            {
                Column column = ColumnAt(baseX + x - 1, baseZ + z - 1);
                columns[x + (z * PaddedSize)] = column;

                // Do rozsahu jde jen vnitřek: podle něj se rozhoduje, jestli je chunk celý
                // nad terénem nebo celý pod ním, a lem do toho nepatří.
                if (x is > 0 and <= Chunk.Size && z is > 0 and <= Chunk.Size)
                {
                    lowest = Math.Min(lowest, column.Surface);
                    highest = Math.Max(highest, column.Surface);
                }
            }
        }

        return new ColumnBlock(columns, lowest, highest);
    }

    /// <summary>
    /// Zahodí sloupce mimo dosah. Volá streamer při uvolňování chunků — bez toho by cache
    /// rostla, dokud se hráč pohybuje.
    /// </summary>
    public void ForgetColumnsOutside(Vector2i center, int radiusChunks)
    {
        int radiusSquared = radiusChunks * radiusChunks;

        foreach (Vector2i key in _columnCache.Keys)
        {
            int dx = key.X - center.X;
            int dz = key.Y - center.Y;

            if ((dx * dx) + (dz * dz) > radiusSquared)
            {
                _columnCache.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Kolik sloupců drží cache. Jen pro diagnostiku.</summary>
    public int CachedColumns => _columnCache.Count;

    /// <summary>
    /// Rozsah výšek povrchu v daném sloupci chunků, <b>pokud už je spočítaný</b>. Vrací false,
    /// když se na sloupec ještě nesahalo — volající to má brát jako „nevím", ne jako „nic tam
    /// není". Používá streamer, aby nemeshoval chunky hluboko pod povrchem.
    /// </summary>
    public bool TryGetSurfaceRange(int chunkX, int chunkZ, out int lowest, out int highest)
    {
        if (_columnCache.TryGetValue(new Vector2i(chunkX, chunkZ), out ColumnBlock? block))
        {
            lowest = block.Lowest;
            highest = block.Highest;
            return true;
        }

        lowest = 0;
        highest = 0;
        return false;
    }

    private sealed record ColumnBlock(Column[] Columns, int Lowest, int Highest);

    private void FillVoxels(Chunk chunk, ReadOnlySpan<Column> columns, int baseX, int baseY, int baseZ)
    {
        for (int z = 0; z < Chunk.Size; z++)
        {
            for (int x = 0; x < Chunk.Size; x++)
            {
                int worldX = baseX + x;
                int worldZ = baseZ + z;
                (int surface, _, Biome biome) = columns[(x + 1) + ((z + 1) * PaddedSize)];
                float slope = SlopeAt(columns, x, z);
                int deepslateAt = DeepslateLevel(worldX, worldZ);

                for (int y = 0; y < Chunk.Size; y++)
                {
                    int worldY = baseY + y;

                    if (worldY >= WorldHeight)
                    {
                        continue;
                    }

                    if (worldY > surface)
                    {
                        // Nad terénem: buď vzduch, nebo voda, když je to pod hladinou.
                        if (worldY <= SeaLevel)
                        {
                            chunk.SetBlock(x, y, z, _water);
                        }

                        continue;
                    }

                    ushort block = SurfaceBlock(biome, surface - worldY, slope, worldY, deepslateAt);

                    // Pláž a mořské dno: kolem hladiny je písek místo trávy. Bez toho by
                    // z vody vystupoval travnatý sráz a břeh by vypadal jako useknutá louka.
                    if (surface <= SeaLevel + BeachHeight && surface - worldY < 4 && slope < 1.4f)
                    {
                        block = _sand;
                    }

                    // Nejspodnější vrstva zůstává vždycky plná, aby se do světa nedalo propadnout.
                    if (worldY > 0 && IsCave(worldX, worldY, worldZ))
                    {
                        // Zaplavují se jen jeskyně POD MOŘSKÝM DNEM, ne všechno pod
                        // úrovní hladiny. Rozhoduje výška povrchu sloupce, ne výška
                        // voxelu: jinak by byla zatopená každá jeskyně pod horou, protože
                        // ta leží taky níž než hladina — a ze světa by zmizely všechny
                        // hluboké jeskyně. (Odhalil to test na jeskyně, který spadl na nule.)
                        if (surface < SeaLevel && worldY <= SeaLevel)
                        {
                            chunk.SetBlock(x, y, z, _water);
                        }

                        continue;
                    }

                    // RUDA JEN TAM, KDE UŽ JE KÁMEN. Nahrazuje se až hotový blok, takže
                    // se ruda nikdy neobjeví v ornici ani v písku — a hlavně se tím nedá
                    // rozbít pravidlo o pláži a povrchu, které rozhodlo o řádek výš.
                    if (block == _stone || block == _deepslate)
                    {
                        ushort ore = OreAt(worldX, worldY, worldZ, surface, biome);

                        if (ore != BlockRegistry.Air)
                        {
                            block = ore;
                        }
                    }

                    chunk.SetBlock(x, y, z, block);
                }
            }
        }
    }

    /// <summary>
    /// Jaká ruda leží na téhle souřadnici, nebo vzduch.
    /// </summary>
    /// <remarks>
    /// <para><b>Žíly, ne jednotlivá zrna.</b> Kdyby o rudě rozhodoval hash jednoho voxelu,
    /// byla by po skále rovnoměrně rozprášená a hledání by nedávalo smysl — buď narazíš,
    /// nebo ne. Hrubá mřížka drží spolu sousední voxely, takže vzniknou hnízda o několika
    /// kusech, která se vyplatí vykopat celá.</para>
    ///
    /// <para><b>Hloubka rozhoduje o tom, co se najde.</b> Uhlí je všude, železo od poloviny
    /// dolů, diamant až u dna. Je to jediné, co dělá ze sestupu odměnu; bez toho by se
    /// vyplatilo kopat vodorovně hned pod povrchem.</para>
    /// </remarks>
    private ushort OreAt(int worldX, int worldY, int worldZ, int surface, Biome biome)
    {
        int cellX = worldX >> 1;
        int cellY = worldY >> 1;
        int cellZ = worldZ >> 1;

        // Uvnitř 2×2×2 hnízda se několik voxelů vynechá, takže žíla není pravidelná kostka.
        if (OreHash(worldX, worldY, worldZ, 1) % 5u == 0u)
        {
            return BlockRegistry.Air;
        }

        bool? exposed = null;
        bool Exposed()
        {
            exposed ??= IsExposedToCave(worldX, worldY, worldZ);
            return exposed.Value;
        }

        // Tesseris má hladinu moře kolem Y 305 a povrch se běžně pohybuje okolo Y 250–350.
        // Pásma proto používají skutečné Y světa a u povrchových rud také hloubku pod lokálním
        // povrchem. Prosté roztažení minecraftových -64…320 přes 1024 bloků posouvalo uhlí
        // nesmyslně vysoko a v běžných jeskyních skoro nebylo.
        int depth = surface - worldY;

        float diamond = Triangle(worldY, 4, 24, 112);
        if (Vein(cellX, cellY, cellZ, 11, diamond, 650)
            && (!Exposed() || ExposureRoll(worldX, worldY, worldZ, 12, 0.42f)))
        {
            return _diamondOre;
        }

        float redstone = Triangle(worldY, 4, 32, 128);
        if (Vein(cellX, cellY, cellZ, 21, redstone, 190))
        {
            return _redstoneOre;
        }

        float gold = Triangle(worldY, 16, 96, 220);
        if (biome == Biome.Badlands && depth is >= 6 and <= 190)
        {
            gold = MathF.Max(gold, Triangle(depth, 6, 48, 190));
        }
        if (Vein(cellX, cellY, cellZ, 31, gold, biome == Biome.Badlands ? 150 : 330))
        {
            return _goldOre;
        }

        float lapis = InRange(worldY, 24, 260)
            ? MathF.Max(0.12f, Triangle(worldY, 24, 140, 260))
            : 0f;
        if (Vein(cellX, cellY, cellZ, 41, lapis, 380)
            && (!Exposed() || ExposureRoll(worldX, worldY, worldZ, 42, 0.55f)))
        {
            return _lapisOre;
        }

        float copper = Triangle(worldY, 120, 245, 360);
        if (Vein(cellX, cellY, cellZ, 51, copper, 145))
        {
            return _copperOre;
        }

        float undergroundIron = Triangle(worldY, 24, 165, 330);
        float mountainIron = Triangle(depth, 8, 72, 240);
        float iron = MathF.Max(undergroundIron, mountainIron);
        if (worldY >= 16 && depth >= 6)
        {
            iron = MathF.Max(iron, 0.08f);
        }
        if (Vein(cellX, cellY, cellZ, 61, iron, mountainIron > undergroundIron ? 145 : 185))
        {
            return _ironOre;
        }

        bool mountain = biome is Biome.Highlands or Biome.SnowyPeaks or Biome.StonyPeaks or Biome.FrozenPeaks;
        float emerald = mountain
            ? Triangle(depth, 4, 42, 180) * InverseLerp(SeaLevel - 24, SeaLevel + 280, surface)
            : 0f;
        if (Vein(cellX, cellY, cellZ, 71, emerald, 420))
        {
            return _emeraldOre;
        }

        float coal = depth is >= 4 and <= 300
            ? MathF.Max(0.20f, Triangle(depth, 4, 48, 300))
            : 0f;
        if (Vein(cellX, cellY, cellZ, 81, coal, 125)
            && (!(depth > 110 && Exposed())
                || ExposureRoll(worldX, worldY, worldZ, 82, 0.72f)))
        {
            return _coalOre;
        }

        return BlockRegistry.Air;
    }

    private static float Triangle(int y, int minimum, int peak, int maximum)
    {
        if (y < minimum || y > maximum)
        {
            return 0f;
        }

        return y <= peak
            ? InverseLerp(minimum, peak, y)
            : 1f - InverseLerp(peak, maximum, y);
    }

    private static float InverseLerp(int from, int to, int value) =>
        from == to ? 1f : Math.Clamp((value - from) / (float)(to - from), 0f, 1f);

    private static bool InRange(int value, int minimum, int maximum) =>
        value >= minimum && value <= maximum;

    private static bool Vein(int x, int y, int z, int salt, float weight, int oneInAtPeak)
    {
        if (weight <= 0f)
        {
            return false;
        }

        uint threshold = (uint)((uint.MaxValue / (double)oneInAtPeak) * Math.Clamp(weight, 0f, 1f));
        return OreHash(x, y, z, salt) <= threshold;
    }

    private static bool ExposureRoll(int x, int y, int z, int salt, float retainedFraction) =>
        OreHash(x, y, z, salt) / (double)uint.MaxValue < retainedFraction;

    private bool IsExposedToCave(int x, int y, int z)
    {
        if (y <= CaveBottom || y >= CaveTop)
        {
            return false;
        }

        return IsCave(x - 1, y, z) || IsCave(x + 1, y, z)
            || IsCave(x, y - 1, z) || IsCave(x, y + 1, z)
            || IsCave(x, y, z - 1) || IsCave(x, y, z + 1);
    }

    private void PopulateSolidChunkOres(
        Chunk chunk,
        ReadOnlySpan<Column> columns,
        int baseX,
        int baseY,
        int baseZ)
    {
        for (int z = 0; z < Chunk.Size; z++)
        {
            for (int x = 0; x < Chunk.Size; x++)
            {
                Column column = columns[(x + 1) + ((z + 1) * PaddedSize)];
                Biome biome = column.Biome;
                for (int y = 0; y < Chunk.Size; y++)
                {
                    ushort ore = OreAt(baseX + x, baseY + y, baseZ + z, column.Surface, biome);
                    if (ore != BlockRegistry.Air)
                    {
                        chunk.SetBlock(x, y, z, ore);
                    }
                }
            }
        }
    }

    private static uint OreHash(int x, int y, int z, int salt)
    {
        unchecked
        {
            uint h = 2166136261u ^ (uint)salt;
            h = (h ^ (uint)x) * 16777619u;
            h = (h ^ (uint)y) * 16777619u;
            h = (h ^ (uint)z) * 16777619u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            return h ^ (h >> 15);
        }
    }

    /// <summary>
    /// Povrchová pravidla: který blok leží v dané hloubce pod povrchem.
    ///
    /// <para><b>Sklon rozhoduje dřív než biom.</b> Na strmé stěně se ornice neudrží a
    /// prosvítá podloží — proto je bok hory kamenný, zatímco mírný svah kousek vedle nese
    /// trávu. Tohle je jediné pravidlo, které dělá rozdíl mezi „kamennou horou" a „horou
    /// porostlou trávou s kamenem pod ní", a přesně tak to řeší i Minecraft (podmínky
    /// <c>steep</c> a <c>stone_depth</c> v surface rules).</para>
    ///
    /// <para>V poušti se ale skála neukáže — písek se sype a pod ním je pískovec. Kdyby
    /// se sklon vyhodnocoval stejně všude, měly by duny kamenné hrany.</para>
    /// </summary>
    /// <param name="depth">Kolik bloků pod povrchem. 0 je povrch.</param>
    /// <param name="slope">Převýšení na blok, spočítané ze sousedních sloupců.</param>
    private ushort SurfaceBlock(Biome biome, int depth, float slope, int worldY, int deepslateAt)
    {
        ushort bedrock = worldY < deepslateAt ? _deepslate : _stone;

        if (slope >= SteepSlope)
        {
            return biome switch
            {
                // Duna zůstane pískem; pod ní se obnaží pískovec, ne kámen.
                Biome.Desert => depth < 2 ? _sand : _sandstone,

                // Ve strmé stěně badlands prosvítají vrstvy usazenin — to je jejich znak.
                Biome.Badlands => depth < 2 ? _redSand : _terracotta,

                Biome.FrozenPeaks => depth < 2 ? _ice : bedrock,

                // Sutě: na mírnějším z těch strmých svahů se drží štěrk, na nejstrmějším
                // už se neudrží nic a je vidět holá skála.
                _ => slope < VerySteepSlope && depth < 2 ? _gravel : bedrock,
            };
        }

        return biome switch
        {
            Biome.Desert => depth < 4 ? _sand : depth < 12 ? _sandstone : bedrock,
            Biome.Badlands => depth < 3 ? _redSand : depth < 20 ? _terracotta : bedrock,
            Biome.Savanna => depth == 0 ? _dryGrass : depth < 4 ? _dirt : bedrock,
            Biome.Tundra => depth == 0 ? _snow : depth < 4 ? _dirt : bedrock,

            // Vysočiny mají ornici mělkou — proto je na nich skála pořád na dosah.
            Biome.Highlands => depth == 0 ? _grass : depth < 2 ? _dirt : bedrock,

            Biome.FrozenPeaks => depth < 4 ? _ice : bedrock,
            Biome.SnowyPeaks => depth < 5 ? _snow : bedrock,
            Biome.StonyPeaks => depth < 3 ? _gravel : bedrock,
            _ => depth == 0 ? _grass : depth < 4 ? _dirt : bedrock,
        };
    }

    /// <summary>
    /// Povrchový blok pro vzdálený terén (LOD).
    ///
    /// <para><b>Sklon se sem musí předat.</b> První verze ho ignorovala a brala blok jen
    /// podle biomu — jenže povrchová pravidla dávají na strmou stěnu kámen, takže tam, kde
    /// chunky ukázaly šedou skálu, LOD nakreslil zelenou trávu. Zadavatel to popsal slovy
    /// „první LOD není vůbec podobný tomu nultému". Teď se použije totéž pravidlo jako
    /// u chunků, jen bez hloubky.</para>
    /// </summary>
    public ushort FarSurfaceBlock(Biome biome, float slope) =>
        SurfaceBlock(biome, depth: 0, slope, worldY: BaseHeight, deepslateAt: DeepslateLine);

    /// <summary>
    /// Povrchový blok vzdáleného terénu se <b>zohledněním výšky</b>, tedy včetně pláže
    /// a mořského dna.
    /// </summary>
    /// <remarks>
    /// <para><b>Proč nestačí <see cref="FarSurfaceBlock(Biome, float)"/>.</b> Ten rozhoduje
    /// jen podle biomu a sklonu, takže na mořské dno dá trávu — na souši je to správně, pod
    /// vodou ne. Chunky mají navíc pravidlo, že kolem hladiny je písek, a bez něj vypadalo
    /// vzdálené dno jako zatopená louka, zatímco blízké bylo písčité.</para>
    ///
    /// <para>Pravidlo je <b>záměrně shodné</b> s tím v generování chunků, jen bez hloubkové
    /// složky — LOD zná jen povrch, ne jednotlivé voxely pod ním. Když se mění tam, musí se
    /// změnit i tady, jinak se přechod mezi chunky a LOD zase rozejde.</para>
    /// </remarks>
    /// <param name="surface">Výška povrchu sloupce, tedy dna tam, kde je nad ním voda.</param>
    public ushort FarSurfaceBlockAt(Biome biome, float slope, int surface)
    {
        if (surface <= SeaLevel + BeachHeight && slope < 1.4f)
        {
            return _sand;
        }

        return FarSurfaceBlock(biome, slope);
    }

    /// <summary>
    /// Povrchový blok pro vzdálený terén včetně lesa.
    ///
    /// <para><b>Proč se les kreslí barvou, ne stromy.</b> Jednotlivý strom má šest bloků
    /// a na dvou kilometrech je z něj méně než pixel — kreslit ho tam by stálo geometrii
    /// a nebylo by ho vidět. Zato hustý háj je z té dálky souvislá tmavě zelená plocha,
    /// a přesně tak se dá nakreslit: povrch v lese dostane blok listí místo trávy.</para>
    ///
    /// <para>Rozhoduje <b>tentýž</b> lesní šum, podle kterého rostou skutečné stromy, takže
    /// na hranici dohledu les nezmizí ani nepřiskočí — jen se z barevné plochy stanou
    /// jednotlivé koruny.</para>
    ///
    /// <para>Na strmině se les nekreslí ze stejného důvodu, z jakého tam nerostou stromy.</para>
    /// </summary>
    /// <summary>
    /// Zapojení lesa: 0 = mýtina, 1 = hustý háj. Bez zapnuté vegetace vždycky nula.
    /// Vystaveno kvůli ladicím nástrojům, které kreslí mapu porostu.
    /// </summary>
    public float ForestDensity(int worldX, int worldZ) => _trees?.Forest(worldX, worldZ) ?? 0f;

    /// <summary>
    /// Povrchový blok sloupce. Používá sázení rostlin, když podklad leží v sousedním
    /// chunku a nedá se přečíst z toho právě generovaného.
    /// </summary>
    public ushort SurfaceBlockAt(int worldX, int surface, int worldZ)
    {
        Column column = ColumnAt(worldX, worldZ);

        float slope = MathF.Max(
            MathF.Abs(ColumnAt(worldX + 1, worldZ).Exact - ColumnAt(worldX - 1, worldZ).Exact),
            MathF.Abs(ColumnAt(worldX, worldZ + 1).Exact - ColumnAt(worldX, worldZ - 1).Exact)) / 2f;

        return SurfaceBlock(column.Biome, depth: 0, slope, surface, DeepslateLevel(worldX, worldZ));
    }

    /// <summary>
    /// Rostlina pro vzdálený terén i s výškou povrchu, na které stojí.
    /// </summary>
    /// <remarks>
    /// <para>Rozhoduje <b>tentýž</b> výpočet jako u chunků (<see cref="TreePlanter.PlantAt"/>),
    /// takže trs v LOD stojí na témž místě jako ten v chunku a na hranici pásma nepřeskočí.</para>
    ///
    /// <para><b>Podklad se bere z biomu bez sklonu.</b> Přesný sklon chce čtyři další
    /// vzorky výšky na sloupec a LOD prochází desítky tisíc sloupců na dlaždici —
    /// na tom, jestli vyroste stéblo, přitom skoro nic nemění.</para>
    /// </remarks>
    /// <param name="surface">Výška povrchu sloupce, na kterou rostlina patří.</param>
    public ushort FarPlantAt(int worldX, int worldZ, out int surface)
    {
        Column column = ColumnAt(worldX, worldZ);
        surface = column.Surface;

        // Pod hladinou nic neroste — a trs zapíchnutý do mořského dna by navíc vykukoval
        // nad vodu, protože LOD dno leží níž než hladina.
        if (surface < SeaLevel)
        {
            return BlockRegistry.Air;
        }

        ushort ground = FarSurfaceBlock(column.Biome, slope: 0f);

        return _trees?.PlantAt(worldX, worldZ, ground, this) ?? BlockRegistry.Air;
    }

    /// <summary>
    /// Koruna stromu na dané souřadnici, pokud tam nějaký stojí. Používá vzdálený terén.
    /// </summary>
    public bool TryCanopyAt(int worldX, int worldZ, out TreePlanter.Canopy canopy)
    {
        if (_trees is null)
        {
            canopy = default;
            return false;
        }

        return _trees.TryCanopyAt(worldX, worldZ, this, out canopy);
    }

    public ushort FarSurfaceBlock(Biome biome, float slope, int worldX, int worldZ)
    {
        // Pod hladinou je vidět voda, ne dno.
        if (ColumnAt(worldX, worldZ).Surface < SeaLevel)
        {
            return _water;
        }

        ushort ground = FarSurfaceBlock(biome, slope);

        if (_trees is null || slope > 1.6f)
        {
            return ground;
        }

        ushort canopy = _trees.FarCanopyBlock(biome, worldX, worldZ);
        return canopy == BlockRegistry.Air ? ground : canopy;
    }

    /// <summary>
    /// Ve které výšce v tomhle sloupci přechází kámen v deepslate. Počítá se <b>jednou na
    /// sloupec</b>, ne na každý voxel — jinak by to byl vzorek šumu na každou kostku světa.
    /// Bez rozvlnění by přechod byl vodorovný řez přes celou mapu.
    /// </summary>
    private int DeepslateLevel(int worldX, int worldZ) =>
        DeepslateLine + (int)(_edge.Sample(worldX * 0.0035f, worldZ * 0.0035f) * 18f);

    /// <summary>
    /// Sklon sloupce jako převýšení na blok, z rozdílu sousedů přes dva bloky.
    ///
    /// <para>Počítá se z <b>nezaokrouhlené</b> výšky. Ze zaokrouhlené to nejde: na mírném
    /// svahu se rozdíl sousedů střídá mezi 1 a 2 a práh se přepíná blok po bloku, z čehož
    /// je na svahu zrnitá směs kamene a trávy místo souvislých ploch.</para>
    /// </summary>
    private static float SlopeAt(ReadOnlySpan<Column> columns, int x, int z)
    {
        int stride = PaddedSize;
        int here = (x + 1) + ((z + 1) * stride);

        float dx = columns[here + 1].Exact - columns[here - 1].Exact;
        float dz = columns[here + stride].Exact - columns[here - stride].Exact;

        return MathF.Max(MathF.Abs(dx), MathF.Abs(dz)) * 0.5f;
    }

    /// <summary>
    /// Jeskyně. Dvě nezávislá šumová pole a podmínka, že obě jsou blízko nuly, dají
    /// protáhlé chodby místo plochých dutin — průnik dvou ploch je křivka.
    /// </summary>
    private bool IsCave(int worldX, int worldY, int worldZ)
    {
        if (worldY < CaveBottom || worldY > CaveTop)
        {
            return false;
        }

        float x = worldX * 0.018f;
        float y = worldY * 0.032f;
        float z = worldZ * 0.018f;

        float a = _caveA.Sample(x, y, z);
        float b = _caveB.Sample(x, y, z);

        // U stropu pásma se chodby zužují, aby se neotevíraly do krajiny.
        float taper = MathF.Min(1f, (CaveTop - worldY) / 8f);

        return ((a * a) + (b * b)) < 0.022f * taper;
    }
}
