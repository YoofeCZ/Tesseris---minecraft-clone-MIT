#version 450

// Text v souradnicich obrazovky. Projekce je ortogonalni s pocatkem vlevo nahore.

layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aTexCoord;

layout(push_constant) uniform Push
{
    mat4 projection;  // 0..63
    vec4 color;       // 64..79  xyz = barva textu, w nevyuzito
} pc;

layout(location = 0) out vec2 vTexCoord;

void main()
{
    gl_Position = pc.projection * vec4(aPosition, 0.0, 1.0);
    vTexCoord = aTexCoord;
}
