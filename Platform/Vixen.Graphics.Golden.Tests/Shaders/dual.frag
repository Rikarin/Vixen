#version 450

// Two colour attachments, written differently.
//
// A pass that declares two targets and a backend that names only the first draw buffer writes one
// and discards the other, with no error from anything — so the fixture reads the *second* one back.

layout(location = 0) in vec4 varying_colour;
layout(location = 0) out vec4 first;
layout(location = 1) out vec4 second;

void main() {
    first = varying_colour;
    second = vec4(1.0 - varying_colour.rgb, 1.0);
}
