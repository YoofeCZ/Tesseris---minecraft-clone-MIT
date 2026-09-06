#version 450
#extension GL_GOOGLE_include_directive : require

// Pruhledny a vyrezovy pruchod: sklo, voda a rostliny.
//
// Proti chunk_opaque.frag ma navic ALFA TEST. U rostlin je textura z vetsiny dira
// a sklo ma vlastni pruhlednost, takze se tu discard opravdu uplatni.

layout(location = 0) in vec2 vTexCoord;
layout(location = 1) in float vLayer;
layout(location = 2) in float vShade;
layout(location = 3) in float vDistance;
layout(location = 4) in vec3 vWorld;

// Je nad fragmentem opravdu voda? 1 = ano nebo nevime, 0 = prokazatelne sucho (strop).
// Vysvetleni v chunk_opaque.frag.
layout(location = 5) in float vSubmerged;
layout(location = 6) in float vCloud;
layout(location = 7) in float vBlockLight;
layout(location = 8) in float vFlame;

layout(set = 0, binding = 0) uniform sampler2DArray uTextures;

#include "luanti_light.glsl"

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 cameraAndFogStart;
    vec4 fogColorAndFogEnd;
    vec4 water;
    vec4 chunkOffset;

    // Svetelne matice po trech vec4 misto mat4, viz chunk_opaque.frag.
    vec4 lightRow0;               // 128..143
    vec4 lightRow1;               // 144..159
    vec4 lightRow2;               // 160..175
    vec4 unusedRow0;           // 176..191
    vec4 unusedRow1;           // 192..207
    vec4 unusedRow2;           // 208..223

    vec4 frameTuning;          // x = sila stinu, y = vaha aktualni epochy (< 0 = vypnuto), z = pomer kaskad, w = denni svetlo
    vec4 lightColor;            // xyz = barva svetla, w = svit mesice
} pc;

layout(location = 0) out vec4 FragColor;

// Vysvetleni vseho je v chunk_opaque.frag, kopie k porovnani ve water_common.glsl.txt.
const vec3 WaterScatter = vec3(0.13, 0.34, 0.44);
const vec3 AbsorptionRatio = vec3(2.17, 0.71, 0.46);
// Zustava jako zaloha pro pruchody, ktere denni dobu neznaji; zive kresleni bere barvu
// z pc.lightColor, ktera se meni s pohybem slunce.
const vec3 SunTint = vec3(1.00, 0.955, 0.845);
const vec3 SkyTint = vec3(0.72, 0.79, 0.99);
// Vysvetleni je v chunk_opaque.frag. Preklad je bez -I, takze se kod musi zkopirovat;
// kdyz se meni tam, MUSI se zmenit i tady.
// Vzorec je prevzaty ze Shadertoy MdlXz8 (Dave_Hoskins) se svolenim autora.
const float CausticTau = 6.28318530718;
const float CausticIntensity = 0.005;

// Jedna vrstva vzoru. Mod z ni dela dlazdici — proto se v Caustics skladaji dve.
float CausticLayer(vec2 uv, float time)
{
    vec2 q = mod(uv * CausticTau, CausticTau) - 250.0;
    vec2 i = q;

    float base = (time * 0.5) + 23.0;
    float c = 1.0;

    for (int n = 0; n < 5; n++)
    {
        // Cas se meni s kazdou iteraci — bez toho vychazi vzor vyrazne chudsi a pravidelnejsi.
        // Vysvetleni v chunk_opaque.frag.
        float t = base * (1.0 - (3.5 / float(n + 1)));

        i = q + vec2(cos(t - i.x) + sin(t + i.y), sin(t - i.y) + cos(t + i.x));

        c += 1.0 / length(vec2(q.x / (sin(i.x + t) / CausticIntensity),
                               q.y / (cos(i.y + t) / CausticIntensity)));
    }

    c /= 5.0;
    c = 1.17 - pow(c, 1.4);

    return pow(abs(c), 8.0);
}

