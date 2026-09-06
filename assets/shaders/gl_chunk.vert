#version 460 core

// Vrchol chunku pro OpenGL.
//
// Rozvrzeni odpovida PRESNE tomu, co vyrobil mesher (MeshBuffer): osm floatu, ktere jdou
// na GPU beze zmeny. Zadne prerovnavani do ciziho formatu se nedeje.
//
// Verze 460 core - nejvyssi vydana. Na macOS Apple zastavil OpenGL na 4.1, takze tam
// se bude muset snizit; Mac se bude resit Metalem zvlast.
//
// Zdroj se drzi v ASCII. Poznamka u puvodniho chunk.vert rika, ze si na diakritice
// rozbil parser ovladac AMD.

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in float aLayer;
layout(location = 3) in float aShade;
layout(location = 4) in float aBlockLight;

uniform mat4 uViewProjection;

// xyz = kamera, w = zacatek mlhy
uniform vec4 uCameraAndFogStart;

out vec2 vTexCoord;
out float vLayer;
out float vShade;
out float vBlockLight;
out float vDistance;

// ROZBALENI STINENI. Mesher do jednoho floatu zabalil sest poli (viz
// ChunkMesher.EmitCross) a bez tohohle by rostliny vysly bile - jejich zabalena hodnota
// je radove vetsi nez jedna, takze pouzita primo jako jas vsechno presviti. U plnych
// bloku je to cislo shodou okolnosti rovne stineni, takze teren vypada spravne i bez
// rozbaleni; pozna se to az na trave.
//
//     +2      nad blokem je voda
//     +4  * n  n = kolikaty clanek rostliny zdola
//    +32  * m  m = vyska celeho sloupce minus jedna
//   +256      drobny porost
//  +1024 * l  l = blokove svetlo
//
// Poradi je ZAVAZNE: od nejvyssiho pole dolu. Obracene by se priznak porostu pripocetl
// k vysce sloupce. Deleni mocninami dvojky je v plovouci carce presne; floor(), ne mod()
// - mod() se u zapornych cisel chova jinak a je to ticha past.
//
// POZOR na jmeno parametru: "packed" je v GLSL rezervovane slovo a shader se s nim
// neprelozi. Ve Vulkan verzi tentyz nazev projde, protoze glslangValidator je
// tolerantnejsi - zkopirovat sem kus shaderu z Vulkanu tedy nemusi stacit.
void UnpackShade(float packedShade, out float shade, out float submerged, out float bakedLight)
{
    bakedLight = floor(packedShade / 1024.0);
    packedShade -= bakedLight * 1024.0;
    bakedLight /= 15.0;

    float plant = floor(packedShade / 256.0);
    packedShade -= plant * 256.0;

    float total = floor(packedShade / 32.0);
    packedShade -= total * 32.0;

    float segment = floor(packedShade / 4.0);
    packedShade -= segment * 4.0;

    submerged = step(1.5, packedShade);
    shade = packedShade - (submerged * 2.0);
}

void main()
{
    gl_Position = uViewProjection * vec4(aPosition, 1.0);

    vTexCoord = aTexCoord;
    vLayer = aLayer;

    float shade;
    float submerged;
    float bakedLight;
    UnpackShade(aShade, shade, submerged, bakedLight);

    vShade = shade;

    // Blokove svetlo je ve dvou mistech: zabalene ve stineni a zvlast ve vlastnim poli.
    // Plati to vyssi - mesher jedno z nich u casti geometrie nechava nulove.
    vBlockLight = max(bakedLight, aBlockLight);

    // Vzdalenost od kamery pro mlhu se pocita zvlast, ne z gl_Position.w - ta je
    // vzdalenost od PROMITACI ROVINY, ne od oka, a do rohu obrazu by mlha sedala driv
    // nez uprostred.
    vDistance = distance(aPosition, uCameraAndFogStart.xyz);
}
