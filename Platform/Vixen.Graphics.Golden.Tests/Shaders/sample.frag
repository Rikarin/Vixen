#version 450

// Samples one texture across the whole target, with the sampler bound beside it.
//
// The fixtures built on this are about the *sampler*: nearest against linear at a magnification of
// 32, and repeat against clamp outside 0..1. Both are invisible in a command-stream assertion and
// unmistakable in a picture.

layout(set = 2, binding = 0) uniform texture2D source;
layout(set = 2, binding = 1) uniform sampler sourceSampler;

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 target;

void main() { target = texture(sampler2D(source, sourceSampler), uv); }
