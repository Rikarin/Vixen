#version 450

// A composited group's surface, put through the colour matrix its `filter` asks for on the way into
// the frame.
//
// This is `ui-image.frag` with nine multiplies and three adds in front of it, and it exists as a
// second module rather than a branch in that one because of what the alternative would have cost:
// `ui-image.frag` draws every viewport, thumbnail and video frame in the interface, so putting a
// push-constant block on it would make every one of those pay for a range to be written and would
// put an unfiltered group's identity matrix on the wire once per frame per group. A group with a
// filter is rare; a pipeline switch for the one draw that has one is not worth avoiding.
//
// ⚠ What it deliberately does *not* have is a pass of its own. A colour matrix is per pixel — no
// neighbourhood, so nothing to read out of a second surface — which means it can ride the composite
// draw the group was going to make anyway. `ui-blur.frag` gets a scratch target and two passes
// because a convolution genuinely cannot read and write one attachment; this would be spending that
// price for nothing. See `UiRenderer.Compose`.

layout(set = 0, binding = 0) uniform texture2D source;
layout(set = 0, binding = 1) uniform sampler source_sampler;

// ⚠ At offset 16, past the vertex stage's projection, in the same fragment range `ui-blur.frag`
// declares sixteen bytes of. The pipeline layout promises forty-eight there and a shader is free to
// read fewer — the reverse is the error — which is what lets one layout serve every UI pipeline and
// keeps a pipeline change from disturbing the descriptor set. See `UiRenderer`'s constructor.
layout(push_constant) uniform Filter {
    // Three rows of a 4x5 colour matrix, each `xyz` the coefficients and `w` the offset. The alpha
    // row is `0 0 0 1 0` for all seven functions this represents and the alpha column is zero for
    // all seven, so neither is sent. See `UiColorMatrix`.
    layout(offset = 16) vec4 red;
    vec4 green;
    vec4 blue;
} push;

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;

layout(location = 0) out vec4 target;

void main() {
    // ⚠ Premultiplied, always, with no `varying_shape.x` branch — and that is the difference between
    // this and `ui-image.frag`, which needs one. This pipeline is bound for a composite quad and
    // nothing else: `UiGeometryBuilder.Layer` is the only thing that emits a surface-backed image
    // draw, and `UiRenderer.SubmitDraw` reaches this pipeline only for a draw whose layer carries a
    // filter. A straight-alpha texture can never arrive here, so the flag would be a branch on a
    // constant.
    vec4 sampled = texture(sampler2D(source, source_sampler), varying_texcoord);

    // ⚠ <b>The matrix is applied to premultiplied colour, with the offset scaled by alpha.</b> A
    // colour matrix is defined on un-premultiplied colour — `c' = M·(c/a) + o` — and multiplying the
    // result back by `a` gives `M·c + o·a`, which needs no division and no guard for `a == 0`.
    // Dividing the alpha out to transform "the colour" and multiplying it back is the same mistake
    // `ui-blur.frag` refuses for the same reason: the colour under a zero alpha is not a colour, and
    // an `invert(1)` would turn every transparent texel of a viewport-sized surface opaque white.
    vec3 filtered = vec3(
        dot(push.red.rgb, sampled.rgb) + (push.red.w * sampled.a),
        dot(push.green.rgb, sampled.rgb) + (push.green.w * sampled.a),
        dot(push.blue.rgb, sampled.rgb) + (push.blue.w * sampled.a)
    );

    // ⚠ Clamped to `[0, a]` and not to `[0, 1]`. Premultiplied colour is valid only up to its own
    // alpha, and clamping there is exactly clamping the un-premultiplied colour to `[0, 1]`, which is
    // what CSS specifies. It is also what makes `brightness(2)` the same picture as the software
    // renderer's: the attachment would clamp to one on the way out anyway and a float buffer would
    // not, so an unclamped shader and an unclamped CPU port would part company on the brightest
    // pixels alone. `UiColorMatrix.Apply` does this, once, in the same place.
    filtered = clamp(filtered, vec3(0.0), vec3(sampled.a));

    // The rest is `ui-image.frag`'s premultiplied path verbatim: the group's opacity is the composite
    // quad's vertex alpha and is applied here, once. See that file for why the out alpha is the same
    // in both encodings.
    float alpha = sampled.a * varying_colour.a;
    target = vec4(filtered * varying_colour.rgb * varying_colour.a, alpha);
}
