#version 450

// in display space, so this pass deliberately does not apply a second gamma conversion.
// The floating-point target still preserves lighting energy above display white for bloom
// and the fitted filmic shoulder.

layout(location = 0) in vec2 vUv;
layout(set = 0, binding = 0) uniform sampler2D uScene;

layout(push_constant) uniform Push
{
    // w encodes both FXAA and split tone: sign enables FXAA, abs(w)-1 is strength.
    vec4 tuning;
} pc;

layout(location = 0) out vec4 FragColor;

vec3 AcesFitted(vec3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((color * (a * color + b)) / (color * (c * color + d) + e), 0.0, 1.0);
}

// FXAA 3-style edge search adapted from Luanti's client/shaders/fxaa implementation.
// That shader credits Armin Ronacher and WebGL-Minecraft. The required copyright,
// conditions and disclaimer are preserved in README.md (Licence section).
vec3 SampleFxaa(vec2 uv)
{
    const float reduceMin = 1.0 / 128.0;
    const float reduceMul = 1.0 / 8.0;
    const float spanMax = 8.0;
    const vec3 lumaWeights = vec3(0.299, 0.587, 0.114);
    vec2 inverseSize = 1.0 / vec2(textureSize(uScene, 0));

    vec3 rgbNW = texture(uScene, uv + vec2(-1.0, -1.0) * inverseSize).rgb;
    vec3 rgbNE = texture(uScene, uv + vec2( 1.0, -1.0) * inverseSize).rgb;
    vec3 rgbSW = texture(uScene, uv + vec2(-1.0,  1.0) * inverseSize).rgb;
    vec3 rgbSE = texture(uScene, uv + vec2( 1.0,  1.0) * inverseSize).rgb;
    vec3 rgbM = texture(uScene, uv).rgb;

    float lumaNW = dot(rgbNW, lumaWeights);
    float lumaNE = dot(rgbNE, lumaWeights);
    float lumaSW = dot(rgbSW, lumaWeights);
    float lumaSE = dot(rgbSE, lumaWeights);
    float lumaM = dot(rgbM, lumaWeights);
    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    vec2 direction;
    direction.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    direction.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));
    float reduction = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * reduceMul), reduceMin);
    float reciprocalMinimum = 1.0 / (min(abs(direction.x), abs(direction.y)) + reduction);
    direction = clamp(direction * reciprocalMinimum, vec2(-spanMax), vec2(spanMax)) * inverseSize;

    vec3 rgbA = 0.5 * (
        texture(uScene, uv + direction * (1.0 / 3.0 - 0.5)).rgb +
        texture(uScene, uv + direction * (2.0 / 3.0 - 0.5)).rgb);
    vec3 rgbB = rgbA * 0.5 + 0.25 * (
        texture(uScene, uv + direction * -0.5).rgb +
        texture(uScene, uv + direction *  0.5).rgb);
    float lumaB = dot(rgbB, lumaWeights);
    return (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;
}

void main()
{
    bool fxaaEnabled = pc.tuning.w > 0.0;
    float splitTone = max(abs(pc.tuning.w) - 1.0, 0.0);
    vec3 source = max(fxaaEnabled ? SampleFxaa(vUv) : texture(uScene, vUv).rgb, vec3(0.0));
    vec3 color = AcesFitted(source * max(pc.tuning.x, 0.001));

    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    color = mix(vec3(luma), color, pc.tuning.y);
    color = (color - 0.5) * pc.tuning.z + 0.5;

    float highlight = smoothstep(0.48, 0.92, luma);
    float shadow = 1.0 - smoothstep(0.08, 0.52, luma);
    color *= mix(vec3(1.0), vec3(1.035, 1.008, 0.965), highlight * splitTone);
    color *= mix(vec3(1.0), vec3(0.965, 0.985, 1.035), shadow * splitTone);

    FragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
