#version 450
#extension GL_GOOGLE_include_directive : require

// Neprusvitna varianta chunk.frag.
//
// Rozdil je jediny a je zasadni: NENI tu discard. Fragment shader s discardem si
// grafika nesmi predtestovat hloubkou dopredu (early-Z), protoze do posledni chvile
// naplno a az potom zahodi.
//
// early_fragment_tests je tu navic explicitne, aby zameru bylo videt i v kodu.
layout(early_fragment_tests) in;

layout(location = 0) in vec2 vTexCoord;
layout(location = 1) in float vLayer;
layout(location = 2) in float vShade;
layout(location = 3) in float vDistance;
layout(location = 4) in vec3 vWorld;

// JE NAD FRAGMENTEM OPRAVDU VODA? 1 = ano nebo nevime, 0 = prokazatelne sucho.
//
// Sama vyska to nerozhodne — jeskyne pod urovni more je pod ni skoro vzdycky. Mesher
// sonduje vzhuru a pozna, jestli driv narazi na kapalinu nebo na strop; vysledek se veze
layout(location = 5) in float vSubmerged;
layout(location = 6) in float vCloud;
layout(location = 7) in float vBlockLight;

layout(set = 0, binding = 0) uniform sampler2DArray uTextures;

// Musi presne odpovidat bloku ve vertex shaderu, jinak glslangValidator ohlasi neshodu.
//
// jednou predchozi. Mapa se totiz neprekresluje kazdy snimek — kdyby se prekreslovala,
// otacela by se s pohybem slunce mrizka jejich texelu a alfa test listi by se do ni
// pokazde trefil jinak, takze by koruna v mape doslova vrela. Viz

#include "luanti_light.glsl"

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 cameraAndFogStart;
    vec4 fogColorAndFogEnd;   // xyz = barva OBLOHY, w = konec vzdusne mlhy
    vec4 water;               // x = hladina, y = pohltivost hloubkou, z = pohltivost drahou
    vec4 chunkOffset;

    // SVETELNE MATICE PO TRECH VEC4, NE JAKO MAT4.
    //
    // Prolinani dvou epoch potrebuje matice dve, a dve mat4 by blok pretahly na 288 B,
    // tedy pres limit karty. Svetelna projekce je ale ORTOGONALNI: nema perspektivni
    // deleni, takze jeji ctvrty sloupec je vzdycky (0,0,0,1) a nese nulu informace.
    // Uzitecnych je jen dvanact cisel a ta se poslou primo. Souradnice se z nich slozi
    // tremi skalarnimi souciny, coz je i levnejsi nez nasobeni matici.
    vec4 lightRow0;               // 128..143
    vec4 lightRow1;               // 144..159
    vec4 lightRow2;               // 160..175
    vec4 unusedRow0;           // 176..191
    vec4 unusedRow1;           // 192..207
    vec4 unusedRow2;           // 208..223

    // y NENI VYPINAC, ALE VAHA. Blok je na 256 B, tedy na stropu, a volny float v nem uz
    // nepocitat", nezaporna je vaha aktualni epochy proti predchozi.
    vec4 frameTuning;          // 224..239  x = sila stinu, y = vaha epochy, z = pomer kaskad, w = denni svetlo
    vec4 lightColor;            // 240..255  xyz = barva svetla, w = svit mesice
} pc;

layout(location = 0) out vec4 FragColor;

// JEDNA BARVA VODY, NE TRI.
//
// Predchozi verze mela ShallowWater, DeepWater a jeste rucni prechod mezi nimi. Vysledek
// protoze prechod od tyrkysove melciny k tmave hloubce uz VYPADNE ZE SPEKTRALNIHO UTLUMU
// sam. Kdyz se tataz barva vynasobi utlumem, ktery je pro cervenou petkrat rychlejsi nez
// pro modrou, melcina vyjde tyrkysova a hloubka modra bez jedine konstanty navic.
//
// Rucne michat jeste druhou barvu pres to znamenalo zapocitat tyz jev dvakrat.
const vec3 WaterScatter = vec3(0.13, 0.34, 0.44);

