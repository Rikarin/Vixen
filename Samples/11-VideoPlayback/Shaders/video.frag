#version 450

// Three planes and six numbers. This is the whole of what a video costs a renderer: the decoder
// produced Y, Cb and Cr at their own sizes, they were uploaded exactly as they came, and the
// conversion to RGB happens here — in the sampler's own filtering for the chroma, and in six
// multiply-adds for the colour.
//
// Doing it on the CPU instead would mean touching four times as many bytes and uploading four times
// as many, to do on a core what this does for nothing.
//
// ⚠ This is what `VideoRenderer` expects, field for field. Nothing checks that on any engine — a
// mismatch is a picture in the wrong place or the wrong colour rather than an error — so the block
// below and `VideoConstants` are commented with the same names, and the mode below is
// `VideoSampleMode`.

layout(set = 0, binding = 0) uniform texture2D luma_plane;
layout(set = 0, binding = 1) uniform texture2D blue_plane;
layout(set = 0, binding = 2) uniform texture2D red_plane;
layout(set = 0, binding = 3) uniform sampler plane_sampler;

layout(push_constant) uniform Constants {
    vec4 placement;
    vec4 crop;

    // x: luma offset, y: luma scale, z: V's contribution to red, w: U's to blue.
    vec4 luma;

    // x: U's contribution to green, y: V's — both negative. z: the tint alpha. w: the sample mode.
    vec4 chroma;
} constants;

layout(location = 0) in vec2 varying_texcoord;

layout(location = 0) out vec4 target;

void main() {
    vec2 uv = varying_texcoord;
    float alpha = constants.chroma.z;
    int mode = int(constants.chroma.w + 0.5);

    // ⚠ Packed colour has already been through the conversion and must not go through it again;
    // greyscale never had chroma and supplies its own neutral. Counting planes cannot tell those two
    // apart, which is why the mode is passed rather than inferred.
    if (mode == 2) {
        vec3 packed_rgb = texture(sampler2D(luma_plane, plane_sampler), uv).rgb;

        target = vec4(packed_rgb * alpha, alpha);
        return;
    }

    // Sampled as bytes rather than as fractions, because that is what the coefficients are written
    // for: they carry the 255/219 and 255/224 that limited range needs, and expressing them for
    // 0..1 samples would mean two sets of constants that have to agree.
    float y = texture(sampler2D(luma_plane, plane_sampler), uv).r * 255.0;

    // 128 is the neutral both differences are stored around, so a greyscale picture is the planar
    // case with u and v pinned there — one branch rather than a second path through the arithmetic.
    float u = mode == 1 ? 0.0 : (texture(sampler2D(blue_plane, plane_sampler), uv).r * 255.0) - 128.0;
    float v = mode == 1 ? 0.0 : (texture(sampler2D(red_plane, plane_sampler), uv).r * 255.0) - 128.0;

    // The chroma planes are a quarter of the size and are magnified by the sampler, which is why it
    // is linear: point sampling shows as blocking on every colour edge.
    float base = (y - constants.luma.x) * constants.luma.y;

    vec3 rgb = vec3(
        base + (v * constants.luma.z),
        base + (u * constants.chroma.x) + (v * constants.chroma.y),
        base + (u * constants.luma.w)
    );

    // Premultiplied, because the pipeline blends premultiplied — which is what lets a video fade out
    // over a menu rather than darkening towards black as its alpha falls.
    vec3 colour = clamp(rgb / 255.0, 0.0, 1.0) * alpha;

    target = vec4(colour, alpha);
}
