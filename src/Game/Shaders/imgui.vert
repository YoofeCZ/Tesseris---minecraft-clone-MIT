#version 450

// ImGui posila vrcholy v pixelech obrazovky. Prevod do clip prostoru se dela tady,
// aby to shader zvladl bez matice zvenku — staci rozmery okna v push konstantach.

layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in vec4 inColor;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;

layout(push_constant) uniform Push
{
    vec2 scale;       // 2 / velikost okna
    vec2 translate;   // -1
} pc;

void main()
{
    gl_Position = vec4((inPosition * pc.scale) + pc.translate, 0.0, 1.0);
    vUv = inUv;
    vColor = inColor;
}