// CAUSTICS.
//
// Zvlnena hladina funguje jako spojka: kde je prohnuta dovnitr, svetlo se pod ni sbiha
// a na dne vznikne svetla sit. Neni to ozdoba — je to jedina vec, podle ktere oko pozna,
// ze se diva skrz vodu a ne na modrou mlhu.
//
// Pocita se z TEHOZ vlneni jako hladina ve water.frag. Zisk jasu je pro male vychylky
// zhruba 1 - h*Laplacian(vyska), a Laplacian sinusovky je -A*f^2*sin(faze), takze staci
// secist ctyri sinusovky. Zadna textura, zadny dalsi pruchod.
// Soucet amplitud vsech ctyr clenu Laplacianu. Slouzi k NORMALIZACI na rozsah zhruba
// <-1, 1>, aby se sila efektu dala nastavit jedinym cislem a nezavisela na tom, kolik
// vln se secte.
//
// Prvni verze tohle nemela a byla to prepalena katastrofa: nenormalizovany Laplacian
// dosahoval 0,22, nasobil se hloubkou az 20 a vysledek se umocnil na treti — zisk jasu
// tedy vychazel az STO PADESAT SEDMKRAT. Dno svitilo neonovou tyrkysovou.
// PROC ITERATIVNI OHYBANI SOURADNIC A NE VORONOI.
//
// Predchozi verze pocitala Voronoi a brala vzdalenost k hranici mezi bunkami. Vysledek
// byl technicky spravny, ale vypadal jako POPRASKANA HLINA nebo zelvi krunyr: souvisla
// pavucina, kde kazda cara pokracuje do sousedni. Skutecne kaustiky takove nejsou.
//
// Na dne jsou ODDELENE mekke svetelne skvrny, ktere se navzajem nedotykaji. Ta spojitost
// je vada Voronoi z principu — hranice bunek MUSI tvorit souvislou sit, protoze kazda
// bunka sousedi s dalsi. Zadnym ladenim sirky nebo jasu se to odstranit nedalo.
//
// Tenhle postup mistto toho v kazdem kroku OHNE souradnice podle sebe sama a pricte
// prevracenou vzdalenost k ohnisku. Svetlo se tak hromadi v izolovanych SINGULARITACH,
// ktere spolu zadne hrany nemaji — a presne to davaji skvrny misto site.
//
// PUVOD A SVOLENI: Shadertoy MdlXz8 od Dave_Hoskinse, ktery ho podle vlastniho popisu nasel
// na GLSL Sandboxu, upravil a udelal tile-able. Na dotaz, jestli smi byt pouzity v MIT
// projektu, autor 15. 6. 2026 verejne odpovedel "Yes you can use it". Kod je proto prevzaty
// VERNE — meni se jen vstup (world-space misto UV) a napojeni na nas utlum.
//
// Predchozi verze tady mela vlastni prepis s jednou konstantou misto p.x a p.y. Fungoval,
// ale rusil DROBNOU ASYMETRII MEZI OSAMI, kterou ma originál (x jde pres sin, y pres cos
// a oba se deli mirne jinou hodnotou). Prave ta asymetrie dela vzor neusporadanym.
const float CausticTau = 6.28318530718;
const float CausticIntensity = 0.005;

