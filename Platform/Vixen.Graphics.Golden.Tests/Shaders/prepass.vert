#version 450

// A depth prepass, in the shape Library/Pipeline/DepthOnly.rvn has: position through a transform and
// nothing else. No colour output and no fragment stage at all — a pass with no colour attachments
// needs none, and the whole point of a prepass is that it does not run the material's.
//
// The transform arrives as a push constant at offset 0, which is where TransformRenderFeature puts
// it. `world * position` rather than the transpose: the engine stores row-major with the translation
// in M41..M43, the shader reads the same bytes as column-major, and the two conventions cancel.
// docs/plan/07 § E says so; this fixture is what proves it, because only a device can.

layout(location = 0) in vec3 position;
layout(location = 1) in vec4 colour;

layout(push_constant) uniform Push { mat4 world; } push;

void main() {
    gl_Position = push.world * vec4(position, 1.0);
}
