#version 450
#extension GL_GOOGLE_include_directive : require
#include "cloud_common.glsl"

// Obloha pocitana ze smeru pohledu.
//
// PROC VUBEC. Nebe bylo do teto chvile jedina plocha barva v ClearColor. Na snimku
// plochu okamzite precte jako namalovane pozadi, ne jako prostor.
//
// JAKY MODEL. Neni to Preethamuv ani Hosek-Wilkinuv model; oba chteji LUT nebo poradnou
// hrst koeficientu a resi presnou barvu oblohy pri dane turbiditě. Tady staci ta cast
// jejich chovani, ktera je videt:
//
//   1. Rayleighuv rozptyl je silnejsi pro kratke vlnove delky, takze vzhuru — kde je
//      vrstva vzduchu tenka — projde hlavne modra. To dela SYTY NADHLAVNIK.
//   2. K obzoru vede paprsek mnohem delsi drahou, rozptyli se uz i delsi vlnove delky
//      a barva vybledne skoro do bila. To dela SVETLY OBZOR.
//   3. Miev rozptyl na aerosolech je silne dopredny, takze kolem slunce vznika zare,
//      ktera se s uhlem rychle ztraci.
//
// Prechod 1 -> 2 neni linearni v uhlu, ale zhruba v prevracene delce drahy. Mocnina
// s exponentem kolem 0,4 to trefi dost dobre na to, aby rozdil nebyl poznat, a stoji
// jednu instrukci misto integralu podel paprsku.
//
// Zdroj se drzi v ASCII stejne jako ostatni shadery, viz chunk.vert.

layout(location = 0) in vec3 vDirection;

layout(set = 0, binding = 0) uniform sampler2DArray uTextures;

layout(push_constant) uniform Push
{
    // Musi presne odpovidat bloku ve sky.vert, kde je i vysvetleni.
    vec4 rayRight;
    vec4 rayUp;
    vec4 rayForward;
    vec4 cameraAndTime;
    vec4 sunAndSea;
    vec4 zenithAndUnder;
    vec4 horizon;
} pc;

layout(location = 0) out vec4 FragColor;

// Musi sedet na tychz konstantach v chunk_opaque.frag, viz water_common.glsl.txt.
const vec3 WaterScatter = vec3(0.13, 0.34, 0.44);
const vec3 AbsorptionRatio = vec3(2.17, 0.71, 0.46);

// Barva slunecniho kotouce. Nad 1 schvalne — slunce je jasnejsi nez papir a bez toho
// splyne s okolnim nebem.
const vec3 SunColor = vec3(1.6, 1.5, 1.3);

// SPOLECNA FUNKCE OBLOHY.
//
// POZOR: tatáz funkce je i ve water.frag, protoze hladina odrazi nebe a odraz musi
// sedet na tom, co je nad ni doopravdy videt. Preklad shaderu je bez -I, takze se
// sdilet hlavickou neda. Kdyz se meni tady, MUSI se zmenit i tam.
// UHLOVY POLOMER SLUNECNIHO CTVERCE, merene jako tangens.
//
// Puvodni kotouc byl smoothstep(0.9992, 0.9997) nad kosinem uhlu, coz odpovida zhruba
// 2,3 stupne — tedy ctyrikrat vic nez skutecne slunce (pul stupne). Zamerne: realne slunce
// vypada ve hre jako vada obrazu. Tangens 2,3 stupne je 0,04.
const float SunSize = 0.042;

