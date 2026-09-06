using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;

namespace Tesseris.Game.World;

/// <summary>
/// Denní doba: kde je slunce, kde měsíc a jakou barvu má obloha.
/// </summary>
/// <remarks>
/// <para>Celý cyklus je funkce jediného čísla — podílu dne od půlnoci. Nic si nepamatuje
/// mezi snímky, takže se dá čas kdykoli přetočit a scéna hned odpovídá.</para>
///
/// <para><b>Slunce nechodí přes nadhlavník.</b> Dráha je nakloněná, jako by hráč stál
/// mimo rovník — v poledne stojí slunce vysoko, ale ne přesně nad hlavou. Kolmé slunce
/// dělá ve voxelovém světě mrtvé osvětlení: všechny svislé stěny dostanou týž jas
/// a reliéf zmizí.</para>
///
/// <para><b>Náklon musí být kolmo na dráhu, ne v její rovině.</b> První verze počítala
/// <c>(cos a · sin T, sin a, cos a · cos T)</c>, což rovinu dráhy jen otočilo kolem
/// svislice — v poledne z toho vyšlo přesně <c>(0, 1, 0)</c>, tedy slunce v nadhlavníku,
/// navzdory tomu, co je napsané o řádek výš. Stálo to dvakrát: mrtvé polední osvětlení
/// a hlavně nestabilní osy stínové mapy, protože kolmé slunce je pro <c>LookAt</c>
/// degenerovaný případ a základna se u něj překlápí.</para>
/// </remarks>
public sealed class DayCycle
{
    /// <summary>Jak dlouho trvá celý den ve vteřinách.</summary>
    public const float DayLength = 1200f;

    // Minecraft-style clock sections expressed as fractions of a 24-hour day.
    // Dawn 05:00-06:00 and dusk 18:00-19:00 each last 50 real seconds when the
    // complete cycle lasts 20 minutes.
    private const float DawnStart = 5f / 24f;
    private const float Sunrise = 6f / 24f;
    private const float Sunset = 18f / 24f;
    private const float DuskEnd = 19f / 24f;

    /// <summary>Náklon dráhy slunce od svislice, v radiánech.</summary>
    private const float Tilt = 0.42f;

    /// <summary>
    /// Podíl dne, 0 až 1. Nula je půlnoc, 0,25 východ, 0,5 poledne, 0,75 západ.
    /// </summary>
    public float TimeOfDay { get; set; } = 0.32f;

    /// <summary>
    /// Běží čas sám?
    /// </summary>
    /// <remarks>
    /// <para><b>Dlouho tu bylo výchozí NE</b>, protože při pohybu slunce stíny blikaly:
    /// stínová mapa se každý snímek překreslovala z aktuálního směru slunce, takže se
    /// mřížka mapy pod alfa-testovaným listím pořád podsouvala a listy se do texelů
    /// pokaždé trefily jinak.</para>
    ///
    /// <para>Mapa se nově kreslí po epochách a mezi nimi se prolíná
    /// (<c>ChunkRenderer.UpdateShadowEpoch</c>), takže se obsah mezi snímky nemění a čas
    /// může běžet. Zpomalit a zastavit se dá klávesou <c>−</c>, zrychlit <c>+</c>.</para>
    /// </remarks>
    public bool Running { get; set; } = true;

    /// <summary>Je vizuální poloha slunce zamknutá, zatímco biologický čas dál běží?</summary>
    /// <remarks>
    /// Používá se pro natáčení růstu bez střídání světla a tmy. Neovlivňuje
    /// <see cref="BiologicalSeconds"/> ani rychlost nastavenou v <see cref="Speed"/>.
    /// </remarks>
    public bool SunFrozen { get; set; }

    /// <summary>Kolikrát rychleji než výchozí tempo.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>Samostatný násobek růstu, stárnutí a přírodních změn; sluncem nehýbe.</summary>
    public float NatureSpeed { get; set; } = 1f;

    /// <summary>Směr KE slunci. Pod obzorem míří dolů.</summary>
    public Vector3 SunDirection => DirectionAt(TimeOfDay);

    /// <summary>Směr K měsíci. Stojí proti slunci.</summary>
    public Vector3 MoonDirection => -SunDirection;

    /// <summary>
    /// Jak vysoko je slunce, −1 až 1. Nad nulou je den, pod nulou noc.
    /// </summary>
    public float SunHeight => SunDirection.Y;

