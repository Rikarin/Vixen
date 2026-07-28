#version 450

// A ground plane, seen from above, asking a shadow atlas whether each point is lit.
//
// The plane is not geometry: the fragment derives a world position on y = 0 from its own UV, so the
// fixture needs no camera and the picture is a plan view of the shadow. What is under test is
// everything between the cascade and the sample — the tile the caster was rendered into, the scale
// and offset that address it, the reversed-depth comparison, and the clip-to-texture flip.

layout(set = 2, binding = 0) uniform Constants {
    mat4 shadowMatrix;

    // xy scale, zw offset: where this cascade's tile sits in the atlas. With one cascade these are
    // (1,1) and (0,0) and prove nothing, which is why the fixture uses two.
    vec4 tile;

    // The world rectangle the screen maps onto: xy is the minimum corner, zw the size.
    vec4 ground;
} c;

layout(set = 2, binding = 1) uniform texture2D shadowMap;
layout(set = 2, binding = 3) uniform sampler shadowSampler;

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 target;

void main() {
    vec3 world = vec3(c.ground.x + uv.x * c.ground.z, 0.0, c.ground.y + uv.y * c.ground.w);

    vec4 clip = c.shadowMatrix * vec4(world, 1.0);
    vec3 ndc = clip.xyz / clip.w;

    // Clip space is +Y up and a texture's rows run down, which is the flip the negative-height
    // viewport applied when the caster was drawn. Leaving it out mirrors the shadow about the
    // cascade's centre — visible here, invisible in any unit test of the matrix.
    vec2 shadowUv = ndc.xy * vec2(0.5, -0.5) + 0.5;

    // Outside this cascade is *unshadowed*, not "sample anyway". A tile is a window onto a shared
    // atlas, so a UV past its edge lands in the neighbouring cascade — whose projection is a
    // different size and centre, so the caster it stored appears somewhere unrelated. The first run
    // of this fixture drew the shadow as a cross: the real square, plus cascade 0's square arriving
    // through the tile next door. A real renderer falls through to the next cascade here; this one
    // has nothing further to ask.
    if (any(lessThan(shadowUv, vec2(0.0))) || any(greaterThan(shadowUv, vec2(1.0)))
        || ndc.z < 0.0 || ndc.z > 1.0) {
        target = vec4(1.0);
        return;
    }

    vec2 atlas = shadowUv * c.tile.xy + c.tile.zw;
    float stored = texture(sampler2D(shadowMap, shadowSampler), atlas).r;

    // Reversed depth: nearer the light is *larger*. A receiver is in shadow when something stored a
    // larger depth than its own. Getting this backwards lights exactly the shadow and shadows
    // everything else, which is a picture nobody could mistake for correct.
    float lit = ndc.z >= stored - 0.002 ? 1.0 : 0.25;
    target = vec4(vec3(lit), 1.0);
}
