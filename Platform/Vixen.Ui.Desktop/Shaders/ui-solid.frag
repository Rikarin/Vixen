#version 450

// Tessellated paths: the one UI primitive that is real geometry rather than a distance field.
//
// A triangle carries no signed distance to its own boundary, so its edge cannot be resolved here the
// way a box's or a glyph's is. What it carries instead is a coverage the tessellator interpolated —
// one across the interior, running to zero over a half-pixel strip along the outline. That is the
// whole of the antialiasing, and it is why this shader reads `shape.x` for the same reason the text
// one does.

layout(location = 0) in vec2 varying_texcoord;
layout(location = 1) in vec4 varying_colour;
layout(location = 2) in vec4 varying_shape;

layout(location = 0) out vec4 target;

void main() {
    float alpha = varying_colour.a * clamp(varying_shape.x, 0.0, 1.0);
    target = vec4(varying_colour.rgb * alpha, alpha);
}
