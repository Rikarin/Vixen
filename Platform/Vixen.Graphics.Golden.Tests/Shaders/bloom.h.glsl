// Shared by the three bloom variants. The block's offsets are Bloom.rvn's own — texelSize at 0,
// threshold at 8, knee at 12, filterRadius at 16, intensity at 20, the whole thing 32 bytes — which
// is what Library/PostFx/Bloom.reflect.json reports and therefore what EffectConstants fills from.
// Getting one of them wrong here would be a picture that is wrong in a way only a reference catches.

layout(set = 2, binding = 0) uniform Constants {
    vec2 texelSize;
    float threshold;
    float knee;
    float filterRadius;
    float intensity;
} c;

layout(set = 2, binding = 1) uniform texture2D source;
layout(set = 2, binding = 3) uniform sampler sourceSampler;

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 target;

vec3 Tap(float dx, float dy) {
    return texture(sampler2D(source, sourceSampler), uv + vec2(dx, dy)).rgb;
}

float Luminance(vec3 colour) { return dot(colour, vec3(0.2126, 0.7152, 0.0722)); }
