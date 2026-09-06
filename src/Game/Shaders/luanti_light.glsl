// SKLADANI SVETLA PODLE LUANTI.
//
// Prevzato z Luanti (LGPL-2.1-or-later), src/client/mapblock_mesh.cpp, funkce
// encode_light a final_color_blend. Ciselna cast osvetlovaciho modelu je v C#
// v Tesseris.Engine.Rendering.LuantiLight; tohle je jeji druha polovina.
//
//   night = nocni (umela) banka, tymz zpusobem
//
// Cely trik je v tom, ze se do vrcholu neuklada hotovy jas, ale POMER obou bank. Denni
// cast se nasobi barvou slunce, ktera s dennim cyklem hasne, nocni pevnym 1.04. Proto
// pochoden sviti v noci uplne stejne jako v poledne a jeskyne nezesvetla s vychodem
// slunce — a nic z toho nevyzaduje premeshovani.
//
// Nasobeni dvema neni libovolne: b je PRUMER obou bank, tedy nejvys 0.5, takze dvojka
// vraci rozsah zpatky na 0..1.
vec3 LuantiBlend(float day, float night, vec3 sunlightColor)
{
    const vec3 artificialColor = vec3(1.04);

    float d = max(day - night, 0.0);
    float sum = d + night;
    float ratio = sum > 0.0 ? d / sum : 0.0;
    float b = 0.5 * sum;

    vec3 light = b * ((ratio * sunlightColor) + ((1.0 - ratio) * artificialColor)) * 2.0;

    // ZDURAZNENI MODRE V SERU. Doslovna tabulka z Luanti: kazda polozka pokryva osm
    // urovni modre. Bez toho je sero sede a vypada jako spatne exponovana fotka.
    const float emphaseBlue[16] = float[16](
        1.0, 4.0, 6.0, 6.0, 6.0, 5.0, 4.0, 3.0,
        2.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
    int band = int(clamp((light.r + light.g + light.b) / 3.0 * 255.0, 0.0, 255.0)) / 8;
    light.b += (band < 16 ? emphaseBlue[band] : 0.0) / 255.0;

    return light;
}
