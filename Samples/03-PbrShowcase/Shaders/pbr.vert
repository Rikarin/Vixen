#version 450

// The scene's one vertex shader. Position and normal in, world position and world normal out.
//
// The model matrix arrives as a push constant rather than through a per-draw descriptor set, which
// is worth saying out loud because it is not what a real renderer does: `Vixen.Rendering`'s
// TransformRenderFeature packs transforms into one dynamic uniform buffer and binds it with an
// offset per draw, which scales to thousands of objects. Twenty-five spheres do not need that, and
// the push-constant path keeps the sample's binding story to one page.

layout(set = 0, binding = 0) uniform View {
    mat4 viewProjection;
    vec4 eye;          // xyz, w unused
    vec4 lightDirection; // xyz normalised, pointing *towards* the light; w unused
    vec4 lightColour;  // rgb radiance, a unused
    vec4 ambient;      // rgb, a unused
} view;

layout(push_constant) uniform Push {
    mat4 model;
    vec4 material; // albedo is derived from the grid; x metallic, y roughness, zw unused
} push;

layout(location = 0) in vec3 position;
layout(location = 1) in vec3 normal;

layout(location = 0) out vec3 worldPosition;
layout(location = 1) out vec3 worldNormal;

void main() {
    // ⚠ The matrix goes on the *left*, and that reads backwards for an engine whose convention is
    // row-vector `mul(v, M)`. Both statements are true at once and ADR-003 explains why: the host
    // stores matrices row-major and GLSL reads a `mat4` column-major, so the matrix the shader sees
    // is the transpose of the one that was written — and `M_glsl * v` is therefore exactly
    // `v * M_host`. Same bytes, read two ways, composing to the right answer at no cost.
    //
    // Getting it the other way round produces a scene that is empty rather than wrong, because
    // every vertex lands outside the clip volume. Which is what the first version of this sample
    // did, and what a screenshot would have shown in a second.
    vec4 world = push.model * vec4(position, 1.0);

    worldPosition = world.xyz;

    // No inverse transpose: every transform in this sample is a translation and a uniform scale, so
    // the upper 3×3 is orthogonal up to that scale and normalising is enough. A non-uniform scale
    // would need the real thing, and would be wrong here in a way that only shows up on a squashed
    // object.
    worldNormal = normalize(vec3(push.model * vec4(normal, 0.0)));

    gl_Position = view.viewProjection * world;
}
