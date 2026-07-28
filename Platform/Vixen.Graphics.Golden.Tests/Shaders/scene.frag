#version 450

// Flat vertex colour. The picture's arithmetic is entirely in the blend and the depth test, so the
// fragment stage deliberately does nothing that could disguise either.

layout(location = 0) in vec4 varying_colour;
layout(location = 0) out vec4 target;

void main() { target = varying_colour; }
