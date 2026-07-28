#version 450

// A post pass shaped like Library/PostFx/Tonemap.rvn's first two steps: sample the scene and apply
// an exposure the host put in a uniform block.
//
// Three things are under test and each fails visibly. The *texture and sampler* prove the descriptor
// set the allocator wrote points at what the node declared — bind them to the wrong indices and the
// image is black or garbage. The *offsets* prove EffectConstants filled the block from the effect's
// parameter table: whitePoint sits after exposure, so writing either at the other's offset turns a
// dimmed picture into a black or a blown-out one. And the whole thing only draws at all if the
// vertex stage's three vertices cover the screen.

layout(set = 0, binding = 0) uniform texture2D source;
layout(set = 0, binding = 1) uniform sampler sourceSampler;

layout(set = 0, binding = 2) uniform Constants {
    float exposure;
    float whitePoint;
} constants;

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 target;

void main() {
    vec3 colour = texture(sampler2D(source, sourceSampler), uv).rgb * constants.exposure;

    // Reinhard against the white point, so a wrong whitePoint is a wrong picture rather than an
    // unused byte. Zero would make this divide by one and change nothing, which is exactly the
    // "unset parameter arrives as zero" failure the default-carrying work was about.
    target = vec4(colour / (1.0 + colour / max(constants.whitePoint, 0.001)), 1.0);
}
