// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Rendering.Lighting;
using Xunit;

namespace Tests;

/// <summary>
///     Tetrahedral light-probe interpolation: which four probes a position sits between, and in
///     what proportions.
/// </summary>
/// <remarks>
///     <para>
///         The spherical-harmonic half of this was already built and tested — projection, linear
///         blending, evaluation, all of it in <c>EnvironmentLightingTests</c>. What is asserted
///         here is the half that took exact predicates to get right: that a position between
///         probes finds a cell at all, that the cell it finds is the one containing it, and that
///         the weights make the interpolation exact for anything linear.
///     </para>
///     <para>
///         Linear is the strongest property available and the one worth leaning on. Barycentric
///         coordinates reproduce affine functions exactly, so a set of probes whose coefficients
///         vary linearly with position must sample back to that same linear function at every
///         point inside the hull — whichever cell the walk landed in, and whichever way the
///         cospherical ties in a grid happened to be broken. A wrong cell, a mis-wound face or a
///         weight in the wrong slot all break it.
///     </para>
/// </remarks>
public class LightProbeTests {
    // --- Interpolation ------------------------------------------------------

    /// <summary>Sampling at a probe returns that probe.</summary>
    [Fact]
    public void A_probe_samples_as_itself() {
        var volume = LightProbeVolume.Build(Grid(3, static position => position.X + (2f * position.Y)));

        Assert.True(volume.IsTetrahedral);

        foreach (var position in volume.Positions.ToArray()) {
            var sampled = volume.Sample(position);

            Assert.Equal(position.X + (2f * position.Y), sampled.L00.X, 4);
        }
    }

    /// <summary>
    ///     A linear field sampled anywhere inside the hull comes back as the same linear field.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The test that says the cell and the weights are both right. It is run over a grid
    ///         because a grid is what people author and because a grid is cospherical eight probes
    ///         at a time — every cell in this mesh exists because a tie was broken, and the
    ///         property holds whichever way each one went.
    ///     </para>
    ///     <para>
    ///         A relative tolerance rather than the decimal places the tests around it use.
    ///         <c>Assert.Equal(expected, actual, precision)</c> rounds both values to that many
    ///         places and compares them, which is a cliff and not a tolerance: two results a single
    ///         ULP apart disagree whenever they land either side of a rounding boundary, and over
    ///         generated positions something eventually does. That is what made this test fail
    ///         about one run in ten while the arithmetic under it was never wrong — and asking for
    ///         <em>more</em> places would have made it worse, not better, because it puts the
    ///         boundaries closer together. The other tests here can use precision because they
    ///         sample at a probe or outside the hull, where the answer comes back exact and there
    ///         is no boundary to straddle.
    ///     </para>
    ///     <para>
    ///         1e-5 because a couple of ULPs is the whole of the error. The weights are solved in
    ///         <see langword="double" /> and only the four-term blend that applies them is float,
    ///         so over 200,000 generated positions the worst relative error measured was 2.3e-7,
    ///         which is about two ULPs at the magnitude this field reaches. Two orders above that
    ///         leaves room for a JIT that reassociates the sum differently, and is still far below
    ///         what the defects this test exists for would produce: a wrong cell, a mis-wound face
    ///         or a weight in the wrong slot moves the answer by a fraction of the field, not by an
    ///         ULP.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_linear_field_interpolates_exactly() {
        static float Field(Vector3 position) => 1f + (0.5f * position.X) - (0.25f * position.Y) + (2f * position.Z);

        var volume = LightProbeVolume.Build(Grid(4, Field));