    /// <summary>
    /// Kolik denního světla dopadá, 0 až 1.
    /// </summary>
    /// <remarks>
    /// Minecraftové časování drží plný den mezi 06:00 a 18:00. Východ 05:00–06:00 a
    /// západ 18:00–19:00 používají plynulou křivku, takže světlo na hranách úseků neskočí.
    /// </remarks>
    public float Daylight
    {
        get
        {
            float time = TimeOfDay - MathF.Floor(TimeOfDay);

            if (time >= DawnStart && time < Sunrise)
            {
                return SmoothStep((time - DawnStart) / (Sunrise - DawnStart));
            }

            if (time >= Sunrise && time <= Sunset)
            {
                return 1f;
            }

            if (time > Sunset && time < DuskEnd)
            {
                return 1f - SmoothStep((time - Sunset) / (DuskEnd - Sunset));
            }

            return 0f;
        }
    }

    /// <summary>
    /// Kolik svítí měsíc, 0 až 1. Noc není úplná tma, ale modré šero.
    /// </summary>
    /// <summary>
    /// Kolik je v noci vidět. Nula je úplná tma, jednička denní jas.
    /// </summary>
    /// <remarks>
    /// <para>Prochází to celou scénou přes jediné číslo: <see cref="Moonlight"/> čtou
    /// všechny shadery terénu, rostlin i vody jako <c>lightColor.w</c> a násobí jím
    /// výsledek. Změna se proto projeví všude stejně a nemůže se rozejít.</para>
    ///
    /// <para>Výchozích 0,22 je hodnota, se kterou se noc ladila. Vyšší číslo znamená
    /// „vidím v noci víc", nižší tmavší noc.</para>
    /// </remarks>
    public float NightBrightness { get; set; } = 0.22f;

    /// <summary>
    /// Co znamená plný noční jas, tedy <c>/night 1</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Druhý knoflík nad prvním.</b> <see cref="NightBrightness"/> je 0 až 1
    /// a říká, kolik z dostupného rozsahu se využije; tohle říká, jak velký ten rozsah
    /// je. Bez toho byla jednička natvrdo denní jas, což je přesvětlené, a jemně se to
    /// dalo ladit jen v úzkém pásmu kolem nuly.</para>
    ///
    /// <para>Mění se přes <c>/nightmax</c>. Jednička zachovává původní chování.</para>
    /// </remarks>
    public float NightBrightnessCeiling { get; set; } = 1f;

    /// <summary>Kolik měsíčního světla dopadá. Nula ve dne, v noci podle nastavení.</summary>
    public float Moonlight => (1f - Daylight) * NightBrightness * NightBrightnessCeiling;

    /// <summary>Je nad obzorem slunce, nebo měsíc?</summary>
    public bool IsDay => SunHeight > 0f;

    /// <summary>Posune čas.</summary>
    public void Update(float deltaSeconds)
    {
        if (!Running || SunFrozen)
        {
            return;
        }

        TimeOfDay += deltaSeconds * Speed / DayLength;
        TimeOfDay -= MathF.Floor(TimeOfDay);
    }

    /// <summary>
    /// Převede skutečné sekundy na biologický čas světa.
    /// </summary>
    /// <remarks>
    /// Růst stromů, obnova vegetace, stáří lesa a tlení listí musí používat přesně stejný
    /// násobek jako slunce. Když hráč čas zastaví, zastaví se i příroda; při 64× rychlosti
    /// proběhne za jednu skutečnou sekundu minuta biologického času.
    /// </remarks>
    public float BiologicalSeconds(float realSeconds) =>
        Running && float.IsFinite(realSeconds) && realSeconds > 0f
            ? realSeconds * Speed * NatureSpeed
            : 0f;

    /// <summary>Biologický čas použitelný pro nový růst, pouze když je slunce nad obzorem.</summary>
    /// <remarks>
    /// V noci mohou rostliny dýchat a les dál stárne, ale fotosyntéza nevyrábí novou hmotu.
    /// Herně je hranice záměrně čitelná: od východu do západu růst běží, v noci stojí.
    /// </remarks>
    public float GrowthSeconds(float realSeconds) =>
        IsDay ? BiologicalSeconds(realSeconds) : 0f;

    /// <summary>
    /// Jas oblohy 0–1. Luanti <c>time_brightness</c> ze <c>src/client/game.cpp</c>.
    /// </summary>
    /// <remarks>
    /// Není to totéž co <see cref="DayNightRatio"/>: poměr je lineární podíl denního
    /// světla, tohle je jeho jas po průchodu převodní křivkou. V noci vyjde 0,096,
    /// v poledne 1,0.
    /// </remarks>
    public float SkyBrightness => LuantiLight.BrightnessF(DayNightRatio / 1000f);

