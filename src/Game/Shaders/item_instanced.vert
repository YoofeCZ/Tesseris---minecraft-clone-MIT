#version 450

// INSTANCOVANE ITEMY NA PASECH.
//
// Geometrie je JEDNA mala kostka, ktera se nahraje jednou a uz se nikdy nemeni.
// Vsechno, cim se jednotlivy item lisi, prijde per instance z druhe vazby: kam ho dat,
// jakou ma texturu a kolik na nej sviti.
//
// Proti puvodni ceste je rozdil zasadni. Ta skladala kazdy snimek znovu geometrii vsech
// itemu na CPU do jedne velke mesh a celou ji nahravala — pri 20 000 itemech to je pres
// 15 MB vrcholu na snimek. Tady se nahrava jen 16 bajtu na item.

// --- vazba 0: geometrie kostky, spolecna vsem ---
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;

// --- vazba 1: data instance, jina pro kazdy item ---
layout(location = 2) in vec3 inOffset;   // stred itemu ve svete
layout(location = 3) in float inLayer;   // vrstva v texturovem poli
layout(location = 4) in float inScale;   // velikost kostky
layout(location = 5) in float inLight;   // 0 az 1, kolik na item sviti

layout(location = 0) out vec2 vUv;
layout(location = 1) out float vLayer;
layout(location = 2) out float vLight;
layout(location = 3) out float vDistance;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 cameraAndFogStart;   // xyz kamera, w zacatek mlhy
    vec4 fogColorAndEnd;      // rgb barva mlhy, w konec mlhy
} pc;

void main()
{
    vec3 world = inOffset + (inPosition * inScale);

    gl_Position = pc.viewProjection * vec4(world, 1.0);
    vUv = inUv;
    vLayer = inLayer;
    vLight = inLight;
    vDistance = distance(world, pc.cameraAndFogStart.xyz);
}
