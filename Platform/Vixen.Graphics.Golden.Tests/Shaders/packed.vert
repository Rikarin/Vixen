#version 450

// The mesh layout with the colour packed into four bytes rather than four floats.
//
// A `UNorm8X4` attribute is the one vertex format whose mistake is invisible: read as `UInt8X4` it
// arrives as 0..255 instead of 0..1, which saturates every channel and produces a picture that is
// simply white. Nothing errors and the geometry is identical.

layout(location = 0) in vec2 position;
layout(location = 1) in vec4 colour;
layout(location = 0) out vec4 varying_colour;

void main() {
    gl_Position = vec4(position, 0.0, 1.0);
    varying_colour = colour;
}