vec3 Caustics(vec2 p, float time, float depth, float distance)
{
    float focus = (1.0 - exp(-depth * 0.35)) * exp(-depth * 0.055);
    float fade = exp(-distance * 0.012);

    float strength = focus * fade;

    // Tlumeni se pocita driv nez vzor: kde je efekt stejne neviditelny, usetri to dvacet
    // goniometrickych funkci na fragment.
    if (strength < 0.004)
    {
        return vec3(1.0);
    }

    // Dve vrstvy v nesouměřitelnem meritku a natocene, aby nebyla videt dlazdice.
    // Vysvetleni v chunk_opaque.frag.
    vec2 turned = vec2((p.x * 0.6235) - (p.y * 0.7818),
                       (p.x * 0.7818) + (p.y * 0.6235));

    float first = CausticLayer(p * 0.11, time * 0.55);
    float second = CausticLayer(turned * 0.079, (time * 0.43) + 7.3);

    float light = max(first, second);

    // Kaustiky nejsou bile — svetlo uz proslo kusem vody, takze mu chybi cervena.
    vec3 tint = vec3(0.62, 0.94, 1.0);

    // Sila je zamerne nizka — vyssi hodnota vytahne podklad do saturace.
    return vec3(1.0) + (light * strength * 0.55 * tint);
}

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

    // Sachty svetla se tu zkousely a jsou pryc — pres dno z nich byly svetle vlnite pruhy.
    // Vysvetleni v chunk_opaque.frag.
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
    vec4 texel = texture(uTextures, vec3(vTexCoord, vLayer));

    // ANIMOVANY PIXELOVY OHEN NA DVOU KRIZOVYCH PLOCHACH.
    // Maska meni siluetu bez deformace drevene tycky. Nahodny okraj a nekolik jisker
    // zabrani tomu, aby plamen pusobil jako nehybny obrazek.
    bool flameFragment = vFlame > 0.5 && vFlame < 1.5 && vTexCoord.y < 0.48;
    bool torchStickFragment = vFlame > 1.5;
    if (flameFragment)
    {
        float time = pc.water.w;
        vec2 pixel = floor(vec2(vTexCoord.x * 22.0, vTexCoord.y * 46.0));
        vec2 p = vec2((pixel.x + 0.5) / 22.0, (pixel.y + 0.5) / 46.0);
        float height = clamp(p.y / 0.48, 0.0, 1.0);
        float tick = floor(time * 11.0);
        float sway = sin(time * 7.3 + vWorld.x * 1.7 + vWorld.z * 2.1)
            * (1.0 - height) * 0.12;
        float lick = sin((p.y * 31.0) - time * 13.0) * 0.035;
        float width = mix(0.055, 0.39, smoothstep(0.0, 1.0, height));
        float hash = fract(sin(dot(pixel + vec2(tick, tick * 0.37),
            vec2(12.9898, 78.233))) * 43758.5453);
        width *= mix(0.78, 1.10, hash);

        float distanceFromFlame = abs((p.x - 0.5) - sway - lick);
        float body = 1.0 - step(width, distanceFromFlame);
        float sparkColumn = 1.0 - step(0.055, abs((p.x - 0.5) - sway * 1.7));
        float sparks = sparkColumn * step(0.91, hash)
            * (1.0 - smoothstep(0.0, 0.34, height));
        float alpha = max(body, sparks);

        float inner = 1.0 - smoothstep(width * 0.18, width * 0.72, distanceFromFlame);
        float hot = inner * smoothstep(0.18, 0.92, height);
        vec3 outerColor = mix(vec3(1.0, 0.16, 0.015), vec3(1.0, 0.48, 0.025), height);
        vec3 flameColor = mix(outerColor, vec3(1.0, 0.94, 0.28), hot);
        flameColor *= 0.92 + 0.16 * sin(time * 17.0 + hash * 6.28318);
        texel = vec4(flameColor, alpha);
    }

    // ALFA TEST S PEVNYM PRAHEM.
    //
    // Zkousel se tu prah rostouci se vzdalenosti, ale byla to zaplata na spatnou vec:
    // mipmapy tehdy braly alfu jako maximum a rozlevaly obsah rostliny do prazdna, takze
    // se stebla roztahla az k horni hrane dlazdice. Rostouci prah to jen schovaval v dalce,
    // zblizka kriz zustaval.
    //
    // Mipmapy ted zachovavaji POKRYTI (viz GlTextureArray.PreserveCoverage), takze se nic
    // nikam nerozliva a staci pevny prah — tentyz jako u vzdaleneho terenu.
    if (texel.a < 0.02)
    {
        discard;
    }

    float sunlit = 1.0;
    float shaded = vShade * sunlit * vCloud;

    // Táž úvaha jako v chunk_opaque.frag: denní banka se násobí barvou slunce, noční
    // banka pevným 1,04, a poměr obou rozhoduje, kolik je které.
    vec3 color = texel.rgb * LuantiBlend(
        clamp(shaded, 0.0, 1.0), clamp(vBlockLight, 0.0, 1.0), pc.lightColor.rgb);

    // barva tedy nezavisi na slunci ani na ulozenem svetle okolniho voxelu. Dreveny drik
    // zustava materialem, ale dostava teply odraz ohne, nejsilnejsi u jeho horniho konce.
    if (flameFragment)
    {
        color = texel.rgb * 1.18;
    }
    else if (torchStickFragment)
    {
        float nearFlame = 1.0 - smoothstep(0.49, 0.96, vTexCoord.y);
        vec3 selfLight = texel.rgb * mix(
            vec3(0.72, 0.48, 0.28),
            vec3(1.42, 0.88, 0.42),
            nearFlame);
        color = max(color, selfLight);
    }

    // Nasobeni prizname zaridi, ze suchy fragment pod urovni more nema ani kaustiky, ani
    // ztmaveni hloubkou — obojí se pocita prave z teto hodnoty.
    float depthBelow = max(0.0, pc.water.x - vWorld.y) * vSubmerged;

    if (depthBelow > 0.0)
    {
        float upward = clamp(vShade, 0.0, 1.0);
        color *= mix(vec3(1.0), Caustics(vWorld.xz, pc.water.w, depthBelow, vDistance), upward);
    }

    // Suchy fragment pod urovni more (jeskyne, podzemi) nesmi dostat vodni mlhu.
    float waterPath = UnderwaterPath(vDistance) * vSubmerged;
    color = ApplyWater(color, waterPath, depthBelow);

    // GLOBALNI NOCNI ZTLUMENI.
    //
    // Bez tohohle je v noci videt skoro jako ve dne: LuantiBlend vyse sice nese BARVU
    // svetla (pc.lightColor.rgb), ale ne jeho UROVEN. Vzdaleny teren se pritom tlumi
    // (far.frag vola ApplyLightTint), takze popredi bylo jasnejsi nez obzor za nim.
    //
    // Nasobi se jen urovni, ne celym ApplyLightTint: ten navic michá SkyTint s barvou
    // slunce, kterou uz LuantiBlend zapocital — pouzit ho cely by barvu nanesl dvakrat.
    //
    // frameTuning.w je denni svetlo (0 v noci, 1 ve dne), lightColor.w mesicni svit.
    // V noci tedy zbude mesicni uroven, ve dne plna.
    color *= mix(pc.lightColor.w, 1.0, pc.frameTuning.w);


    float airPath = vDistance - waterPath;
    float fogStart = pc.cameraAndFogStart.w;
    float fogEnd = pc.fogColorAndFogEnd.w;

    float fog = clamp((airPath - fogStart) / max(fogEnd - fogStart, 0.001), 0.0, 1.0);
    fog *= fog;

    // Bez Vivid, stejne jako v chunk_opaque.frag: Luanti barvu textury nesyti.
    FragColor = vec4(mix(Tinted(color, vWorld), pc.fogColorAndFogEnd.xyz, fog), texel.a);
}
