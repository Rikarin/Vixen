#version 450

// The material pass's vertex stage: the same geometry the prepass drew, through the same transform.
//
// It has to agree with prepass.vert exactly, or the depth it computes will not equal the depth the
// prepass wrote and an EQUAL test rejects everything. That agreement is the one real constraint a
// prepass puts on a renderer, and a picture is the only thing that checks it.

layout(location = 0) in vec3 position;
layout(location = 1) in vec4 colour;

layout(push_constant) uniform Push { mat4 world; } push;

layout(location = 0) out vec4 varying_colour;

void main() {
    gl_Position = push.world * vec4(position, 1.0);
    varying_colour = colour;
}
