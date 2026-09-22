#version 450

// The one vertex shader all three UI pipelines share, because they read one vertex layout. Two
// layouts would mean two buffers and two uploads to save sixteen bytes on a vertex count in the
// thousands; an interface is not a mesh.

layout(location = 0) in vec2 position;   // document pixels, already projected
layout(location = 1) in vec2 texcoord;   // atlas UV for text, offset from the centre for a box
layout(location = 2) in vec4 colour;     // linear
layout(location = 3) in vec4 shape;      // a shape index, a pixel range, or a coverage
layout(location = 4) in float w;         // what the position was divided by; one on all but a projected quad

// A push constant rather than a uniform block: it is four floats that change once a frame, and a
// descriptor set for that would be a set to allocate, bind and invalidate everything above.
layout(push_constant) uniform Push {
    vec2 scale;
    vec2 offset;
} push;

layout(location = 0) out vec2 varying_texcoord;
layout(location = 1) out vec4 varying_colour;
layout(location = 2) out vec4 varying_shape;

// ⚠ Flat, and separate from `varying_shape` for that reason. A box's shape index is the same at all
// four of its corners, and a path's coverage in the same vector is the opposite case and has to be
// interpolated — so the two cannot share a qualifier and do not share a slot.
//
// ⚠ The `flat` itself is **insurance rather than a covered claim**: interpolating a value that is
// equal at all three corners is exact, so removing the qualifier changes no picture any fixture here
// draws, and a sabotage that removed it failed to fail. What it insures against is an index large
// enough that a float stops holding it exactly — past sixteen million boxes — where the last bit of
// an array index is a box drawn with another box's radii.
layout(location = 3) flat out int varying_index;

void main() {
    // ⚠ Multiplied back up by the vertex's own `w` so that the rasteriser's divide reproduces the
    // projected position and, in the same stroke, interpolates the varyings by `1/w` — which is what
    // makes a composited group's texture coordinate correct across a quad under a perspective
    // (#548). One everywhere else, where the multiply and the divide are both the identity.
    gl_Position = vec4(((position * push.scale) + push.offset) * w, 0.0, w);
    varying_texcoord = texcoord;
    varying_colour = colour;
    varying_shape = shape;
    varying_index = int(shape.x + 0.5);
}
