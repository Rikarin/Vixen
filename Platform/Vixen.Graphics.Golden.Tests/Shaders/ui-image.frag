#version 450

// A texture drawn into the interface: an image, a video frame, a viewport's render target.
//
// The same two bindings the text shader has, and deliberately so — one descriptor set layout serves
// both, so an image costs a set to allocate and nothing to design. What differs is what the sample
// *means*: text reads three distance channels and reconstructs a coverage from them, and this reads
// a colour and believes it.
//
// ⚠ `varying_shape.y` and `varying_shape.z` are what make a viewer's channel and colour-space
// toggles reach the picture — https://github.com/Rikarin/Vixen/issues/611. They ride the existing
// `shape` stream rather than a new vertex attribute, because three of its four components were
// unread on this path: the vertex layout, `ui.vert` and every other pipeline are untouched, and two
// images asking for different channels still batch.

layout(set = 0, binding = 0) uniform texture2D source;
layout(set = 0, binding = 1) uniform sampler source_sampler;

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;   // the tint, linear and straight-alpha
layout(location = 2) in vec4 varying_shape;

layout(location = 0) out vec4 target;

// The exact sRGB EOTF for one channel — a decode, `Raven/Library/Core/ColorSpaces.rvn:26`'s.
float srgb_to_linear_channel(float c) {
    if (c <= 0.04045) {
        return c / 12.92;
    }

    return pow((c + 0.055) / 1.055, 2.4);
}

vec3 srgb_to_linear(vec3 colour) {
    return vec3(
        srgb_to_linear_channel(colour.x),
        srgb_to_linear_channel(colour.y),
        srgb_to_linear_channel(colour.z)
    );
}

void main() {
    vec4 source_colour = texture(sampler2D(source, source_sampler), varying_texcoord);

    // ⚠ An isolated channel is shown **opaque**, and that is the difference between a viewer and a
    // tint. `UiImageChannel.Alpha` asks "what is in the alpha", so the one number that must not also
    // decide how much of it you can see is the alpha itself — a grey drawn at its own value would
    // show the chequerboard through exactly the texels it is reporting on, and the reader could not
    // tell 0.2 coverage from a 0.2 grey. The other three isolates are opaque for the same reason one
    // step weaker: a red channel read through the image's own alpha is two numbers multiplied and
    // neither is legible.
    vec3 rgb = source_colour.rgb;
    float coverage = source_colour.a;
    int channel = int(round(varying_shape.y));

    if (channel > 0) {
        float value = source_colour.x;

        if (channel == 2) {
            value = source_colour.y;
        }

        if (channel == 3) {
            value = source_colour.z;
        }

        if (channel == 4) {
            value = source_colour.w;
        }

        rgb = vec3(value, value, value);
        coverage = 1.0;
    }

    // ⚠ A decode, where the toggle is named for an encode, and the swapchain is why. The interface
    // presents to a `Bgra8UNormSrgb` target, so the hardware applies the sRGB *encode* on every
    // write and this stage's output is linear. A stored 0.5 therefore reaches the screen as 188/255,
    // which is the correct answer for a colour and the wrong one for a roughness map: "shown as
    // stored" means the number an author typed is the number on the glass. Decoding here cancels the
    // hardware's encode exactly, so 0.5 shows as 128/255. Zero is the identity and is what every
    // image quad already carries.
    if (varying_shape.z > 0.5) {
        rgb = srgb_to_linear(rgb);
    }

    // ⚠ The target is premultiplied — that is what the other three pipelines write and what the
    // blend state expects — and the *source* is premultiplied on exactly one of the two paths
    // through here. A texture a host uploaded holds straight alpha, so its colour has to be
    // multiplied by its own coverage on the way out; a composited group's surface was written by
    // these same pipelines and already has been. Doing it twice darkens every partly covered texel,
    // which reads as a dark fringe around everything inside the group.
    float alpha = coverage * varying_colour.a;

    // ⚠ `varying_shape.x` is what says which, and zero is the straight-alpha case every image quad
    // already carries — see `UiGeometryBuilder.Layer`, which is the only thing that emits a one. The
    // out alpha is the same either way: premultiplied is `source_colour.a * varying_colour.a` too,
    // because `source_colour.a` *is* the coverage in both encodings. Only the colour's factor
    // differs, so this is one interpolation rather than two branches.
    float scale = mix(alpha, varying_colour.a, varying_shape.x);

    target = vec4(rgb * varying_colour.rgb * scale, alpha);
}
