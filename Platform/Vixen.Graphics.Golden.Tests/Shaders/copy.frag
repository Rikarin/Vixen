#version 450

// Samples one texture and writes it. The bloom chain publishes its result as a declared transient, so
// something has to read it for the pyramid to survive culling at all — and a bloom-only view is what
// makes the glow checkable rather than swamped by the source that produced it.

layout(set = 2, binding = 1) uniform texture2D source;
layout(set = 2, binding = 3) uniform sampler sourceSampler;

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 target;

void main() { target = vec4(texture(sampler2D(source, sourceSampler), uv).rgb, 1.0); }
