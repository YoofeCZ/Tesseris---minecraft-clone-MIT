#version 450

// Low spatial fog for valleys, shores and water surfaces. Unlike ordinary distance fog,
// this integrates density through world space between the camera and resolved scene depth.

layout(location = 0) in vec3 vDirection;
layout(location = 0) out vec4 outColor;

// Deliberately identical to the water descriptor. The first two bindings are not sampled;
// sharing its layout gives this pass the resolved depth in the correct Vulkan image layout.
layout(set = 0, binding = 0) uniform sampler2DArray uTextures;
layout(set = 0, binding = 1) uniform sampler2D uScene;
layout(set = 0, binding = 2) uniform sampler2D uDepth;

layout(push_constant) uniform Push
{
    vec4 rayRight;            // w = near plane
    vec4 rayUp;               // w = far plane
    vec4 rayForward;
    vec4 cameraAndTime;
    vec4 sunAndSea;
    vec4 zenithAndUnder;
    vec4 horizon;
} pc;

float Hash12(vec2 p)
{
    vec3 q = fract(vec3(p.xyx) * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return fract((q.x + q.y) * q.z);
}

float ValueNoise(vec2 p)
{
    vec2 cell = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - (2.0 * f));

    float a = Hash12(cell);
    float b = Hash12(cell + vec2(1.0, 0.0));
    float c = Hash12(cell + vec2(0.0, 1.0));
    float d = Hash12(cell + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Vulkan depth 0..1 becomes forward-axis distance. The unnormalised sky ray length then
// turns it into true ray distance away from the centre of the screen.
float RayDistanceFromDepth(float depth, float rayLength)
{
    float nearPlane = pc.rayRight.w;
    float farPlane = pc.rayUp.w;
    float viewDistance = (nearPlane * farPlane)
        / max(farPlane - (depth * (farPlane - nearPlane)), 1e-5);
    return viewDistance * rayLength;
}

void main()
{
    if (pc.rayForward.w <= 0.001)
    {
        outColor = vec4(0.0);
        return;
    }

    // Underwater attenuation already belongs to the water shaders. Stacking grey fog on it
    // would erase refraction and turn every dive into a flat wall.
    if (pc.zenithAndUnder.w > 0.001)
    {
        outColor = vec4(0.0);
        return;
    }

    vec2 uv = gl_FragCoord.xy / vec2(textureSize(uDepth, 0));
    float sceneDepth = texture(uDepth, uv).r;
    float rayLength = length(vDirection);
    vec3 direction = vDirection / rayLength;

    // Sky pixels have no depth. Keep a fixed cap so cost and opacity do not grow with LOD.
    float maximum = sceneDepth >= 0.999999
        ? 1400.0
        : RayDistanceFromDepth(sceneDepth, rayLength);
    maximum = min(maximum, 1400.0);

    // Keep the immediate building area crisp, then let low fog become visible gradually.
    // Its vertical density profile keeps mountain tops clear even though valleys below
    // can already contain a substantial volume of mist.
    const float StartDistance = 96.0;
    if (maximum <= StartDistance)
    {
        outColor = vec4(0.0);
        return;
    }

    // Restrict marching to the actual height layer. Looking down from a mountain therefore
    // spends all twelve samples inside fog instead of wasting them in a kilometre of air.
    float sea = pc.sunAndSea.w;
    float layerBottom = sea - 28.0;
    // The cinematic layer is deliberately tall. A shallow slab looked like a single dry
    // horizontal stripe when observed from a mountain instead of filling the whole valley.
    float layerTop = sea + 230.0;
    float begin = StartDistance;
    float end = maximum;

    if (abs(direction.y) > 1e-5)
    {
        float first = (layerBottom - pc.cameraAndTime.y) / direction.y;
        float second = (layerTop - pc.cameraAndTime.y) / direction.y;
        begin = max(begin, min(first, second));
        end = min(end, max(first, second));
    }
    else if (pc.cameraAndTime.y < layerBottom || pc.cameraAndTime.y > layerTop)
    {
        outColor = vec4(0.0);
        return;
    }

    if (end <= begin)
    {
        outColor = vec4(0.0);
        return;
    }

    const int MaximumSteps = 12;
    int steps = int(round(mix(4.0, float(MaximumSteps), clamp(pc.rayForward.w, 0.25, 1.0))));
    float stride = (end - begin) / float(steps);
    float jitter = Hash12(gl_FragCoord.xy) - 0.5;
    float opticalDepth = 0.0;
    float clusteredLight = 0.0;
    float drift = pc.cameraAndTime.w * 0.0015;

    for (int i = 0; i < MaximumSteps; i++)
    {
        if (i >= steps)
        {
            break;
        }

        float alongRay = begin + ((float(i) + 0.5 + (jitter * 0.72)) * stride);
        vec3 world = pc.cameraAndTime.xyz + (direction * alongRay);
        float altitude = world.y - sea;

        // Y enters both noise planes differently, making this a spatial volume rather than
        // one flat texture sliding across the water.
        float broad = ValueNoise(
            (world.xz * 0.0052) + vec2(world.y * 0.0027, drift));
        float detail = ValueNoise(
            (world.zx * 0.017) + vec2(-drift * 1.7, world.y * 0.009));
        float shape = smoothstep(0.20, 0.70, (broad * 0.72) + (detail * 0.28));

        // A continuous moist body carries the scene while broad noise adds visible volume.
        // The previous base was three times weaker and vanished between isolated wisps.
        float heightFade = exp(-max(altitude, 0.0) * 0.010)
            * (1.0 - smoothstep(175.0, 230.0, altitude));
        float valleyBand = exp(-abs(altitude - 28.0) * 0.015);
        float distanceFade = smoothstep(StartDistance, StartDistance + 384.0, alongRay);
        float density = ((0.00085 * heightFade)
            + (0.00320 * valleyBand * shape * shape)) * distanceFade;

        opticalDepth += density * stride;
        clusteredLight += valleyBand * shape;
    }

    float alpha = min(1.0 - exp(-opticalDepth), 0.74);
    if (alpha < 0.001)
    {
        outColor = vec4(0.0);
        return;
    }

    float daylight = smoothstep(-0.10, 0.22, pc.sunAndSea.y);
    // The fog itself must remain below the LDR bloom threshold (0.80). The old moist
    // base was around 0.9 and bloom therefore blurred the entire horizon into a white
    // rectangle. Only a deliberately narrow forward lobe may cross that threshold.
    float towardSun = pow(max(dot(direction, normalize(pc.sunAndSea.xyz)), 0.0), 22.0);
    vec3 moistDay = vec3(0.70, 0.73, 0.72);
    vec3 neutralMist = mix(pc.horizon.rgb, moistDay, daylight * 0.48);
    vec3 sunScatter = vec3(1.0, 0.76, 0.48) * towardSun * daylight * 0.18;
    float clustered = clamp(clusteredLight / float(steps), 0.0, 1.0);
    vec3 fogColor = min(
        neutralMist + (sunScatter * (0.70 + (clustered * 0.30))),
        vec3(0.88, 0.86, 0.82));

    outColor = vec4(fogColor, alpha);
}
