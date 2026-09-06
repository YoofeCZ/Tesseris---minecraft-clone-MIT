#version 450

layout(push_constant) uniform Push
{
    mat4 viewProjection;
    vec4 color;
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    FragColor = pc.color;
}