// JEDNA VRSTVA VZORU. Vraci jas v rozsahu zhruba <0, 1>.
//
// Vsimni si mod na prvnim radku: ten dela z vzoru DLAZDICI. Originál s tim nema problem,
// protoze na Shadertoy je videt jedina dlazdice pres celou obrazovku. U nas se dlazdice
// opakuje kazdych par bloku, takze se na ni z vysky diva jako na tapetu — proto se v Caustics
// skladaji dve tyhle vrstvy v ruznem meritku a natoceni.
float CausticLayer(vec2 uv, float time)
{
    vec2 q = mod(uv * CausticTau, CausticTau) - 250.0;
    vec2 i = q;

    // Posun o 23 je z originalu: v case nula uz ma vzor rozvinuty tvar misto rozjezdu.
    float base = (time * 0.5) + 23.0;
    float c = 1.0;

    for (int n = 0; n < 5; n++)
    {
        // CAS SE MENI S KAZDOU ITERACI, a to je zasadni.
        //
        // Prvni prepis tady mel jedno t spocitane pred smyckou. Vzor pak vychazel VYRAZNE
        // chudsi a pravidelnejsi, protoze vsechny iterace bezely synchronne. Originál pro
        // n = 0 dava -2,5 nasobek casu, pro n = 1 uz jen -0,75 — kazda vrstva ohybu se hybe
        // jinou rychlostí a AZ Z TOHO vznikne ten neusporadany pohyb.
        float t = base * (1.0 - (3.5 / float(n + 1)));

        // Kazdy krok ohne souradnice podle sebe sama — proto vysledek nema pravidelnou mrizku.
        i = q + vec2(cos(t - i.x) + sin(t + i.y), sin(t - i.y) + cos(t + i.x));

        // Prevracena vzdalenost k ohnisku: kde se jmenovatel blizi nule, jde hodnota prudce
        // nahoru a vznikaji izolovane singularity misto spojitych hran. Tohle je duvod, proc
        // vyjdou ODDELENE skvrny a ne souvisla sit, jakou daval Voronoi.
        c += 1.0 / length(vec2(q.x / (sin(i.x + t) / CausticIntensity),
                               q.y / (cos(i.y + t) / CausticIntensity)));
    }

    c /= 5.0;
    c = 1.17 - pow(c, 1.4);

    // Osma mocnina oddeli skvrny od pozadi. Bez ni je z toho jen sedy sum pres celou plochu.
    return pow(abs(c), 8.0);
}

vec3 Caustics(vec2 p, float time, float depth, float distance)
{
    // OSTROST SITE MA MAXIMUM V MALE HLOUBCE, ne v hluboke.
    //
    // Tesne pod hladinou se paprsky jeste nestihly sejit a sit skoro neni; o par bloku niz
    // je nejostrejsi; hloub se ohniska ruznych casti vlny promichaji a sit se rozmaze.
    //
    // Rozpad s hloubkou zaroven resi to, ze shader nepozna, jestli je fragment OPRAVDU pod
    // vodou, nebo jen pod urovni more — v hluboke suche jeskyni je efekt uz neznatelny.
    float focus = (1.0 - exp(-depth * 0.35)) * exp(-depth * 0.055);

    // A SE VZDALENOSTI SE EFEKT MUSI VYTRATIT UPLNE.
    //
    // Skvrna ma kolem peti bloku. Ze stovky bloku a pri sikmem pohledu na ni pripada zlomek
    // pixelu, takze z ni nevznikne vzor, ale MOARE — hustý sikmý raster pres celou zatoku.
    // Presne to bylo videt na leteckem zaberu.
    float fade = exp(-distance * 0.012);

    float strength = focus * fade;

    // TLUMENI SE POCITA DRIV NEZ SAMOTNY VZOR, a to schvalne.
    //
    // Pet iteraci stoji dvacet goniometrickych funkci na fragment. Podminka "pod urovni
    // more" plati pro velkou cast sveta — hluboke dno, jeskyne, vsechno dalekove — a tam
    // vsude by se to spocitalo jen proto, aby se vysledek vynasobil skoro nulou. Tenhle
    // jediny navrat to utne drive, nez to zacne stat vykon.
    if (strength < 0.004)
    {
        return vec3(1.0);
    }

    // DVE VRSTVY, ABY NEBYLA VIDET DLAZDICE.
    //
    // Jedna vrstva sama vypadala jako TAPETA: mod uvnitr CausticLayer dela z vzoru dlazdici
    // a pri devitiblokove delce jich je pres obrazovku videt nekolik desitek, takze oko
    // okamzite chytne opakujici se motiv. Pri puvodnich petadvaceti blocich to videt nebylo,
    // protoze dlazdice byly na obrazovce jen dve tri.
    //
    // Druha vrstva to rozbije dvema veclmi najednou:
    //
    //   * NESOUMERITELNE MERITKO — 0,11 proti 0,079, tedy devet bloku proti necelym trinacti.
    //     Spolecna perioda obou vyjde tak dlouha, ze se do dohledu nevejde.
    //   * NATOCENI zhruba o padesat stupnu. I kdyby meritka nahodou vyslapla spolecnou
    //     periodu, mrizky nejsou rovnobezne a zadny pravidelny raster nevznikne.
    //
    // Rozdilna rychlost casu (0,55 a 0,43) pak brani tomu, aby se obe vrstvy pohybovaly
    // svorne a tvorily zdanlivy spolecny tvar.
    vec2 turned = vec2((p.x * 0.6235) - (p.y * 0.7818),
                       (p.x * 0.7818) + (p.y * 0.6235));

    float first = CausticLayer(p * 0.11, time * 0.55);
    float second = CausticLayer(turned * 0.079, (time * 0.43) + 7.3);

    // Maximum, ne soucet: kde se obe vrstvy potkaji, ma zustat jasna skvrna — soucet by tam
    // dal dvojnasobek a vypalil by bily flek.
    float light = max(first, second);

    // KAUSTIKY NEJSOU BILE. Svetlo, ktere se sem dostalo, uz proslo kusem vody, takze mu
    // chybi cervena — je namodrale zelene. Originál to resi prictenim vec3(0, 0.35, 0.5)
    //
    // Prvni verze vracela cistou bilou a prave proto pusobila prepalene i po ztlumeni:
    // nesla poznat od primeho slunce na suchu.
    vec3 tint = vec3(0.62, 0.94, 1.0);

    // Kaustiky jen PRIDAVAJI svetlo, neubiraji ho: voda pod hladinou nikde netmavne kvuli
    // tomu, ze se jinde svetlo sbihá. Proto 1 + neco, a nikdy 1 - neco.
    //
    // SILA JE ZAMERNE NIZKA. Prvni verze mela 1,6, tedy zisk jasu az dva a pul nasobku —
    // pisek i kamen tim vyjely do saturace. Skutecne kaustiky podklad jen prosvetli, barva
    // pod nimi musi zustat videt.
    return vec3(1.0) + (light * strength * 0.55 * tint);
}

