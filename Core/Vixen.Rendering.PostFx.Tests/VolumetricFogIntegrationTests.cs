// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
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

    /// <summary>
    ///     The same march, with the sun's visibility varying along the ray — <c>Scatter</c>'s
    ///     shadow term folded into <c>Integrate</c>'s accumulation.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Visibility multiplies the in-scatter and never the extinction</b>, which is the one
    ///     line the two passes have to agree on. Extinction is what the medium is; visibility is what
    ///     reaches it. A shader that shadowed the extinction as well would thin its fog inside every
    ///     beam — plausible-looking, and backwards.
    /// </remarks>
    static (float Accumulated, float Transmittance) MarchShadowed(
        int slices,
        float near,
        float far,
        float extinction,
        float inscatter,
        Func<int, float> visibility
    ) {
        var accumulated = 0f;
        var transmittance = 1f;

        for (var z = 0; z < slices; z++) {
            var step = SliceDepth(z + 1, slices, near, far) - SliceDepth(z, slices, near, far);
            var stepTransmittance = MathF.Exp(-extinction * step);

            var integral = extinction > 1e-6f ? (1f - stepTransmittance) / extinction : step;

            accumulated += transmittance * inscatter * visibility(z) * integral;
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
    ///     An occluder between the sun and a stretch of the ray darkens exactly that stretch, and
    ///     leaves the transmittance either side of it alone.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the property the whole feature is for.</b> Unshadowed fog is a function of
    ///         distance and altitude, which is what the analytic falloff already was — a beam is the
    ///         <em>absence</em> of light behind a caster, and nothing derivable from a pixel's depth
    ///         can produce one.
    ///     </para>
    ///     <para>
    ///         The transmittance assertion is the half that catches the likelier bug. Shadowing is a
    ///         lighting term, so the medium a shadowed ray crosses is the same medium: a frame where
    ///         the shaft appeared <em>and</em> the fog thinned inside it would look almost right and
    ///         be wrong about what fog is.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_occluder_darkens_the_stretch_behind_it_and_nothing_else() {
        const int Slices = 64;
        const float Near = 0.5f;
        const float Far = 64f;
        const float Extinction = 0.05f;
        const float Inscatter = 0.3f;

        var lit = MarchShadowed(Slices, Near, Far, Extinction, Inscatter, _ => 1f);

        // A wall across the middle third of the grid: the froxels behind it see no sun.
        var shadowed = MarchShadowed(
            Slices,
            Near,
            Far,
            Extinction,
            Inscatter,
            z => z is >= 20 and < 40 ? 0f : 1f
        );

        Assert.True(shadowed.Accumulated < lit.Accumulated, "the shaded stretch scatters nothing toward the camera");

        // ⚠ Exactly equal, not merely close. Visibility never touches the extinction.
        Assert.Equal(lit.Transmittance, shadowed.Transmittance, 6);

        // And the darkening is precisely the shaded slabs' own contribution, not a scaling of the
        // whole ray: what the shadowed march lost is what a march of only those slabs would add.
        var occluded = MarchShadowed(
            Slices,
            Near,
            Far,
            Extinction,
            Inscatter,
            z => z is >= 20 and < 40 ? 1f : 0f
        );

        Assert.Equal(lit.Accumulated, shadowed.Accumulated + occluded.Accumulated, 5);
    }

    /// <summary>Full visibility is the unshadowed march, exactly — the term is a factor of one.</summary>
    /// <remarks>
    ///     ⚠ What this pins is that turning shadowing on cannot change a frame with nothing casting.
    ///     A visibility folded in as an addend, or applied to the ambient as well, would move the
    ///     open-sky answer — and the open sky is most of every frame.
    /// </remarks>
    [Fact]
    public void An_unoccluded_ray_is_the_unshadowed_march() {
        var plain = March(64, 0.5f, 64f, 0.05f, 0.3f);
        var shadowed = MarchShadowed(64, 0.5f, 64f, 0.05f, 0.3f, _ => 1f);

        Assert.Equal(plain.Accumulated, shadowed.Accumulated, 6);
        Assert.Equal(plain.Transmittance, shadowed.Transmittance, 6);
    }

    /// <summary>
    ///     The same wall, the same width, costs more radiance near the camera than far from it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The asymmetry is the transmittance in front: a slab's contribution is weighted by what
    ///         survived the medium before it, so an occluder moved outward removes less. Which is why
    ///         a beam reads strongest where it enters frame and washes out with distance.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured over equal spans in metres, and the first attempt at this test was not.</b>
    ///         Under the geometric split the far slices are enormously longer — slices 40–56 cover
    ///         about 24 m where slices 8–24 cover about 2 — so comparing equal <em>slice counts</em>
    ///         measures the slicing rather than the shadowing, and reports the opposite answer with
    ///         complete confidence. Slabs are selected here by depth for exactly that reason.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_same_wall_costs_more_radiance_near_the_camera_than_far_from_it() {
        const int Slices = 64;
        const float Near = 0.5f;
        const float Far = 64f;
        const float Width = 2f;

        Func<int, float> Wall(float from) =>
            z => {
                var depth = SliceDepth(z, Slices, Near, Far);

                return depth >= from && depth < from + Width ? 0f : 1f;
            };

        var near = MarchShadowed(Slices, Near, Far, 0.08f, 0.3f, Wall(4f));
        var far = MarchShadowed(Slices, Near, Far, 0.08f, 0.3f, Wall(24f));

        Assert.True(near.Accumulated < far.Accumulated, "the nearer wall removes more of what reaches the camera");

        // Neither wall is medium: both rays cross the same fog.
        Assert.Equal(near.Transmittance, far.Transmittance, 6);
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

    /// <summary>
    ///     ⚠ A volume that says <c>0</c> has an opinion; a volume that says nothing does not.
    /// </summary>
    /// <remarks>
    ///     The trap the optional fields exist to avoid, and it is easy to flatten by accident. Null
    ///     means "no opinion about fog", so a cellar volume that only darkens the grade leaves the
    ///     mist alone. Zero means "no fog <em>here</em>", which is how a designer clears the mist a
    ///     level-wide volume filled out of an interior. Treating the two the same makes every volume
    ///     in the level silently delete the fog.
    /// </remarks>
    [Fact]
    public void No_opinion_and_no_fog_are_different_answers() {
        var silent = PostProcessOverlay.None;
        silent.Add(new() { Saturation = 0.5f }, 1f);

        Assert.Null(silent.VolumetricDensity);

        var cleared = PostProcessOverlay.None;
        cleared.Add(new PostProcessSettings { VolumetricDensity = 0f }, 1f);

        Assert.NotNull(cleared.VolumetricDensity);
        Assert.Equal(0f, cleared.VolumetricDensity!.Value.Over(0.06f), 5);

        // And the authored value survives the volume that never mentioned it.
        Assert.Equal(0.06f, silent.VolumetricDensity?.Over(0.06f) ?? 0.06f, 5);
    }

    /// <summary>Every volumetric field is folded, listed as an opinion, and counted by IsEmpty.</summary>
    /// <remarks>
    ///     A field added to <c>PostProcessSettings</c> and missed in any one of the three is a volume
    ///     whose setting is authored, shown in the inspector, and never reaches the frame.
    /// </remarks>
    [Fact]
    public void The_volumetric_fields_reach_all_three_of_the_folds_seams() {
        var settings = new PostProcessSettings {
            VolumetricDensity = 0.1f,
            VolumetricAlbedo = new Vector3(0.5f, 0.6f, 0.7f),
            VolumetricPhaseG = 0.3f
        };

        Assert.False(settings.IsEmpty);

        List<string> opinions = [];
        settings.Opinions(opinions);

        Assert.Equal(["volumetricDensity", "volumetricAlbedo", "volumetricPhaseG"], opinions);

        var overlay = PostProcessOverlay.None;
        overlay.Add(settings, 1f);

        Assert.False(overlay.IsEmpty);
        Assert.Equal(0.1f, overlay.VolumetricDensity!.Value.Over(0.02f), 5);
        Assert.Equal(0.3f, overlay.VolumetricPhaseG!.Value.Over(0.7f), 5);
        Assert.Equal(new Vector3(0.5f, 0.6f, 0.7f), overlay.VolumetricAlbedo!.Value.Over(Vector3.One));
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
