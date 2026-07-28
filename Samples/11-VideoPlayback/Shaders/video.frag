#version 450

// Three planes and six numbers. This is the whole of what a video costs a renderer: the decoder
// produced Y, Cb and Cr at their own sizes, they were uploaded exactly as they came, and the
// conversion to RGB happens here — in the sampler's own filtering for the chroma, and in six
// multiply-adds for the colour.
//
// Doing it on the CPU instead would mean touching four times as many bytes and uploading four times
// as many, to do on a core what this does for nothing.

layout(set = 0, binding = 0) uniform texture2D luma_plane;
layout(set = 0, binding = 1) uniform texture2D blue_plane;
layout(set = 0, binding = 2) uniform texture2D red_plane;
layout(set = 0, binding = 3) uniform sampler plane_sampler;

layout(push_constant) uniform Constants {
    // xy: scale, zw: offset. Letterboxing, so a 16:9 video in a window of another shape is bordered
    // rather than stretched.
    vec4 fit;

    // x: luma offset, y: luma scale, z: V's contribution to red, w: U's to blue.
    vec4 luma;

    // x: U's contribution to green, y: V's. Both negative.
    vec4 chroma;
} constants;

layout(location = 0) in vec2 varying_texcoord;

layout(location = 0) out vec4 target;

void main() {
    vec2 uv = (varying_texcoord - constants.fit.zw) * constants.fit.xy;

    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
        target = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    // Sampled as bytes rather than as fractions, because that is what the coefficients are written
    // for: they carry the 255/219 and 255/224 that limited range needs, and expressing them for
    // 0..1 samples would mean two sets of constants that have to agree.
    float y = texture(sampler2D(luma_plane, plane_sampler), uv).r * 255.0;
    float u = (texture(sampler2D(blue_plane, plane_sampler), uv).r * 255.0) - 128.0;
    float v = (texture(sampler2D(red_plane, plane_sampler), uv).r * 255.0) - 128.0;

    // The chroma planes are a quarter of the size and are magnified by the sampler, which is why it
    // is linear: point sampling shows as blocking on every colour edge.
    float base = (y - constants.luma.x) * constants.luma.y;

    vec3 rgb = vec3(
        base + (v * constants.luma.z),
        base + (u * constants.chroma.x) + (v * constants.chroma.y),
        base + (u * constants.luma.w)
    );

    target = vec4(clamp(rgb / 255.0, 0.0, 1.0), 1.0);
}