// BAREVNA TEPLOTA SVETLA.
//
// svet jedna barva jen ruzne tmava a vypada jako plastovy model — vsechny plochy maji
//
// Je to jedno nasobeni na fragment a udela to vic nez cokoli jineho za tu cenu.
// Zustava jako zaloha pro pruchody, ktere denni dobu neznaji; zive kresleni bere barvu
// z pc.lightColor, ktera se meni s pohybem slunce.
const vec3 SunTint = vec3(1.00, 0.955, 0.845);
const vec3 SkyTint = vec3(0.72, 0.79, 0.99);

vec3 ApplyLightTint(vec3 color, float shade)
{
    // BARVA SVETLA SE MENI S DENNI DOBOU. Nizke slunce sviti do oranzova, v noci zbude
    // studene mesicni sero — proto se misto pevneho SunTint bere pc.lightColor.
    vec3 warm = pc.lightColor.rgb;

    // V noci se cely svet ztlumi na mesicni svit. Neni to uplna tma: obloha sviti i po
    // zapadu a hrac musi videt, kam slape.
    float level = mix(pc.lightColor.w, 1.0, pc.frameTuning.w);

    return color * mix(SkyTint, warm, clamp(shade, 0.0, 1.0)) * level;
}

// JAK DLOUHO VEDE POHLEDOVY PAPRSEK VODOU.
//
// Tohle je jadro celeho podvodniho vzhledu. Mlha se nesmi pocitat na celou vzdalenost,
// ale jen na tu cast paprsku, ktera je opravdu pod hladinou:
//
//   * kamera i fragment pod vodou  -> cela vzdalenost,
//   * kamera i fragment nad vodou  -> nic,
//   * paprsek hladinu protina      -> podil podle vysek.
//
// Bez toho platila pod vodou kratka modra mlha na uplne vsechno, takze se z vody nedalo
// koukat ven — breh ani obloha nad hladinou nebyly videt. Zadavatel to popsal slovy
// "bylo by fajn, kdybych videl nad vodu".
float UnderwaterPath(float distance)
{
    float sea = pc.water.x;
    float cameraY = pc.cameraAndFogStart.y;

    // KAMERA SE POCITA ZA PONORENOU UZ KOUSEK NAD HLADINOU.
    //
    // Objektiv neni bod. Kdyz je stred kamery presne na urovni vody, spodni pulka obrazu uz
    // je pod hladinou — a prepnuti presne na nule proto prichazi POZDE: hrac je po krk ve
    // vode a svet kolem nej se porad kresli jako suchy.
    //
    // Tri desetiny bloku odpovidaji zhruba polovine zorneho pole u hladiny. Vic uz by bylo
    // videt jako chyba (podvodni nadech pri stani na brehu), min neni poznat.
    bool cameraUnder = cameraY <= (sea + 0.3);
    bool fragmentUnder = vWorld.y <= sea;

    if (cameraUnder == fragmentUnder)
    {
        return cameraUnder ? distance : 0.0;
    }

    float span = vWorld.y - cameraY;
    float toSurface = abs(span) < 1e-4 ? 0.0 : clamp((sea - cameraY) / span, 0.0, 1.0);

    // Kamera pod vodou: vodou vede zacatek paprsku. Nad vodou: az jeho konec.
    float path = distance * (cameraUnder ? toSurface : 1.0 - toSurface);

    if (!cameraUnder)
    {
        return path;
    }

    // MINIMALNI DRAHA VODOU. Tohle je jadro problemu s "pruhem kostek na hladine".
    //
    // Kdyz je kamera TESNE pod hladinou, vyjde (sea - cameraY) skoro nula, takze toSurface
    // je treba 0,01 a draha vodou k plazi dve ste bloku daleko je DVA BLOKY. Voda ji tedy
    // skoro neobarvi a plaz zustane jasne zluta a ostra uprostred jinak modre sceny.
    //
    // Fyzikalne to sedi — oko je pod hladinou jen tenkou vrstvickou — ale vizualne to
    // vypada jako chyba vykreslovani, protoze kamera se u hladiny pohybuje porad a ten pruh
    // naskakuje a mizi.
    //
    // Predtim tu bylo SNELLOVO OKNO, ktere drahu omezovalo SHORA (na 1,51 nasobku hloubky).
    // To slo presne opacnym smerem a pruh jeste zhorsovalo; proto je pryc a proto nestacilo
    // jen zvednout jeho spodni hranici na 5 a pak 22 bloku.
    //
    // Dvacet bloku je tolik, aby plaz zmodrala jako zbytek sceny, a zaroven ne tolik, aby
    // zmizely siluety pevniny na obzoru — ty pri pohledu z hloubky vypadaji dobre.
    return max(path, min(distance, 20.0));
}

