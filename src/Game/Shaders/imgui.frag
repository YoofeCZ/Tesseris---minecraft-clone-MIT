#version 450

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;

layout(set = 0, binding = 0) uniform sampler2D uFont;

layout(location = 0) out vec4 FragColor;

void main()
{
    // ImGui posila barvu uz v prostoru zobrazeni, takze se tu zadna gamma neresi —
    // druha konverze by panely rozjasnila proti zbytku obrazu.
    FragColor = vColor * texture(uFont, vUv);
}
