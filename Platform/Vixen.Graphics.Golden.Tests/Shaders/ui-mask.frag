#version 450

// A composited group's surface, put through its `filter` colour matrix and then through the coverage
// its `mask-image` asks for, on the way into the frame.
//
// This is `ui-colour.frag` with a ramp after it, and it exists as a third module rather than a branch
// in that one for the reason that one exists rather than a branch in `ui-image.frag`: a group with a
// mask is rare, and a pipeline switch for the one draw that has one is cheaper than a push-constant
// range every viewport, thumbnail and video frame in the interface would have to write.
//
// ⚠ It carries the colour matrix as well as the mask, and that is not duplication — it is the only
// arrangement that works. A pipeline is chosen once per draw, so a group that has both a `filter` and
// a `mask-image` has to be served by one module that does both; two modules would silently drop
// whichever one lost the coin toss. `UiRenderer.SubmitDraw` pushes the identity matrix here when the
// group has no filter, which costs forty-eight bytes on a draw that was already paying for a switch.
//
// ⚠ What it deliberately does *not* have is a pass of its own. A mask is per pixel — no
// neighbourhood, so nothing to read out of a second surface — so it rides the composite draw the
// group was going to make anyway, exactly as the colour matrix does. See `UiRenderer.Compose`.
//
// ⚠ <b>And unlike the colour matrix, where this is applied is not free to differ between the two
// executors.</b> A colour matrix is the same affine map at every pixel, so it commutes with the
// Gaussian and with the bilinear sampler, and `SoftwareUiRasterizer` is allowed to fold it into the
// finished surface instead. A mask is a scalar that varies with position and commutes with neither:
// `m(p)·Σ wᵢsᵢ` is not `Σ wᵢ·m(pᵢ)·sᵢ` wherever the ramp is not flat across the kernel, which is
// precisely over a blurred edge. So the seam is fixed on both paths — the composite draw, after the
// blur and after the matrix — and `SoftwareUiRasterizer.Composite` says so in the same words.

layout(set = 0, binding = 0) uniform texture2D source;
layout(set = 0, binding = 1) uniform sampler source_sampler;

// ⚠ At offset 16, past the vertex stage's projection, in the same fragment range `ui-blur.frag`
// declares sixteen bytes of and `ui-colour.frag` forty-eight. This is the widest of the three at a
// hundred and twelve, which is what set the pipeline layout's fragment range — and 16 + 112 is 128,
// which is exactly the push-constant size Vulkan guarantees on every device. See `UiRenderer`'s
// constructor, whose comment records that the number is a floor and not a preference.
layout(push_constant) uniform Mask {
    // Three rows of a 4x5 colour matrix, as `ui-colour.frag` documents them. The identity when the
    // group has no `filter`.
    layout(offset = 16) vec4 red;
    vec4 green;
    vec4 blue;

    // The mask box in document pixels: `xy` its centre, `zw` half its size. ⚠ The element's border
    // box and not the layer's bounds, which a blur has already outset — see `UiMask`.
    vec4 box;

    // `xy` the gradient's direction, `z` the shape, `w` non-zero when the middle stop is read.
    //
    // ⚠ The shape is `GradientShape`'s own numbering — 1 linear, 2 radial, 3 conic — and *not* a
    // zero-based one of this shader's. The enum's zero is `None`, which never reaches here. Writing
    // the obvious 0/1/2 here instead cost an afternoon: a linear mask arrives as 1, took the radial
    // branch, and drew a plausible round fade that only `UiCompositingTests` could tell was wrong.
    vec4 ramp;

    // `xyz` the three stops' coverages, `w` where the first stop sits.
    vec4 alphas;

    // `xy` where the middle and last stops sit. `zw` unused.
    vec4 stops;
} push;

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;

layout(location = 0) out vec4 target;

// Where `t` sits between two stops, flat outside them. A zero-width span is a hard edge rather than
// a division by zero: `from-50% to-50%` is a legal declaration and a step is what it means.
float mask_span(float t, float from, float to) {
    float width = to - from;

    return width > 1e-4 ? clamp((t - from) / width, 0.0, 1.0) : (t < from ? 0.0 : 1.0);
}

