#version 450

// ZAPORNA VRSTVA ZNAMENA PLNOU BARVU. Panel inventare potrebuje kreslit i obdelniky
// pozadi a ramecky, a mit na to druhou pipeline by znamenalo druhy stav i druhy buffer
// kvuli jedne barve. Zaporna vrstva se proto bere jako "netexturuj".

layout(location = 0) in vec2 vTexCoord;
layout(location = 1) in float vLayer;
layout(location = 2) in vec4 vColor;

layout(set = 0, binding = 0) uniform sampler2DArray uAtlas;

layout(push_constant) uniform Push
{
    mat4 projection;
    vec4 tint;
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    if (vLayer < 0.0)
    {
        FragColor = vColor;
        return;
    }

    vec4 texel = texture(uAtlas, vec3(vTexCoord, vLayer));

    // Vyrez, ne michani: ikony nastroju jsou z vetsi casti dira a poloprusvitny okraj
    // by pres pozadi panelu udelal seda lem.
    if (texel.a < 0.35)
    {
        discard;
    }

    FragColor = vec4(texel.rgb * vColor.rgb, 1.0);
}
