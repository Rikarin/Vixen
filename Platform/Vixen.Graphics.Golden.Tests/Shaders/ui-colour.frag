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
// declares sixteen bytes of. The pipeline layout promises a hundred and twenty-eight and a shader is
// free to read fewer — the reverse is the error — which is what lets one layout serve every UI
// pipeline and keeps a pipeline change from disturbing the descriptor set. See `UiRenderer`'s
// constructor.
layout(push_constant) uniform Filter {
    // Three rows of a 4x5 colour matrix, each `xyz` the coefficients and `w` the offset. The alpha
    // row is `0 0 0 1 0` for all seven functions this represents and the alpha column is zero for
    // all seven, so neither is sent. See `UiColorMatrix`.
    layout(offset = 16) vec4 red;
    vec4 green;
    vec4 blue;

    // The border box a `backdrop-filter` is clipped to: `xy` its centre and `zw` half its size, in
    // <b>target texels</b>. `UiLayer.BackdropBounds`, which is the element's border box and not the
    // group's ink, pre-multiplied by the frame's scale.
    //
    // ⚠ Texels and not document pixels, and the remark here said document pixels until #1200. A
    // layer surface is `ceil(surface × scale)`, so the point below carries the display's scale in
    // it; the box has to be in the same space or a rounded backdrop is drawn at half size in the
    // top-left quadrant of its element on a 2× display. `UiRenderer.Corner` is where the multiply
    // happens — the host, rather than this stage, because the coverage band below is one unit of its
    // own argument wide and only a box in texels keeps it one texel.
    vec4 box;

    // `x` the corner radius, in target texels, uniform across the four corners or zero —
    // `UiLayer.BackdropRadius` times the frame's scale.
    //
    // ⚠ Zero is the whole of the "this draw is not a rounded backdrop" test, and it has to be a
    // number rather than an absent push. This pipeline also serves every filtered group, and one of
    // those following a rounded backdrop would otherwise clip itself to the previous draw's box.
    // `UiRenderer.SubmitDraw` pushes this block whole on every draw that reaches here.
    vec4 corner;
} push;

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;

layout(location = 0) out vec4 target;

// The signed distance to a box with an elliptical corner, negative inside. `ui-box.frag`'s
// `box_distance`, copied line for line — see that file for why the corner quadrant is the only place
// the ellipse is an ellipse. ⚠ Copied rather than approximated for the radius this shader actually
// gets: a backdrop's radius is uniform, and a "simplification" for the uniform case parts company
// with the element's own background wherever the radius exceeds one half-extent and not the other,
// which is a one-texel light ring between a panel and the glass behind it.
float box_distance(vec2 point, vec2 half_size, vec2 radius) {
    vec2 r = min(max(radius, vec2(0.0)), half_size);
    vec2 q = abs(point) - half_size + r;

    if (r.x <= 0.0 || r.y <= 0.0) {
        vec2 square = abs(point) - half_size;
        return length(max(square, 0.0)) + min(max(square.x, square.y), 0.0);
    }

    if (q.x <= 0.0 && q.y <= 0.0) {
        return max(q.x - r.x, q.y - r.y);
    }

    if (q.x <= 0.0) {
        return q.y - r.y;
    }

    if (q.y <= 0.0) {
        return q.x - r.x;
    }

    return (length(q / r) - 1.0) * min(r.x, r.y);
}

// How much of this pixel the border box covers — one where there is no radius, so the multiply at
// the end of `main` is the identity on every other draw this pipeline serves.
//
// ⚠ A one-pixel band, hard-coded, where `ui-box.frag` takes its width from a screen-space
// derivative. A composite quad covers bounds `UiGeometryBuilder` has already rounded out to whole
// pixels over a surface that is the viewport's size, so the mapping is one to one and `fwidth` of
// this distance is one by construction. `SoftwareUiRasterizer.Composite` says it in the same words,
// which is what makes the two executors' corners agree to the texel rather than to a threshold.
float backdrop_coverage(vec2 point) {
    if (push.corner.x <= 0.0) {
        return 1.0;
    }

    return clamp(0.5 - box_distance(point - push.box.xy, push.box.zw, vec2(push.corner.x)), 0.0, 1.0);
}

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

    // ⚠ The point comes from the texture coordinate times the surface size, which is a <i>target
    // texel</i> — `ui-mask.frag` argues it at length and this is the same expression for the same
    // reason. It is what `gl_FragCoord` would give and the two shaders' remarks claimed the opposite
    // until #1200; the box above is pre-multiplied to meet it.
    float coverage = backdrop_coverage(varying_texcoord * vec2(textureSize(sampler2D(source, source_sampler), 0)));

    // The rest is `ui-image.frag`'s premultiplied path verbatim: the group's opacity is the composite
    // quad's vertex alpha and is applied here, once. See that file for why the out alpha is the same
    // in both encodings. ⚠ The coverage multiplies all four channels, because the sample is
    // premultiplied — leaving `rgb` alone would brighten the corner texels towards full strength as
    // the curve closes, which reads as a light ring around the panel.
    float alpha = sampled.a * varying_colour.a * coverage;
    target = vec4(filtered * varying_colour.rgb * varying_colour.a * coverage, alpha);
}
