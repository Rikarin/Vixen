#version 450

// A texture drawn into the interface: an image, a video frame, a viewport's render target.
//
// The same two bindings the text shader has, and deliberately so — one descriptor set layout serves
// both, so an image costs a set to allocate and nothing to design. What differs is what the sample
// *means*: text reads three distance channels and reconstructs a coverage from them, and this reads
// a colour and believes it.

layout(set = 0, binding = 0) uniform texture2D source;
layout(set = 0, binding = 1) uniform sampler source_sampler;

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;   // the tint, linear and straight-alpha
layout(location = 2) in vec4 varying_shape;

layout(location = 0) out vec4 target;

void main() {
    vec4 source_colour = texture(sampler2D(source, source_sampler), varying_texcoord);

    // ⚠ The target is premultiplied — that is what the other three pipelines write and what the
    // blend state expects — and the *source* is premultiplied on exactly one of the two paths
    // through here. A texture a host uploaded holds straight alpha, so its colour has to be
    // multiplied by its own coverage on the way out; a composited group's surface was written by
    // these same pipelines and already has been. Doing it twice darkens every partly covered texel,
    // which reads as a dark fringe around everything inside the group.
    float alpha = source_colour.a * varying_colour.a;

    // ⚠ `varying_shape.x` is what says which, and zero is the straight-alpha case every image quad
    // already carries — see `UiGeometryBuilder.Layer`, which is the only thing that emits a one. The
    // out alpha is the same either way: premultiplied is `source_colour.a * varying_colour.a` too,
    // because `source_colour.a` *is* the coverage in both encodings. Only the colour's factor
    // differs, so this is one interpolation rather than two branches.
    float scale = mix(alpha, varying_colour.a, varying_shape.x);

    target = vec4(source_colour.rgb * varying_colour.rgb * scale, alpha);
}
