#version 450

// Fragment shader vzdaleneho terenu.
//
// PROTI chunk_opaque.frag ma NAVIC JEDINOU VEC: zahodi fragmenty blizsi nez zadana mez.
//
// Proc to musi byt az tady. Dlazdice LOD se kresli cela, i kdyz jen kus z ni presahuje
// za dosah chunku — jeji blizsi cast tedy lezi mezi chunky. Rezat to pri stavbe dlazdice
// nejde: dlazdice se postavi jednou a hrac se k ni pak priblizi, takze rozhodnuti
// zapecene do meshe za chvili neplati. Rezat cele dlazdice pri kresleni taky ne:
// dlazdice hranici prekracuje.
//
// Cena je vypnuty early-Z na tomhle pruchodu. Zmereno, ze na p99 to nema vliv.

layout(location = 0) in vec2 vTexCoord;
layout(location = 1) in float vLayer;
layout(location = 2) in float vShade;
layout(location = 3) in float vDistance;
layout(location = 4) in vec3 vWorld;
layout(location = 6) in float vCloud;

layout(set = 0, binding = 0) uniform sampler2DArray uTextures;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 cameraAndFogStart;
    vec4 fogColorAndFogEnd;
    vec4 water;
    vec4 chunkOffset;   // w = mez, blize k ni se u vzdaleneho terenu nic nekresli

    // DENNI DOBA MUSI DOJIT I SEM. Blok se driv koncil o radek vyse, takze vzdaleny teren
    // barvu svetla ani uroven dne vubec nevidel — a v noci zustal osviceny, kdyz uz bylo
    // v bloku push konstant jsou dane a nesmi se rozejit s tim, co posila ChunkRenderer.
    vec4 lightRow0;
    vec4 lightRow1;
    vec4 lightRow2;
    vec4 unusedRow0;
    vec4 unusedRow1;
    vec4 unusedRow2;

    vec4 frameTuning;  // w = kolik denniho svetla dopada
    vec4 lightColor;    // xyz = barva svetla, w = svit mesice
} pc;

layout(location = 0) out vec4 FragColor;

// Vysvetleni vseho je v chunk_opaque.frag, kopie k porovnani ve water_common.glsl.txt.
const vec3 WaterScatter = vec3(0.13, 0.34, 0.44);
const vec3 AbsorptionRatio = vec3(2.17, 0.71, 0.46);
const vec3 SunTint = vec3(1.00, 0.955, 0.845);
const vec3 SkyTint = vec3(0.72, 0.79, 0.99);

// BARVA A UROVEN SVETLA PODLE DENNI DOBY. Tataz funkce jako v chunk_opaque.frag — kdyz se
// meni tam, MUSI se zmenit i tady, jinak se vzdaleny teren rozejde s blizkym na svu.
vec3 ApplyLightTint(vec3 color, float shade)
{
    // Vzdaleny teren nema dve svetelne banky — nese jen jedno cislo jasu. Prochazi proto
    // toutez cestou jako denni banka blizkeho terenu: nasobi se barvou slunce a dvojkou,
    // protoze LuantiBlend deli prumerem dvou bank. Bez toho by byl vzdaleny teren proti
    // blizkemu dvakrat tmavsi a na hranici LOD by byl videt sev.
    return color * clamp(shade, 0.0, 1.0) * pc.lightColor.rgb;
}

// Caustics vzdaleny teren NEMA. Zacinaji az za dosahem chunku, kde je hladina od oka
// stovky bloku a sit by mela na pixel nekolik ok — byl by z ni sum, ne svetlo.

float UnderwaterPath(float distance)
{
    float sea = pc.water.x;
    float cameraY = pc.cameraAndFogStart.y;

    // Kamera se pocita za ponorenou uz kousek nad hladinou — objektiv neni bod.
    // Vysvetleni v chunk_opaque.frag.
    bool cameraUnder = cameraY <= (sea + 0.3);
    bool fragmentUnder = vWorld.y <= sea;

    if (cameraUnder == fragmentUnder)
    {
        return cameraUnder ? distance : 0.0;
    }

    float span = vWorld.y - cameraY;
    float toSurface = abs(span) < 1e-4 ? 0.0 : clamp((sea - cameraY) / span, 0.0, 1.0);

    float path = distance * (cameraUnder ? toSurface : 1.0 - toSurface);

    // Max(0) tu musi byt — kamera se pocita za ponorenou uz nad hladinou a zaporna draha by
    // barvu zesilila. Vysvetleni v chunk_opaque.frag.
    // Minimalni draha vodou: tesne pod hladinou vyjde toSurface skoro nula a plaz by zustala
    // jasne zluta uprostred modre sceny. Vysvetleni v chunk_opaque.frag.
    return max(path, min(distance, 20.0));
}

vec3 ApplyWater(vec3 color, float path, float depthBelowSurface)
{
    vec3 transmittance = exp(-AbsorptionRatio * pc.water.z * path);
    vec3 downwelling = exp(-AbsorptionRatio * pc.water.y * max(depthBelowSurface, 0.0));

    float cameraDepth = max(0.0, pc.water.x - pc.cameraAndFogStart.y);
    float middle = mix(depthBelowSurface, cameraDepth, 0.5);

    vec3 scattered = WaterScatter * exp(-AbsorptionRatio * pc.water.y * middle);

    return (color * downwelling * transmittance) + (scattered * (1.0 - transmittance));
}

