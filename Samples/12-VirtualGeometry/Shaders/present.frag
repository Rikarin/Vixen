#version 450

// The debug view of the visibility buffer: every pixel of it names a visible cluster and a triangle
// within it, packed the way ClusterRaster.rvn packs them — a slot biased by one so zero means
// "nothing covered this pixel", seven low bits of triangle.
//
// The cluster index becomes a colour through an integer hash, so two neighbouring clusters get
// unrelated colours and the cut is legible: watch the patches change size as the camera moves and
// you are watching the traversal choose a different level of detail per cluster, per frame. The
// triangle bits darken the colour slightly so the geometry reads inside each patch.
//
// This is deliberately a picture of phase 4's output, not phase 5's: the buffer holds identities,
// not colours, and shading it for real is the material resolve's job. A sample of the resolve is a
// sample of the whole clustered-lighting frame, which is a different sample.

layout(set = 0, binding = 0) uniform utexture2D identities;
layout(set = 0, binding = 1) uniform sampler pointSampler;

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 target;

// Wang's integer hash: cheap, and adjacent inputs land far apart, which is exactly what makes
// adjacent clusters distinguishable.
uint hash(uint x) {
    x = (x ^ 61u) ^ (x >> 16);
    x *= 9u;
    x = x ^ (x >> 4);
    x *= 0x27d4eb2du;
    x = x ^ (x >> 15);
    return x;
}

void main() {
    ivec2 size = textureSize(usampler2D(identities, pointSampler), 0);
    ivec2 texel = clamp(ivec2(uv * vec2(size)), ivec2(0), size - 1);
    uint id = texelFetch(usampler2D(identities, pointSampler), texel, 0).x;

    if (id == 0u) {
        // Nothing covered this pixel: a quiet vertical gradient, so the silhouette is legible.
        target = vec4(vec3(0.02, 0.025, 0.035) + vec3(0.0, 0.01, 0.02) * uv.y, 1.0);
        return;
    }

    uint slot = (id >> 7) - 1u;
    uint triangle = id & 0x7Fu;

    uint mixed = hash(slot);
    vec3 colour = vec3(
        float((mixed >> 0) & 0xFFu),
        float((mixed >> 8) & 0xFFu),
        float((mixed >> 16) & 0xFFu)
    ) / 255.0;

    // Lifted off black — a hash can land dark — and modulated per triangle so the tessellation
    // shows without a wireframe pass.
    colour = mix(vec3(0.25), vec3(1.0), colour);
    colour *= 0.8 + 0.2 * (float(hash(triangle + 97u) & 0xFFu) / 255.0);

    target = vec4(colour, 1.0);
}
