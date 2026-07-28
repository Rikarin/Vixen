#version 450

// The one post-processing pass: exposure, an ACES-shaped filmic curve, and nothing else.
//
// The scene renders into Rgba16Float and this is what turns radiance into a displayable image. It
// is here rather than in Vixen.Rendering.PostFx's Tonemap node for the same reason the BRDF is
// written out above: this sample drives the RHI and the render graph directly, so that what it
// demonstrates is the *rendering*, not the compositor's node graph.
//
// Writing to an sRGB swapchain, so the encoding is the hardware's and this curve is purely tonal.
// Doing both — a pow(1/2.2) here as well — is the classic double-encode and produces an image that
// looks washed out in a way that is very hard to attribute.

layout(set = 0, binding = 0) uniform texture2D scene;
layout(set = 0, binding = 1) uniform sampler sceneSampler;

layout(push_constant) uniform Push { vec4 exposure; } push;

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 target;

vec3 aces(vec3 x) {
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * ((a * x) + b)) / ((x * ((c * x) + d)) + e), 0.0, 1.0);
}

void main() {
    vec3 radiance = texture(sampler2D(scene, sceneSampler), uv).rgb * push.exposure.x;
    target = vec4(aces(radiance), 1.0);
}