// SPEKTRALNI UTLUM: TRI KOEFICIENTY, NE JEDEN.
//
// Tohle je nejdulezitejsi zmena celeho podvodniho vzhledu. Puvodne se michalo do JEDNE
// konstantni barvy, takze dno, steny i dalka koncily na teze hodnote a pod hladinou byla
// jednolita tmave modra placka — presne to, co zadavatel videl na snimku.
//
// Voda ale nepohlcuje vsechny vlnove delky stejne. Cervena mizi radove desetkrat rychleji
// nez modra: v deseti metrech uz z ni prakticky nic nezbyva, kdezto modra dosahne desitek
// metru. Prave tenhle NEPOMER dela podvodni svet modrozelenym misto sedym, a je zdarma —
// staci misto skalaru pouzit vec3.
//
// Pomer 2,17 : 0,71 : 0,46 odpovida tvaru absorpcniho spektra ciste vody. Absolutni
// meritko urcuji pc.water.y a .z, aby se dohled dal ladit bez zasahu do shaderu.
const vec3 AbsorptionRatio = vec3(2.17, 0.71, 0.46);

vec3 ApplyWater(vec3 color, float path, float depthBelowSurface)
{
    // Draha paprsku: jak daleko je pod hladinou videt.
    vec3 transmittance = exp(-AbsorptionRatio * pc.water.z * path);

    // Hloubka fragmentu: kolik vody muselo projit svetlo shora, nez na nej dopadlo.
    // Ridi, jak dobre je z lodky videt dno — bez toho by pohled kolmo dolu prosel skoro
    // cistou vodou, protoze draha je kratka.
    vec3 downwelling = exp(-AbsorptionRatio * pc.water.y * max(depthBelowSurface, 0.0));

    // Rozptylena voda, do ktere se to, co je pohlceno, promeni.
    //
    // Utlumuje se HLOUBKOU V PULCE DRAHY, ne hloubkou fragmentu. Svetlo, ktere se do oka
    // rozptyli, prislo shora nekde mezi okem a fragmentem — kdyby se bralo podle fragmentu,
    // ztmavla by dalka do cerna misto do modra, protoze vzdalene dno lezi hluboko.
    float cameraDepth = max(0.0, pc.water.x - pc.cameraAndFogStart.y);
    float middle = mix(depthBelowSurface, cameraDepth, 0.5);

    vec3 scattered = WaterScatter * exp(-AbsorptionRatio * pc.water.y * middle);

    // ZKOUSELY SE TU SACHTY SVETLA A JSOU PRYC. Zapsano, aby se nezkousely znovu stejne.
    //
    // Myslenka byla spravna: rozptylena voda neni v celem objemu stejne jasna, protoze
    // hladina svetlo sbiha do sloupu. Provedeni ale bylo hrube — jeden vzorek vzoru v pulce
    // drahy, kterym se nasobilo rozptylene svetlo. Pri pohledu shora z toho pres cele dno
    // lezely svetle vlnite pruhy, ktere vypadaly jako mastny film, ne jako svetlo v objemu.
    //
    // Poradne se to musi integrovat PODEL paprsku (nekolik vzorku mezi okem a fragmentem),
    // jinak sloup nema hloubku a je z nej jen dalsi vzor natazeny pres plochu.
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
    return max(saturated * (1.08 + (0.10 * luma)), vec3(0.0));
}

