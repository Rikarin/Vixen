// Shared by the four bloom variants. The block's offsets are Bloom.rvn's own — texelSize at 0,
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

float Luminance(vec3 colour) { return dot(colour, vec3(0.2126, 0.7152, 0.0722)); }

// Karis' weight, which turns a sum of taps into an average biased towards the darker ones, so a
// highlight in a single texel is pulled towards its neighbours instead of dragging the whole kernel
// up with it. `#define BLOOM_KARIS` before including this file is a variant's `Mode == 1`: Bloom.rvn
// applies the weight on the first downsample and nowhere else, and exactly one .frag beside this file
// defines it.
#ifdef BLOOM_KARIS
vec3 Tap(float dx, float dy) {
    vec3 colour = texture(sampler2D(source, sourceSampler), uv + vec2(dx, dy)).rgb;
    return colour * (1.0 / (1.0 + Luminance(colour)));
}
#else
vec3 Tap(float dx, float dy) {
    return texture(sampler2D(source, sourceSampler), uv + vec2(dx, dy)).rgb;
}
#endif

// Jimenez's 13-tap downsample: a centre box plus four corner boxes, overlapping. The overlap is the
// point — a 4x4 filter built from 2x2 bilinear taps, so it costs 13 samples instead of 16 and its
// kernel has no zeros to alias through.
//
// Here rather than in a .frag because modes 1 and 2 are the same filter and differ only in whether
// Tap() weights, which is how Bloom.rvn shares its own Downsample() between them. The prefilter and
// upsample variants compile it unused; the alternative is these twenty lines in two files that have
// to stay in step.
vec3 Downsample() {
    vec2 d = c.texelSize;

    vec3 a = Tap(-d.x * 2.0,  d.y * 2.0);
    vec3 b = Tap( 0.0,        d.y * 2.0);
    vec3 e = Tap( d.x * 2.0,  d.y * 2.0);
    vec3 f = Tap(-d.x * 2.0,  0.0);
    vec3 g = Tap( 0.0,        0.0);
    vec3 h = Tap( d.x * 2.0,  0.0);
    vec3 i = Tap(-d.x * 2.0, -d.y * 2.0);
    vec3 j = Tap( 0.0,       -d.y * 2.0);
    vec3 k = Tap( d.x * 2.0, -d.y * 2.0);

    vec3 l = Tap(-d.x,  d.y);
    vec3 m = Tap( d.x,  d.y);
    vec3 n = Tap(-d.x, -d.y);
    vec3 o = Tap( d.x, -d.y);

    vec3 total = (l + m + n + o) * 0.125;
    total += g * 0.125;
    total += (a + e + i + k) * 0.03125;
    total += (b + f + h + j) * 0.0625;
    return total;
}
