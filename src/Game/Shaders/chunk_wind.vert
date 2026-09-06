#version 450
#extension GL_GOOGLE_include_directive : require
#include "cloud_common.glsl"

// Vrchol chunku: pozice v souradnicich chunku, dlazdicovaci UV, vrstva texture array
//
// Poznamky k tomuhle shaderu patri do C# k ChunkRenderer, ne sem. Zdroj se drzi
// v ASCII: puvodne kvuli ovladaci AMD, ktery si na diakritice rozbil parser, a i kdyz
// se ted preklada offline pres glslangValidator, neni duvod na tom neco menit.

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in float aLayer;
layout(location = 3) in float aShade;
layout(location = 4) in float aBlockLight;

// Slozky jsou sbalene do vec4, aby nebylo nutne resit zarovnani vec3 na 16 bajtu.
// Rozvrzeni musi byt SHODNE ve vertex i fragment shaderu - Vulkan ma jeden push
// constant blok na cely pipeline, ne jeden na stupen.
layout(push_constant) uniform Push
{
    mat4 viewProjection;      // 0..63
    vec4 cameraAndFogStart;   // 64..79    xyz = kamera, w = zacatek mlhy
    vec4 fogColorAndFogEnd;   // 80..95    xyz = barva mlhy, w = konec mlhy
    vec4 water;               // 96..111   x = hladina, y = pohltivost, z = barva hloubky
    vec4 chunkOffset;         // 112..127  xyz = posun chunku, w = blizky rez
    vec4 lightRow0;
    vec4 lightRow1;
    vec4 lightRow2;
    vec4 unusedRow0;
    vec4 unusedRow1;
    vec4 unusedRow2;
    vec4 frameTuning;
    vec4 lightColor;
} pc;

layout(location = 0) out vec2 vTexCoord;
layout(location = 1) out float vLayer;
layout(location = 2) out float vShade;
layout(location = 3) out float vDistance;

// Svetovy bod fragmentu.
//
// Y rika, kolik vody je nad nim, tedy jak moc ho voda pohltila. XZ potrebuji caustics:
// svetlo se pod hladinou soustreduje podle toho, kde je hladina zakrivena, a to je
// funkce svetovych souradnic. Drive to byla jen vyska.
layout(location = 4) out vec3 vWorld;

// JE NAD FRAGMENTEM OPRAVDU VODA? 1 = ano nebo nevime, 0 = prokazatelne sucho.
//
// Sama vyska to nerozhodne: jeskyne pod urovni more je pod ni skoro vzdycky, a shader ji
// proto zaliv modrou clonou, i kdyz je uvnitr sucho. Mesher to naopak pozna — sonduje
// vzhuru a hleda, jestli driv narazi na kapalinu nebo na strop.
layout(location = 5) out float vSubmerged;
layout(location = 6) out float vCloud;
layout(location = 7) out float vBlockLight;
layout(location = 8) out float vFlame;

// VITR.
//
// Posouva vrcholy porostu. Pouziva se JEN pro vyrezovy pruchod (trava, kytky, listi) -
// pevne bloky se hybat nesmi, jinak by se rozjely stены terenu a vznikly by mezi nimi
// spary.
//
// Tvar vlny: dve sinusovky s nesoumeritelnymi periodami, aby se vzor neopakoval po par
// metrech. Faze bere SVETOVOU polohu, takze soused se hyba jinak nez ja a porost se vlni
// jako pole, ne jako jeden kus.
//
// Amplituda roste s vyskou uvnitr bloku: spodek stebla drzi u zeme, vrsek se ohyba
// nejvic. Bez toho by cely trs klouzal do strany i s korenem.
//
// Mesher do nej zabalil tri pole (viz ChunkMesher.EmitCross):
//   +2      nad blokem je voda
//   +4  * n  n = kolikaty clanek rostliny zdola
//  +32  * m  m = vyska celeho sloupce minus jedna
//
// Poradi je ZAVAZNE: od nejvyssiho pole dolu. Obracene by step(1.5, ...) videlo
// clanek misto vody a sucha rostlina by dostala vodni utlum.
//
// PARAMETR SE NESMI JMENOVAT "packed" - je to v GLSL rezervovane slovo. glslangValidator
// i ovladac AMD to prezili, ovladac NVIDIE ne: hlasi
// "syntax error, unexpected identifier, expecting ')' at token shade", tedy chybu az na
// dalsim parametru, takze z hlasky vubec neni poznat, ze vadi jmeno prvniho.
//
// Deleni ctyrkou a dvaatriceti je mocnina dvojky, tedy v plovouci carce presne.
// floor(), ne mod() - mod() se u zapornych cisel chova jinak a je to ticha past.
void UnpackShade(
    float packedShade, out float shade, out float submerged, out float segment, out float total,
    out float plant, out float blockLight)
{
    blockLight = floor(packedShade / 1024.0);
    packedShade -= blockLight * 1024.0;
    blockLight /= 15.0;

    // NEJVYSSI POLE PRVNI. Priznak drobneho porostu lezi nad vsemi ostatnimi (maximum
    // bez nej je 255), takze se musi odloupnout driv nez cokoli jineho — jinak by se
    // pripocetl k vysce sloupce a vitr by z travy udelal osmiblokovou chaluhu.
    plant = floor(packedShade / 256.0);
    packedShade -= plant * 256.0;

    total = floor(packedShade / 32.0);
    packedShade -= total * 32.0;

    segment = floor(packedShade / 4.0);
    packedShade -= segment * 4.0;

    submerged = step(1.5, packedShade);
    shade = packedShade - (submerged * 2.0);

    // Mesher uklada vysku minus jedna, aby se do tri bitu vesel sloupec az osmi bloku.
    total += 1.0;
}

