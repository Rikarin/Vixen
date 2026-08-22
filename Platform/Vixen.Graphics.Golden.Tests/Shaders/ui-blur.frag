#version 450

// One axis of the separable Gaussian a composited group's `filter: blur()` is made of.
//
// Run twice over a finished layer surface — once across, once down — with a scratch target between,
// because a convolution cannot read and write one attachment. What it consumes and produces is
// premultiplied colour, which is what every other UI pipeline writes, so there is no un-premultiply
// here and there must not be one: a weighted sum of premultiplied samples *is* the premultiplied
// weighted sum. Dividing the alpha out to blur "the colour" and multiplying it back gives a halo of
// the wrong hue wherever the group's edge meets transparent black, because the colour under a zero
// alpha is not a colour.
//
// ⚠ The quad is the group's own composite quad, drawn with this pipeline instead of the image one —
// so the vertex stage, the bindings and the projection are all the ones already there, and the only
// thing this pass needs that the geometry does not carry is the kernel.

layout(set = 0, binding = 0) uniform texture2D source;
layout(set = 0, binding = 1) uniform sampler source_sampler;

// ⚠ At offset 16, past the vertex stage's projection, and declared as its own range for that stage
// alone. One block spanning both stages would mean every pipeline sharing this layout had to declare
// the whole of it — see `UiRenderer`'s pipeline layout, which is one layout precisely so that a
// pipeline change cannot disturb a descriptor set.
layout(push_constant) uniform Kernel {
    // x, y: one texel along the axis being swept, in UVs. Zero on the other axis.
    // z: the standard deviation, in texels of this surface.
    // w: how many taps each side of the centre — `UiLayer.KernelRadius`, and the same number the
    //    software rasterizer takes from the same method.
    layout(offset = 16) vec4 kernel;
} push;

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;

layout(location = 0) out vec4 target;

void main() {
    vec2 step = push.kernel.xy;
    float sigma = push.kernel.z;
    int reach = int(push.kernel.w);

    float denominator = 2.0 * sigma * sigma;

    // ⚠ Summed over the *whole* kernel, including the taps that will fall outside the surface, and
    // that asymmetry is the point. Normalising by the taps that landed inside is what a clamp-to-edge
    // sampler amounts to — it lets the edge row stand in for everything past it — and near a group
    // that runs to the viewport edge that lifts the result towards the edge's own colour instead of
    // letting it fade. Dividing by the full sum makes a tap outside contribute the transparent black
    // that is actually there. It still absorbs the truncation at `UiLayer.MaximumKernel`, because
    // that tail is inside the surface and simply never summed.
    float total = 1.0;

    for (int i = 1; i <= reach; i++) {
        total += 2.0 * exp(-float(i * i) / denominator);
    }

    vec4 sum = vec4(0.0);

    for (int i = -reach; i <= reach; i++) {
        vec2 at = varying_texcoord + (step * float(i));

        // ⚠ Tested rather than left to the sampler. The sampler is the atlas's — linear and
        // clamped, shared by every pipeline through one descriptor set layout — so an out-of-range
        // read would return the edge texel rather than nothing, and this shader does not get to
        // choose a border colour without a sampler of its own in a set of its own.
        if (at.x < 0.0 || at.x > 1.0 || at.y < 0.0 || at.y > 1.0) {
            continue;
        }

        float weight = exp(-float(i * i) / denominator) / total;
        sum += texture(sampler2D(source, source_sampler), at) * weight;
    }

    // ⚠ No tint. The composite quad's vertex colour carries the group's opacity, which is applied
    // once, later, by `ui-image.frag` when the blurred surface is composited into the frame.
    // Applying it here as well would fade the group by the square of its alpha.
    target = sum;
}
