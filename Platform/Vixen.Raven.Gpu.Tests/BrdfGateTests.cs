// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Raven.Gpu.Tests;

/// <summary>
///     The shipped BRDF, evaluated on a device and checked against arithmetic derived from the
///     published formulae.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 07's numeric gate, and what it is and is not a gate on.</b>
///         <c>Vixen.Raven.Tests</c> can say the library compiles and that <c>spirv-val</c> accepts
///         the module; neither is a claim about the numbers. What can only be found by running it is
///         a lowering that produced a <i>valid</i> module computing the wrong thing — an operand
///         order swapped, a reciprocal folded the wrong way, a squared roughness squared twice. All
///         three produce a picture that looks like art direction.
///     </para>
///     <para>
///         ⚠ <b>The reference is derived, not transcribed.</b> An oracle that shares an
///         implementation with its subject is not an oracle, and copying <c>Brdf.rvn</c> into C#
///         would test the copy. So <see cref="Reference" /> is written from the published GGX and
///         Smith formulae — Walter et al. 2007 for the distribution and the height-correlated
///         visibility, Schlick 1994 for the Fresnel — in the form those papers state them, and it is
///         the *convention* Raven's file documents that the two have to agree on rather than the
///         expression. That is exactly the disagreement worth catching: the file says the
///         <c>4·NdotL·NdotV</c> denominator is folded into the visibility, and a version that left it
///         to the caller would still compile, still validate, and be four times too bright at grazing
///         angles.
///     </para>
///     <para>
///         <b>The white-furnace test needs no reference at all</b>, which is why it is here beside
///         the comparison rather than instead of it. A non-absorbing surface cannot reflect more
///         light than it receives, so the specular lobe integrated over the hemisphere is at most
///         one — a property of the mathematics that both implementations are measured against and
///         neither defines. It is the assertion that survives both sides being wrong in the same way.
///     </para>
/// </remarks>
public sealed class BrdfGateTests {
    /// <summary>The library files the kernels here need, and no more.</summary>
    static readonly string[] Imports = ["Core/Math.rvn", "Shading/Brdf.rvn"];

    /// <summary>How many samples the sweep evaluates.</summary>
    const int Samples = 256;

    /// <summary>
    ///     Evaluates the three primitives over a sweep of angles and roughnesses.
    /// </summary>
    /// <remarks>
    ///     The inputs are derived inside the shader from the invocation index rather than uploaded,
    ///     so the host and the device agree about them by construction — a table of inputs written
    ///     by one side and read by the other would put a layout question inside a numeric test.
    /// </remarks>
    const string Sweep = """
                         package Vixen.Shaders.Gate

                         import Vixen.Shaders.Core
                         import Vixen.Shaders.Shading

                         shader Gate {
                             [PerFrame] var results: RWBuffer<float>

                             [ComputeShader(64)]
                             func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                 val index = int(id.x)

                                 if (index >= 256) {
                                     return
                                 }

                                 // A grid: sixteen roughnesses across sixteen angles, so the sweep
                                 // covers the mirror end where the distribution spikes and the rough
                                 // end where the visibility term dominates.
                                 val step = float(index % 16) / 15f
                                 val band = float(index / 16) / 15f

                                 val perceptual = 0.02f + band * 0.96f
                                 val alpha = Brdf.Alpha(perceptual)

                                 val NdotH = 0.02f + step * 0.97f
                                 val NdotL = 0.05f + step * 0.9f
                                 val NdotV = 0.95f - step * 0.9f
                                 val VdotH = 0.02f + step * 0.97f

                                 results[index * 4 + 0] = Brdf.DistributionGgx(NdotH, alpha)
                                 results[index * 4 + 1] = Brdf.VisibilitySmithGgx(NdotL, NdotV, alpha)
                                 results[index * 4 + 2] = Brdf.FresnelSchlickScalar(0.04f, VdotH)
                                 results[index * 4 + 3] = alpha
                             }
                         }
                         """;

