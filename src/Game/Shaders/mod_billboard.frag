#version 450

layout(set = 0, binding = 0) uniform sampler2D colorTexture;

layout(location = 0) in vec2 fragUv;
layout(location = 1) in vec4 fragColor;
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 sampled = texture(colorTexture, fragUv) * fragColor;
    if (sampled.a <= 0.001)
    {
        discard;
    }
    outColor = sampled;
}
