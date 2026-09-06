#version 450

layout(location = 0) in vec2 vUv;
layout(location = 1) in float vLayer;
layout(location = 2) in float vLight;
layout(location = 3) in float vDistance;

layout(set = 0, binding = 0) uniform sampler2DArray uTextures;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 cameraAndFogStart;   // xyz kamera, w zacatek mlhy
    vec4 fogColorAndEnd;      // rgb barva mlhy, w konec mlhy
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    vec4 texel = texture(uTextures, vec3(vUv, vLayer));

    // ALFA TEST, ne michani. Item je bud videt, nebo ne; michanim by se vypnul zapis do
    // hloubky a dvacet tisic kostek by se navzajem prekreslovalo v nahodnem poradi.
    if (texel.a < 0.5)
    {
        discard;
    }

    vec3 color = texel.rgb * clamp(vLight, 0.0, 1.0);

    // Stejna mlha jako u zbytku sveta, aby itemy v dalce nevystupovaly z krajiny.
    float fogStart = pc.cameraAndFogStart.w;
    float fogEnd = max(pc.fogColorAndEnd.w, fogStart + 1.0);
    float fog = clamp((vDistance - fogStart) / (fogEnd - fogStart), 0.0, 1.0);
    color = mix(color, pc.fogColorAndEnd.rgb, fog * fog);

    FragColor = vec4(color, 1.0);
}
