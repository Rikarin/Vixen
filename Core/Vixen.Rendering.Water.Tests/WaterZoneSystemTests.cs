// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Water;
using Vixen.Water;
using Xunit;

namespace Tests;

/// <summary>
///     Zones and bodies, folded out of a scene — [docs/plan/35 § D3, § W3].
/// </summary>
/// <remarks>
///     <para>
///         The arithmetic is <c>Vixen.Water</c>'s and is tested there. What is here is the wiring, and
///         the two things about it that are silent when they are wrong: whether a body actually
///         reaches a zone, and whether the fold is quietly re-rasterising every frame.
///     </para>
///     <para>
///         ⚠ The second is the one that would never be noticed. A field re-rasterised every frame
///         looks <em>identical</em> to one re-rasterised every hundredth; the only symptom is frame
///         time, and the only place it shows is a profile nobody takes until something else is slow.
///     </para>
/// </remarks>
public sealed class WaterZoneSystemTests : IDisposable {
    readonly World world = new();
    readonly RenderView view = new("Camera");

    /// <inheritdoc />
    public void Dispose() {
        world.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A source that hands out one square lake, wherever it is asked.</summary>
    sealed class Square(float half) : IWaterSplineSource {
        public int Calls { get; private set; }

        public Spline? SplineFor(string name, in Matrix4x4 placement) {
            Calls++;

            if (name.Length == 0) {
                return null;
            }

            var origin = placement.Translation;

            return new(
                Spline.SmoothTangents(
                    [
                        new(origin.X - half, origin.Y, origin.Z - half),
                        new(origin.X + half, origin.Y, origin.Z - half),
                        new(origin.X + half, origin.Y, origin.Z + half),
                        new(origin.X - half, origin.Y, origin.Z + half)
                    ],
                    closed: true,
                    tension: 1f
                ),
                closed: true
            );
        }
    }

    Entity Zone(WaterZoneComponent component) {
        var entity = world.Create();

        world.Add(entity, component);
        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });

        return entity;
    }

    Entity Body(Vector3 at, string spline = "Lake") {
        var entity = world.Create();

        world.Add(entity, WaterBodyComponent.Default with { Spline = spline, SurfaceHeight = at.Y });
        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(at) });

        return entity;
    }

    WaterZoneSystem System(float half = 40f) =>
        new(view) { Splines = new Square(half), Ground = new FlatWaterGround(-10f) };

    // --- The fold ------------------------------------------------------------

    /// <summary>A zone claims the bodies its window reaches, and rasterises them into one field.</summary>
    [Fact]
    public void A_zone_claims_the_bodies_its_window_reaches() {
        var zone = Zone(WaterZoneComponent.Default);

        Body(new(0f, 2f, 0f));

        var system = System();

        system.Fold(world);

        Assert.Equal(1, system.ZoneCount);
        Assert.Equal(1, system.BodyCount);
        Assert.Equal(0, system.ZonelessBodies);

        var state = system.States[zone];

        Assert.Single(state.Bodies);
        Assert.True(state.Field!.Sample(Vector2.Zero).Coverage > 0.9f);
        Assert.Equal(2f, state.Field.Sample(Vector2.Zero).SurfaceHeight, 0.01f);
    }

    /// <summary>
    ///     ⚠ A body no zone reaches is counted, not silently dropped.
    /// </summary>
    /// <remarks>
    ///     Unreal's rule is that a water zone must exist or nothing renders, and it is the right rule —
    ///     the field is the interchange every consumer reads. What is not right is discovering it from
    ///     a blank frame, which is why the number exists. It is the answer to "I placed a lake and
    ///     there is no water".
    /// </remarks>
    [Fact]
    public void A_body_outside_every_zone_is_counted() {
        Zone(WaterZoneComponent.Default with { Extent = 256f });

        Body(new(0f, 2f, 0f));
        Body(new(4_000f, 2f, 0f));

        var system = System();

        system.Fold(world);

        Assert.Equal(2, system.BodyCount);
        Assert.Equal(1, system.ZonelessBodies);
    }

    /// <summary>And a body whose spline has not loaded is a different number.</summary>
    /// <remarks>
    ///     ⚠ Distinct from the one above because the fix is different: one is a zone that does not
    ///     reach, the other is an asset that has not loaded or a name that is wrong. One number for
    ///     both would send an author to look at the zone's extent when the spline is what is missing.
    /// </remarks>
    [Fact]
    public void A_body_whose_spline_is_missing_is_a_different_number() {
        Zone(WaterZoneComponent.Default);

        Body(new(0f, 2f, 0f));
        Body(new(0f, 2f, 0f), spline: string.Empty);

        var system = System();

        system.Fold(world);

        Assert.Equal(1, system.BodyCount);
        Assert.Equal(1, system.UnresolvedBodies);
        Assert.Equal(0, system.ZonelessBodies);
    }

    /// <summary>
    ///     ⚠ The diagnostic and the claim agree, away from the origin.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every other fold test here sits at the origin, and that is the trap: the diagnostic
    ///         once tested reach against a centre of zero while the claim tested against the eye, so
    ///         the two agreed exactly at the world origin and lied in both directions everywhere else
    ///         — a claimed body counted as zoneless, and a zoneless one counted as covered.
    ///     </para>
    ///     <para>
    ///         Both follow the view, because the window does — a claim staked to a fixed point is the
    ///         fixed-extent zone doc 35 rejects as not surviving an open world.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_fold_agrees_with_itself_away_from_the_origin() {
        var zone = Zone(WaterZoneComponent.Default);

        Body(new(5_000f, 2f, 5_000f));

        view.Position = new(5_000f, 0f, 5_000f);

        var system = System();

        system.Fold(world);

        // Standing on the lake: claimed, and the diagnostic says so.
        Assert.Equal(0, system.ZonelessBodies);
        Assert.Single(system.States[zone].Bodies);
        Assert.True(system.States[zone].Field!.Sample(new(5_000f, 5_000f)).Coverage > 0.9f);

        // From the origin the window reaches nothing, and the diagnostic agrees about that too
        // rather than reporting the far body as covered.
        view.Position = new(0f, 0f, 0f);
        system.Fold(world);

        Assert.Equal(1, system.ZonelessBodies);
        Assert.Empty(system.States[zone].Bodies);
    }

    /// <summary>
    ///     ⚠ A body containing the whole window is claimed from inside it.
    /// </summary>
    /// <remarks>
    ///     A camera in the middle of an ocean is nowhere near any shoreline —
    ///     <c>WaterBodyKind.Ocean</c>'s own description is water "extending past it to the horizon" —
    ///     and a claim that only walked the boundary would leave exactly that body unclaimed: dry
    ///     ground mid-ocean, counted zoneless with a zone standing right there.
    /// </remarks>
    [Fact]
    public void A_body_containing_the_window_is_claimed_from_inside_it() {
        var zone = Zone(WaterZoneComponent.Default);

        Body(new(0f, 2f, 0f));

        // A lake four kilometres to a side against a 512-metre window: every boundary point is
        // thousands of metres outside it.
        var system = System(half: 4_000f);

        system.Fold(world);

        Assert.Equal(0, system.ZonelessBodies);
        Assert.Single(system.States[zone].Bodies);
        Assert.True(system.States[zone].Field!.Sample(Vector2.Zero).Coverage > 0.9f);
    }

    // --- What makes the threshold real ---------------------------------------

    /// <summary>
    ///     ⚠ A still scene rasterises once, and folding it again costs nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The claim the whole amortisation rests on, and the one that is invisible when it
    ///         fails.</b> A field re-rasterised every frame looks identical to one re-rasterised every
    ///         hundredth; the only symptom is frame time.
    ///     </para>
    ///     <para>
    ///         ⚠ It failed the first time it was written, and the cause is worth keeping: the fold
    ///         built a fresh <c>WaterBody</c> every frame, so the zone was handed a different list
    ///         every frame, marked its field dirty every frame, and re-rasterised every frame. Bodies
    ///         are cached by identity now, and <see cref="WaterZoneSystem.RebuiltBodies" /> is the
    ///         reading that says so.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_still_scene_rasterises_once_however_many_frames_run() {
        var zone = Zone(WaterZoneComponent.Default);

        Body(new(0f, 2f, 0f));

        var system = System();

        system.Fold(world);

        var state = system.States[zone];

        Assert.Equal(1, state.RasterCount);
        Assert.Equal(1, system.RebuiltBodies);

        for (var frame = 0; frame < 200; frame++) {
            system.Fold(world);
        }

        Assert.Equal(1, state.RasterCount);
        Assert.Equal(0, system.RebuiltBodies);
        Assert.Equal(WaterZoneUpdate.None, state.LastUpdate);
    }

    /// <summary>
    ///     ⚠ A zone whose scroll threshold was never set still amortises.
    /// </summary>
    /// <remarks>
    ///     A component can hold zero without anyone typing it — a chunk's column is zeroed memory,
    ///     and a scene that never states the field deserialises the same way — and a threshold of
    ///     zero re-rasterised the whole field <em>every frame</em>, the exact cost the amortisation
    ///     exists to avoid, visible nowhere but frame time. The component's seam folds it to the
    ///     default; the kernel refuses it outright.
    /// </remarks>
    [Fact]
    public void A_zeroed_scroll_threshold_amortises_rather_than_rasterising_every_frame() {
        var component = WaterZoneComponent.Default with { ScrollThreshold = 0f };

        Assert.Equal(WaterZone.Default.ScrollThreshold, component.Zone.ScrollThreshold);

        var zone = Zone(component);

        Body(new(0f, 2f, 0f));

        var system = System();

        for (var frame = 0; frame < 50; frame++) {
            system.Fold(world);
        }

        Assert.Equal(1, system.States[zone].RasterCount);
    }

    /// <summary>A body that moved rebuilds, and the field with it.</summary>
    [Fact]
    public void A_body_that_moved_rebuilds_and_rerasterises() {
        var zone = Zone(WaterZoneComponent.Default);
        var body = Body(new(0f, 2f, 0f));

        var system = System();

        system.Fold(world);
        system.Fold(world);

        var state = system.States[zone];

        Assert.Equal(1, state.RasterCount);

        world.Get<WorldTransform>(body).Value = Matrix4x4.FromTranslation(new(80f, 2f, 0f));

        system.Fold(world);

        Assert.Equal(1, system.RebuiltBodies);
        Assert.Equal(2, state.RasterCount);
        Assert.Equal(WaterZoneUpdate.Changed, state.LastUpdate);

        // And the water is where the body now is.
        Assert.Equal(0f, state.Field!.Sample(Vector2.Zero).Coverage);
        Assert.True(state.Field.Sample(new(80f, 0f)).Coverage > 0.9f);
    }

    /// <summary>Walking past the threshold scrolls the window; walking inside it does not.</summary>
    [Fact]
    public void Walking_past_the_threshold_scrolls_the_window() {
        var zone = Zone(WaterZoneComponent.Default);

        Body(new(0f, 2f, 0f));

        var system = System(half: 4_000f);

        system.Fold(world);

        var state = system.States[zone];

        view.Position = new(60f, 0f, 0f);
        system.Fold(world);
        Assert.Equal(1, state.RasterCount);

        view.Position = new(70f, 0f, 0f);
        system.Fold(world);
        Assert.Equal(2, state.RasterCount);
        Assert.Equal(WaterZoneUpdate.Scrolled, state.LastUpdate);
    }

    /// <summary>A zone whose entity is gone takes its field with it.</summary>
    /// <remarks>
    ///     ⚠ A dictionary that only ever grew would hold a field per zone for as long as the world
    ///     lived, and a level streaming regions in and out would do that once per region — which is a
    ///     leak that looks like memory the level legitimately needs.
    /// </remarks>
    [Fact]
    public void A_zone_that_is_gone_takes_its_field_with_it() {
        var zone = Zone(WaterZoneComponent.Default);

        Body(new(0f, 2f, 0f));

        var system = System();

        system.Fold(world);
        Assert.Single(system.States);

        world.Destroy(zone);
        system.Fold(world);

        Assert.Empty(system.States);
        Assert.Equal(0, system.ZoneCount);
    }

    /// <summary>A zone that cannot be rasterised is skipped rather than thrown over.</summary>
    /// <remarks>
    ///     An author dragging a resolution through an invalid value should see the last good frame,
    ///     not an exception out of a system — and <c>WaterZone.Validate</c> is what says which values
    ///     those are.
    /// </remarks>
    [Fact]
    public void An_impossible_zone_is_skipped_rather_than_thrown_over() {
        Zone(WaterZoneComponent.Default with { Resolution = 1 });

        Body(new(0f, 2f, 0f));

        var system = System();

        system.Fold(world);

        Assert.Equal(0, system.ZoneCount);
        Assert.Empty(system.States);

        // And the body is then reaching nothing, which is the number that says why.
        Assert.Equal(1, system.ZonelessBodies);
    }

    /// <summary>
    ///     ⚠ A zeroed body component is unresolved, not a crash.
    /// </summary>
    /// <remarks>
    ///     A zeroed component's spline is <see langword="null" /> — not a name a source can be asked
    ///     for — and a fold that passed it through was a <see cref="NullReferenceException" /> out of
    ///     whatever source looked at it first, once per frame. It counts into
    ///     <see cref="WaterZoneSystem.UnresolvedBodies" />, the same number a spline that has not
    ///     loaded counts into, because the fix is the same: state which asset the body means.
    /// </remarks>
    [Fact]
    public void A_zeroed_body_component_is_unresolved_rather_than_a_crash() {
        Zone(WaterZoneComponent.Default);

        var entity = world.Create();

        world.Add(entity, new WaterBodyComponent());
        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });

        var system = System();

        system.Fold(world);

        Assert.Equal(0, system.BodyCount);
        Assert.Equal(1, system.UnresolvedBodies);
    }

    /// <summary>An unset shore falloff takes the default rather than a hard edge.</summary>
    /// <remarks>
    ///     ⚠ Zero is what a zeroed component holds, and a hard edge on water reads as a cut in the
    ///     terrain from a long way off — a bug nobody typed. A deliberate near-hard edge is a small
    ///     stated value; unset takes the two-metre beach.
    /// </remarks>
    [Fact]
    public void An_unset_shore_falloff_takes_the_default() {
        var zone = Zone(WaterZoneComponent.Default);
        var entity = world.Create();

        world.Add(entity, WaterBodyComponent.Default with { Spline = "Lake", ShoreFalloff = 0f });
        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });

        var system = System();

        system.Fold(world);

        var body = Assert.Single(system.States[zone].Bodies);

        Assert.Equal(WaterBodyComponent.Default.ShoreFalloff, body.ShoreFalloff);
    }

    // --- An asset that is not ready yet is not an asset that does not exist ---

    /// <summary>A source that answers null until the asset it is standing in for has landed.</summary>
    /// <remarks>
    ///     Which is <c>AssetWaterSource</c>'s behaviour and not an unkind test double: it starts a
    ///     <see cref="Task" /> on the first ask and polls it, so every game source answers null for the
    ///     first few frames <em>by construction</em>. Every other source in this file is a literal and
    ///     answers on the first ask, which is exactly why nothing here caught the fold caching the
    ///     null.
    /// </remarks>
    sealed class Streaming(float half) : IWaterSplineSource {
        readonly Square square = new(half);

        public bool Landed { get; set; }

        public int Calls { get; private set; }

        public Spline? SplineFor(string name, in Matrix4x4 placement) {
            Calls++;

            return Landed ? square.SplineFor(name, placement) : null;
        }
    }

    /// <summary>
    ///     ⚠ A spline that arrives late is asked for again, and the lake appears.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The fold caches a body against its component and its placement, and it used to cache
    ///         the <em>failure</em> with it.</b> A body whose <c>SplineFor</c> answered null was
    ///         recorded as unresolved, and because nothing about the component or the transform then
    ///         changed it was never asked again for the life of the world — so a lake named in a scene
    ///         could never appear in a running game, and the failure was permanent rather than
    ///         transient.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The count of asks is the assertion, not just the body count.</b> A fold that
    ///         resolved once and then held the answer would satisfy "the lake appears" on any test
    ///         whose source answers immediately — which is every other source in this file, and is why
    ///         the defect survived a suite that covers this fold thoroughly.
    ///     </para>
    ///     <para>
    ///         The tail is the other half and guards the over-correction: caching nothing at all would
    ///         also make the lake appear, and would rebuild every body every frame — marking every zone
    ///         dirty and re-rasterising every field, which is
    ///         <see cref="A_still_scene_rasterises_once_however_many_frames_run" />'s whole subject.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_spline_that_arrives_late_is_asked_again_and_the_lake_appears() {
        var zone = Zone(WaterZoneComponent.Default);

        Body(new(0f, 2f, 0f));

        var source = new Streaming(40f);
        var system = new WaterZoneSystem(view) { Splines = source, Ground = new FlatWaterGround(-10f) };

        for (var frame = 0; frame < 5; frame++) {
            system.Fold(world);
        }

        Assert.Equal(0, system.BodyCount);
        Assert.Equal(1, system.UnresolvedBodies);
        Assert.Equal(5, source.Calls);

        source.Landed = true;
        system.Fold(world);

        Assert.Equal(1, system.BodyCount);
        Assert.Equal(0, system.UnresolvedBodies);
        Assert.Single(system.States[zone].Bodies);
        Assert.True(system.States[zone].Field!.Sample(Vector2.Zero).Coverage > 0.9f);

        // And the success is still cached: the source is not asked again, no body is rebuilt, and the
        // field is not re-rasterised.
        var asked = source.Calls;
        var rasterised = system.States[zone].RasterCount;

        for (var frame = 0; frame < 50; frame++) {
            system.Fold(world);
        }

        Assert.Equal(asked, source.Calls);
        Assert.Equal(0, system.RebuiltBodies);
        Assert.Equal(rasterised, system.States[zone].RasterCount);
    }

    // --- The components themselves -------------------------------------------

    /// <summary>The components carry what the kernel's own descriptions want.</summary>
    [Fact]
    public void The_components_translate_into_the_kernels_own_types() {
        var zone = WaterZoneComponent.Default;

        Assert.Null(zone.Zone.Validate());
        Assert.Equal(2f, zone.Zone.MetresPerTexel, 1e-5f);

        var body = WaterBodyComponent.Default with { Depth = 5f, Velocity = 1.5f, AudioIntensity = 0.8f };

        Assert.Equal(5f, body.Profile.Depth);
        Assert.Equal(1.5f, body.Profile.Velocity);
        Assert.Equal(0.8f, body.Profile.AudioIntensity);
    }

    /// <summary>
    ///     ⚠ The default resolution is a power of two plus one, and that is load bearing.
    /// </summary>
    /// <remarks>
    ///     512 m over 257 samples is two metres exactly; over 256 it is 2.0078, and a snap grid stated
    ///     in whole metres would then land the window on a fraction of a texel — which is a shoreline
    ///     that crawls while the camera moves and is invisible in any screenshot of it.
    /// </remarks>
    [Fact]
    public void The_default_resolution_is_a_power_of_two_plus_one() {
        Assert.Equal(257, WaterZoneComponent.Default.Resolution);

        var wrong = WaterZoneComponent.Default with { Resolution = 256, CoarsestTexel = 4f };

        Assert.NotNull(wrong.Zone.Validate());
    }

    // --- The sea state a zone names — docs/plan/35 § D6's one asset kind -------

    /// <summary>A source holding one sea state under one name.</summary>
    sealed class Sea(string name, WaterWaveSpectrum spectrum) : IWaterWaveSource {
        public int Calls { get; private set; }

        public WaterWaveSpectrum? SpectrumFor(string asked) {
            Calls++;

            return asked == name ? spectrum : null;
        }
    }

    /// <summary>A named sea state replaces the inline one, in the component every consumer reads.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted through <see cref="WaterZoneSystem.Zones" /> rather than through a resolver
    ///     the test calls itself.</b> The whole design is that the name becomes a value in exactly one
    ///     place, so that the vertex stage and the underwater shape cannot disagree about what sea
    ///     this is — and the only way to check that is to read what those two read.
    /// </remarks>
    [Fact]
    public void A_zone_naming_a_sea_state_draws_that_one() {
        var gale = WaterWaveSpectrum.Default with { WindSpeed = 24f, Count = WaterWaveCount.ThirtyTwo };

        Zone(WaterZoneComponent.Default with { Waves = WaterWaveSpectrum.Calm, WaveAsset = "NorthSea" });

        var system = System();

        system.Waves = new Sea("NorthSea", gale);
        system.Fold(world);

        var (_, component) = Assert.Single(system.Zones);

        Assert.Equal(24f, component.Waves.WindSpeed);
        Assert.Equal(WaterWaveCount.ThirtyTwo, component.Waves.Count);
        Assert.Equal(0, system.UnresolvedWaves);
    }

    /// <summary>
    ///     ⚠ A name that does not resolve keeps the zone's own waves and counts — it does not flatten
    ///     the sea.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The opposite of a body whose spline is missing, deliberately.</b> A body with no
    ///         curve has no shape to draw and is not rendered; a zone with no spectrum has a perfectly
    ///         good window, and rendering it dead flat reads as the water stack being broken rather
    ///         than as one asset still streaming — which is a bug report about the renderer for a
    ///         problem in the content.
    ///     </para>
    ///     <para>
    ///         The assertion that matters is the wind speed, not the count: a fallback that quietly
    ///         became <see cref="WaterWaveSpectrum.Default" /> would also leave the count at one and
    ///         would be a different sea again.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_sea_state_that_has_not_loaded_falls_back_to_the_zones_own_and_is_counted() {
        Zone(WaterZoneComponent.Default with { Waves = WaterWaveSpectrum.Calm, WaveAsset = "Missing" });

        var system = System();

        system.Waves = new Sea("NorthSea", WaterWaveSpectrum.Default);
        system.Fold(world);

        var (_, component) = Assert.Single(system.Zones);

        Assert.Equal(WaterWaveSpectrum.Calm.WindSpeed, component.Waves.WindSpeed);
        Assert.Equal(WaterWaveSpectrum.Calm.Count, component.Waves.Count);
        Assert.Equal(1, system.UnresolvedWaves);
    }

    /// <summary>And with no source at all, which is the host that never wired one.</summary>
    [Fact]
    public void A_zone_naming_a_sea_state_with_no_source_is_counted() {
        Zone(WaterZoneComponent.Default with { Waves = WaterWaveSpectrum.Calm, WaveAsset = "NorthSea" });

        var system = System();

        system.Fold(world);

        Assert.Equal(1, system.UnresolvedWaves);
        Assert.Equal(WaterWaveSpectrum.Calm.WindSpeed, system.Zones[0].Component.Waves.WindSpeed);
    }

    /// <summary>
    ///     ⚠ An asset that arrives carrying a spectrum the evaluator refuses counts, and is not used.
    /// </summary>
    /// <remarks>
    ///     A file somebody edited outside the editor, past the importer that would have refused it.
    ///     Substituting it in would generate <em>zero</em> waves — see <c>WaterMeshRenderer.Stage</c>,
    ///     which asks <c>Validate</c> before it generates — so the zone would draw a mirror where the
    ///     inline spectrum would have drawn a sea. It counts for the same reason a missing file does:
    ///     the sea on screen is not the one the zone named.
    /// </remarks>
    [Fact]
    public void A_sea_state_the_evaluator_refuses_is_counted_and_not_used() {
        var backwards = WaterWaveSpectrum.Default with { MinimumWavelength = 60f, MaximumWavelength = 4f };

        Assert.NotNull(backwards.Validate());

        Zone(WaterZoneComponent.Default with { Waves = WaterWaveSpectrum.Calm, WaveAsset = "Broken" });

        var system = System();

        system.Waves = new Sea("Broken", backwards);
        system.Fold(world);

        Assert.Equal(1, system.UnresolvedWaves);
        Assert.Equal(WaterWaveSpectrum.Calm.MinimumWavelength, system.Zones[0].Component.Waves.MinimumWavelength);
    }

    /// <summary>A zone naming nothing never reaches the source.</summary>
    /// <remarks>
    ///     The negative control for the count: a fold that asked for the empty name would count every
    ///     zone in a project that uses no sea-state assets, and the diagnostic would read as broken
    ///     from the first frame of every scene.
    /// </remarks>
    [Fact]
    public void A_zone_naming_no_sea_state_never_asks() {
        Zone(WaterZoneComponent.Default);

        var system = System();
        var sea = new Sea("NorthSea", WaterWaveSpectrum.Default);

        system.Waves = sea;
        system.Fold(world);

        Assert.Equal(0, sea.Calls);
        Assert.Equal(0, system.UnresolvedWaves);
    }

    // --- The surface seam, and the clock — docs/plan/35 § D1 and § D2 ---------

    /// <summary>A place inside a zone's window answers a query; a place outside answers nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is dry and is not an error.</b> It is what a boat outside every window gets, and
    ///     a solver leaves it to gravity rather than guessing — the same answer
    ///     <see cref="WaterZoneSystem.ZonelessBodies" /> counts for a body.
    /// </remarks>
    [Fact]
    public void A_place_inside_a_window_has_a_query_and_a_place_outside_does_not() {
        Zone(WaterZoneComponent.Default with { Extent = 256f });

        Body(new(0f, 2f, 0f));

        var system = System();

        system.Fold(world);

        Assert.NotNull(system.QueryAt(Vector2.Zero));
        Assert.Null(system.QueryAt(new(5_000f, 0f)));
    }

    /// <summary>
    ///     ⚠ The same query object twice, because it is asked once per pontoon per fixed step.
    /// </summary>
    /// <remarks>
    ///     A <see cref="WaterQuery" /> sums its spectrum into up to thirty-two waves in its
    ///     constructor. Building one per ask is that sum per pontoon per step, which for a river of
    ///     crates is thousands of times a second — invisible in a picture and plainly visible in a
    ///     profile, which is the wrong order to find it in.
    /// </remarks>
    [Fact]
    public void The_query_is_cached_per_zone_rather_than_built_per_ask() {
        Zone(WaterZoneComponent.Default);

        Body(new(0f, 2f, 0f));

        var system = System();

        system.Fold(world);

        var first = system.QueryAt(Vector2.Zero);

        Assert.NotNull(first);
        Assert.Same(first, system.QueryAt(new(4f, 4f)));

        // And a fold that changed nothing does not rebuild it either — the sea state is the key.
        system.Fold(world);

        Assert.Same(first, system.QueryAt(Vector2.Zero));
    }

    /// <summary>A resolved sea state reaches the query a boat floats on, not just the vertex stage.</summary>
    /// <remarks>
    ///     The other half of "the name becomes a value in exactly one place": if the substitution
    ///     happened only in what <see cref="WaterZoneSystem.Zones" /> publishes, the renderer would
    ///     draw the named sea and a solver would float boats on the inline one.
    /// </remarks>
    [Fact]
    public void A_named_sea_state_reaches_the_query_too() {
        var gale = WaterWaveSpectrum.Default with { WindSpeed = 24f, Count = WaterWaveCount.ThirtyTwo };

        Zone(WaterZoneComponent.Default with { Waves = WaterWaveSpectrum.Calm, WaveAsset = "NorthSea" });
        Body(new(0f, 2f, 0f));

        var system = System();

        system.Waves = new Sea("NorthSea", gale);
        system.Fold(world);

        var query = system.QueryAt(Vector2.Zero);

        Assert.NotNull(query);
        Assert.Equal(32, query.Waves.Length);
        Assert.Equal(24f, query.Spectrum.WindSpeed);
    }

    /// <summary>
    ///     ⚠ The fold does not advance the clock. <see cref="WaterClockSystem" /> does, in a phase
    ///     early enough for the fixed step.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A regression test for a one-frame drift that shipped.</b> The zone system folds in
    ///         <see cref="SystemPhase.PreRender" /> — it has to, because a body is rasterised where
    ///         <c>TransformSystem</c> has just put it — and <see cref="SystemPhase.FixedUpdate" />,
    ///         where a buoyancy solver runs, is <em>earlier in the same frame</em>. A clock advanced
    ///         during the fold therefore handed the solver last frame's water time while the vertex
    ///         stage drew this frame's, and a boat sat exactly one frame of swell behind the water
    ///         underneath it.
    ///     </para>
    ///     <para>
    ///         Small, constant, and invisible until the frame rate changed — which is the drift § D2's
    ///         whole seam test exists to prevent, arriving through the back door of a phase order.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_fold_does_not_touch_the_clock_and_the_phases_say_why() {
        var system = System();

        system.WaterTime = 12.5f;
        system.Fold(world);

        Assert.Equal(12.5f, system.WaterTime);

        // And the phases, which is the reason the clock is a second system rather than a line in the
        // fold: EarlyUpdate < FixedUpdate < PreRender, so the writer is before every reader.
        Assert.Equal(
            SystemPhase.EarlyUpdate,
            typeof(WaterClockSystem).GetCustomAttribute<UpdateInGroupAttribute>()!.Phase
        );

        Assert.Equal(
            SystemPhase.PreRender,
            typeof(WaterZoneSystem).GetCustomAttribute<UpdateInGroupAttribute>()!.Phase
        );

        Assert.True(SystemPhase.EarlyUpdate < SystemPhase.FixedUpdate);
        Assert.True(SystemPhase.FixedUpdate < SystemPhase.PreRender);
    }
}