vec3 WindOffset(vec3 world, vec2 uv, float segment, float total, float time)
{
    float phase = (world.x * 0.42) + (world.z * 0.31) + (time * 1.35);
    float gust = (world.x * 0.11) - (world.z * 0.09) + (time * 0.42);

    // Naraz vetru: pomala vlna, ktera obcas zesili a obcas skoro ustane.
    float strength = 0.45 + (0.55 * sin(gust));

    vec2 sway = vec2(sin(phase), cos(phase * 0.83)) * strength;

    // VYSKA SE MERI OD PATY CELE ROSTLINY, NE OD PATY BLOKU.
    //
    // Drive to byl fract(world.y) a bylo to spatne: vrchol na hranici bloku ma svetove Y
    // presne cele, takze fract vratil nulu - a spicka stebla dostala tutez nulu jako pata.
    // Zbyl jen konstantni clen amplitudy, ktery posouval celou kartu jako jeden kus.
    //
    // Texturova souradnice to rekne spolehlive: V roste smerem dolu (mesher dava spodnim
    // rohum V=1 a hornim V=0), takze 1-V je podil vysky od paty karty.
    // Cislo clanku rekne, kolik CELYCH bloku rostliny je pod touhle kartou. Chaluha
    // o peti blocich je pak jedna vec: spodek drzi u dna a ohyba se az vrsek.
    float height = segment + (1.0 - uv.y);

    // Podil vysky CELE rostliny: 0 u paty, 1 u spicky. Bez deleni celkovou vyskou by
    // se profil nasytil hned nad prvnim blokem a sloupec by se naklanel jako tuha tyc.
    float t = height / total;

    // Spodni ctvrtina rostliny drzi u zeme - tam je koren a ten se hybat nema.
    float rooted = clamp((t - 0.25) / 0.75, 0.0, 1.0);

    // Druha mocnina: ohyb se rozjizdi pozvolna a nejvic az u spicky, jako se ohyba stéblo.
    // Linearni prubeh vypada, jako by se stéblo lamalo v jednom bode.
    //
    // Amplituda roste s delkou stvolu - delsi rostlina se ohne dal. Jednoblokovy porost
    // ma total = 1, takze pro travu a listi z toho vyjde presne to, co pred zmenou.
    float amount = 0.17 * total * rooted * rooted;

    return vec3(sway.x, 0.0, sway.y) * amount;
}

void main()
{
    float shade;
    float submerged;
    float segment;
    float total;
    float plant;
    float blockLight;
    UnpackShade(aShade, shade, submerged, segment, total, plant, blockLight);

    vec3 world = aPosition + pc.chunkOffset.xyz;

    // Pomocne cele pole v aShade rozlisuje emisivni casti pochodne. Hodnota 1 je plamen,
    // hodnota 2 je pevny drik. Do fragmentu ji posilame beze ztraty; skutecne blokove
    // svetlo prichazi samostatnym atributem aBlockLight.
    float flame = blockLight * 15.0;

    // Values 0 and 1 are foliage/vegetation. Values 2 and 3 are static cutout geometry:
    // ground clutter, ladders, doors and glass panes must not sway in the wind.
    if (plant < 1.5)
    {
        world += WindOffset(world, aTexCoord, segment, total, pc.water.w);
    }
    float distance = length(world - pc.cameraAndFogStart.xyz);

    gl_Position = pc.viewProjection * vec4(world, 1.0);

    vTexCoord = aTexCoord;
    vLayer = aLayer;

    vSubmerged = submerged;
    vShade = shade;
    vBlockLight = aBlockLight;
    vFlame = flame;

    // Tataz vzdalenost, ze ktere se pocitalo morfovani. Znovu ji merit z morfovane pozice
    // by dalo o kousek jine cislo a mlha by se na hranici pasma nepatrne zlomila.
    vDistance = distance;

    vWorld = world;
    // nic takoveho nema — mrak je u nej jen vrstva geometrie nad hlavou.
    vCloud = 1.0;
}
