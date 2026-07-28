#version 450

// A shadow caster: world from a push constant, view-projection from the per-view block.
//
// This is the shape a caster has always wanted and could not have. The world matrix is the object's
// and comes from TransformRenderFeature; the view-projection is the *cascade's* and comes from set 1,
// bound once per view by SingleStageRenderer. Before that existed, the only way to get a cascade's
// matrix into a shader was to compose it into the object's world transform on the CPU — which works
// for one object in one view and is not a design.

layout(set = 1, binding = 0) uniform View {
    mat4 viewProjection;
    vec3 position;
} view;

layout(location = 0) in vec3 position;
layout(location = 1) in vec4 colour;

layout(push_constant) uniform Push { mat4 world; } push;

void main() {
    gl_Position = view.viewProjection * (push.world * vec4(position, 1.0));
}
