#version 450

// Vrchol vody. Proti chunk.vert posila cely svetovy bod, ne jen jeho vysku.
//
// Voda potrebuje svetove XZ na vlny a cely bod na smer pohledu. chunk.vert se kvuli tomu
// nemenil: pouzivaji ho ctyri pipeliny, kterym by dve slozky navic byly k nicemu.
//
// Zdroj se drzi v ASCII stejne jako ostatni shadery, viz chunk.vert.

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in float aLayer;
layout(location = 3) in float aShade;

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 cameraAndFogStart;
    vec4 fogColorAndFogEnd;
    vec4 water;               // x = hladina, y = pohltivost hloubkou, z = pohltivost drahou, w = cas
    vec4 chunkOffset;
} pc;

layout(location = 0) out vec2 vTexCoord;
layout(location = 1) out float vLayer;
layout(location = 2) out float vShade;
layout(location = 3) out float vDistance;
layout(location = 4) out vec3 vWorld;

void main()
{
    vec3 world = aPosition + pc.chunkOffset.xyz;
    gl_Position = pc.viewProjection * vec4(world, 1.0);

    vTexCoord = aTexCoord;
    vLayer = aLayer;
    vShade = aShade;
    vDistance = length(world - pc.cameraAndFogStart.xyz);
    vWorld = world;
}
