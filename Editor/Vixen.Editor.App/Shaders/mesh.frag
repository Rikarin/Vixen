#version 450

layout(location = 0) in vec3 varying_normal;
layout(location = 1) in vec4 varying_colour;

layout(push_constant) uniform Push {
    mat4 view_projection;
    vec4 light;
} push;

layout(location = 0) out vec4 target;

void main() {
    vec3 surface = normalize(varying_normal);

    // The pipeline is two-sided, so the inside of an open shape — a plane seen from below, a cone
    // with the camera inside it — arrives with its normal pointing away from the viewer. Flipping it
    // lights that face rather than leaving it flat black, which is what an editor wants: a surface
    // you can see is a surface you can judge the shape of.
    if (!gl_FrontFacing) {
        surface = -surface;
    }

    float lambert = max(dot(surface, -normalize(push.light.xyz)), 0.0);
    float shade = push.light.w + ((1.0 - push.light.w) * lambert);

    // Premultiplied, which is what the blend state expects — the same convention line.frag writes in.
    vec3 lit = varying_colour.rgb * shade;
    target = vec4(lit * varying_colour.a, varying_colour.a);
}