    /// <summary>
    /// Barva nadhlavníku. Luanti <c>SkyboxDefaults::getSkyColorDefaults</c> a
    /// <c>Sky::update</c>.
    /// </summary>
    /// <remarks>
    /// <para>Tři pevné barvy podle jasu oblohy — den, svítání, noc — a celá se vynásobí
    /// jasem. Hranice jsou Luantiho: svítání je jas 0,20 až 0,35, noc pod 0,13.</para>
    ///
    /// <para><b>Předchozí verze míchala barvy podle výšky slunce.</b> Bylo to vlastní
    /// a hlavně to nedrželo krok se zbytkem: obloha hasla podle <c>SunHeight</c>, světlo
    /// podle <c>Daylight</c> a stíny podle třetí veličiny. Luanti má na všechno jednu
    /// křivku, a proto to vypadá jako jeden celek.</para>
    /// </remarks>
    public Vector3 Zenith() => SkyColor(
        day: new Vector3(97f, 181f, 245f),
        dawn: new Vector3(180f, 186f, 250f),
        night: new Vector3(0f, 107f, 255f));

    /// <summary>
    /// Barva u obzoru. Luanti ji používá i jako barvu mlhy — mlha je vzdálený obzor.
    /// </summary>
    public Vector3 Horizon() => SkyColor(
        day: new Vector3(144f, 211f, 246f),
        dawn: new Vector3(186f, 193f, 240f),
        night: new Vector3(64f, 144f, 255f));

    private Vector3 SkyColor(Vector3 day, Vector3 dawn, Vector3 night)
    {
        float brightness = SkyBrightness;

        Vector3 chosen = brightness is >= 0.20f and < 0.35f
            ? dawn
            : brightness < 0.13f ? night : day;

        return chosen / 255f * brightness;
    }

    /// <summary>
    /// Podíl denního světla 0–1000. Luanti <c>time_to_daynight_ratio</c>.
    /// </summary>
    /// <remarks>
    /// Tohle je jediné číslo, kterým denní doba vstupuje do osvětlení světa. Jas ve
    /// vrcholu je zapečený z doby meshování a nemění se; mění se jen barva, kterou se
    /// násobí — viz <see cref="LightColor"/>.
    /// </remarks>
    public float DayNightRatio => LuantiLight.DayNightRatio(TimeOfDay);

    /// <summary>
    /// Barva světla, které na svět dopadá. Luanti <c>get_sunlight_color</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Není to barevný nádech, ale plnohodnotný násobitel jasu.</b> V poledne
    /// vyjde zhruba (0,96; 0,96; 1,06), v noci (0,135; 0,135; 0,250). Kdo tuhle hodnotu
    /// použije, nesmí už svět tlumit podruhé podle <see cref="Daylight"/> — noc by pak
    /// byla tmavá dvakrát.</para>
    ///
    /// <para><b>Předchozí verze míchala měsíční modrou s teplou oranžovou podle výšky
    /// slunce.</b> Bylo to výtvarně hezčí, ale nesouviselo s ničím dalším: světlo hasnulo
    /// podle <c>SunHeight</c>, zatímco stíny se řídily <c>Daylight</c> a mlha ještě něčím
    /// jiným. Luanti má na všechno jedinou křivku, a proto to drží pohromadě.</para>
    /// </remarks>
    /// <summary>
    /// Barva světla, kterou se násobí celý svět.
    /// </summary>
    /// <remarks>
    /// <para><b>Noční jas zvedá i barvu, nejen množství světla.</b> Bez toho by vyšší
    /// <see cref="NightBrightness"/> přidal jas, ale barva by zůstala noční modrá —
    /// a ta má měřeno <c>(0,135; 0,135; 0,250)</c>, tedy skoro žádnou zelenou. Tráva
    /// a listí by proto zůstaly černé, zatímco písek a sníh by se rozzářily, protože
    /// mají světlé a téměř neutrální albedo. Přesně na tohle se zadavatel ptal:
    /// „proč písek vypadá v noci ok, ale tráva je až moc tmavá".</para>
    ///
    /// <para>S rostoucím jasem se proto noční barva přiklání k neutrální — při plné
    /// hodnotě je světlo bílé a barvy světa zůstanou vlastní, jen ztmavené.</para>
    /// </remarks>
    public Vector3 LightColor()
    {
        Vector3 colour = LuantiLight.SunlightColor(DayNightRatio);

        // Ve dne se nic nemění: noc se pozná podle toho, kolik denního světla dopadá.
        float night = 1f - Daylight;
        if (night <= 0f)
        {
            return colour;
        }

        // Nula až 0,22 je původní chování, výš se barva narovnává. Dělí se rozsahem
        // nad výchozí hodnotou, aby posuvník od 50 % výš měl znatelný účinek.
        float lift = Math.Clamp((NightBrightness - DefaultNightBrightness) / (1f - DefaultNightBrightness), 0f, 1f);
        if (lift <= 0f)
        {
            return colour;
        }

        // K čemu se narovnává: šedá o jasu původní barvy. Tím se nemění, KOLIK světla
        // dopadá — to řeší Moonlight — ale přestane se ubírat zelená a červená.
        float grey = MathF.Max(colour.X, MathF.Max(colour.Y, colour.Z));

        return Vector3.Lerp(colour, new Vector3(grey, grey, grey), lift * night);
    }