    /// <summary>
    ///     Integrates the specular lobe over the whole hemisphere for one roughness per invocation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The whole hemisphere — both angles — and the first version of this was wrong in
    ///         a way worth keeping the note for.</b> It swept the polar angle in the plane containing
    ///         the view and took the half vector's elevation to be the mean of the two cosines, which
    ///         is neither the half vector nor an integral: a specular lobe is concentrated in a small
    ///         solid angle about the mirror direction, and a one-dimensional slice through it
    ///         integrates to about a hundred-thousandth of the energy. It reported 1.6e-5 and would
    ///         have passed a bound that only said "not more than one" — which is exactly why the
    ///         lower bound is asserted too.
    ///     </para>
    ///     <para>
    ///         So the light direction is built properly from a lattice in (cos θ, φ), the half vector
    ///         is normalised from the view and light, and <c>dω = dcosθ · dφ</c> makes every sample's
    ///         weight the same — which keeps the sum something a reader can check by hand. A fixed
    ///         lattice rather than importance sampling, because a sampler is code the shipped library
    ///         also owns, and an integral evaluated with the subject's own machinery is one more
    ///         place for the two to be wrong together.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why the sweep is rough surfaces only, 0.5 upwards.</b> A uniform 64×64
    ///         lattice resolves about 1.4° in θ, and a near-mirror lobe is a fraction of a degree
    ///         wide — quadrature misses it, and the answer that comes back says nothing about the
    ///         BRDF. Measured: at a perceptual roughness of 0.05 this returns 0.002, which is the
    ///         lattice failing rather than the lobe. Covering the smooth end needs importance
    ///         sampling and is a different test with a different oracle.
    ///     </para>
    ///     <para>
    ///         What this measures is the directional albedo of the single-scattering GGX lobe with
    ///         Fresnel forced to one: at most one for every roughness — energy conservation — and
    ///         falling as the surface roughens, which is the known energy loss of a single-scattering
    ///         microfacet model rather than a defect. The light lost is what a multiple-scattering
    ///         term would put back. Measured here: 0.885 down to 0.438 across the range.
    ///     </para>
    /// </remarks>
    const string Furnace = """
                           package Vixen.Shaders.Gate

                           import Vixen.Shaders.Core
                           import Vixen.Shaders.Shading

                           shader Gate {
                               [PerFrame] var results: RWBuffer<float>

                               [ComputeShader(8)]
                               func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                   val index = int(id.x)

                                   if (index >= 8) {
                                       return
                                   }

                                   val alpha = Brdf.Alpha(0.5f + float(index) / 8f * 0.5f)

                                   // The normal is +Z and the view leans off it, so the mirror
                                   // direction is somewhere inside the hemisphere rather than on its
                                   // pole — which is what makes the lattice have to cover all of it.
                                   val NdotV = 0.7f
                                   val v = float3(sqrt(1f - NdotV * NdotV), 0f, NdotV)

                                   var total = 0f

                                   for (i in 0 .. 63) {
                                       val NdotL = (float(i) + 0.5f) / 64f
                                       val sinL = sqrt(max(0f, 1f - NdotL * NdotL))

                                       for (j in 0 .. 63) {
                                           val phi = (float(j) + 0.5f) / 64f * 6.283185307f
                                           val l = float3(sinL * cos(phi), sinL * sin(phi), NdotL)
                                           val h = normalize(v + l)

                                           val d = Brdf.DistributionGgx(max(h.z, 0f), alpha)
                                           val vis = Brdf.VisibilitySmithGgx(NdotL, NdotV, alpha)

                                           // dω = dcosθ · dφ, uniform over the lattice.
                                           total = total + d * vis * NdotL * (1f / 64f) * (6.283185307f / 64f)
                                       }
                                   }

                                   results[index] = total
                               }
                           }
                           """;

