#version 450

// Obloha: fullscreen trojuhelnik BEZ vertex bufferu.
//
// Vrcholy se nenacitaji z pameti, shader si je vyrobi z gl_VertexIndex. Trojuhelnik
// (ne dva trojuhelniky pres obrazovku) proto, ze na hranicnim svu dvou trojuhelniku
//
// Zdroj se drzi v ASCII stejne jako ostatni shadery, viz chunk.vert.

layout(push_constant) uniform Push
{
    // OSY PAPRSKU MISTO INVERZNI MATICE.
    //
    // Obloha nema geometrii, takze si smer paprsku musi dopocitat sama. Drive to delala
    // tak, ze bod na vzdalene rovine prevedla zpatky do sveta INVERZI matice, kterou se
    // kresli teren. Pomer blizke a vzdalene roviny je ale 0,12 : 8192 a takova matice je
    // na inverzi spatne podminena: mereni ve float ukazalo, ze paprsek miri vedle az
    // o 0,55 pixelu a pri rovnomernem otaceni kolisa jeho krok o 0,41 px, zatimco samotny
    // krok je 0,376 px. Kolisani bylo vetsi nez pohyb — teren stal a slunce poskakovalo.
    //
    // Osy uz nesou rozevreni pohledu, takze paprsek je proste
    //   forward + ndc.x * right + ndc.y * up
    // a nic se pri tom neodecita od velkych cisel.
    vec4 rayRight;            // 0..15    osa doprava, nasobena tg(fov/2) a pomerem stran
    vec4 rayUp;               // 16..31   osa nahoru ve VULKANSKE orientaci, tedy dolu ve svete
    vec4 rayForward;          // 32..47   smer pohledu
    vec4 cameraAndTime;       // 48..63   xyz = kamera, w = cas
    vec4 sunAndSea;           // 64..79   xyz = smer ke slunci, w = hladina
    vec4 zenithAndUnder;      // 80..95   xyz = barva nadhlavniku, w = 1 kdyz je kamera pod vodou
    vec4 horizon;             // 96..111  xyz = barva u obzoru
} pc;

// Smer pohledu ve svete, NENORMALIZOVANY.
//
// Normalizuje se az ve fragmentu. Body na vzdalene rovine lezi v rovine, takze jejich
// linearni interpolace pres trojuhelnik je presna — kdyby se normalizovalo tady,
// interpolovaly by se uz zkracene vektory a smer by uprostred obrazu nesedel.
layout(location = 0) out vec3 vDirection;

void main()
{
    // Vrcholy (-1,-1), (3,-1), (-1,3). Prekryji obrazovku a prebytek se orizne.
    vec2 ndc = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2) * 2.0 - 1.0;

    // Hloubka 0 je ve Vulkanu blizka rovina. Na cem presne se kresli, je jedno —
    // pipeline ma hloubkovy test i zapis vypnuty.
    gl_Position = vec4(ndc, 0.0, 1.0);

    // Paprsek se sklada primo z os kamery. Zadna inverze, zadne deleni, zadne odcitani
    // velkych cisel — vsechny tri clenu maji velikost kolem jednicky.
    vDirection = pc.rayForward.xyz + (ndc.x * pc.rayRight.xyz) + (ndc.y * pc.rayUp.xyz);
}
