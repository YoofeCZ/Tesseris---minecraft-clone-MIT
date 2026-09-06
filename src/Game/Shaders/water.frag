#version 450
#extension GL_GOOGLE_include_directive : require
#include "cloud_common.glsl"

// VODA.
//
// Vsechno, cim voda vypada jako voda, ZAVISI NA SMERU POHLEDU — do textury ani do vrcholu
// se to zapect neda. Puvodni verze byla dlazdice s alfou 205, tedy osmdesat procent
// neprusvitna modra deska.
//
// Ctyri jevy, serazene podle toho, kolik je jich videt:
//
//   1. FRESNEL. Kolmo dolu odrazi voda dve procenta a je pruhledna; skoro vodorovne
//      odrazi skoro vsechno a je z ni zrcadlo. Schlick 1994: R = R0 + (1-R0)(1-cos)^5,
//      R0 = ((n1-n2)/(n1+n2))^2 = 0,0204 pro n = 1,333 (potvrzeno tabulkou dielektrik
//      v dokumentaci Filamentu i memem Sebastiena Lagardeho).
//
//   2. ODRAZ OBLOHY. Fresnel rekne KOLIK se odrazi, tohle CO. Nebe se dopocita touz
//      funkci jako v sky.frag, jen pro odrazeny smer.
//
//   3. VLNY. Ne posunem vrcholu — greedy meshing dela z hladiny obri obdelniky o ctyrech
//      vrcholech, takze by nebylo co posouvat. Misto toho se ve fragmentu naklani NORMALA
//      souctem ctyr postupnych vln (GPU Gems 1, kapitola 1).
//
//   4. TRPYT SLUNCE. GGX, NE Blinn-Phong. Tohle je rozdil, na kterem prvni verze ztroskotala:
//      Blinn-Phong ma exponencialne padajici chvost, takze odlesk ma ostry okraj a za nim
//      neni nic — pri uzke mocnine z nej vyjdou bile blafy s tvrdymi hranami. GGX ma chvost
//      radove 1/uhel^4, tedy uzke jasne jadro a kolem nej dlouhou slabou zar. Presne to dela
//      trpytive pole citelnym na desitky metru misto jedne prepalene skvrny.
//
// POHLED ZESPODU JE PROSTE PRUHLEDNY. Zduvodneni, proc se tu NEPOCITA uplny vnitrni odraz
// a Snellovo okno, je u vypoctu Fresnela niz.
//
// PUVOD TECHNIK. Vzorce jsou z publikovanych praci (Schlick 1994, Trowbridge-Reitz 1975,
// GPU Gems 1 kap. 1) nebo z volne licencovanych zdroju; kod je psany znovu. Dva postupy
// jsou prevzate jako MYSLENKA z Velorenu (GPL-3.0, kod NEpouzit): utlum vlnove normaly
// se vzdalenosti a orez odrazoveho paprsku nad obzor.
//
// Zdroj se drzi v ASCII stejne jako ostatni shadery, viz chunk.vert.

layout(location = 0) in vec2 vTexCoord;
layout(location = 1) in float vLayer;
layout(location = 2) in float vShade;
layout(location = 3) in float vDistance;
layout(location = 4) in vec3 vWorld;

layout(set = 0, binding = 0) uniform sampler2DArray uTextures;

// KOPIE HOTOVE SCENY a HLOUBKA. Obojí zaridi ChunkRenderer tim, ze pred vodnim pruchodem
// necha GlRenderer.CaptureSceneForWater rozdelit snimek: obraz se zkopiruje stranou
// a hloubka se prepne na cteni. Cist primo z prilohy, do ktere se zrovna kresli, Vulkan
// zakazuje.
layout(set = 0, binding = 1) uniform sampler2D uScene;
layout(set = 0, binding = 2) uniform sampler2D uDepth;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 cameraAndFogStart;
    vec4 fogColorAndFogEnd;
    vec4 water;               // x = hladina, y = pohltivost hloubkou, z = pohltivost drahou, w = cas
    vec4 chunkOffset;

    // DENNI DOBA MUSI DOJIT I SEM. Blok se driv koncil o radek vyse, takze hladina barvu
    // svetla ani uroven dne vubec nevidela — a v noci zustala jasne modra, kdyz uz byl
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

// Barva a uroven svetla podle denni doby. Tataz funkce jako v chunk_opaque.frag.
const vec3 WaterSkyTint = vec3(0.72, 0.79, 0.99);

vec3 ApplyLightTint(vec3 color, float shade)
{
    // Uroven svetla uz nese pc.lightColor.rgb (Luanti get_sunlight_color), takze se
    // nesmi tlumit jeste jednou podle denniho svetla — noc by byla tmava dvakrat.
    return color * pc.lightColor.rgb * clamp(shade, 0.0, 1.0)
    // A JESTE UROVEN, ne jen barva. lightColor.rgb nese, JAKE je svetlo, ne KOLIK ho je —
    // v noci ma mesicni odstin, ale porad nenulovou velikost. Bez tohohle cinitele
    // zustaval vzdaleny teren a voda svitit, zatimco blizky teren uz spravne ztmavl
    // (chunk_opaque.frag), takze byla dalka jasnejsi nez popredi.
    //
    // frameTuning.w je denni svetlo (0 v noci, 1 ve dne), lightColor.w mesicni svit.
        * mix(pc.lightColor.w, 1.0, clamp(pc.frameTuning.w, 0.0, 1.0));
}

layout(location = 0) out vec4 FragColor;

const float Pi = 3.14159265;

// Musi sedet na tychz konstantach v chunk_opaque.frag, viz water_common.glsl.txt.
const vec3 WaterScatter = vec3(0.13, 0.34, 0.44);
const vec3 AbsorptionRatio = vec3(2.17, 0.71, 0.46);

const vec3 SunColor = vec3(1.6, 1.5, 1.3);

// Musi sedet na FaceShading.SunDirection v C#, jinak by trpyt na vode byl jinde nez
// osvetlene strany kopcu a scena by si odporovala.
const vec3 SunDirection = normalize(vec3(0.55, 0.75, -0.30));

// Musi sedet na ChunkRenderer.ZenithColor a SkyColor.
const vec3 Zenith = vec3(0.20, 0.42, 0.82);
const vec3 Horizon = vec3(0.62, 0.76, 0.92);

const float F0 = 0.02;

