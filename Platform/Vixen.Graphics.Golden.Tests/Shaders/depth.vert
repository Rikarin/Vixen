#version 450

layout(location = 0) in vec3 position;
layout(location = 1) in vec4 colour;
layout(location = 0) out vec4 varying_colour;

void main() {
    gl_Position = vec4(position, 1.0);
    varying_colour = colour;
}
