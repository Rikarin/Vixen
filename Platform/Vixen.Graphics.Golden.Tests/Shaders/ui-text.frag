#version 450

// Multi-channel signed distance field text.
//
// A single-channel field rounds off corners, because one distance cannot describe two edges meeting
// at a point — which is what a serif is made of. Three fields whose median reconstructs the true
// shape keep the corner. The median is the whole trick, and it is why the three channels must never
// be treated as a colour.

layout(set = 0, binding = 0) uniform texture2D atlas;
layout(set = 0, binding = 1) uniform sampler atlas_sampler;

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;   // x: screen pixels per unit of the field's range

layout(location = 0) out vec4 target;

// Branchless via min and max: three comparisons and no divergence, where a sort costs more for the
// same answer.
float median_of(float a, float b, float c) {
    return max(min(a, b), min(max(a, b), c));
}

void main() {
    vec3 field = texture(sampler2D(atlas, atlas_sampler), varying_texcoord).rgb;

    // The atlas is sampled linearly and never as sRGB: the values are distances, not light. Decoding
    // them as colour is the classic mistake and shows as text that is too thin.
    float distance = median_of(field.r, field.g, field.b) - 0.5;

    // One atlas serves every size because the range arrives already scaled by the size being drawn.
    float coverage = clamp((distance * max(varying_shape.x, 1e-4)) + 0.5, 0.0, 1.0);

    // Premultiplied, which is what the UI blend state expects.
    //
    // ⚠ **The alpha is folded first and named, and the grouping is the whole of the point** — #1225.
    // This line was `varying_colour.rgb * varying_colour.a * coverage`, which associates as
    // `(rgb·a)·coverage`, where `UiText` premultiplies `Ui.Premultiply(float4(rgb, a·coverage))` and
    // so computes `rgb·(a·coverage)`. Float multiplication is not associative, so those are two
    // numbers and not one written twice — a last-place bit of a colour channel before an
    // `Rgba8UNorm` store, which is exactly the size of the disagreement `UiRavenAgreementTests`
    // refuses at `ImageTolerance.Exact`. `ui-box.frag`'s own last two lines are already this shape.
    float alpha = varying_colour.a * coverage;

    target = vec4(varying_colour.rgb * alpha, alpha);
}