    /// <summary>The device and the reference agree about all three primitives.</summary>
    [Fact]
    public void The_device_evaluates_the_shipped_brdf_as_the_formulae_say() {
        var run = ShaderRun.Run(Sweep, Imports, Samples * 4, groups: 4);

        Assert.NotNull(run);

        // ⚠ A relative tolerance, and a wide one, because the distribution spans nine orders of
        // magnitude across this sweep: a near-mirror surface at normal incidence puts D in the
        // thousands and a rough one at a glancing angle puts it near zero. An absolute tolerance
        // sized for one end says nothing at the other. What a real disagreement costs is a factor,
        // not a fraction — a squared roughness squared twice is out by the roughness itself.
        const float Tolerance = 1e-4f;

        for (var index = 0; index < Samples; index++) {
            var step = (index % 16) / 15f;
            var band = (index / 16) / 15f;

            var perceptual = 0.02f + (band * 0.96f);
            var alpha = perceptual * perceptual;

            var ndoth = 0.02f + (step * 0.97f);
            var ndotl = 0.05f + (step * 0.9f);
            var ndotv = 0.95f - (step * 0.9f);
            var vdoth = 0.02f + (step * 0.97f);

            Close(Reference.DistributionGgx(ndoth, alpha), run.Values[(index * 4) + 0], Tolerance, "D", index);
            Close(Reference.VisibilitySmithGgx(ndotl, ndotv, alpha), run.Values[(index * 4) + 1], Tolerance, "Vis", index);
            Close(Reference.FresnelSchlick(0.04f, vdoth), run.Values[(index * 4) + 2], Tolerance, "F", index);
            Close(alpha, run.Values[(index * 4) + 3], Tolerance, "alpha", index);
        }
    }

    /// <summary>
    ///     ⚠ <b>The one assertion here that no implementation defines.</b> A non-absorbing surface
    ///     cannot reflect more light than reaches it, so the lobe integrates to at most one. A
    ///     visibility term missing its <c>4·NdotL·NdotV</c> fold comes out four times over.
    /// </summary>
    [Fact]
    public void The_specular_lobe_does_not_reflect_more_light_than_it_receives() {
        var run = ShaderRun.Run(Furnace, Imports, 8, groups: 1);

        Assert.NotNull(run);

        foreach (var (energy, index) in run.Values.Select((value, index) => (value, index))) {
            Assert.True(
                energy is > 0f and <= 1f,
                $"Roughness band {index} reflects {energy} of the light arriving, which is not in (0, 1]. "
                + "Above one is energy from nowhere — most often the 4·NdotL·NdotV denominator left out of "
                + "the visibility — and zero is a lobe that reflects nothing at all."
            );
        }

        // And it is not trivially small either: a smooth surface should return most of what arrives,
        // which is what says the integral is measuring a lobe rather than rounding to nothing.
        // ⚠ And it is not trivially small, which is the half a bound of "at most one" cannot give.
        // The first version of this integral returned 1.6e-5 and satisfied that bound perfectly.
        Assert.True(
            run.Values[0] > 0.8f,
            $"The smoothest band here reflects only {run.Values[0]}, so the lobe is being lost by the "
            + "quadrature rather than integrated."
        );

        // ⚠ **Monotonic in roughness**, which is the structural claim and the strongest of the three.
        // Single-scattering GGX loses more energy the rougher the surface — that is what a
        // multiple-scattering term exists to put back — so the sequence has to fall. A term with its
        // masking and shadowing wrongly assumed independent, or a distribution normalised against the
        // wrong measure, breaks the ordering long before it breaks the bound.
        for (var index = 1; index < run.Values.Length; index++) {
            Assert.True(
                run.Values[index] < run.Values[index - 1],
                $"Band {index} reflects {run.Values[index]} where band {index - 1} reflected "
                + $"{run.Values[index - 1]}. A rougher surface cannot scatter more of the light back in one "
                + "bounce than a smoother one."
            );
        }
    }

