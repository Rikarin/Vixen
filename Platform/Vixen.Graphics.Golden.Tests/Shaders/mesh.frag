#version 450

layout(location = 0) in vec4 varying_colour;
layout(location = 0) out vec4 target;

void main() { target = varying_colour; }
