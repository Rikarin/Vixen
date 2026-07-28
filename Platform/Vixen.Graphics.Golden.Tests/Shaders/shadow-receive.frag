#version 450

// A ground plane, seen from above, asking a shadow atlas whether each point is lit — and asking the
// *right cascade* of it.
//
// The plane is not geometry: the fragment derives a world position on y = 0 from its own UV, so the
// fixture needs no camera to draw and the picture is a plan view of the shadow. What is under test is
// everything between the cascades and the sample — which cascade a point falls in, the tile that
// cascade was rendered into, the reversed-depth comparison, and the clip-to-texture flip.
//
// The block is laid out the way `ForwardPlus.rvn` lays its own out: an array of `{mat4, float}` under
// std140, so the matrices and splits `ShadowMapRenderer` publishes land here without the fixture
// composing anything. That is the point of this version — the previous one was handed cascade one's
// matrix and its tile by the test, which tested `AtlasProjection` and nothing downstream of it.

#define CASCADES 2

struct Cascade {
    // World straight into this cascade's own tile of the atlas: `ShadowCascades.AtlasProjection`
    // folds the tile's scale and offset into the matrix, so there is nothing to multiply here.
    mat4 viewProjection;

    // The view depth this cascade covers up to.
    float split;
};

layout(set = 2, binding = 0) uniform Constants {
    Cascade cascades[CASCADES];

    // World to view, for the depth the selection is made on.
    mat4 view;

    // The world rectangle the screen maps onto: xy is the minimum corner, zw the size.
    vec4 ground;
} c;

layout(set = 2, binding = 1) uniform texture2D shadowMap;
layout(set = 2, binding = 3) uniform sampler shadowSampler;

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 target;

// The nearest cascade that still covers this depth, which is what `ForwardPlus.CascadeOf` does and
// what `ShadowCascades.CascadeOf` mirrors on the host. Falling through to the last is what a point
// past the shadow distance gets.
int cascadeOf(float viewDepth) {
    int index = CASCADES - 1;

    for (int i = 0; i < CASCADES; ++i) {
        if (viewDepth <= c.cascades[i].split) {
            index = i;
            break;
        }
    }

    return index;
}

void main() {
    vec3 world = vec3(c.ground.x + uv.x * c.ground.z, 0.0, c.ground.y + uv.y * c.ground.w);

    // Negated, because view space is right-handed: the camera looks down −Z and every distance the
    // cascades are cut at is positive. The same line as `ClusterGrid.DepthOf`.
    float viewDepth = -(c.view * vec4(world, 1.0)).z;

    vec4 clip = c.cascades[cascadeOf(viewDepth)].viewProjection * vec4(world, 1.0);
    vec3 ndc = clip.xyz / clip.w;

    // Clip space is +Y up and a texture's rows run down, which is the flip the negative-height
    // viewport applied when the caster was drawn. Leaving it out mirrors the shadow about the
    // cascade's centre — visible here, invisible in any unit test of the matrix.
    vec2 atlas = ndc.xy * vec2(0.5, -0.5) + 0.5;

    // Off the atlas entirely, or outside the depth range: unlit rather than sampled. There is
    // deliberately no test that the sample landed in the *selected* cascade's tile, because that is
    // what selection is for — a fragment sent to the wrong cascade reads the neighbouring tile and
    // draws somebody else's caster, which is exactly the failure the picture should show rather than
    // one the shader should hide.
    if (any(lessThan(atlas, vec2(0.0))) || any(greaterThan(atlas, vec2(1.0)))
        || ndc.z < 0.0 || ndc.z > 1.0) {
        target = vec4(1.0);
        return;
    }

    float stored = texture(sampler2D(shadowMap, shadowSampler), atlas).r;

    // Reversed depth: nearer the light is *larger*. A receiver is in shadow when something stored a
    // larger depth than its own. Getting this backwards lights exactly the shadow and shadows
    // everything else, which is a picture nobody could mistake for correct.
    float lit = ndc.z >= stored - 0.002 ? 1.0 : 0.25;
    target = vec4(vec3(lit), 1.0);
}
