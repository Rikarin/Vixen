// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
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
public class VolumetricFogIntegrationTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
    readonly EffectSystem effects = new();
    readonly DescriptorAllocator allocator;
    readonly SamplerCache samplers;
    readonly ComputePipelineCache pipelines;
    readonly Dictionary<string, DescriptorSetLayoutHandle> layouts = [];

    public VolumetricFogIntegrationTests() {
        allocator = new(device);
        samplers = new(device);
        pipelines = new(device);

        // Set 2 in the shape the reflection reports, with the indices taken from the generated
        // constants rather than written down: a binding index is declaration order, so a resource
        // added above another renumbers everything below it.
        Declare(
            VolumetricFogInjectKeys.ShaderName,
            new(VolumetricFogInjectKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Compute),
            new(VolumetricFogInjectKeys.TargetBinding, DescriptorKind.StorageTexture, ShaderStage.Compute)
        );

        Declare(
            VolumetricFogKeys.ShaderName,
            new(VolumetricFogKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Compute),
            new(VolumetricFogKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Compute),
            new(VolumetricFogKeys.ShadowMapBinding, DescriptorKind.SampledTexture, ShaderStage.Compute),
            new(VolumetricFogKeys.VolumeSamplerBinding, DescriptorKind.Sampler, ShaderStage.Compute),
            new(VolumetricFogKeys.ShadowSamplerBinding, DescriptorKind.Sampler, ShaderStage.Compute),
            new(VolumetricFogKeys.LightBufferBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
            new(VolumetricFogKeys.ClustersBinding, DescriptorKind.StorageBuffer, ShaderStage.Compute),
            new(VolumetricFogKeys.TargetBinding, DescriptorKind.StorageTexture, ShaderStage.Compute)
        );

        effects.AddProvider(new AlwaysCompiles(layouts));
    }

    void Declare(string shader, params DescriptorBinding[] bindings) =>
        layouts[shader] = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerMaterial, bindings, shader));

    /// <inheritdoc />
    public void Dispose() {
        samplers.Dispose();
        allocator.Dispose();
        device.Dispose();
        GC.SuppressFinalize(this);
    }

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

    // --- The frame's lamps in the air ------------------------------------------

    /// <summary><c>FrameClusters.Of</c>, restated — the lookup a froxel finds its light list by.</summary>
    static int ClusterOf(Vector3 positionVS, Vector2 tanHalfFov, float near, float far) {
        const int TilesX = 16;
        const int TilesY = 9;
        const int Slices = 24;

        var depth = MathF.Max(-positionVS.Z, 1e-6f);
        var ndc = new Vector2(positionVS.X / (depth * tanHalfFov.X), positionVS.Y / (depth * tanHalfFov.Y));
        var uv = Vector2.Clamp(ndc * 0.5f + new Vector2(0.5f), Vector2.Zero, Vector2.One);

        var ratio = MathF.Log(MathF.Max(depth, near) / near) / MathF.Max(MathF.Log(far / near), 1e-6f);
        var slice = Math.Clamp((int)(ratio * Slices), 0, Slices - 1);

        var x = Math.Clamp((int)(uv.X * TilesX), 0, TilesX - 1);
        var y = Math.Clamp((int)(uv.Y * TilesY), 0, TilesY - 1);

        return x + (y * TilesX) + (slice * TilesX * TilesY);
    }

    /// <summary>
    ///     A froxel's cluster comes from the <em>camera's</em> planes, and the fog's own would name a
    ///     different one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The trap this pass is one line away from at all times.</b> The shader already holds
    ///         a near and a far — <c>fogNear</c> and <c>fogFar</c>, the grid it owns — and the cluster
    ///         lookup wants the two the <em>culler</em> cut its slices by, which are the camera's. Both
    ///         pairs are floats called near and far, both produce a cluster index in range, and the
    ///         wrong one reads a plausible list culled for somewhere else entirely.
    ///     </para>
    ///     <para>
    ///         Asserted as a disagreement rather than against a table of expected indices, because what
    ///         has to hold is that the two answers are not interchangeable — a test that pinned one
    ///         number would pass just as well if both pairs happened to agree at the depth chosen.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_cluster_a_froxel_reads_is_cut_by_the_cameras_planes_and_not_the_fogs() {
        var tangents = new Vector2(MathF.Tan(MathF.PI / 6f) * (16f / 9f), MathF.Tan(MathF.PI / 6f));

        // Ten metres in front of the camera, off to one side — well inside both ranges.
        var froxel = new Vector3(1.5f, -0.5f, -10f);

        var correct = ClusterOf(froxel, tangents, 0.1f, 1000f);
        var wrong = ClusterOf(froxel, tangents, 0.5f, 64f);

        Assert.NotEqual(correct, wrong);

        // And the disagreement is entirely in the slice: the tile is a projection and does not know
        // about either pair, which is why the failure is invisible on screen and total in depth.
        Assert.Equal(correct % (16 * 9), wrong % (16 * 9));
    }

    /// <summary>
    ///     A lamp's contribution to a froxel is phase-weighted, and the phase carries no cosine.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>TerrainLit.Punctual</c> multiplies by <c>saturate(dot(n, l))</c> because a surface
    ///     receives light across a tilted face. A froxel has no face — the same argument
    ///     <c>SunVisibility</c> makes about passing <c>NdotL</c> as one — so what replaces the cosine
    ///     is the phase function, which is a property of the medium rather than of a geometry the air
    ///     does not have. The check is that the phase integrates to one over the sphere: a lamp gives
    ///     the medium the same total energy whatever <c>phaseG</c> redistributes it to.
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(0.7f)]
    [InlineData(-0.4f)]
    public void The_phase_a_lamp_is_weighted_by_integrates_to_one_over_the_sphere(float g) {
        // WaterVolume.Phase — Henyey–Greenstein, the same eight lines the sun's term uses.
        static float Phase(float cosTheta, float anisotropy) {
            var squared = anisotropy * anisotropy;
            var denominator = 1f + squared - (2f * anisotropy * cosTheta);

            return (1f - squared) / (4f * MathF.PI * MathF.Max(MathF.Pow(denominator, 1.5f), 1e-6f));
        }

        // ∫ p(cosθ) dΩ = 2π ∫ p(μ) dμ over μ ∈ [−1, 1], by the midpoint rule.
        const int Steps = 20000;

        var total = 0f;

        for (var i = 0; i < Steps; i++) {
            total += Phase((((i + 0.5f) / Steps) * 2f) - 1f, g) * (2f / Steps);
        }

        Assert.Equal(1f, total * 2f * MathF.PI, 3);
    }

    /// <summary>
    ///     With nothing published, the fog is unclustered and both light slots carry the stand-in.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the ordinary frame, not the degenerate one.</b> The Standard Frame emits no
    ///     cluster node, so the absent case is the one almost every document takes — and it still has
    ///     to write a complete set, because a permutation does not fold a binding away. The assertion
    ///     that both slots hold the <em>same</em> handle is what pins the stand-in as one buffer
    ///     rather than a pair that would have to be kept in step.
    /// </remarks>
    [Fact]
    public void A_frame_that_culled_nothing_binds_the_stand_in_into_both_light_slots() {
        using var h = Build();
        Frame(h);

        Assert.False(h.Fog.Clustered);

        foreach (var step in new[] { h.Fog.Steps[1], h.Fog.Steps[2] }) {
            Assert.False(step.Parameters.Get(VolumetricFogKeys.UseClusteredLights));

            var lights = Binding(step, VolumetricFogKeys.LightBufferBinding);
            var lists = Binding(step, VolumetricFogKeys.ClustersBinding);

            Assert.True(lights.Buffer.IsValid);
            Assert.Equal(lights.Buffer, lists.Buffer);
        }
    }

    /// <summary>Both published buffers turn it on, and either one alone does not.</summary>
    /// <remarks>
    ///     ⚠ Both halves, because they are published by different things: the light list by the
    ///     lighting feature and the cluster lists by the shading pass's own <c>SceneBuffers</c> line.
    ///     A frame with a light list and no lists would index a cluster nothing filled, which is not a
    ///     dimmer picture — it is thirty-two arbitrary indices into a real buffer.
    /// </remarks>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Clustering_is_detected_from_both_published_buffers(bool lights, bool lists, bool expected) {
        using var h = Build();

        var light = device.CreateBuffer(new(1024, BufferUsage.Storage, MemoryAccess.DeviceLocal, "Lights"));
        var cluster = device.CreateBuffer(new(1024, BufferUsage.Storage, MemoryAccess.DeviceLocal, "Clusters"));

        if (lights) {
            h.Frame.Parameters.Set(ParameterKeys.New<BufferHandle>("ForwardPlus.lightBuffer"), light);
        }

        if (lists) {
            h.Frame.Parameters.Set(ParameterKeys.New<BufferHandle>("ForwardPlus.clusters"), cluster);
        }

        Frame(h);

        Assert.Equal(expected, h.Fog.Clustered);
        Assert.Equal(expected, h.Fog.Steps[1].Parameters.Get(VolumetricFogKeys.UseClusteredLights));

        if (expected) {
            Assert.Equal(light, Binding(h.Fog.Steps[1], VolumetricFogKeys.LightBufferBinding).Buffer);
            Assert.Equal(cluster, Binding(h.Fog.Steps[1], VolumetricFogKeys.ClustersBinding).Buffer);
        }

        device.Destroy(light);
        device.Destroy(cluster);
    }

    /// <summary>
    ///     The cluster range reaching the shader is the camera's, and the fog's own is a different
    ///     pair.
    /// </summary>
    /// <remarks>
    ///     The host half of the arithmetic above. Both numbers are on this node — <c>Near</c> and
    ///     <c>Far</c> are the froxel grid's — so the two pairs are one substitution apart at the call
    ///     site as well as in the shader.
    /// </remarks>
    [Fact]
    public void The_cluster_planes_are_the_cameras_and_the_grids_are_the_fogs() {
        using var h = Build();
        Frame(h);

        var step = h.Fog.Steps[1];

        Assert.Equal(0.1f, step.Parameters.Get(VolumetricFogKeys.ClusterNear), 5);
        Assert.Equal(1000f, step.Parameters.Get(VolumetricFogKeys.ClusterFar), 5);
        Assert.Equal(0.5f, step.Parameters.Get(VolumetricFogKeys.FogNear), 5);
        Assert.Equal(64f, step.Parameters.Get(VolumetricFogKeys.FogFar), 5);
    }

    static ResourceBinding Binding(ComputeRenderer step, uint binding) =>
        Assert.Single(step.Descriptors.Bindings, entry => entry.Binding == binding);

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }

        public required GraphicsCompositor Compositor { get; init; }

        public required RenderGraph Graph { get; init; }

        public required VolumetricFogRenderer Fog { get; init; }

        public required SceneConstants Frame { get; init; }

        public void Dispose() {
            Fog.Dispose();
            Frame.Dispose();
            Graph.DisposePool();
            System.Dispose();
        }
    }

    Harness Build() {
        var size = new Int2(320, 180);
        var system = new RenderSystem();
        var constants = new SceneConstants(device, "Scene");

        var view = new RenderView("Main") { Camera = RenderCamera.Default };

        var fog = new VolumetricFogRenderer {
            Name = "Volumetrics",
            View = view,
            Frame = constants,
            Samplers = samplers,
            Pipelines = pipelines,
            Allocator = allocator,
            Device = device
        };

        var compositor = new GraphicsCompositor(system) { FrameSize = size };
        compositor.Game = fog;

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Fog = fog,
            Frame = constants
        };
    }

    /// <summary>An effect for whatever is asked for, with the layout its shader declared.</summary>
    /// <remarks>
    ///     Copied rather than shared with <c>AutoExposureTests</c>, which is what that fixture already
    ///     records about <c>BloomTests</c>: it is five lines of stub, and a fixture reaching into
    ///     another fixture makes one test class's private shape part of another's contract.
    /// </remarks>
    static Effect Compiled(EffectKey key, DescriptorSetLayoutHandle layout) =>
        new() {
            Key = key,
            Stages = [new(ShaderStage.Compute, [1, 2, 3, 4], "main")],
            SetLayouts = [default, default, layout, default],
            ConstantBufferSize = 512
        };

    sealed class AlwaysCompiles(Dictionary<string, DescriptorSetLayoutHandle> layouts) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            Compiled(key, layouts.TryGetValue(key.ShaderName, out var layout) ? layout : default);
    }

    void Frame(Harness h) {
        var list = device.BeginCommandList();

        allocator.BeginFrame();
        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }
}
