#ifndef TESSERIS_CLOUD_COMMON
#define TESSERIS_CLOUD_COMMON

// Volumetric cloud layer for Tesseris.
//
// The density and front-to-back march follow the method demonstrated by Inigo Quilez
// in "Clouds" (XslGRr): periodic 3D value noise, octave LOD, a density gradient in the
// sun direction and distance-growing samples. The implementation here is original and
// scaled for Tesseris's 512-block-tall world. Lighting integration follows the
// energy-conserving transmittance formulation described in the Frostbite 2016 notes.
//
// The previous cloud field used 420-block macro cells and only 18 view samples. From a
// normal camera height a single smooth cell therefore covered most of the sky and its
// phase term turned the edge into a white outline. These constants keep individual
// billows readable while leaving a safe gap above the tallest generated mountain.
const float CloudBaseHeight = 920.0;
const float CloudTopHeight = 1160.0;
const float CloudLayerThickness = CloudTopHeight - CloudBaseHeight;
// Twice the base renderer wrap: one weather field takes roughly 3.3 hours to cross its
// full period. The resulting ~2.05 blocks/second is visibly alive from the ground while
const float CloudTimeWrap = 11967.9720137;
const float CloudNoiseTile = 64.0;
const float CloudShapeScale = 96.0;
const float CloudWeatherScale = CloudShapeScale * 4.0;
const float CloudWeatherWorldPeriod = CloudNoiseTile * CloudWeatherScale;
const float CloudMaximumViewDistance = 3400.0;

float CloudHash(vec2 p)
{
    p = mod(p, CloudNoiseTile);
    vec3 q = fract(vec3(p.xyx) * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return fract((q.x + q.y) * q.z);
}

float CloudNoise(vec2 p)
{
    vec2 cell = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - (2.0 * f));
    float a = CloudHash(cell);
    float b = CloudHash(cell + vec2(1.0, 0.0));
    float c = CloudHash(cell + vec2(0.0, 1.0));
    float d = CloudHash(cell + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float CloudHash3(vec3 p)
{
    p = mod(p, vec3(CloudNoiseTile, 32.0, CloudNoiseTile));
    p = fract(p * 0.1031);
    p += dot(p, p.yxz + 33.33);
    return fract((p.x + p.y) * p.z);
}

float CloudNoise3(vec3 p)
{
    vec3 cell = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - (2.0 * f));

    float n000 = CloudHash3(cell);
    float n100 = CloudHash3(cell + vec3(1.0, 0.0, 0.0));
    float n010 = CloudHash3(cell + vec3(0.0, 1.0, 0.0));
    float n110 = CloudHash3(cell + vec3(1.0, 1.0, 0.0));
    float n001 = CloudHash3(cell + vec3(0.0, 0.0, 1.0));
    float n101 = CloudHash3(cell + vec3(1.0, 0.0, 1.0));
    float n011 = CloudHash3(cell + vec3(0.0, 1.0, 1.0));
    float n111 = CloudHash3(cell + vec3(1.0, 1.0, 1.0));

    float bottom = mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y);
    float top = mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y);
    return mix(bottom, top, f.z);
}

vec2 CloudDrift(float time)
{
    // One complete weather period, and therefore four complete shape periods, over the
    // renderer's deliberately slow wrapped time. Both noise layers join without a jump.
    float phase = fract(abs(time) / CloudTimeWrap);
    return vec2(phase * CloudWeatherWorldPeriod, 0.0);
}

float CloudFbm(vec3 point, int octaveCount)
{
    float value = 0.0;
    float weight = 0.5;
    float totalWeight = 0.0;

    // Close samples get five octaves. Distant samples stop earlier, matching XslGRr's
    // explicit map5/map4/map3/map2 stages without duplicating the density function.
    for (int octave = 0; octave < 5; octave++)
    {
        if (octave >= octaveCount)
        {
            break;
        }

        value += (CloudNoise3(point) * 2.0 - 1.0) * weight;
        totalWeight += weight;

        float octaveScale = octave == 0 ? 2.02
            : (octave == 1 ? 2.03 : (octave == 2 ? 2.01 : 2.02));
        point = (point * octaveScale) + vec3(7.13, 3.71, 11.17);
        weight *= 0.5;
    }

    return value / max(totalWeight, 0.001);
}

