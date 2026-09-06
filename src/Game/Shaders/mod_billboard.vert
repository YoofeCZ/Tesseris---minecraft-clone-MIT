#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in vec4 inColor;

layout(location = 0) out vec2 fragUv;
layout(location = 1) out vec4 fragColor;

layout(push_constant) uniform PushConstants
{
    mat4 viewProjection;
} pushConstants;

void main()
{
    gl_Position = pushConstants.viewProjection * vec4(inPosition, 1.0);
    fragUv = inUv;
    fragColor = inColor;
}
