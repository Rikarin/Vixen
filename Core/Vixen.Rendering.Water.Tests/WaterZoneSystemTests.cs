// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
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
}