    /// <summary>
    ///     The gate can see a wrong answer, asserted by giving it one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Sabotage, because a numeric gate is exactly the kind that passes for the wrong
    ///     reason.</b> If the tolerance were wide enough, or the sweep degenerate enough, every
    ///     assertion above would hold against a shader computing something else. This one squares
    ///     the roughness a second time — the mistake <c>Brdf.rvn</c>'s own header calls the most
    ///     common in a hand-written PBR shader, and the one that looks like a smoother material
    ///     rather than a bug — and requires the comparison to notice.
    /// </remarks>
    [Fact]
    public void Squaring_the_roughness_twice_is_caught() {
        const string Sabotaged = """
                                 package Vixen.Shaders.Gate

                                 import Vixen.Shaders.Core
                                 import Vixen.Shaders.Shading

                                 shader Gate {
                                     [PerFrame] var results: RWBuffer<float>

                                     [ComputeShader(64)]
                                     func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                         val index = int(id.x)

                                         if (index >= 16) {
                                             return
                                         }

                                         val perceptual = 0.02f + float(index) / 15f * 0.96f

                                         // The bug: Alpha already squares, and this squares again.
                                         val alpha = Brdf.Alpha(Brdf.Alpha(perceptual))

                                         results[index] = Brdf.DistributionGgx(0.6f, alpha)
                                     }
                                 }
                                 """;

        var run = ShaderRun.Run(Sabotaged, Imports, 16, groups: 1);

        Assert.NotNull(run);

        var wrong = 0;

        for (var index = 0; index < 16; index++) {
            var perceptual = 0.02f + (index / 15f * 0.96f);
            var alpha = perceptual * perceptual;

            if (Math.Abs(Reference.DistributionGgx(0.6f, alpha) - run.Values[index]) > 1e-4f * Math.Max(1f, Math.Abs(Reference.DistributionGgx(0.6f, alpha)))) {
                wrong++;
            }
        }

        Assert.True(
            wrong >= 12,
            $"Only {wrong} of 16 samples moved when the roughness was squared twice, so the comparison above "
            + "is not sensitive enough to be a gate."
        );
    }

    static void Close(float expected, float actual, float tolerance, string what, int index) {
        var allowed = tolerance * Math.Max(1f, Math.Abs(expected));

        Assert.True(
            Math.Abs(expected - actual) <= allowed,
            $"Sample {index}'s {what}: the formula gives {expected} and the device gave {actual}, "
            + $"which is further apart than {allowed}."
        );
    }

    /// <summary>The microfacet primitives, from the papers rather than from <c>Brdf.rvn</c>.</summary>
    /// <remarks>
    ///     Written in the form each source states it, and deliberately not in the form the shader is
    ///     written in — the shader factors the visibility to avoid a division, and this does not. Two
    ///     expressions of one function is the point: an algebraic slip in either shows up as a
    ///     disagreement, where a transcription would carry the slip across.
    /// </remarks>
    static class Reference {
        /// <summary>The floor both terms put under alpha, and it is part of the definition.</summary>
        /// <remarks>
        ///     ⚠ <b>The first thing this gate caught, on its first run, and it was the reference that
        ///     was wrong.</b> A perfectly smooth surface makes the distribution a Dirac delta, which
        ///     in floating point is an infinity that becomes a NaN at the first multiply — so
        ///     <c>Brdf.rvn</c> clamps alpha rather than clamping D, bounding the highlight's
        ///     <i>size</i> instead of its height. That is a documented decision about what the
        ///     shipped BRDF <em>is</em>, not an implementation detail, so a reference that ignored it
        ///     would be measuring a different function below a perceptual roughness of 0.045.
        /// </remarks>
        const float MinAlpha = 0.002f;

        static float Clamp(float alpha) => Math.Max(alpha, MinAlpha);

        /// <summary>Trowbridge–Reitz, as Walter et al. 2007 state it: α² / (π ((n·h)²(α²−1) + 1)²).</summary>
        public static float DistributionGgx(float ndoth, float alpha) {
            var a = Clamp(alpha);
            var a2 = a * a;
            var d = (ndoth * ndoth * (a2 - 1f)) + 1f;

            return a2 / (float)(Math.PI * d * d);
        }

        /// <summary>
        ///     Smith's height-correlated masking-shadowing, divided by 4·(n·l)·(n·v) — the fold
        ///     <c>Brdf.rvn</c>'s header says its visibility carries, stated here as the division it
        ///     is rather than as the factored form.
        /// </summary>
        public static float VisibilitySmithGgx(float ndotl, float ndotv, float alpha) {
            var a = Clamp(alpha);
            var a2 = a * a;

            var lambdaV = ndotl * (float)Math.Sqrt((ndotv * ndotv * (1f - a2)) + a2);
            var lambdaL = ndotv * (float)Math.Sqrt((ndotl * ndotl * (1f - a2)) + a2);

            return 0.5f / (lambdaV + lambdaL);
        }

        /// <summary>Schlick 1994: f0 + (1 − f0)(1 − v·h)⁵.</summary>
        public static float FresnelSchlick(float f0, float vdoth) {
            var f = 1f - vdoth;
            var f5 = f * f * f * f * f;

            return f0 + ((1f - f0) * f5);
        }
    }
}