// How far along its gradient line a point is, from zero at the start to one at the end.
//
// ⚠ The parameterisation is `ui-box.frag`'s, to the constant, and `UiMask.Coverage` is the third
// copy. A `mask-image` and a `background-image` written with the same gradient have to produce ramps
// that line up, and the only way to be sure of that is to compute the same number the same way.
float mask_progress(vec2 offset, vec2 half_size, vec2 axis, int kind) {
    if (kind == 2) {
        // `ellipse farthest-corner at center`: the point over the half size puts the farthest *side*
        // at one and the corner at root two, so the reciprocal of root two is the whole of it.
        vec2 normalised = offset / max(half_size, vec2(1e-4));

        return length(normalised) * 0.70710678;
    }

    if (kind == 3) {
        // CSS starts at twelve o'clock and sweeps clockwise; screen space is y-down, so up is -y and
        // `atan(x, -y)` is already CSS's angle. The axis's own angle is the `from <angle>`.
        float angle = atan(offset.x, -offset.y) - atan(axis.x, -axis.y);
        float turns = (angle / 6.28318531) + 1.0;

        return turns - floor(turns);
    }

    vec2 direction = dot(axis, axis) > 1e-12 ? normalize(axis) : vec2(1.0, 0.0);
    float reach = abs(direction.x * half_size.x) + abs(direction.y * half_size.y);

    return ((dot(offset, direction) / max(reach, 1e-4)) * 0.5) + 0.5;
}

void main() {
    // ⚠ Premultiplied, always, with no `varying_shape.x` branch — `ui-colour.frag`'s remark applies
    // here word for word. This pipeline is bound for a composite quad and nothing else, so a
    // straight-alpha texture can never arrive and the flag would be a branch on a constant.
    vec4 sampled = texture(sampler2D(source, source_sampler), varying_texcoord);

    // The colour matrix, on premultiplied colour with the offset scaled by alpha, clamped to `[0, a]`.
    // See `ui-colour.frag`, which argues every line of this.
    vec3 filtered = vec3(
        dot(push.red.rgb, sampled.rgb) + (push.red.w * sampled.a),
        dot(push.green.rgb, sampled.rgb) + (push.green.w * sampled.a),
        dot(push.blue.rgb, sampled.rgb) + (push.blue.w * sampled.a)
    );

    filtered = clamp(filtered, vec3(0.0), vec3(sampled.a));

    // ⚠ <b>The point comes from the texture coordinate times the surface size, which *is* the
    // document pixel.</b> Every layer surface is the size of the viewport — see `UiLayer` — so this
    // product needs no origin subtracted, which is exactly why `SoftwareUiRasterizer` can compute the
    // identical number from the identical varying. Using `gl_FragCoord` instead would be right at a
    // scale of one and wrong at every other, because the surface is in target texels and the mask box
    // is in document pixels.
    vec2 point = varying_texcoord * vec2(textureSize(sampler2D(source, source_sampler), 0));
    float progress = mask_progress(point - push.box.xy, push.box.zw, push.ramp.xy, int(push.ramp.z + 0.5));

    float coverage = push.ramp.w > 0.5
        ? (progress < push.stops.x
            ? mix(push.alphas.x, push.alphas.y, mask_span(progress, push.alphas.w, push.stops.x))
            : mix(push.alphas.y, push.alphas.z, mask_span(progress, push.stops.x, push.stops.y)))
        : mix(push.alphas.x, push.alphas.z, mask_span(progress, push.alphas.w, push.stops.y));

    coverage = clamp(coverage, 0.0, 1.0);

    // ⚠ <b>All four channels, because the sample is premultiplied.</b> Scaling coverage on
    // premultiplied colour is `(rgb·m, a·m)` — the whole vector. The `(rgb, a·m)` an ordinary
    // straight-alpha image would want is the premultiply mistake `ui-image.frag`'s `varying_shape.x`
    // exists to prevent, wearing a new hat: leaving `rgb` alone brightens every masked texel towards
    // full strength as the mask closes, which reads as a glow along the fading edge.
    float alpha = sampled.a * varying_colour.a * coverage;

    target = vec4(filtered * varying_colour.rgb * varying_colour.a * coverage, alpha);
}
