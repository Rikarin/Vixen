#version 450

layout(location = 0) in vec2 position;
layout(location = 1) in vec4 colour;
layout(push_constant) uniform Push { vec2 offset; } push;
layout(location = 0) out vec4 varying_colour;

void main() {
    gl_Position = vec4(position + push.offset, 0.0, 1.0);
    varying_colour = colour;
}
