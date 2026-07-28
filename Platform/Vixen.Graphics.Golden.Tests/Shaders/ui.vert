#version 450

// The one vertex shader all three UI pipelines share, because they read one vertex layout. Two
// layouts would mean two buffers and two uploads to save sixteen bytes on a vertex count in the
// thousands; an interface is not a mesh.

layout(location = 0) in vec2 position;   // document pixels
layout(location = 1) in vec2 texcoord;   // atlas UV for text, offset from the centre for a box
layout(location = 2) in vec4 colour;     // linear
layout(location = 3) in vec4 shape;      // half size, radius, thickness — or the pixel range

// A push constant rather than a uniform block: it is four floats that change once a frame, and a
// descriptor set for that would be a set to allocate, bind and invalidate everything above.
layout(push_constant) uniform Push {
    vec2 scale;
    vec2 offset;
} push;

layout(location = 0) out vec2 varying_texcoord;
layout(location = 1) out vec4 varying_colour;
layout(location = 2) out vec4 varying_shape;

void main() {
    gl_Position = vec4((position * push.scale) + push.offset, 0.0, 1.0);
    varying_texcoord = texcoord;
    varying_colour = colour;
    varying_shape = shape;
}