// TEPLOTA KRAJINY.
//
// je sytost v poradku. Skutecna krajina ma pasy: nekde travu do zluta a sucha, jinde do
// modrozelena a chladna, a mezi tim plynuly prechod.
//
// Pocita se ze SVETOVE POLOHY, ne z biomu. Biom by byl presnejsi, ale musel by se protahnout
// az do vrcholu, a to je dalsi bajt na kazdy vrchol v celem svete. Sum v meritku stovek
// bloku dela tyz dojem: velke plynule oblasti, ktere se nekryji s hranicemi chunku.
float TempNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);

    // Hladky prechod mezi mrizkovymi body, jinak by byly videt ctverce.
    f = f * f * (3.0 - (2.0 * f));

    // Ctyri rohy bunky. Hash je levny a staci — nejde o kvalitu sumu, ale o to,
    // aby se vzor neopakoval v dohledu.
    vec4 h = fract(sin(vec4(
        dot(i, vec2(127.1, 311.7)),
        dot(i + vec2(1.0, 0.0), vec2(127.1, 311.7)),
        dot(i + vec2(0.0, 1.0), vec2(127.1, 311.7)),
        dot(i + vec2(1.0, 1.0), vec2(127.1, 311.7)))) * 43758.5453);

    return mix(mix(h.x, h.y, f.x), mix(h.z, h.w, f.x), f.y);
}

vec3 Tinted(vec3 color, vec3 world)
{
    // Dve meritka: velke pasy pres stovky bloku a jemnejsi promena uvnitr nich.
    float warm = (TempNoise(world.xz * 0.0035) * 0.7) + (TempNoise(world.xz * 0.017) * 0.3);
    warm = (warm - 0.5) * 2.0;

    // JEN NA ZELEN. Kamen ani pisek se barvit nemaji — u nich by z toho byly skvrny.
    // Pozna se to podle toho, o kolik zelena prevysuje ostatni kanaly; travu a listi to
    // chytne, hlinu skoro ne a kamen vubec.
    float greenness = clamp((color.g - max(color.r, color.b)) * 3.5, 0.0, 1.0);

    // Teplo tahne k zlutozelene (vic cervene, min modre), chlad k modrozelene.
    vec3 shift = vec3(0.16, 0.02, -0.10) * warm;

    return clamp(color + (shift * greenness), 0.0, 1.0);
}

// SYTOST A KONTRAST NA ZAVER.
//
// Cela cesta barvy je jen nasobeni jasem, takze nejsvetlejsi mozny pixel je albedo textury
// jen se odtahne od sedi a stred se prohne do kontrastu.
//
// Merenim se potvrdilo, ze textury same o sobe mdle NEJSOU (sytost 0,97 az 1,00 proti
// referenci), takze se nesmi prehanet — jde o dorovnani toho, co ubere nasobeni jasem.
vec3 Vivid(vec3 color)
{
    // Luma podle citlivosti oka, ne prosty prumer. Prumer by zelenou podhodnotil a trava
    // by po zesyteni ujela do jedovate.
    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));

    // Odtazeni od sedi. Hodnota nad 1 sytost zvysuje.
    vec3 saturated = mix(vec3(luma), color, 1.12);

    // A JAS NAHORU, ne dolu. Prvni verze mela nasobitel 0,94 az 1,08, takze tmave plochy
    // jeste ztmavila — merenim vyslo, ze prumerny jas travy klesl z 0,277 na 0,253, tedy
    // presny opak toho, k cemu to melo slouzit. Ted zacina nad jednickou a se svetlem roste.
    return clamp(saturated * (1.08 + (0.10 * luma)), 0.0, 1.0);
}

void main()
{
    float horizontalDistance = length(vWorld.xz - pc.cameraAndFogStart.xz);
    if (horizontalDistance < pc.chunkOffset.w)
    {
        discard;
    }

    // ZADNY MIP BIAS.
    //
    // Byl tu, dokud se hledala pricina vlniteho vzoru na vzdalenem terenu a myslelo se,
    // ze ji dela opakovani textury pres velke bunky. Nebyla to pravda: vzor delaly svisle
    // steny teras, ktere mely vodorovne UV 0..1 pres celou sirku bunky misto na blok
    // (viz FarTerrain.AddSide). Bias navic pracoval PROTI anizotropni filtraci — jeden
    // tlacil uroven nahoru, druha dolu — a jedinym jistym vysledkem byl rozmazany obzor.
    vec4 texel = texture(uTextures, vec3(vTexCoord, vLayer));
    float shaded = vShade * vCloud;
    vec3 color = ApplyLightTint(texel.rgb * shaded, shaded);

    float waterPath = UnderwaterPath(vDistance);
    color = ApplyWater(color, waterPath, max(0.0, pc.water.x - vWorld.y));

    // NOCNI ZTLUMENI AZ ZA PODVODNIM ROZPTYLEM.
    //
    // ApplyWater pricita WaterScatter, coz je PEVNA barva nezavisla na denni dobe.
    // Kdyz se tlumilo drive (uvnitr ApplyLightTint), rozptyl se pricetl az potom a
    // neztlumeny — dno pod hladinou proto v noci svitilo modrozelene, i kdyz sama
    // hladina uz tmava byla. Vypadalo to, ze "sviti vzdalena voda", ale svitilo dno.
    //
    // Blizky teren to ma stejne (chunk_opaque.frag), proto tam problem nebyl.
    color *= mix(pc.lightColor.w, 1.0, clamp(pc.frameTuning.w, 0.0, 1.0));


    float airPath = vDistance - waterPath;
    float fogStart = pc.cameraAndFogStart.w;
    float fogEnd = pc.fogColorAndFogEnd.w;

    float fog = clamp((airPath - fogStart) / max(fogEnd - fogStart, 0.001), 0.0, 1.0);
    fog *= fog;

    FragColor = vec4(mix(Tinted(color, vWorld), pc.fogColorAndFogEnd.xyz, fog), 1.0);
}