    /// <summary>Výchozí síla měsíčního svitu. Nad ní se začne narovnávat barva světla.</summary>
    private const float DefaultNightBrightness = 0.22f;

    /// <summary>Na kolik pevných poloh za den je rozdělená dráha slunce pro stínovou mapu.</summary>
    /// <remarks>
    /// 720 kroků je půl stupně na krok, tedy při výchozí délce dne nová poloha zhruba
    /// jednou za 1,7 sekundy.
    /// </remarks>
    /// <summary>Na kolik pevných poloh se dělí denní dráha pro stínovou mapu.</summary>
    public const float ShadowSteps = 720f;

    /// <summary>
    /// Směr ke slunci zaokrouhlený na pevné kroky, pro stínovou mapu.
    /// </summary>
    /// <remarks>
    /// <para><b>Zaokrouhluje se, a to je celá oprava lezoucích okrajů.</b> Dřív sem chodil
    /// plynulý směr a stínová mapa se z něj překreslovala každý snímek. Slunce se přitom
    /// za snímek pootočí o tisíciny stupně — dost na to, aby se mřížka texelů mapy posunula
    /// o zlomek texelu a okraj stínu se přelil jinam. Hrany se pak vlní na místě, i když
    /// se ve scéně nic nehýbe.</para>
    ///
    /// <para>S pevnými kroky je mapa mezi dvěma polohami bit po bitu tatáž, takže okraj
    /// STOJÍ. Cenou je skok při každé změně kroku — ten je ale menší než to, oč se stín
    /// za tu dobu stejně posune, a rozostření filtru ho dál změkčí.</para>
    ///
    /// <para>Předchozí pokus o totéž selhal proto, že se zaokrouhloval jen směr, ale mapa
    /// se stejně překreslovala každý snímek. Zaokrouhlení musí platit i pro rozhodnutí,
    /// KDY se mapa překreslí — viz <c>ChunkRenderer.UpdateShadowEpoch</c>.</para>
    /// </remarks>
    public Vector3 ShadowSunDirection => DirectionAt(ShadowTimeOfDay);

    /// <summary>Denní doba zaokrouhlená na krok stínové mapy.</summary>
    /// <summary>
    /// Denní doba pro stínovou mapu. <b>Nezaokrouhluje se.</b>
    /// </summary>
    /// <remarks>
    /// <para>Chvíli tu bylo zaokrouhlení na 720 pevných poloh za den. Mělo zabránit tomu,
    /// aby se mřížka texelů mapy s pohybem slunce otáčela a okraje stínů se vlnily —
    /// jenže tím se z plynulého pohybu stínu staly SKOKY. Stín stál a jednou za 1,7 s
    /// popojel. Přesně tak to vypadalo a bylo to horší než vada, kterou to mělo léčit.</para>
    ///
    /// <para>Skutečná příčina vlnění byla jinde: obě kaskády se zarovnávaly na texel
    /// DALEKÉ mapy (1,56 bloku), takže se ta blízká s texelem 0,39 posouvala po čtyřech
    /// svých texelech naráz a alfa-testovaná koruna se pokaždé přerasterizovala jinak.
    /// Od chvíle, kdy si každá kaskáda zarovnává svůj vlastní střed, může slunce jít
    /// plynule — viz <c>ChunkRenderer.BuildLightMatrix</c>.</para>
    /// </remarks>
    public float ShadowTimeOfDay => TimeOfDay;

    /// <summary>Směr ke slunci pro zadaný podíl dne.</summary>
    /// <remarks>
    /// Úhel 0 je východ slunce, tedy směr přesně na východní obzor. Odečtení čtvrtiny
    /// posune nulu z půlnoci na východ.
    ///
    /// <para>Náklon zvedá dráhu ze svislé roviny do strany: v kulminaci vyjde
    /// <c>(0, cos T, sin T)</c>, což je výška <c>90° − T</c>. Pro <c>T = 0,42</c> je to
    /// 66° a svislá složka nikdy nepřeleze 0,913 — nikdy tedy nenastane případ, kdy je
    /// směr ke slunci rovnoběžný se svislicí.</para>
    /// </remarks>
    private static Vector3 DirectionAt(float time)
    {
        float angle = (time - 0.25f) * MathF.PI * 2f;

        return Vector3.Normalize(new Vector3(
            MathF.Cos(angle),
            MathF.Sin(angle) * MathF.Cos(Tilt),
            MathF.Sin(angle) * MathF.Sin(Tilt)));
    }

    private static float SmoothStep(float value)
    {
        float t = Math.Clamp(value, 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}