// KOPIE Z sky.frag. Preklad je bez -I, takze hlavicku sdilet nejde. Kdyz se meni tam,
// MUSI se zmenit i tady — jinak by hladina odrazela jine nebe, nez jake je nad ni.
const float SunSize = 0.042;

vec3 SkyColor(vec3 dir, vec3 sunDir, vec3 zenith, vec3 horizon, float time)
{
    float up = clamp(dir.y, 0.0, 1.0);
    vec3 color = mix(horizon, zenith, pow(up, 0.42));

    float down = clamp(-dir.y, 0.0, 1.0);
    color = mix(color, horizon * 0.72, down * 0.6);

    // Siroky opar zustava kulaty (rozptyl v atmosfere je izotropni). Tesna zare pow(sun, 260)
    // odsud ZMIZELA — byl to kruh tak jasny, ze hranaty kotouc pod sebou schoval. Nahrazuje
    // ji nize zar pocitana touz hranatou metrikou. Vysvetleni v sky.frag.
    float sun = max(dot(normalize(dir), sunDir), 0.0);
    color += SunColor * pow(sun, 5.0) * 0.09;
    // Hranate slunce a paprsky — vysvetleni v sky.frag. Kosinus uhlu dava z definice KRUH,
    // takze hranaty tvar potrebuje prumet do roviny kolme ke slunci a Cebysevovu vzdalenost.
    vec3 d = normalize(dir);
    float toward = dot(d, sunDir);

    if (toward > 0.001)
    {
        vec3 helper = abs(sunDir.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
        vec3 right = normalize(cross(helper, sunDir));
        vec3 top = cross(sunDir, right);

        vec2 local = vec2(dot(d, right), dot(d, top)) / toward;

        float away = length(local);
        float around = atan(local.y, local.x);

        float box = max(abs(local.x), abs(local.y));

        float rays = (sin((around * 9.0) + (time * 0.11)) * 0.5) + 0.5;
        rays += (sin((around * 17.0) - (time * 0.07)) * 0.5) + 0.5;
        rays *= 0.5;

        // Paprsky zacinaji az za kotoucem — jejich utlum je kruhovy a u stredu by saturoval
        // do bila kruhovou oblast, ktera hranaty tvar schova. Vysvetleni v sky.frag.
        float rayStart = smoothstep(SunSize, SunSize * 4.0, away);

        color += SunColor * rays * rayStart * exp(-away * 3.0) * 0.14;
        color += SunColor * 0.45 * exp(-max(0.0, box - SunSize) * 60.0);
        color = mix(color, SunColor, 1.0 - smoothstep(SunSize * 0.82, SunSize, box));
    }

    return color;
}

// Gradient jedne postupne vlny. Vraci se rovnou derivace, ne vyska — normala je stejne
// z gradientu a skladat vysku a pak ji derivovat by bylo o krok navic.
vec2 WaveGradient(vec2 p, vec2 dir, float frequency, float amplitude, float speed, float time)
{
    float phase = (dot(p, dir) * frequency) + (time * speed);
    return dir * (amplitude * frequency * cos(phase));
}

// Normala hladiny.
//
// Souciny amplituda*frekvence jsou schvalne skoro stejne (kolem 0,05): vysledny stredni
// kvadraticky sklon vyjde 0,067, tedy necele ctyri stupne. Prvni verze mela 0,52, coz je
// sedmadvacet stupnu — z more byly viditelne pruhy pres celou zatoku.
//
// Rad velikosti sedi na Coxovu-Munkovu statistiku sklonu hladiny, ale zamerne u DOLNI
// hranice: jejich vzorec plati pro otevreny ocean s plne rozvinutymi vlnami a pro jezero
// by dal rozmazane zrcadlo, ve kterem se obloha rozteče.
vec3 WaveNormal(vec2 p, float time)
{
    // Nesoumeritelne smery i vlnove delky. Kdyby byly nasobky sebe navzajem, vzor by se
    // opakoval po par metrech a bylo by videt razitko.
    vec2 gradient = vec2(0.0);

    // KLIDNEJSI HLADINA. Amplitudy sniezene na 60 % a rychlosti na 40 % puvodnich hodnot:
    // ne kvuli sklonu (ten byl v poradku), ale kvuli POHYBU. Puvodni rychlosti 1,05 az 3,02
    // rozhoupaly odraz tak, ze hladina pusobila jako rozbourene more i na malem jezere.
    gradient += WaveGradient(p, vec2( 0.862,  0.507), 0.42, 0.072, 0.42, time);
    gradient += WaveGradient(p, vec2(-0.423,  0.906), 0.73, 0.041, 0.59, time);
    gradient += WaveGradient(p, vec2( 0.291, -0.957), 1.31, 0.022, 0.84, time);
    gradient += WaveGradient(p, vec2(-0.951, -0.309), 2.37, 0.011, 1.21, time);

    return normalize(vec3(-gradient.x, 1.0, -gradient.y));
}

// Trpyt slunce podle GGX (Trowbridge-Reitz 1975).
//
// NENI to energeticky poctivy BRDF a je to zamer. Skutecna spicka D_GGX roste s 1/drsnost^4,
// takze mezi blizkou a vzdalenou hladinou je rozdil sedmisetnasobny — s tim se da pracovat
// jen kdyz scena prochazi tone mappingem, a ta tudy nevede. Lalok se proto normalizuje na
// KONSTANTNI spicku a bere se z nej jen TVAR: uzke jadro a dlouhy chvost. To je presne ta
// vlastnost, kvuli ktere se GGX na vodu bere.
float SunGlint(vec3 normal, vec3 view, float roughness)
{
    vec3 halfway = normalize(view + SunDirection);

    float NoH = max(dot(normal, halfway), 0.0);
    float NoL = max(dot(normal, SunDirection), 0.0);

    float a = roughness * roughness;
    float a2 = a * a;
    float d = (((NoH * a2) - NoH) * NoH) + 1.0;

    // (a2/d)^2 misto a2/(Pi*d*d): tvar je tyz, spicka vyjde vzdycky 1.
    float shape = (a2 / max(d, 1e-8));

    return shape * shape * NoL;
}

// Promitne svetovy bod do obrazu a vrati jeho hloubku.
//
// PROC PRES SVETOVE SOURADNICE, a ne pres view space, jak to dela vetsina navodu:
// raymarch ve view prostoru potrebuje projekcni matici A jeji inverzi. Blok push
// konstant ma ale 128 bajtu, je plny a 128 je zaroven zarucene minimum Vulkanu, takze
// se nic dalsiho poslat neda (viz ChunkRenderer.PushConstantSize). Ve svetovych
// souradnicich staci viewProjection, kterou uz posilame kvuli vrcholum.
//
// Vulkanske NDC ma z rovnou v rozsahu 0..1 — na rozdil od OpenGL, kde bylo -1..1 —
// takze vysledek jde porovnat primo s obsahem hloubkoveho bufferu. Zadny prevod na
// linearni vzdalenost, a tedy ani zadna inverzni matice, neni potreba.
bool ProjectToScreen(vec3 world, out vec2 uv, out float depth)
{
    vec4 clip = pc.viewProjection * vec4(world, 1.0);

    // Bod v rovine kamery nebo za ni. Delenim by vysel nesmysl.
    if (clip.w <= 1e-4)
    {
        return false;
    }

    vec3 ndc = clip.xyz / clip.w;

    uv = (ndc.xy * 0.5) + 0.5;
    depth = ndc.z;

    return true;
}

// Utlum u okraju obrazu.
//
// Odraz umi ukazat jen to, co uz na obrazovce je. Kdyz paprsek trefi neco tesne u kraje,
// staci aby hrac pootocil hlavu a odrazeny predmet vypadne z obrazu — z toho je blikajici
// lem po celem okraji hladiny. Plynulym utlumem se misto skoku objevi prechod do oblohy.
float ScreenFade(vec2 uv)
{
    const float Margin = 0.09;

    vec2 toEdge = min(uv, vec2(1.0) - uv) / Margin;
    return clamp(min(toEdge.x, toEdge.y), 0.0, 1.0);
}

// SCREEN-SPACE ODRAZ.
//
// Tohle je ta vec, ktera na hladine chybela. Predtim se odrazela POUZE analyticka obloha
// ze SkyColor, takze bylo jedno, jak presne je spocitany Fresnel — v jezere se nezrcadlil
// breh, stromy ani kopce, protoze nebylo co zrcadlit. Voda proto vypadala porad stejne
// bez ohledu na to, co kolem ni stalo.
//
// Vraci SILU zasahu (0 az 1), ne jen ano/ne. Nula znamena "nic se netrefilo, vezmi
// oblohu"; mezihodnoty vznikaji u okraju obrazu a na konci dosahu, aby odraz do oblohy
// prechazel plynule misto aby skoncil hranou.
//
// KROK ROSTE KVADRATICKY. Blizko hladiny je hustota potreba (tam jsou detaily, ktere maji
// byt videt), daleko uz staci hruby krok. Rovnomerne deleni by na stejny pocet vzorku
// doslo pekne blizko, takze vzdalene kopce by se v odrazu neobjevily vubec.
float TraceReflection(vec3 origin, vec3 direction, out vec3 hit)
{
    // DOSAH MUSI SAHAT AZ NA OBZOR, ne jen do okoli.
    //
    // Prvni verze mela 96 bloku a nefungovala: odrazeny paprsek od skoro vodorovne hladiny
    // stoupa vzhuru, kde neni nic nez obloha, a jedine, co ma smysl odrazet — breh, stromy
    // a kopce — lezi mnohem dal. Merenim vysla voda cela cerna.
    //
    // 384 sedi na ChunkRenderer.FogEnd: dal uz teren splyva s oblohou, takze by se odrazela
    // barva mlhy, kterou stejne dava SkyColor.
    //
    // 32 kroku, ne 48: pri plochem pohledu pres more projde trasovanim vetsina obrazovky
    // a kazdy krok je jeden vzorek hloubky navic. Predloha z godotshaders ma ve vychozim
    // nastaveni 512, coz je pro fragment hladiny mimo jakykoli rozpocet.
    const int Steps = 32;
    const float Range = 384.0;

    // Kolikrat se zasah zpresni pulenim. Sest pulen zkrati posledni krok na sestaticetinu,
    // tedy z pomala petadvaceti bloku na necely pul bloku.
    const int Refine = 6;

    hit = vec3(0.0);

    // Konec predchoziho kroku. Zasah lezi mezi nim a tim, ktery ho nasel — pulit se bude
    // prave v tomhle rozpeti.
    float behind = 0.0;

    for (int i = 1; i <= Steps; i++)
    {
        float t = float(i) / float(Steps);
        vec3 point = origin + (direction * (t * t * Range));

        vec2 uv;
        float depth;

        if (!ProjectToScreen(point, uv, depth))
        {
            return 0.0;
        }

        if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
        {
            return 0.0;
        }

        float scene = texture(uDepth, uv).r;

        // Vyprazdnena hloubka znaci, ze sem zadna geometrie nedosahla — je tam obloha.
        // Paprsek tudy jen prolete; zastavit se na ni by znamenalo vzit barvu nebe
        // dvakrat, jednou odsud a jednou z SkyColor niz.
        if (scene >= 0.999999)
        {
            behind = t;
            continue;
        }

        if (depth > scene)
        {
            // ZPRESNENI ZASAHU PULENIM.
            //
            // Bez nej odraz na dalce PROBLIKAVAL. Hruby krok totiz povrch preskoci a zasah
            // ohlasi az kus ZA nim — barva se pak cte z mista, ktere s odrazem nesouvisi,
            // a to misto se pri kazdem pohybu kamery nebo vlny skokem prelozi jinam.
            // Pulenim se najde skutecny prusecik, takze odraz po hladine klouze misto aby
            // skakal. Sest kroku stoji sest vzorku hloubky, ale JEN kdyz se neco trefilo.
            float lo = behind;
            float hi = t;

            for (int j = 0; j < Refine; j++)
            {
                float mid = (lo + hi) * 0.5;

                vec2 muv;
                float mdepth;

                if (!ProjectToScreen(origin + (direction * (mid * mid * Range)), muv, mdepth))
                {
                    break;
                }

                float mscene = texture(uDepth, muv).r;

                // Za geometrii nebo v obloze: prusecik lezi bliz, jdi do dolni poloviny.
                if (mscene < 0.999999 && mdepth > mscene)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }

            vec2 fuv;
            float fdepth;

            if (!ProjectToScreen(origin + (direction * (hi * hi * Range)), fuv, fdepth))
            {
                return 0.0;
            }

            // TEST TLOUSTKY.
            //
            // Bez nej dela SSR HALO KOLEM PREDMETU. Paprsek proleti za tenkym kmenem nebo
            // za korunou, hloubka rekne "jsem za necim" a odraz vezme barvu z mista, ktere
            // s nim nesouvisi. Kolem stromu tak vznikne obrys z barvy pozadi.
            //
            // Prah se odvozuje ze SAMOTNEHO KROKU, ne z pevneho cisla: hloubka je
            // nelinearni, takze tyz rozdil znamena u nohou centimetry a na obzoru stovky
            // metru. Spocita se, o kolik se hloubka zmeni na posledni delce kroku, a zasah
            // se uzna jen tehdy, kdyz paprsek nezajel hloub nez o poldruhe teto hodnoty.
            vec2 luv;
            float ldepth;

            if (!ProjectToScreen(origin + (direction * (lo * lo * Range)), luv, ldepth))
            {
                return 0.0;
            }

            float hitScene = texture(uDepth, fuv).r;
            float stride = abs(fdepth - ldepth);

            if (fdepth - hitScene > (stride * 1.5) + 1e-7)
            {
                return 0.0;
            }

            hit = texture(uScene, fuv).rgb;

            // Utlum na konci dosahu. Bez nej by v miste, kde paprsku dojdou kroky,
            // vznikl ostry oblouk mezi odrazem a oblohou.
            float reach = 1.0 - smoothstep(0.75, 1.0, hi);

            return ScreenFade(fuv) * reach;
        }

        behind = t;
    }

    return 0.0;
}

// KAUSTIKY NA SPODNI STRANE HLADINY.
//
// Tentyz vzorec jako na dne (chunk_opaque.frag, puvod Shadertoy MdlXz8 od Dave_Hoskinse
// se svolenim autora) — jen se pouzije jinde. Pri pohledu ZESPODU se svetlo na zvlnene
// hladine lame a soustredi, takze hladina neni jednolita plocha, ale hraje.
//
// Pouziva se VYHRADNE ve vetvi cameraUnder. Shora by to bylo spatne: tam je hladina zrcadlo
// a kaustiky se promitaji na dno, ne na ni.
const float CausticTau = 6.28318530718;
const float CausticIntensity = 0.005;

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

void main()
{
    // ZNAMENKO CASU NESE VOLBU VZHLEDU VODY.
    //
    // Push konstanty jsou plne a Vulkan zarucuje jen 128 bajtu, takze rozsirit je nejde bez
    // rizika. Cas je vzdycky kladny, takze zaporna hodnota nikdy nevznikne omylem a da se
    // pouzit jako priznak; abs() vrati puvodni cas.
    float time = abs(pc.water.w);
    bool fancy = pc.water.w > 0.0;

    // BLIZKY REZ PRO VZDALENE MORE.
    //
    // Tyz shader kresli hladinu chunku i hladinu vzdaleneho terenu. U chunku je mez nula
    // a podminka nikdy nezabere; u LOD dlazdic se jim odrizne ta cast, ktera lezi uvnitr
    // dosahu chunku, aby se hladina nekreslila dvakrat pres sebe. Stejny princip jako
    // ve far.frag, zduvodneni je tam.
    bool distant = pc.chunkOffset.w > 0.0;

    float horizontalDistance = length(vWorld.xz - pc.cameraAndFogStart.xz);
    if (horizontalDistance < pc.chunkOffset.w)
    {
        discard;
    }

    // GEOMETRICKA NORMALA Z DERIVACI, ne z atributu vrcholu. Vrchol nese jen jedno cislo
    // jasu a pridavat normalu kvuli vode do formatu, ktery sdili ctyri dalsi pipeliny,
    // by bylo draz nez dve derivace tady.
    vec3 normal = normalize(cross(dFdx(vWorld), dFdy(vWorld)));

    vec3 view = normalize(pc.cameraAndFogStart.xyz - vWorld);

    // Normala vzdycky proti divakovi: hladina se kresli bez cullingu, takze tentyz
    // trojuhelnik se vidi shora i zespodu a znamenko z cross() na tom zavisi.
    if (dot(normal, view) < 0.0)
    {
        normal = -normal;
    }

    bool surface = abs(normal.y) > 0.5;
    // Kamera se pocita za ponorenou uz kousek nad hladinou — objektiv neni bod a prepnuti
    // presne na nule prichazi pozde. Vysvetleni v chunk_opaque.frag; hodnota MUSI sedet
    // na ostatni shadery, jinak by se hladina prepnula driv nez teren pod ni.
    bool cameraUnder = pc.cameraAndFogStart.y <= (pc.water.x + 0.3);

    // Geometricka normala se schova PRED zvlnenim. Screen-space odraz z ni pak trasuje
    // klidnejsi paprsek — zduvodneni je u jeho volani niz.
    vec3 flatNormal = normal;

    // Drsnost ROSTE SE VZDALENOSTI, a to je nutnost, ne kosmetika. Kdyz je lalok uzsi nez
    // pixel, nevznikne trpyt, ale aliasing — v prvni verzi z toho byly bile obdelniky
    // s tvrdymi hranami po cele hladine. Je to chudsi bratr Toksvigova filtru: misto aby
    // se rozptyl normal poctive merIl, dosadi se za nej vzdalenost.
    float roughness = mix(0.035, 0.19, clamp(vDistance / 200.0, 0.0, 1.0));

    if (surface)
    {
        // Vlnova normala se s dalkou vraci k plocha, jinak by na obzoru pripadlo nekolik
        // vlnovych delek na pixel. NE ale uplne: kdyby se vyrovnala na nulu, rozpadne se
        float weight = max(0.16, min(1.0, 1.0 / pow(max(vDistance * 0.06, 1e-3), 0.75)));

        vec3 waves = WaveNormal(vWorld.xz, time);
        if (normal.y < 0.0)
        {
            waves = -waves;
        }

        normal = normalize(mix(normal, waves, weight));
    }

    float cosTheta = clamp(dot(normal, view), 0.0, 1.0);

    // ZESPODU SE ZADNY UPLNY VNITRNI ODRAZ NEPOCITA.
    //
    // Fyzikalne existuje: nad kritickym uhlem 48,6 stupne neni svet nad vodou videt vubec
    // a hladina je zevnitr zrcadlo. Snellovo okno je skutecny jev a potapeci ho tak vidi.
    //
    // JENZE VE HRE JE TO SPATNE, a to ze dvou duvodu:
    //
    //   1. Polomer okna na hladine je hloubka krat tangens kritickeho uhlu. Cim blize je
    //      hrac hladine, tim MENSI ten svetly kruh je — presne naopak, nez co hrac ceka.
    //      Kdyz vyplouva nahoru, svet se mu zaviraf misto aby se otviral.
    //   2. Blokove hry to nedelaji. V Minecraftu ani v zadnem beznem shader packu (vcetne
    //      Complementary) uplny vnitrni odraz neni — pod vodou je videt cely svet nahore
    //      pres modrou mlhu. To je i to, co zadavatel opakovane zadal.
    //
    // Zustava jen Fresnel, ktery s uhlem roste, takze pri strmem pohledu vzhuru je hladina
    // skoro cira a k vodorovne se plynule meni v zrcadlo. Prechod je tim spojity a nikde
    // se nelame na ostrou hranici.
    float fresnel = F0 + ((1.0 - F0) * pow(1.0 - cosTheta, 5.0));

    // Zdola se odrazivost jeste zastropuje. Bez toho by pri smykovem pohledu vysla na
    // jednicku a vznikla by tataz tmava deska jako s uplnym vnitrnim odrazem, jen bez
    // ostreho okraje.
    if (cameraUnder)
    {
        fresnel = min(fresnel, 0.62);
    }

    vec3 color;
    float alpha;

    // JEDNODUCHA VODA: dlazdice z atlasu, prosvitajici.
    //
    // Vypada jako v blokovych hrach a hlavne je pres ni videt, co je pod hladinou — pres
    // odrazivou hladinu poznat neni. Vlneni, podvodni pohled ani kaustiky se tim nevypinaji;
    // odpada jen odraz oblohy a vlastni barva povrchu.
    //
    // Pohled zespodu si resi vetev nize sama, protoze u nej je hladina PRUHLEDNA a svet nad
    // ni prosviti tim, co uz je nakresleno za ni. Michat do toho dlazdici by ho zakrylo.
    if (!fancy && !cameraUnder)
    {
        // VLNI SE SAMA DLAZDICE.
        //
        // Textura vody je staticky obrazek, takze aby vypadala jako tekouci voda, musi se
        // hybat souradnice, kterou se vzorkuje. Dve slozky:
        //
        //   - POMALY UNOS. Cela dlazdice plyne jednim smerem, jako proud.
        //   - VLNENI. Souradnice se navic rozkmita podle svetove polohy, takze se kresba
        //     zvlni a nejede jako tapeta na pasu.
        //
        // Vzorkuje se s opakovanim (TextureArray zaklada pole s repeat), takze posun UV
        // nepretece do sousedni dlazdice atlasu.
        vec2 drift = vec2(time * 0.035, time * 0.021);

        vec2 wobble = vec2(
            sin((vWorld.z * 0.55) + (time * 0.9)),
            cos((vWorld.x * 0.48) - (time * 1.1))) * 0.045;

        vec4 tile = texture(uTextures, vec3(vTexCoord + drift + wobble, vLayer));

        // DLAZDICE JE SAMA O SOBE TMAVA, kolem (18, 115, 209). Nasobenim se zesvetlit neda:
        // cervena je skoro nula, takze z ni nasobek nic nevytahne a modra uz je u stropu.
        // do melciny misto aby zustala sytě modrou deskou.
        vec3 shallow = vec3(0.42, 0.72, 0.86);
        vec3 base = mix(tile.rgb, shallow, 0.45);

        // JAS SE NEMODULUJE VUBEC.
        //
        // Zkouselo se to dvakrat a pokazde z toho byly skvrny. Nejdriv "trpyt" jako mocnina
        // vlny, ze ktereho vznikly ostre svetle body; pak mirnejsi nasobeni jasu, jenze
        // nasobek az 1,66 pretekl pres bilou a po hladine byly BILE FLEKY. Cely pohyb proto
        // dela vylucne posun souradnic vyse — dlazdice se vlni, jas zustava, jak je.
        color = ApplyLightTint(base * vShade, vShade);

        // PRUHLEDNOST JE STEJNA NABLIZKO I DALEKO.
        //
        // Zkouselo se houstnuti s dalkou, aby se schovaly hrbolky na dne u obzoru. Byla to
        // spatna cesta: schovalo to i to, kvuli cemu ma smysl mit vodu pruhlednou — z dalky
        // pod hladinu videt nebylo vubec.
        //
        // Bok drzi min pruhlednosti nez hladina: pres svislou stenu se hleda do hloubky,
        // kde uz stejne neni co videt, a plne pruhledny bok by prozradil, ze voda je jen
        // skorapka.
        alpha = surface ? 0.58 : 0.78;

        FragColor = vec4(color * alpha, alpha);
        return;
    }

    if (!surface)
    {
        // Bocni stena u breh nebo u vodopadu: jen modra clona. Zadny odraz oblohy —
        // svisla plocha odrazi vodorovny smer, tedy skoro vzdycky jiny kus terenu.
        color = WaterScatter;
        alpha = 0.55;
    }
    else if (cameraUnder)
    {
        // POHLED ZESPODU. Hladina je PRUHLEDNA a svet nad ni prosviti tim, co uz je
        // nakresleno za ni — obloha i teren nad vodou se kresli pred vodnim pruchodem.
        //
        // Predchozi verze si to, co je nad vodou, dopocitavala sama a byla neprusvitna
        // (alfa 1). Znamenalo to, ze skrz hladinu nebyla videt skutecna geometrie, jen
        // analyticka obloha — takze breh, stromy ani hory nad vodou nebyly videt vubec.
        // Odsud to, ze svet nad hladinou pusobil jako namalovany.
        vec3 reflectDir = reflect(-view, normal);
        color = WaterScatter * mix(1.35, 0.55, clamp(-reflectDir.y, 0.0, 1.0));

        // HLADINA ZESPODU HRAJE. Svetlo se na vlnach lame a soustredi, takze spodni strana
        // neni jednolita plocha. Je to tentyz vzorec jako na dne, jen slabsi — hladina uz
        // je sama o sobe svetla, takze staci naznak.
        //
        // Dve vrstvy v nesouměřitelnem meritku a natocene, aby nebyla videt dlazdice;
        // stejny duvod jako u dna.
        vec2 turned = vec2((vWorld.x * 0.6235) - (vWorld.z * 0.7818),
                           (vWorld.x * 0.7818) + (vWorld.z * 0.6235));

        float lit = max(CausticLayer(vWorld.xz * 0.11, time * 0.55),
                        CausticLayer(turned * 0.079, (time * 0.43) + 7.3));

        // S rostouci vzdalenosti se vzor musi vytratit, jinak z nej pri plochem pohledu pod
        // uz proslo vodou, takze mu chybi cervena a neni bile.
        color *= vec3(1.0) + (lit * exp(-vDistance * 0.02) * 0.45 * vec3(0.62, 0.94, 1.0));

        // Utlum na draze od oka k hladine: cim hloub potapec je, tim vic voda svet nahore
        // schova.
        float transmittance = exp(-pc.water.z * 1.1 * vDistance);

        // LOM PRI POHLEDU ZESPODU.
        //
        // Zespodu je lom VYRAZNEJSI nez shora: paprsek jde z hustsiho prostredi do ridsiho,
        // takze se od kolmice odklani vic. Proto 1,6 proti 0,9 u pohledu shora.
        //
        // Predtim tu zadny nebyl — hladina byla jen pruhledna a svet nad ni prosvital
        // blendingem, tedy PRESNE tam, kde lezi. Zespodu je pritom deformace nejvic videt,
        // protoze cely obraz nad hlavou jde skrz jedinou zvlnenou plochu.
        vec2 screenUv = gl_FragCoord.xy / vec2(textureSize(uScene, 0));
        vec2 refractOffset = normal.xz * (2.6 / max(vDistance, 3.0));
        vec2 refractUv = clamp(screenUv + refractOffset, vec2(0.002), vec2(0.998));

        // Tentyz test jako shora: vzorek blizsi nez hladina lezi pred ni, ne za ni.
        if (texture(uDepth, refractUv).r < gl_FragCoord.z)
        {
            refractUv = screenUv;
        }

        vec3 above = texture(uScene, refractUv).rgb;

        // Rucni slozeni misto blendingu, aby se dal pouzit posunuty vzorek. Vysledek je tyz
        // jako driv (voda*alfa + svet*(1-alfa)), jen je svet nad hladinou lomeny.
        float cover = clamp(mix(1.0, fresnel, transmittance), 0.0, 1.0);

        color = (color * cover) + (above * (1.0 - cover));
        alpha = 1.0;
    }
    else
    {
        // POHLED SHORA.
        vec3 reflectDir = reflect(-view, normal);

        // SCREEN-SPACE ODRAZ SE POCITA Z NEORIZNUTEHO SMERU.
        //
        // Orez nad obzor niz je spravny pro oblohu, ale pro SSR by byl zhoubny: prave
        // paprsky mirici POD horizont trefuji breh, stromy a kopce, tedy presne to, co
        // se ma v hladine zrcadlit. S oriznutym smerem by se odrazelo jen nebe a cela
        // prace by byla k nicemu.
        //
        // TRASUJE SE JEN TAM, KDE JE ODRAZ VIDET — ale rozhoduje o tom SMER PAPRSKU,
        // ne vzdalenost od kamery.
        //
        // Prvni pokus o uspory zkousel vzdalenost (vDistance < 320) a vzdalenou hladinu
        // vynechal uplne. Bylo to PRESNE OBRACENE, nez melo byt: pri plochem pohledu pres
        // more je vzdalena hladina vetsina obrazu, Fresnel na ni vychazi skoro na jednicku,
        // takze je z ni zrcadlo — a odraz kopcu na obzoru je tam nejvic videt. Vypnout SSR
        // podle vzdalenosti znamena vypnout ho tam, kde jedine stoji za to.
        //
        // Rozhoduji proto dve podminky, obe primo o tom, jestli bude odraz videt:
        //
        //   1. FRESNEL. Pri pohledu shora dolu vyjde kolem 0,02 — odraz prispeje dvema
        //      procenty a nikdo ho nepozna, ale raymarch by ho stal desitky vzorku hloubky
        //      na kazdy fragment. Namereno: pri celoobrazovkove vode spadly snimky
        //      z 900 na 124 za vterinu.
        //
        //   2. STRMOST ODRAZU. Kdyz paprsek miri prikre vzhuru, netrefi nic nez oblohu —
        //      a tu uz SkyColor spocita analyticky a zadarmo. Marchovat za ni je ciste
        //      plytvani. Mez 0,45 odpovida zhruba sedmadvaceti stupnum nad vodorovnou.
        //
        // Obe podminky zaberou prave v te situaci, ktera je nejdrazsi (pohled shora na
        // velkou vodni plochu zblizka), a obe pusti trasovani tam, kde je efekt videt.
        vec3 traced = vec3(0.0);
        float tracedWeight = 0.0;

        // OBE PODMINKY JSOU PLYNULE, ne prahy.
        //
        // <b>Tady byl ten svetly pas na mori.</b> Puvodne to bylo `fresnel > 0.08 &&
        // reflectDir.y < 0.45`, tedy dva tvrde prahy — a odraz se pres ne PREKLOPIL NARAZ.
        // Blizko se hrac diva na hladinu strmeji, odraz miri prikre vzhuru a nepocita se;
        // dal se pohled zplosti, mez se prekroci a odraz naskoci. Mezi tim vznikla hrana,
        // ktera se posouvala s hracem, protoze zavisi na UHLU, ne na miste ve svete.
        //
        // Nasobenim dvou plynulych prechodu se z hrany stane pozvolny nabeh. Vaha se
        // pouzije i k tomu, aby se pri temer nulovem prispevku vubec netrasovalo — early-out
        // tim zustava zachovany a vykon taky.
        float angleGate = 1.0 - smoothstep(0.35, 0.55, reflectDir.y);
        float fresnelGate = smoothstep(0.05, 0.13, fresnel);

        float gate = angleGate * fresnelGate;

        if (gate > 0.002)
        {
            // TRASUJE SE PODLE KLIDNEJSI NORMALY, ne podle plne zvlnene.
            //
            // Odraz na dalce BLIKAL a tohle je pricina: krok raymarche roste kvadraticky,
            // takze u konce dosahu meri skoro petadvacet bloku. Vlna mezitim nakloni
            // normalu o par stupnu, paprsek se svede jinam a mine nebo trefi neco jineho —
            // mezi dvema snimky se tak zasah prepina a hladina se treti.
            //
            // Odraz se proto hleda podle normaly z 35 % zvlnene. Neni to podvod: hrubost
            // hladiny odraz rozmazava, a rozmazany odraz je presne to, co ma vzdalena voda
            // ukazovat. Trpyt slunce a Fresnel dal pouzivaji plnou normalu, takze vlny
            // z obrazu nezmizi.
            vec3 calmNormal = normalize(mix(flatNormal, normal, 0.35));
            vec3 calmDir = reflect(-view, calmNormal);

            tracedWeight = TraceReflection(vWorld, calmDir, traced) * gate;

            // ZADNY UTLUM PODLE VZDALENOSTI.
            //
            // Byl tu dvakrat: nejdriv 190 az 340 bloku, pak 300 az 540. Vznikl jako zaplata
            // na blikani odrazu v dobe, nez pribylo zpresneni zasahu pulenim — a jakmile
            // zpresneni resi pricinu, zbyla po nem jen skoda: tam, kde odraz koncil, mela
            // hladina jinou barvu a pres more sel videt PAS. Posunuti dal ho jen zuzilo.
            //
            // Konec dosahu uz osetruje utlum uvnitr TraceReflection (promenna 'reach'),
            // ktery se rozjede az na poslednich ctvrtine kroku a nema tvrdou hranici.
        }

        // Odrazovy paprsek se ORIZNE NAD OBZOR. Bez toho miri u velmi mělkych uhlu pod
        // horizont, kde neni obloha, a na vodorysce z toho vznikne sedy pas. (Postup je
        // z Velorenu, prepsany — jejich kod je GPL a prevzit nejde.)
        reflectDir.y = max(reflectDir.y, 0.015);

        vec3 reflectedDirection = normalize(reflectDir);
        vec3 reflection = SkyColor(reflectedDirection, SunDirection, Zenith, Horizon, time);

        // Full cloud raymarching a second time for every water pixel was disproportionately
        // expensive on laptop and unified-memory GPUs. The sky colour remains reflected;
        // detailed cloud volume is rendered once in the atmospheric pass.

        // Co paprsek nasel, prebije oblohu. Nemisi se pulka s pulkou: kde odraz zasah ma,
        // je obloha zakryta uplne, a kde nema, zustava cista. Mezihodnoty delaji jen
        // okraje obrazu a konec dosahu.
        reflection = mix(reflection, traced, tracedWeight);

        // NOCNI ZTLUMENI AZ TADY, po slozeni celeho odrazu.
        //
        // Puvodne stalo pred timto mixem — a prave ten ho zase prepsal, protoze
        // screen-space odraz (traced) ma pri pohledu pres hladinu velkou vahu.
        // Hladina proto zustavala svitit i s "opravou". Merenim potvrzeno tim, ze
        // vodni plocha obarvena natvrdo cervene byla v noci videt cela.
        //
        // Voda za tmy neni zrcadlo — neni co odrazet. SkyColor vyse pocita z PEVNYCH
        // DENNICH konstant (Zenith, Horizon, SunDirection), takze bez tohohle vraci
        // polednim modrou i o pulnoci; screen-space odraz zase nese jas cele sceny.
        // Obojim se proto prolne barva nocni oblohy vynasobena mesicnim svitem.
        reflection = mix(
            pc.fogColorAndFogEnd.xyz * pc.lightColor.w,
            reflection,
            clamp(pc.frameTuning.w, 0.0, 1.0));

        vec3 glint = SunColor * SunGlint(normal, view, roughness) * 2.2;

        // SPRAVNE SKLADANI FRESNELU.
        //
        // Michani je SrcAlpha/OneMinusSrcAlpha, takze na obrazovku dopadne
        // color*alpha + pozadi*(1-alpha). Kdyz se za alfu dosadi primo Fresnel, vyjde
        // z toho presne F*obloha + (1-F)*dno — tedy fyzikalne spravne deleni svetla na
        // odrazene a prosle. Zadna vlastni "barva vody" uz potreba neni: utlum hloubkou
        // spocital shader dna a pridavat ho i sem by ho zapocitalo dvakrat.
        //
        // Prvni verze mela alfu od 0,42 vys, takze i pri pohledu kolmo dolu lezela pres
        // pisek skoro polovicni modra clona. Melcina z toho byla seda kase.
        //
        // Trpyt se do teze rovnice dostane pres zvyseni alfy o jeho vlastni jas; delenim
        // se pak zajisti, ze soucin color*alpha vyjde presne obloha*F + trpyt.
        float glintLuma = clamp(max(glint.r, max(glint.g, glint.b)), 0.0, 1.0);

        // JISKRENI HLADINY.
        //
        // Hladina shora neni hladke zrcadlo. Vlny ji rozbijeji na plosky s ruznym sklonem
        // a odraz oblohy se na nich TRISTI — proto skutecna voda jiskri i tam, kde slunce
        // primo neodrazi. Bez toho je z hladiny jednolita modra plocha s jednim odleskem.
        //
        // Pouziva se tentyz vzor jako pro kaustiky, jen v jemnejsim meritku: to, co dole
        // svetlo sbiha do skvrn, je nahore prave tim sklonem, ktery odraz trísti. Fyzikalne
        // jde o tyz jev z druhe strany.
        //
        // Meritko je jemnejsi nez na dne (0,16 a 0,11 proti 0,11 a 0,079), protoze plosky
        // na hladine jsou mensi nez ohniska, ktera vrhaji na dno.
        float sparkleFade = exp(-vDistance * 0.012);
        float sparkle = 0.0;

        // Za obzorem se z jiskreni stejne stane sum, takze se tam vubec nepocita. Usetri to
        // deset iteraci na fragment prave tam, kde je vody na obrazovce nejvic.
        if (sparkleFade > 0.02)
        {
            vec2 spun = vec2((vWorld.x * 0.6235) - (vWorld.z * 0.7818),
                             (vWorld.x * 0.7818) + (vWorld.z * 0.6235));

            float lit = max(CausticLayer(vWorld.xz * 0.16, time * 0.6),
                            CausticLayer(spun * 0.11, (time * 0.47) + 3.1));

            // Jiskri se jen tam, kde hladina NENI zrcadlo. Pri plochem pohledu vyjde Fresnel
            // skoro na jednicku, odraz prebije vsechno ostatni a jiskra by v nem zanikla —
            // navic prave tam uz je vzor tak zkraceny perspektivou, ze by z nej bylo moare.
            //
            // SILA MUSI ZUSTAT NIZKA A SOUVISI TO S ABSORPCI VODY. Puvodnich 0,22 se ladilo
            // proti svetle vode; jakmile absorpce stoupla (WaterPathAbsorption 0,009 -> 0,098)
            // a hladina ztmavla, tataz jiskra najednou svitila v mnohem vetsim kontrastu
            // a vypadala jako mastny film na vode. Kdyz se bude menit tmavost vody, tohle
            // cislo se bude muset prehodnotit spolu s ni.
            sparkle = lit * sparkleFade * (1.0 - fresnel) * 0.06;
        }

        // LOM SVETLA NA HLADINE.
        //
        // Dno pod vodou se drive nekreslilo tady, ale PROSVITALO alfa blendingem: alfa byla
        // Fresnel a hardware smichal vodu s tim, co uz bylo v obraze. Dusledek byl, ze dno
        // leželo presne tam, kde je — skutecna voda ho posouva, protoze paprsek na zvlnene
        // hladine meni smer.
        //
        // Blending posunout neumi, takze si voda musi dno vzorkovat sama z kopie sceny.
        // Proto je nize alfa 1 a Fresnel se sklada rucne: to, co delal hardware, se deje tady.
        // Vysledek je stejny (F*obloha + (1-F)*dno), jen s posunutym dnem.
        vec2 screenUv = gl_FragCoord.xy / vec2(textureSize(uScene, 0));

        // SILA POSUNU KLESA SE VZDALENOSTI. Posun je v obrazovych souradnicich, ale vznika
        // ve svete — tyz naklon vlny znamena u brehu posun pres pul metru dna, na obzoru
        // zlomek pixelu. Bez deleni vzdalenosti by vzdalena hladina byla rozmazana kase.
        //
        // Bere se vodorovna slozka normaly: svisla urcuje, jak strme je vlna naklonena, ale
        // smer posunu dava prave to, kam je naklonena do stran.
        vec2 refractOffset = normal.xz * (0.9 / max(vDistance, 4.0));
        vec2 refractUv = clamp(screenUv + refractOffset, vec2(0.002), vec2(0.998));

        // POSUN SE NESMI PODIVAT NA NECO, CO STOJI PRED VODOU.
        //
        // Kdyz posunute UV trefi breh nebo strom pred hladinou, lom by je "nasal" doprostred
        // vody a kolem brehu by vznikl rozmazany lem z kusu terenu. Hloubka to pozna: kdyz je
        // vzorek BLIZ nez hladina, nelezi pod ni a posun se pro nej zahodi.
        if (texture(uDepth, refractUv).r < gl_FragCoord.z)
        {
            refractUv = screenUv;
        }

        vec3 refracted = texture(uScene, refractUv).rgb;

        // Trpyt i jiskra se pricitaji primo. Drive se delily alfou — to bylo potreba jen
        // proto, aby prezily nasobeni alfou v blendingu; ted uz zadne nasobeni nenasleduje.
        alpha = 1.0;
        // ODLESK SLUNCE A JISKRENI SE SKALUJI DENNI DOBOU, ostatni ne. Odraz i lom se ctou
        // z uz hotove sceny a z barvy oblohy, takze denni dobu nesou samy — nasobit je
        // jeste jednou by noc ztmavilo dvakrat. Trpyt je ale primy odlesk slunce a ten
        // v noci proste neni.
        // SKUTECNE DENNI SVETLO, ne nahrazka. Drive tu stalo lightColor.r + 0.04 —
        // jenze cervena slozka je v noci nenulova (mesicni svetlo ma vlastni barvu),
        // takze trpyt a odlesk slunce v noci nezhasly a hladina zustala svitit.
        float daylight = clamp(pc.frameTuning.w, 0.0, 1.0);

        color = (reflection * fresnel)
              + (refracted * (1.0 - fresnel))
              + (glint * daylight)
              + (SunColor * sparkle * daylight);

        // VZDALENA HLADINA UZ NENI ZVLASTNI PRIPAD.
        //
        // Drive tu byla samostatna vetev, ktera vzdalene more kreslila NEPRUHLEDNE a jeho
        // barvu si vymyslela — nejdriv jednou konstantou (WaterScatter * 0.55) pro melcinu
        // LOD melo dno zvednute na hladinu a POD vodou nebyla zadna geometrie, pres kterou
        // by se dalo michat.
        //
        // Obe verze skoncily svem na hranici LOD; ta druha navic nadelala ctvercove
        // a trojuhelnikove artefakty, protoze se hloubka interpolovala pres obri obdelniky
        // slouceneho meshe.
        //
        // FarTerrain uz dno nezvedá a hladinu pokládá jako samostatnou vrstvu nad nej,
        // takze vzdalena voda ma pod sebou skutecny teren a mícha se pres nej uplne stejne
        // jako hladina blizko. Zadny zvlastni pripad neni potreba — a prave proto zmizel
        // sev, ktery se dvema pokusy o barvu nepodarilo odstranit.
    }

    // Vzdusna mlha az nakonec, aby vzdalene more splynulo s obzorem misto aby koncilo
    // ostrym okrajem. Pod vodou se neuplatni — tam je "mlha" samotna voda.
    if (!cameraUnder)
    {
        float fogStart = pc.cameraAndFogStart.w;
        float fogEnd = pc.fogColorAndFogEnd.w;

        float fog = clamp((vDistance - fogStart) / max(fogEnd - fogStart, 0.001), 0.0, 1.0);
        fog *= fog;

        color = mix(color, pc.fogColorAndFogEnd.xyz, fog);
        alpha = mix(alpha, 1.0, fog);
    }

    FragColor = vec4(color, alpha);

    // Textura a jas vrcholu se neuzivaji, ale patri do layoutu pipeline a do formatu
    // vrcholu. Bez odkazu by je prekladac vyhodil a validacni vrstva by hlasila nesoulad.
    if (false)
    {
        FragColor += texture(uTextures, vec3(vTexCoord, vLayer)) * vShade;
    }
}
