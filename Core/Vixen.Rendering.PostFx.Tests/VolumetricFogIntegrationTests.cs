// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Tests;

/// <summary>
///     <c>VolumetricFog.rvn</c>'s march, restated on the CPU where it can be checked against
///     arithmetic.
/// </summary>
/// <remarks>
///     <para>
///         The shape <c>CardRadiosity.Light</c> and <c>TracedIrradianceFiller</c> already have, and
///         for their reason: a compute pass that agrees with a closed form is a pass whose failures
///         are in the plumbing rather than in the maths, and a picture cannot tell the two apart.
///         Where this and the shader disagree, this is right.
///     </para>
///     <para>
///         ⚠ The property being checked is not "the numbers are close". It is that a march over N
///         slabs of a <em>homogeneous</em> medium reproduces the closed form for one slab of the
///         whole length — because if it does, the slicing is not adding or losing energy, which is
///         the failure a slice count change would otherwise hide.
///     </para>
/// </remarks>
public class VolumetricFogIntegrationTests {
    /// <summary>The grid's Z distribution — <c>FroxelGrid.SliceDepth</c>, and <c>ClusterGrid</c>'s.</summary>
    static float SliceDepth(int slice, int slices, float near, float far) =>
        near * MathF.Pow(far / near, slice / (float)slices);

    /// <summary>
    ///     <c>Integrate</c>, one column, over a medium of constant extinction and in-scatter.
    /// </summary>
    static (float Accumulated, float Transmittance) March(
        int slices,
        float near,
        float far,
        float extinction,
        float inscatter,
        float rayScale = 1f
    ) {
        var accumulated = 0f;
        var transmittance = 1f;

        for (var z = 0; z < slices; z++) {
            var step = (SliceDepth(z + 1, slices, near, far) - SliceDepth(z, slices, near, far)) * rayScale;
            var stepTransmittance = MathF.Exp(-extinction * step);

            var integral = extinction > 1e-6f ? (1f - stepTransmittance) / extinction : step;

            accumulated += transmittance * inscatter * integral;
            transmittance *= stepTransmittance;
        }

        return (accumulated, transmittance);
    }

    /// <summary>Beer–Lambert over the whole path, whatever the march cut it into.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    public void The_marched_transmittance_is_the_closed_form_at_every_slice_count(int slices) {
        const float Near = 0.5f;
        const float Far = 64f;
        const float Extinction = 0.05f;

        var (_, transmittance) = March(slices, Near, Far, Extinction, 1f);

        Assert.Equal(MathF.Exp(-Extinction * (Far - Near)), transmittance, 4);
    }

    /// <summary>
    ///     And the in-scatter telescopes to <c>S ⁄ σt · (1 − T)</c> — <c>WaterVolume.InScatter</c>'s
    ///     own closed form, arrived at from the other direction.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is what makes the slab integral worth having. Multiplying the in-scatter by the
    ///     slab's <em>length</em> instead would grow without bound, and the visible failure is fog
    ///     that glows brighter the denser it gets rather than saturating.
    /// </remarks>
    [Theory]
    [InlineData(0.01f)]
    [InlineData(0.05f)]
    [InlineData(0.5f)]
    [InlineData(2f)]
    public void The_marched_inscatter_is_the_closed_form_and_saturates(float extinction) {
        const float Near = 0.5f;
        const float Far = 64f;
        const float Inscatter = 0.3f;

        var (accumulated, transmittance) = March(64, Near, Far, extinction, Inscatter);

        Assert.Equal(Inscatter / extinction * (1f - transmittance), accumulated, 4);

        // Past a few extinction lengths a thicker medium scatters no more toward the camera, because
        // what it scatters in at the far end it absorbs again on the way out.
        Assert.True(accumulated <= Inscatter / extinction);
    }

    /// <summary>An empty medium transmits everything and adds nothing.</summary>
    /// <remarks>
    ///     ⚠ The <c>σt → 0</c> limit is written out in the shader rather than left to the quotient,
    ///     which would be zero over zero — and the visible failure of a NaN here is a froxel column
    ///     that turns the whole pixel black.
    /// </remarks>
    [Fact]
    public void An_empty_volume_is_a_pass_through() {
        var (accumulated, transmittance) = March(64, 0.5f, 64f, 0f, 0.3f);

        Assert.Equal(1f, transmittance, 6);
        Assert.False(float.IsNaN(accumulated));

        // S · d, which is the limit — the medium scatters but absorbs nothing.
        Assert.Equal(0.3f * (64f - 0.5f), accumulated, 3);
    }

    /// <summary>
    ///     A ray off the screen's centre crosses more medium than its view depth suggests.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>FroxelGrid.Ray</c>'s length is exactly that ratio, and dropping it is fog that thins
    ///     toward the corners of the frame — a gradient that reads as a vignette rather than as a
    ///     bug.
    /// </remarks>
    [Fact]
    public void A_ray_off_centre_crosses_more_medium() {
        var centre = March(64, 0.5f, 64f, 0.05f, 0.3f);
        var corner = March(64, 0.5f, 64f, 0.05f, 0.3f, MathF.Sqrt(1f + 1f + 1f));

        Assert.True(corner.Transmittance < centre.Transmittance);
        Assert.True(corner.Accumulated > centre.Accumulated);
    }

    /// <summary>The slice boundaries are a geometric progression from near to far.</summary>
    /// <remarks>
    ///     Which is the whole reason the grid is worth having at sixty-four slices: the nearest is
    ///     centimetres deep and the furthest is metres, so the resolution is spent where a beam's
    ///     edge is actually visible.
    /// </remarks>
    [Fact]
    public void The_slices_are_thin_near_and_thick_far() {
        const int Slices = 64;

        Assert.Equal(0.5f, SliceDepth(0, Slices, 0.5f, 64f), 5);
        Assert.Equal(64f, SliceDepth(Slices, Slices, 0.5f, 64f), 3);

        var first = SliceDepth(1, Slices, 0.5f, 64f) - SliceDepth(0, Slices, 0.5f, 64f);
        var last = SliceDepth(Slices, Slices, 0.5f, 64f) - SliceDepth(Slices - 1, Slices, 0.5f, 64f);

        Assert.True(first < 0.1f, $"the first slice is {first} deep");
        Assert.True(last > 20f * first, $"the last slice is only {last / first} times the first");
    }
}
