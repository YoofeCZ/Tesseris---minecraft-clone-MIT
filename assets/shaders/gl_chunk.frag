#version 460 core

// Fragment chunku pro OpenGL.
//
// Osvetleni je z Luanti a je totozne s tim, co dela luanti_light.glsl na Vulkanu.
// Telo LuantiBlend je tu vlozene primo, protoze GLSL nema #include a rozdelovat to
// na dva soubory by znamenalo skladat je za behu.
//
// Verze 460 core - nejvyssi vydana. Proto out promenna misto gl_FragColor.

uniform sampler2DArray uTextures;

// xyz = barva mlhy, w = konec mlhy
uniform vec4 uFogColorAndFogEnd;
uniform vec4 uCameraAndFogStart;

// Barva slunce podle denni doby. Pocita ji DayCycle pres LuantiLight.SunlightColor.
uniform vec3 uSunlightColor;

in vec2 vTexCoord;
in float vLayer;
in float vShade;
in float vBlockLight;
in float vDistance;

out vec4 fragColor;

// Prevzato z Luanti (LGPL-2.1-or-later), src/client/mapblock_mesh.cpp - encode_light
// a final_color_blend.
//
// Do vrcholu se neuklada hotovy jas, ale POMER obou bank. Denni cast se nasobi barvou
// slunce, ktera s dennim cyklem hasne, nocni pevnym 1.04. Proto pochoden sviti v noci
// stejne jako v poledne a jeskyne nezesvetla s vychodem slunce.
vec3 LuantiBlend(float day, float night, vec3 sunlightColor)
{
    const vec3 artificialColor = vec3(1.04);

    float d = max(day - night, 0.0);
    float sum = d + night;
    float ratio = sum > 0.0 ? d / sum : 0.0;

    // b je PRUMER obou bank, tedy nejvys 0.5 - dvojka vraci rozsah zpatky na 0..1.
    float b = 0.5 * sum;

    vec3 light = b * ((ratio * sunlightColor) + ((1.0 - ratio) * artificialColor)) * 2.0;

    // ZDURAZNENI MODRE V SERU. Bez toho je sero sede a vypada jako spatne exponovana
    // fotka.
    float emphaseBlue[16] = float[16](
        1.0, 4.0, 6.0, 6.0, 6.0, 5.0, 4.0, 3.0,
        2.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);

    int band = int(clamp((light.r + light.g + light.b) / 3.0 * 255.0, 0.0, 255.0)) / 8;
    light.b += (band < 16 ? emphaseBlue[band] : 0.0) / 255.0;

    return light;
}

void main()
{
    vec4 albedo = texture(uTextures, vec3(vTexCoord, vLayer));

    // Dirave textury (listi, kytky) se zahazuji, ne michaji - michani by u nich
    // zaviselo na poradi kresleni.
    if (albedo.a < 0.5)
        discard;

    vec3 light = LuantiBlend(vShade, vBlockLight, uSunlightColor);
    vec3 color = albedo.rgb * light;

    float fogStart = uCameraAndFogStart.w;
    float fogEnd = uFogColorAndFogEnd.w;
    float fog = clamp((vDistance - fogStart) / max(fogEnd - fogStart, 1.0), 0.0, 1.0);
    color = mix(color, uFogColorAndFogEnd.rgb, fog);

    fragColor = vec4(color, albedo.a);
}
