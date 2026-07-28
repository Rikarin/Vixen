#version 450
#include "bloom.h.glsl"

// Mode 2. The plain 13-tap downsample, run for every level below the first. Unweighted taps, because
// what it reads has already been averaged by the level above it — a Karis weight here would darken
// the level and buy no stability that bloom-down-first.frag has not already bought.

void main() {
    target = vec4(Downsample(), 1.0);
}
