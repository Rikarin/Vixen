#version 450
#define BLOOM_KARIS
#include "bloom.h.glsl"

// Mode 1. The same 13-tap downsample as bloom-down.frag, over the prefiltered level, with every tap
// Karis-weighted before it is summed.
//
// The one pass in the chain that averages the least-filtered data, and therefore the one place a
// highlight occupying a single texel can be flattened rather than carried the rest of the way down
// the pyramid — which is what stops it flickering as it moves. The prefilter cannot do it: it takes a
// single tap, and a weight applied to one sample is a darkening rather than an average.

void main() {
    target = vec4(Downsample(), 1.0);
}
