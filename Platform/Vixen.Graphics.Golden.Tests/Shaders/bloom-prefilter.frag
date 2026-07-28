#version 450
#include "bloom.h.glsl"

// Mode 0. Keeps what is above the threshold, with a quadratic knee below it — a hard cut makes bloom
// pop in and out as a highlight crosses it, which is obvious in motion and invisible in a still.
//
// No Karis weighting, matching what Bloom.rvn actually does rather than what it reads as: its Tap()
// applies the weight only when `Mode == 0`, and mode 0 is this one, which samples directly and never
// calls Tap(). So the weighting is unreachable there and absent here. See the note in
// Core/Vixen.Rendering/README.md — it is a defect in the shader, not a divergence in this fixture.

void main() {
    vec3 colour = Tap(0.0, 0.0);
    float luma = Luminance(colour);

    float soft = clamp(luma - c.threshold + c.knee, 0.0, 2.0 * c.knee);
    float contribution = max(soft * soft / max(4.0 * c.knee, 1e-6), luma - c.threshold);
    target = vec4(colour * (contribution / max(luma, 1e-6)), 1.0);
}
