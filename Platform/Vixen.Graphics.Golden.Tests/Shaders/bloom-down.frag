#version 450
#include "bloom.h.glsl"

// Mode 1. Jimenez's 13-tap downsample: a centre box plus four corner boxes, overlapping. The overlap
// is the point — a 4x4 filter built from 2x2 bilinear taps, so it costs 13 samples instead of 16 and
// its kernel has no zeros to alias through.

void main() {
    vec2 d = c.texelSize;

    vec3 a = Tap(-d.x * 2.0,  d.y * 2.0);
    vec3 b = Tap( 0.0,        d.y * 2.0);
    vec3 e = Tap( d.x * 2.0,  d.y * 2.0);
    vec3 f = Tap(-d.x * 2.0,  0.0);
    vec3 g = Tap( 0.0,        0.0);
    vec3 h = Tap( d.x * 2.0,  0.0);
    vec3 i = Tap(-d.x * 2.0, -d.y * 2.0);
    vec3 j = Tap( 0.0,       -d.y * 2.0);
    vec3 k = Tap( d.x * 2.0, -d.y * 2.0);

    vec3 l = Tap(-d.x,  d.y);
    vec3 m = Tap( d.x,  d.y);
    vec3 n = Tap(-d.x, -d.y);
    vec3 o = Tap( d.x, -d.y);

    vec3 total = (l + m + n + o) * 0.125;
    total += g * 0.125;
    total += (a + e + i + k) * 0.03125;
    total += (b + f + h + j) * 0.0625;
    target = vec4(total, 1.0);
}
