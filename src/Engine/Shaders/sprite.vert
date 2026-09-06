#version 450

// Ikony predmetu v souradnicich obrazovky. Tataz projekce jako text: pocatek vlevo
// nahore, osa Y dolu.
//
// Proti textu je tu navic VRSTVA ATLASU: ikony se berou z tehoz pole textur jako bloky,
// takze se nemusi stavet druhy atlas kvuli hrstce obrazku.

layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in float aLayer;
layout(location = 3) in vec4 aColor;

layout(push_constant) uniform Push
{
    mat4 projection;  // 0..63
    vec4 tint;        // 64..79  nasobi barvu vrcholu, pro ztmaveni cele davky
} pc;

layout(location = 0) out vec2 vTexCoord;
layout(location = 1) out float vLayer;
layout(location = 2) out vec4 vColor;

void main()
{
    gl_Position = pc.projection * vec4(aPosition, 0.0, 1.0);

    vTexCoord = aTexCoord;
    vLayer = aLayer;
    vColor = aColor * pc.tint;
}
