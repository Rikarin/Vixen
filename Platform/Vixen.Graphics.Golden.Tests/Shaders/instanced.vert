#version 450

// One quad, drawn many times, moved and tinted by a per-instance vertex buffer.
//
// The step mode is what this pins. A layout declared per-vertex rather than per-instance advances
// four times inside the first instance and then runs off the end of the buffer — which on most
// drivers draws the first instance four times in different colours and nothing else, and looks like
// an instance count that was ignored.

layout(location = 0) in vec2 position;
layout(location = 1) in vec2 offset;
layout(location = 2) in vec4 colour;
layout(location = 0) out vec4 varying_colour;

void main() {
    gl_Position = vec4(position + offset, 0.0, 1.0);
    varying_colour = colour;
}
