#version 450
#include "bloom.h.glsl"

// Mode 2. A 9-tap tent over the level below, added onto the level beside it. The tent is what makes
// the upsampled level smooth rather than blocky, and `previous` is the only binding that exists in
// this variant and not the other two.

layout(set = 2, binding = 2) uniform texture2D previous;

void main() {
    vec2 d = c.texelSize * c.filterRadius;

    vec3 total = Tap(-d.x, d.y) * 1.0 + Tap(0.0, d.y) * 2.0 + Tap(d.x, d.y) * 1.0;
    total += Tap(-d.x, 0.0) * 2.0 + Tap(0.0, 0.0) * 4.0 + Tap(d.x, 0.0) * 2.0;
    total += Tap(-d.x, -d.y) * 1.0 + Tap(0.0, -d.y) * 2.0 + Tap(d.x, -d.y) * 1.0;
    total = total / 16.0;

    vec3 below = texture(sampler2D(previous, sourceSampler), uv).rgb;
    target = vec4(below + total * c.intensity, 1.0);
}