vec3 SkyColor(vec3 dir, vec3 sunDir, vec3 zenith, vec3 horizon, float time)
{
    // Nad obzorem: 0 na obzoru, 1 v nadhlavniku.
    float up = clamp(dir.y, 0.0, 1.0);

    // Exponent 0,42: prechod se drzi nizko u obzoru, presne jak to dela delsi draha
    // paprsku. S linearnim prechodem vypada nebe jako pruh barvy pres pulku obrazu.
    vec3 color = mix(horizon, zenith, pow(up, 0.42));

    // POD obzorem barva dal tmavne. Vidi se tam jen tam, kam nedosahne zadna geometrie
    // — za dohledem a v derach — a plynuly prechod je tam lepsi nez ostry rez.
    float down = clamp(-dir.y, 0.0, 1.0);
    color = mix(color, horizon * 0.72, down * 0.6);

    float sun = max(dot(normalize(dir), sunDir), 0.0);

    // SIROKY OPAR ZUSTAVA KULATY, a to schvalne: rozptyl v atmosfere je izotropni, takze
    // zavisi jen na uhlu od slunce, ne na smeru. Kulaty je tu spravne.
    //
    // TESNA ZARE ODSUD ZMIZELA. Byval tu jeste `pow(sun, 260.0) * 0.85`, ktery kreslil ostrou
    // svatozar tesne u slunce — jenze i ta se pocitala z kosinu uhlu, tedy KRUH, a byla tak
    // jasna, ze saturovala do bila a hranaty kotouc pod sebou uplne schovala. Slunce proto
    // vypadalo kulate i potom, co se kotouc prepsal na ctverec. Zar se ted kresli nize
    // touz hranatou metrikou jako kotouc.
    color += SunColor * pow(sun, 5.0) * 0.09;

    // HRANATE SLUNCE A PAPRSKY.
    //
    // Kotouc se drive kreslil primo z kosinu uhlu (smoothstep nad 'sun'), coz z definice
    // dava KRUH — kosinus zavisi jen na uhlu, ne na tom, kterym smerem od slunce se merí.
    // Hranaty tvar proto potrebuje jinou metriku: smer se promitne do roviny kolme ke
    // slunci a misto delky se vezme vetsi ze slozek (Cebysevova vzdalenost). Mnozina bodu
    // se stejnou takovou vzdalenosti je CTVEREC.
    vec3 d = normalize(dir);
    float toward = dot(d, sunDir);

    // Za zady je slunce jen tehdy, kdyz je toward zaporne. Deleni jim by jinak prumet
    // prevratilo a ctverec by se objevil na protilehle strane oblohy.
    if (toward > 0.001)
    {
        // Libovolna baze v rovine kolme ke slunci. Vyber pomocneho vektoru osetruje pripad,
        // kdy slunce miri presne vzhuru a cross by vysel nulovy.
        vec3 helper = abs(sunDir.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
        vec3 right = normalize(cross(helper, sunDir));
        vec3 top = cross(sunDir, right);

        // Delenim 'toward' vznikne perspektivni prumet, takze ctverec ma na obloze vsude
        // stejnou velikost misto aby se u obzoru protahoval.
        vec2 local = vec2(dot(d, right), dot(d, top)) / toward;

        float box = max(abs(local.x), abs(local.y));

        // PAPRSKY JSOU PRYC, A UZ SE NEVRACEJI.
        //
        // Byly to dve uhlove vlny s nesoumeritelnym poctem ramen, ktere se proti sobe pomalu
        // otacely. Vypadalo to jako hvezdice nakreslena pres nebe, ne jako svetlo — v blokove
        // hre uz vubec, protoze tam neni cim je oduvodnit: mraky ani prach ve vzduchu nejsou,
        // takze paprsek nema na cem vzniknout.
        //
        // Zar kolem kotouce zustava. Ta je fyzikalne v poradku (rozptyl v atmosfere kolem
        // jasneho zdroje) a hlavne dela slunce jasnym mistem obrazu, aniz by kreslila vzor.

        // HRANATA ZARE. Nahrazuje zrusene pow(sun, 260) a pocita se z TEHOZ ctverce jako
        // kotouc, takze uz tvar neprebiji, ale zvyrazni ho. Vzdalenost se meri od OKRAJE
        // kotouce, ne od stredu — jinak by zar uvnitr rostla a okraj by se rozmazal.
        //
        // Sila 0,45 a strmy pokles: pri 0,9 a pozvolnem poklesu saturovala do bila jeste
        // pulku polomeru za okrajem, coz rohy ctverce zaoblilo.
        color += SunColor * 0.45 * exp(-max(0.0, box - SunSize) * 60.0);

        // Samotny ctverec. Prechod na poslednich osmnacti procentech polomeru staci na to,
        // aby okraj nebyl zubaty, a je dost ostry, aby tvar zustal hranaty.
        // OKRAJ SE VYHLAZUJE PODLE PIXELU, NE PODLE POLOMERU. Tohle je oprava toho, proc
        // slunce pri otaceni myší poskakovalo.
        //
        // Drive tu byl smoothstep pres poslednich osmnact procent polomeru, coz je zhruba
        // ctyri a pul pixelu — na pohled dost. Jenze zar kolem kotouce uz sama presahuje
        // jednicku, takze se prechod SATURUJE do bila drive, nez staci probehnout, a z mekke
        // rampy zbude tvrda hrana siroka pixel. Tvrda hrana se pri sub-pixelovem posunu
        // preklapi po CELYCH sloupcich a to je videt jako uskakovani.
        //
        // Zbytek sceny to nedela, protoze ma MSAA — jenze obloha je fullscreen trojuhelnik,
        // kde vsechny vzorky pixelu dostanou tutez barvu, takze na tvary spocitane
        // v shaderu je MSAA slepe. Vyhladit se musi analyticky.
        //
        // Naměřeno pri rovnomernem otaceni o 0,03 stupne na snimek, teziste kotouce:
        // smerodatna odchylka kroku klesla z 3,193 px na 0,095 px a kroky prestaly menit
        // znamenko. Puvodne bylo kolisani vetsi nez samotny pohyb.
        //
        // fwidth(box) je zmena metriky na jeden pixel, takze prechod vyjde vzdy stejne
        // siroky bez ohledu na rozliseni a rozevreni. Strop je proti tomu, aby se u obzoru,
        // kde deleni 'toward' zvedne derivace do nebe, kotouc nerozmazal pres pul oblohy.
        float edge = clamp(fwidth(box) * 1.5, 0.0002, SunSize * 0.5);

        color = mix(color, SunColor, 1.0 - smoothstep(SunSize - edge, SunSize + edge, box));
    }

    return color;
}

// POHLED NA OBLOHU ZPOD HLADINY.
//
// Predchozi verze delala mix(obloha, modra, 0,7), tedy PLOSNE ztmaveni celeho nebe o dve
// tretiny. Gradient, ktery SkyColor poctive spocita, tim zeslabl na tretinu a slunce se
// v tom utopilo — na snimku z toho byla jednolita modra placka pres pulku obrazu.
//
// ZADNE SNELLOVO OKNO. Predchozi verze tu pocitala uplny vnitrni odraz a kreslila okno
// s ostrym okrajem. Je to spravna fyzika, ale herne to nefunguje: polomer okna na hladine
// je hloubka krat tangens kritickeho uhlu, takze cim blize je hrac hladine, tim MENSI
// svetly kruh — svet se pri vyplouvani zaviral misto aby se otviral. Blokove hry to taky
// nedelaji. Zduvodneni je delsi ve water.frag.
//
// Zustava obloha utlumena vodou podle toho, jak dlouhou drahou k oku vede.
//
// Musi to sedet na podvodni vetvi ve water.frag: sky pass kresli tam, kam nedosahne
// geometrie hladiny, a kdyby pocital neco jineho, vznikla by mezi nimi videtelna cara.
vec3 UnderwaterSky(
    vec3 dir, float sea, float cameraY, float absorption, vec3 drySky)
{
    float cosI = clamp(dir.y, 0.0, 1.0);

    // Nahoru obloha, dolu voda. Prechod je mekky, aby na vodorysce nevznikla cara.
    vec3 color = mix(
        WaterScatter * 0.55,
        drySky,
        smoothstep(-0.25, 0.15, dir.y));

    // Utlum na draze oko -> hladina. Delka je hloubka deleno kosinem uhlu od svislice;
    // u obzoru by divergovala, proto je jmenovatel zdola omezeny.
    float depth = max(sea - cameraY, 0.0);
    float path = depth / max(cosI, 0.06);

    vec3 transmittance = exp(-AbsorptionRatio * absorption * path);
    vec3 scattered = WaterScatter * exp(-AbsorptionRatio * absorption * depth);

    return (color * transmittance) + (scattered * (1.0 - transmittance));
}

void main()
{
    vec3 dir = normalize(vDirection);

    vec3 sky = SkyColor(dir, pc.sunAndSea.xyz, pc.zenithAndUnder.xyz, pc.horizon.xyz, pc.cameraAndTime.w);
    vec4 cloud = pc.cameraAndTime.y < CloudBaseHeight
        ? CloudVolume(
            pc.cameraAndTime.xyz, dir, pc.sunAndSea.xyz,
            pc.zenithAndUnder.xyz, pc.horizon.xyz, pc.cameraAndTime.w, gl_FragCoord.xy,
            pc.rayForward.w)
        : vec4(0.0);
    sky = mix(sky, cloud.rgb, cloud.a);

    // Keep the solar disc geometrically stable behind clouds. Per-pixel cloud alpha used
    // to bite a moving, irregular silhouette out of the square disc. The cloud field is
    // now sampled once along the disc centre and that single transmission is applied to
    // every disc pixel. A bank can dim or fully hide the sun, but cannot deform its edges.
    float towardSun = dot(dir, pc.sunAndSea.xyz);
    if (pc.cameraAndTime.y < CloudBaseHeight && towardSun > 0.001)
    {
        vec3 helper = abs(pc.sunAndSea.y) < 0.99
            ? vec3(0.0, 1.0, 0.0)
            : vec3(1.0, 0.0, 0.0);
        vec3 right = normalize(cross(helper, pc.sunAndSea.xyz));
        vec3 top = cross(pc.sunAndSea.xyz, right);
        vec2 local = vec2(dot(dir, right), dot(dir, top)) / towardSun;
        float box = max(abs(local.x), abs(local.y));
        float edge = clamp(fwidth(box) * 1.5, 0.0002, SunSize * 0.5);
        float disc = 1.0 - smoothstep(SunSize - edge, SunSize + edge, box);

        if (disc > 0.001)
        {
            vec4 centreCloud = CloudVolume(
                pc.cameraAndTime.xyz, pc.sunAndSea.xyz, pc.sunAndSea.xyz,
                pc.zenithAndUnder.xyz, pc.horizon.xyz, pc.cameraAndTime.w, vec2(17.0, 23.0),
                pc.rayForward.w);
            vec3 stableDisc = mix(SunColor, centreCloud.rgb, centreCloud.a);
            sky = mix(sky, stableDisc, disc);
        }
    }

    vec3 under = UnderwaterSky(
        dir, pc.sunAndSea.w, pc.cameraAndTime.y, pc.horizon.w, sky);

    FragColor = vec4(mix(sky, under, pc.zenithAndUnder.w), 1.0);

    // Textura se neuziva, ale deskriptor je soucasti layoutu pipeline. Bez odkazu by ji
    // prekladac vyhodil a validacni vrstva by hlasila nesoulad sady s layoutem.
    if (false)
    {
        FragColor += texture(uTextures, vec3(0.0));
    }
}
