#version 450

// A world-space triangle. Position, normal and colour per vertex, and the view-projection in a push
// constant — there is no model matrix for the same reason line.vert has none: what arrives here has
// already been placed by whatever produced it, which is what makes a whole viewport's geometry one
// buffer and one draw.

layout(location = 0) in vec3 position;
layout(location = 1) in vec3 normal;
layout(location = 2) in vec4 colour;

// Eighty bytes, well under the hundred and twenty-eight every implementation guarantees. `light` is
// the direction the light travels in xyz and the ambient term in w; the vertex stage does not read
// it, but the block is one range shared with the fragment stage and has to be declared whole.
layout(push_constant) uniform Push {
    mat4 view_projection;
    vec4 light;
} push;

layout(location = 0) out vec3 varying_normal;
layout(location = 1) out vec4 varying_colour;

void main() {
    gl_Position = push.view_projection * vec4(position, 1.0);

    // Passed through unnormalised: it is unit length per vertex and the interpolation between two
    // unit vectors is not, so the fragment stage normalises once rather than this doing it twice.
    varying_normal = normal;
    varying_colour = colour;
}
