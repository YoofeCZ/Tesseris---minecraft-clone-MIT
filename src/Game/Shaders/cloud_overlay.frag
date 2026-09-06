#version 450
#extension GL_GOOGLE_include_directive : require
#include "cloud_common.glsl"

layout(location = 0) in vec3 vDirection;
layout(set = 0, binding = 0) uniform sampler2DArray uTextures;

layout(push_constant) uniform Push
{
    vec4 rayRight;
    vec4 rayUp;
    vec4 rayForward;
    vec4 cameraAndTime;
    vec4 sunAndSea;
    vec4 zenithAndUnder;
    vec4 horizon;
} pc;

layout(location = 0) out vec4 FragColor;

void main()
{
    if (pc.cameraAndTime.y < CloudBaseHeight)
    {
        FragColor = vec4(0.0);
        return;
    }

    FragColor = CloudVolume(
        pc.cameraAndTime.xyz, normalize(vDirection), pc.sunAndSea.xyz,
        pc.zenithAndUnder.xyz, pc.horizon.xyz, pc.cameraAndTime.w, gl_FragCoord.xy,
        pc.rayForward.w);

    if (false)
    {
        FragColor += texture(uTextures, vec3(0.0));
    }
}