        Gen.Select(Gen.Float[0f, 3f], Gen.Float[0f, 3f], Gen.Float[0f, 3f])
            .Sample(
                query => {
                    var position = new Vector3(query.Item1, query.Item2, query.Item3);
                    var expected = Field(position);
                    var sampled = volume.Sample(position);

                    Assert.True(
                        MathUtil.NearEqual(expected, sampled.L00.X, 1e-5f),
                        $"Expected {expected} at {position}, sampled {sampled.L00.X}."
                    );
                }
            );
    }

    /// <summary>A hint is a shortcut, not a different answer.</summary>
    [Fact]
    public void Threading_a_hint_does_not_change_the_result() {
        static float Field(Vector3 position) => position.X - position.Z;

        var volume = LightProbeVolume.Build(Grid(4, Field));
        var cell = 0;

        for (var step = 0; step < 40; step++) {
            var position = new Vector3(step * 0.07f, 1.5f, 3f - (step * 0.07f));

            var withHint = volume.Sample(position, ref cell);
            var without = volume.Sample(position);

            Assert.Equal(without.L00.X, withHint.L00.X, 5);
        }
    }

    // --- The edges of the bake ----------------------------------------------

    /// <summary>Outside the probes, the nearest one — flat rather than extrapolated.</summary>
    /// <remarks>
    ///     An order-2 fit pushed past the data it came from produces negative irradiance, and a
    ///     negative irradiance is a surface that takes light out of the frame. A seam at the edge
    ///     of the bake is visible; a black patch on a character is a bug report.
    /// </remarks>
    [Fact]
    public void Outside_the_hull_the_nearest_probe_is_used() {
        var volume = LightProbeVolume.Build(Grid(2, static position => position.X));

        var far = new Vector3(20f, 20f, 20f);

        Assert.Equal(volume.Positions[volume.NearestProbe(far)].X, volume.Sample(far).L00.X, 5);
        Assert.Equal(1f, volume.Sample(far).L00.X, 5);
    }

    /// <summary>A floor's worth of probes at one height has no volume, and says so.</summary>
    [Fact]
    public void Coplanar_probes_are_not_tetrahedral() {
        var probes = new List<LightProbe>();

        for (var x = 0; x < 4; x++) {
            for (var z = 0; z < 4; z++) {
                probes.Add(Probe(new(x, 1f, z), x));
            }
        }

        var volume = LightProbeVolume.Build(probes);

        Assert.False(volume.IsTetrahedral);

        // Still answers, with the nearest probe, rather than throwing or returning nothing.
        Assert.Equal(3f, volume.Sample(new(2.9f, 1f, 1f)).L00.X, 5);
    }

    /// <summary>Fewer than four probes is not a volume, and does not pretend to be.</summary>
    [Fact]
    public void A_single_probe_is_used_everywhere() {
        var volume = LightProbeVolume.Build([Probe(new(0f, 0f, 0f), 7f)]);

        Assert.False(volume.IsTetrahedral);
        Assert.Equal(7f, volume.Sample(new(100f, -50f, 3f)).L00.X, 5);
    }

    /// <summary>No probes at all is black, not a crash.</summary>
    [Fact]
    public void An_empty_volume_samples_to_nothing() {
        var volume = LightProbeVolume.Build([]);

        Assert.False(volume.IsTetrahedral);
        Assert.Equal(Vector3.Zero, volume.Sample(Vector3.Zero).L00);
    }

    /// <summary>Two probes in the same place are one probe, and the first one wins.</summary>
    [Fact]
    public void Coincident_probes_are_merged() {
        var volume = LightProbeVolume.Build([
            Probe(new(0f, 0f, 0f), 1f),
            Probe(new(0f, 0f, 0f), 99f),
            Probe(new(1f, 0f, 0f), 2f),
            Probe(new(0f, 1f, 0f), 3f),
            Probe(new(0f, 0f, 1f), 4f)
        ]);

        Assert.Equal(4, volume.Positions.Length);
        Assert.Equal(1f, volume.Sample(new(0f, 0f, 0f)).L00.X, 5);
    }

    // --- Fixtures -----------------------------------------------------------

    static List<LightProbe> Grid(int side, Func<Vector3, float> field) {
        var probes = new List<LightProbe>();

        for (var x = 0; x < side; x++) {
            for (var y = 0; y < side; y++) {
                for (var z = 0; z < side; z++) {
                    var position = new Vector3(x, y, z);
                    probes.Add(Probe(position, field(position)));
                }
            }
        }

        return probes;
    }

    /// <summary>
    ///     A probe carrying one number, put in the constant term where it is easy to read back.
    /// </summary>
    /// <remarks>
    ///     The interpolation is linear in the coefficients and the coefficients do not interact,
    ///     so one channel of one band says everything nine bands of three would. What is under
    ///     test here is the weights, not the harmonics.
    /// </remarks>
    static LightProbe Probe(Vector3 position, float value) =>
        new() { Position = position, Coefficients = new() { L00 = new(value, 0f, 0f) } };
}
