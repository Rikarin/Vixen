#version 450
#include "bloom.h.glsl"

// Mode 0. Keeps what is above the threshold, with a quadratic knee below it — a hard cut makes bloom
// pop in and out as a highlight crosses it, which is obvious in motion and invisible in a still.
//
// No Karis weighting, and deliberately so on both sides: this pass takes one tap, and a weight
// applied to a single sample is a darkening rather than an average. Bloom.rvn puts the weight on the
// first downsample, which is the first pass that averages anything — see bloom-down-first.frag.

void main() {
    vec3 colour = Tap(0.0, 0.0);
    float luma = Luminance(colour);

    float soft = clamp(luma - c.threshold + c.knee, 0.0, 2.0 * c.knee);
    float contribution = max(soft * soft / max(4.0 * c.knee, 1e-6), luma - c.threshold);
    target = vec4(colour * (contribution / max(luma, 1e-6)), 1.0);
}