float CloudDensityLod(vec3 world, float time, int octaveCount)
{
    float height = (world.y - CloudBaseHeight) / CloudLayerThickness;
    if (height <= 0.0 || height >= 1.0)
    {
        return 0.0;
    }

    vec2 moved = world.xz + CloudDrift(time);

    // A broad weather term groups the smaller billows into separate cloud banks. It does
    // not directly draw a silhouette: the 3D FBM below still decides every occupied voxel.
    float weather = CloudNoise((moved / CloudWeatherScale) + vec2(19.0, 7.0));
    weather = weather * 2.0 - 1.0;

    vec3 point = vec3(
        moved.x / CloudShapeScale,
        (world.y - CloudBaseHeight) / 72.0,
        moved.y / CloudShapeScale);
    point += vec3(5.0, 1.0, 13.0);

    float billow = CloudFbm(point, octaveCount);

    // This is the key XslGRr-style implicit density surface. The negative bias cuts blue
    // gaps, FBM grows actual volumes, and height progressively removes mass toward the top.
    float field = -0.20 - (height * 0.38) + (billow * 1.85) + (weather * 0.34);

    // Never expose the mathematical slab as a flat plane. The fade only clips the extreme
    // bottom and top; all visible sides still come from volumetric noise.
    float vertical = smoothstep(0.0, 0.055, height)
        * (1.0 - smoothstep(0.82, 1.0, height));
    return clamp(field * 1.35, 0.0, 1.0) * vertical;
}

float CloudDensity(vec3 world, float time)
{
    return CloudDensityLod(world, time, 5);
}

float CloudDirectionalLight(
    vec3 point, vec3 sunDirection, float time, float density, float daylight)
{
    if (daylight <= 0.001)
    {
        return 0.0;
    }

    // XslGRr derives shape lighting from one density difference toward the sun. Keeping
    // that property matters because this runs for every occupied view sample.
    float towardSun = CloudDensityLod(point + (sunDirection * 34.0), time, 2);
    float gradient = clamp((density - towardSun) * 2.15, 0.0, 1.0);
    float transmission = exp(-towardSun * 1.35);
    return clamp((0.18 + (gradient * 0.92)) * transmission, 0.0, 1.0);
}

float CloudSunTransmission(vec3 world, vec3 sunDirection, float time, float daylight)
{
    float horizon = smoothstep(0.055, 0.22, sunDirection.y) * clamp(daylight, 0.0, 1.0);
    if (horizon <= 0.001 || world.y >= CloudTopHeight)
    {
        return 1.0;
    }

    // The physical ray crosses a layer roughly 600 blocks above the terrain. Close to
    // sunrise its horizontal projection therefore races across the ground many times
    // faster than the clouds themselves, even with a twenty-minute day. Preserve the
    // direction of the sun but compress that parallax to a shallow, bounded offset. The
    const float ShadowParallax = 0.18;
    vec3 shadowDirection = normalize(vec3(
        sunDirection.x * ShadowParallax,
        1.0,
        sunDirection.z * ShadowParallax));

    float enter = max((CloudBaseHeight - world.y) / shadowDirection.y, 0.0);
    float leave = max((CloudTopHeight - world.y) / shadowDirection.y, enter);

    // This runs in every world vertex shader, including far terrain. One broad one-octave
    float opticalDepth = CloudDensityLod(
        world + (shadowDirection * mix(enter, leave, 0.48)), time, 1);
    float occlusion = 1.0 - exp(-opticalDepth * 2.0);
    return 1.0 - (occlusion * horizon * 0.56);
}

float CloudEpochTransmission(
    vec3 world, vec3 currentDepthRow, vec3 previousDepthRow,
    float epochBlend, float time, float daylight)
{
    if (epochBlend < 0.0)
    {
        return 1.0;
    }

    float current = CloudSunTransmission(world, -normalize(currentDepthRow), time, daylight);
    if (epochBlend >= 0.999)
    {
        return current;
    }

    float previous = CloudSunTransmission(world, -normalize(previousDepthRow), time, daylight);
    return mix(previous, current, clamp(epochBlend, 0.0, 1.0));
}