void main()
{
    vec4 texel = texture(uTextures, vec3(vTexCoord, vLayer));
    // nad jeskyni prejde mrak.
    float sunlit = 1.0;
    float shaded = vShade * sunlit * vCloud;

    vec3 color = texel.rgb * LuantiBlend(
        clamp(shaded, 0.0, 1.0), clamp(vBlockLight, 0.0, 1.0), pc.lightColor.rgb);

    // Nasobeni prizname zaridi, ze suchy fragment pod urovni more nema ani kaustiky, ani
    // ztmaveni hloubkou — obojí se pocita prave z teto hodnoty.
    float depthBelow = max(0.0, pc.water.x - vWorld.y) * vSubmerged;

    // Caustics jen na to, co je opravdu pod hladinou, a hlavne na plochy mirici vzhuru:
    // svetlo se sbiha shora, takze na svislou stenu dopada jen okrajove. Jas vrcholu je
    // pro tohle dost dobre meritko, protoze vodorovna plocha ho ma nejvyssi.
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


    // Vzdusna mlha az na ZBYTEK drahy. Pod vodou je jeji podil nulovy, takze obloha
    // pri pohledu vzhuru zustane obloha.
    float airPath = vDistance - waterPath;
    float fogStart = pc.cameraAndFogStart.w;
    float fogEnd = pc.fogColorAndFogEnd.w;

    float fog = clamp((airPath - fogStart) / max(fogEnd - fogStart, 0.001), 0.0, 1.0);

    // Nabeh mlhy uz neni druha mocnina. Ta ji drzela u nuly skoro po celou drahu a pak
    // prudce zavrela; vzdusna perspektiva ma naopak pribyvat pozvolna od zacatku.
    fog = fog * fog * (3.0 - (2.0 * fog));

    // VIVID SE UZ NEPOUZIVA. Bylo to dorovnani jasu, ktery ubralo stare nasobeni
    FragColor = vec4(mix(Tinted(color, vWorld), pc.fogColorAndFogEnd.xyz, fog), 1.0);
}
