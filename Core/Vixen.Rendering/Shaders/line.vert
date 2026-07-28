#version 450

// A world-space line segment. Two vertices, a colour each, and the view-projection in a push
// constant — there is no model matrix because a line is already where it is: a grid line, a debug
// ray, a gizmo arm are all authored in world space by whatever produced them.

layout(location = 0) in vec3 position;
layout(location = 1) in vec4 colour;

// Sixty-four bytes, which is the guaranteed minimum push-constant size — a matrix and nothing else,
// so this fits everywhere without asking the device what it allows.
layout(push_constant) uniform Push {
    mat4 view_projection;
} push;

layout(location = 0) out vec4 varying_colour;

void main() {
    gl_Position = push.view_projection * vec4(position, 1.0);
    varying_colour = colour;
}