vec4 CloudVolume(
    vec3 origin, vec3 ray, vec3 sunDirection, vec3 zenith, vec3 horizon,
    float time, vec2 pixel, float quality)
{
    if (quality <= 0.001)
    {
        return vec4(0.0);
    }
    quality = clamp(quality, 0.25, 1.0);
    float start;
    float finish;
    bool insideLayer = origin.y > CloudBaseHeight && origin.y < CloudTopHeight;

    if (abs(ray.y) < 0.0005)
    {
        if (origin.y <= CloudBaseHeight || origin.y >= CloudTopHeight)
        {
            return vec4(0.0);
        }

        start = 0.0;
        finish = CloudMaximumViewDistance;
    }
    else
    {
        float first = (CloudBaseHeight - origin.y) / ray.y;
        float second = (CloudTopHeight - origin.y) / ray.y;
        start = max(min(first, second), 0.0);
        finish = max(first, second);
    }

    finish = min(finish, CloudMaximumViewDistance);
    if (insideLayer)
    {
        finish = min(finish, 900.0);
    }

    if (finish <= start || start >= CloudMaximumViewDistance)
    {
        return vec4(0.0);
    }

    const int MaximumViewSteps = 32;
    int viewSteps = int(round(mix(10.0, float(MaximumViewSteps), quality)));
    float stepLength = (finish - start) / float(viewSteps);
    if (stepLength <= 0.0001)
    {
        return vec4(0.0);
    }

    float daylight = smoothstep(-0.12, 0.20, sunDirection.y);
    vec3 sunTint = mix(
        vec3(1.00, 0.56, 0.30),
        vec3(1.00, 0.955, 0.845),
        smoothstep(0.02, 0.38, sunDirection.y));
    vec3 skyAmbient = mix(
        vec3(0.25, 0.29, 0.38),
        vec3(0.66, 0.71, 0.78),
        daylight);
    vec3 background = mix(horizon, zenith, pow(clamp(ray.y, 0.0, 1.0), 0.42));

    vec3 accumulated = vec3(0.0);
    float transmittance = 1.0;
    float jitter = fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));

    for (int sampleIndex = 0; sampleIndex < MaximumViewSteps; sampleIndex++)
    {
        if (sampleIndex >= viewSteps)
        {
            break;
        }

        float distance = start + ((float(sampleIndex) + jitter) * stepLength);
        vec3 point = origin + (ray * distance);

        int lod = distance < 700.0 ? 5
            : (distance < 1350.0 ? 4 : (distance < 2300.0 ? 3 : 2));
        lod = max(2, lod - int(round((1.0 - quality) * 2.0)));
        float density = CloudDensityLod(point, time, lod);

        // The last part of the march dissolves into aerial perspective instead of ending
        // in a repeated carpet or a hard maximum-distance ring.
        density *= 1.0 - smoothstep(2700.0, CloudMaximumViewDistance, distance);
        if (density <= 0.004)
        {
            continue;
        }

        float height = clamp((point.y - CloudBaseHeight) / CloudLayerThickness, 0.0, 1.0);
        float directional = CloudDirectionalLight(
            point, sunDirection, time, density, daylight);

        // Dense water clouds become blue-grey inside, while their low-density outer mass
        // remains warm white. Brightness comes from the density gradient, never a drawn rim.
        vec3 material = mix(
            vec3(1.00, 0.965, 0.88),
            vec3(0.29, 0.34, 0.42),
            clamp(density * 0.92, 0.0, 1.0));
        vec3 ambient = material * skyAmbient * (0.82 + (height * 0.24));
        vec3 direct = material * sunTint * directional * daylight * 0.52;
        vec3 sampleColor = ambient + direct;

        // Aerial perspective is evaluated once per sample here because the sky pass has no
        // separate atmosphere LUT. Squared distance mirrors the XslGRr fog term and makes
        // distant banks merge naturally with the horizon.
        float aerial = 1.0 - exp(-distance * distance * 0.00000036);
        sampleColor = mix(sampleColor, background, clamp(aerial, 0.0, 0.92));
        sampleColor = min(sampleColor, vec3(0.78, 0.79, 0.80));

        // Frostbite's analytical slab integration reduces to this when scattering albedo is
        // one: the exact Beer-Lambert loss over the step becomes the scattered contribution.
        float stepTransmission = exp(-density * stepLength * 0.027);
        float contribution = transmittance * (1.0 - stepTransmission);
        accumulated += sampleColor * contribution;
        transmittance *= stepTransmission;

        if (transmittance < 0.012)
        {
            break;
        }
    }

    float opacity = 1.0 - transmittance;
    vec3 straightColor = opacity > 0.0001 ? accumulated / opacity : vec3(0.0);
    return vec4(straightColor, clamp(opacity, 0.0, 1.0));
}

#endif
