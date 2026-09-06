#version 450

// BLOOM: zare kolem svetlych mist.
//
// Vezme hotovy obraz, vytahne z nej to nejsvetlejsi, rozmaze to a PRICTE zpatky. Tim
// se kolem osvetlenych ploch, hladiny a oblohy objevi mekky prelivajici se lem — presne
// to, co dela rozdil mezi "vykreslenym" a "nasvicenym" obrazem.
//
// PRAHUJE SE POD BILOU, protoze cil je LDR (B8G8R8A8Unorm). Skutecny bloom se dela
// z hodnot NAD jednickou, ktere se do obrazu nevejdou; tady zadne takove nejsou, vsechno
// je uz oriznute na 1,0. Bere se proto pasmo tesne pod bilou — je slabsi nez HDR bloom,
// zato nevyzaduje predelat format priloh ve vsech pipeline a rozbit kopii sceny pro vodu
// (vkCmdCopyImage potrebuje shodnou velikost texelu zdroje a cile).
//
// Zdroj se drzi v ASCII stejne jako ostatni shadery, viz chunk.vert.

layout(location = 0) in vec2 vUv;

layout(set = 0, binding = 0) uniform sampler2D uScene;

layout(push_constant) uniform Push
{
    vec4 tuning;   // x = prah, y = sila, z = polomer v pixelech, w = nepouzito
} pc;

layout(location = 0) out vec4 FragColor;

// Kolik z pixelu je "prebytek nad prahem".
//
// Pod prahem nula, nad nim plynule nabiha. Tvrdy rez by udelal na hrane svetla ostrou
// obrys nici caru, ktera pri pohybu kamery blika.
vec3 Excess(vec3 color, float threshold)
{
    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));

    // Mekky nastup pres desetinu rozsahu nad prahem.
    float weight = smoothstep(threshold, threshold + 0.50, luma);

    return max(color - vec3(threshold), vec3(0.0)) * weight;
}

void main()
{
    vec2 texel = 1.0 / vec2(textureSize(uScene, 0));
    float radius = pc.tuning.z;

    // TRINACT VZORKU VE DVOU KRUZICH.
    //
    // Jednopruchodovy blur: skutecny bloom se dela ve dvou pruchodech (vodorovne
    // a svisle) nad zmensenou kopii, coz je levnejsi a hladsi. Tohle je jednodussi
    // — nepotrebuje to dalsi obrazy ani dalsi pipeline — a pri tehle sile zare je
    // rozdil v hladkosti neznatelny.
    //
    // Vzorky lezi na dvou kruzich s ruznym polomerem a pootocene proti sobe, aby
    // se z nich nestal viditelny hvezdicovy vzor.
    vec3 sum = Excess(texture(uScene, vUv).rgb, pc.tuning.x) * 0.20;
    float total = 0.20;

    const int Count = 6;

    for (int i = 0; i < Count; i++)
    {
        float angle = (float(i) / float(Count)) * 6.28318530718;

        // Vnitrni kruh: hustsi, vetsi vaha.
        vec2 inner = vec2(cos(angle), sin(angle)) * radius * 0.55;
        sum += Excess(texture(uScene, vUv + (inner * texel)).rgb, pc.tuning.x) * 0.09;
        total += 0.09;

        // Vnejsi kruh pootoceny o pul kroku, aby vzorky nelezely na tychz paprscich.
        float turned = angle + (3.14159265359 / float(Count));
        vec2 outer = vec2(cos(turned), sin(turned)) * radius;
        sum += Excess(texture(uScene, vUv + (outer * texel)).rgb, pc.tuning.x) * 0.043;
        total += 0.043;
    }

    vec3 glow = (sum / total) * pc.tuning.y;

    // Michani je aditivni (BlendMode.Add), takze se tohle k obrazu PRICTE. Alfa se
    // nepouziva, ale musi byt konecna.
    FragColor = vec4(glow, 1.0);
}
