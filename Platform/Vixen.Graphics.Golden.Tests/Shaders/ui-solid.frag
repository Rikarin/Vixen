#version 450

// Tessellated paths: the one UI primitive that is real geometry rather than a distance field, so the
// only one whose edge is whatever the rasteriser gives it. There is nothing here to antialias with —
// a triangle carries no signed distance to its own boundary.

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;

layout(location = 0) out vec4 target;

void main() {
    target = vec4(varying_colour.rgb * varying_colour.a, varying_colour.a);
}
