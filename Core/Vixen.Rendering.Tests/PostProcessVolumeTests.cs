// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Tests;

/// <summary>What a place says about the frame, and how two places that overlap resolve.</summary>
/// <remarks>
///     Every case here is one where getting it wrong produces a picture rather than an error: a
///     volume that does nothing, a volume that never stops doing something, or two volumes whose
///     overlap resolves to neither of them.
/// </remarks>
public sealed class PostProcessVolumeTests {
    /// <summary>
    ///     ⚠ An unset field falls through, and that is what makes a volume an override.
    /// </summary>
    /// <remarks>
    ///     The property the whole design rests on. A volume that only wants the cellar darker must not
    ///     also reset the grade — so a null contributes nothing, and a volume underneath keeps its
    ///     opinion about everything the one above it did not mention.
    /// </remarks>
    [Fact]
    public void An_unset_field_leaves_what_is_underneath_alone() {
        var overlay = PostProcessOverlay.None;

        overlay.Add(new() { Saturation = 0.5f, BloomIntensity = 0.9f }, 1f);
        overlay.Add(new() { Saturation = 0.2f }, 1f);

        Assert.Equal(0.2f, overlay.Saturation!.Value.Value, 5);
        Assert.Equal(0.9f, overlay.BloomIntensity!.Value.Value, 5);
    }

    /// <summary>
    ///     ⚠ A field nothing has claimed leaves the node's authored value exactly as it was.
    /// </summary>
    /// <remarks>
    ///     A volume cannot express "everything else back to default", and must not: the frame's look
    ///     is the document's, and a volume is an opinion about part of it.
    /// </remarks>
    [Fact]
    public void A_field_no_volume_claims_is_the_documents() {
        var overlay = PostProcessOverlay.None;

        overlay.Add(new() { Saturation = 0.5f }, 1f);

        Assert.Null(overlay.Contrast);
        Assert.Equal(1.06f, overlay.Contrast?.Over(1.06f) ?? 1.06f, 5);
    }

    /// <summary>
    ///     ⚠ A half-faded volume is half applied — toward the <em>node's</em> value, not toward zero.
    /// </summary>
    /// <remarks>
    ///     The reason <see cref="PostProcessOverlay" /> is a different type from
    ///     <see cref="PostProcessSettings" />: the weight has to survive the fold, because what it is
    ///     a weight <em>from</em> lives in the node and not in the volume. Baking it during the fold
    ///     would mean interpolating from a number the fold had to invent.
    /// </remarks>
    [Fact]
    public void A_half_faded_volume_is_half_way_from_the_authored_value() {
        var overlay = PostProcessOverlay.None;

        overlay.Add(new() { Saturation = 2f }, 0.5f);

        Assert.Equal(1.5f, overlay.Saturation!.Value.Over(1f), 5);
        Assert.Equal(0.5f, overlay.Saturation!.Value.Over(-1f), 5);
    }

    /// <summary>
    ///     ⚠ A half-faded volume over a fully applied one lands between the two.
    /// </summary>
    /// <remarks>
    ///     The case that makes a doorway a crossfade rather than a cut, and the one that a fold
    ///     carrying only a value would get wrong: without the weight also moving toward 1, walking
    ///     from a graded room into another would dip through the document's own look on the way.
    /// </remarks>
    [Fact]
    public void A_fading_volume_over_an_applied_one_crossfades_between_them() {
        var overlay = PostProcessOverlay.None;

        overlay.Add(new() { Saturation = 2f }, 1f);
        overlay.Add(new() { Saturation = 4f }, 0.5f);

        Assert.Equal(3f, overlay.Saturation!.Value.Value, 5);
        Assert.Equal(1f, overlay.Saturation!.Value.Weight, 5);
        Assert.Equal(3f, overlay.Saturation!.Value.Over(0f), 5);
    }

    /// <summary>A volume at zero weight contributes nothing at all.</summary>
    [Fact]
    public void A_volume_at_no_weight_is_not_in_the_fold() {
        var overlay = PostProcessOverlay.None;

        overlay.Add(new() { Saturation = 9f }, 0f);

        Assert.True(overlay.IsEmpty);
    }

    // --- The box ------------------------------------------------------------

    /// <summary>Inside is fully applied, and the fade is measured from the surface.</summary>
    /// <remarks>
    ///     ⚠ From the <em>surface</em>, not from the centre. A long thin volume measured from its
    ///     centre would fade in from much further away at its ends than along its sides, which reads
    ///     as a corridor whose grade starts before the corridor does.
    /// </remarks>
    [Fact]
    public void The_falloff_is_measured_from_the_boxs_surface() {
        var volume = PostProcessVolume.Default with { Extents = new(2f, 1f, 10f), BlendRadius = 2f };

        Assert.Equal(1f, volume.Falloff(Vector3.Zero), 5);
        Assert.Equal(1f, volume.Falloff(new(0f, 0f, 9.9f)), 5);

        // One metre outside a face, in a two-metre falloff: half way.
        Assert.Equal(0.5f, volume.Falloff(new(3f, 0f, 0f)), 5);
        Assert.Equal(0.5f, volume.Falloff(new(0f, 2f, 0f)), 5);
        Assert.Equal(0f, volume.Falloff(new(5f, 0f, 0f)), 5);
    }

    /// <summary>A blend radius of zero is a hard edge rather than a volume that never applies.</summary>
    [Fact]
    public void A_zero_blend_radius_is_a_hard_edge() {
        var volume = PostProcessVolume.Default with { Extents = Vector3.One, BlendRadius = 0f };

        Assert.Equal(1f, volume.Falloff(Vector3.Zero), 5);
        Assert.Equal(0f, volume.Falloff(new(1.01f, 0f, 0f)), 5);
    }

    /// <summary>An unbound volume applies everywhere and never consults its extents.</summary>
    [Fact]
    public void An_unbound_volume_reaches_everything() {
        var volume = PostProcessVolume.Default with { Extents = Vector3.Zero, Unbound = true };

        Assert.Equal(1f, volume.Falloff(new(1000f, -400f, 7f)), 5);
    }

    /// <summary>
    ///     ⚠ A zeroed component is inert, and it is its <em>weight</em> that makes it so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What an entity gets the moment the component is added. The other available answer —
    ///         treating a zeroed volume as unbounded — would make adding one in the inspector change
    ///         the whole level's look before anybody had authored anything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its falloff at the origin is 1, and that is correct rather than a leak.</b> A box
    ///         with zero extents contains exactly one point, and the point being tested is that point.
    ///         Special-casing it to zero would be a lie about the geometry; what makes the component
    ///         inert is that a zeroed <c>Weight</c> multiplies the whole thing away, which is the
    ///         product the system actually forms.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_zeroed_volume_is_inert_through_its_weight() {
        var volume = default(PostProcessVolume);

        Assert.Equal(0f, volume.Weight);
        Assert.True(volume.Settings.IsEmpty);

        // Degenerate but honest: the origin is inside a box of no size centred on it.
        Assert.Equal(1f, volume.Falloff(Vector3.Zero), 5);
        Assert.Equal(0f, volume.Falloff(new(0.01f, 0f, 0f)), 5);

        using var world = new World();
        var view = new RenderView("Camera") { Position = Vector3.Zero };
        var system = new PostProcessVolumeSystem(view);
        var entity = world.Create();

        world.Add(entity, default(PostProcessVolume));
        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });

        system.Fold(world);

        Assert.Equal(1, system.VolumeCount);
        Assert.Equal(0, system.ContributingCount);
        Assert.True(system.Overlay.IsEmpty);
    }

    // --- The fold over a world ----------------------------------------------

    /// <summary>Priority decides which volume is on top where two overlap.</summary>
    [Fact]
    public void The_higher_priority_volume_wins_the_overlap() {
        using var world = new World();
        var view = new RenderView("Camera") { Position = Vector3.Zero };
        var system = new PostProcessVolumeSystem(view);

        Place(world, Vector3.Zero, new() { Saturation = 0.25f }, priority: 0);
        Place(world, Vector3.Zero, new() { Saturation = 0.75f }, priority: 5);

        system.Fold(world);

        Assert.Equal(2, system.VolumeCount);
        Assert.Equal(2, system.ContributingCount);
        Assert.Equal(0.75f, system.Overlay.Saturation!.Value.Value, 5);
    }

    /// <summary>
    ///     ⚠ And it wins by being applied last, so a lower-priority volume's other opinions survive.
    /// </summary>
    [Fact]
    public void A_lower_priority_volumes_other_fields_survive() {
        using var world = new World();
        var view = new RenderView("Camera") { Position = Vector3.Zero };
        var system = new PostProcessVolumeSystem(view);

        Place(world, Vector3.Zero, new() { Saturation = 0.25f, FogDensity = 0.4f }, priority: 0);
        Place(world, Vector3.Zero, new() { Saturation = 0.75f }, priority: 5);

        system.Fold(world);

        Assert.Equal(0.4f, system.Overlay.FogDensity!.Value.Value, 5);
    }

    /// <summary>A camera outside every volume folds to nothing, and the frame is the document's.</summary>
    [Fact]
    public void A_camera_outside_everything_folds_to_nothing() {
        using var world = new World();
        var view = new RenderView("Camera") { Position = new(500f, 0f, 0f) };
        var system = new PostProcessVolumeSystem(view);

        Place(world, Vector3.Zero, new() { Saturation = 0.25f }, priority: 0);

        system.Fold(world);

        Assert.Equal(1, system.VolumeCount);
        Assert.Equal(0, system.ContributingCount);
        Assert.True(system.Overlay.IsEmpty);
    }

    /// <summary>
    ///     ⚠ The box is tested in the volume's own space, so rotating one rotates the box.
    /// </summary>
    /// <remarks>
    ///     An axis-aligned world-space test would make a rotated volume change shape rather than
    ///     orientation — which nobody notices until a level has been built around one.
    /// </remarks>
    [Fact]
    public void A_rotated_volume_is_a_rotated_box() {
        using var world = new World();

        // A metre outside the short axis, and well inside the long one. Turned a quarter turn about
        // Y, that same world point is inside — which is only true if the test runs in local space.
        var view = new RenderView("Camera") { Position = new(4f, 0f, 0f) };
        var system = new PostProcessVolumeSystem(view);

        var entity = Place(
            world,
            Vector3.Zero,
            new() { Saturation = 0.5f },
            priority: 0,
            extents: new(1f, 1f, 8f),
            blendRadius: 0f
        );

        system.Fold(world);
        Assert.Equal(0, system.ContributingCount);

        Turn(world, entity, Quaternion.FromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f));
        system.Fold(world);

        Assert.Equal(1, system.ContributingCount);
    }

    /// <summary>An unbound volume applies wherever the camera is.</summary>
    [Fact]
    public void An_unbound_volume_applies_from_anywhere() {
        using var world = new World();
        var view = new RenderView("Camera") { Position = new(9000f, 9000f, 9000f) };
        var system = new PostProcessVolumeSystem(view);

        Place(world, Vector3.Zero, new() { Saturation = 0.5f }, priority: 0, unbound: true);

        system.Fold(world);

        Assert.Equal(1, system.ContributingCount);
        Assert.Equal(0.5f, system.Overlay.Saturation!.Value.Value, 5);
    }

    /// <summary>A volume with no opinions is not counted as contributing.</summary>
    /// <remarks>
    ///     <see cref="PostProcessVolumeSystem.ContributingCount" /> is what answers "the volume I
    ///     placed is not reaching the camera", so a volume that reaches it and says nothing must not
    ///     inflate the number — otherwise the one diagnostic the feature has reports success for the
    ///     commonest way of getting it wrong.
    /// </remarks>
    [Fact]
    public void An_empty_volume_reaches_the_camera_and_still_contributes_nothing() {
        using var world = new World();
        var view = new RenderView("Camera") { Position = Vector3.Zero };
        var system = new PostProcessVolumeSystem(view);

        Place(world, Vector3.Zero, PostProcessSettings.None, priority: 0);

        system.Fold(world);

        Assert.Equal(1, system.VolumeCount);
        Assert.Equal(0, system.ContributingCount);
    }

    // --- The shapes ---------------------------------------------------------

    /// <summary>
    ///     ⚠ A volume loaded without a shape is the box it has always been.
    /// </summary>
    /// <remarks>
    ///     The compatibility claim [35 § B2](../../docs/plan/35-water.md) rests on: every volume
    ///     authored against doc 32 has no shape field in its YAML, so the enum's zero has to be the
    ///     box. A default of anything else silently changes the shape of every volume in every
    ///     existing level, and the symptom is a grade that no longer reaches a corner.
    /// </remarks>
    [Fact]
    public void A_volume_with_no_shape_stated_is_a_box() {
        Assert.Equal(PostProcessShapeKind.Box, default(PostProcessVolume).Shape);
        Assert.Equal(PostProcessShapeKind.Box, PostProcessVolume.Default.Shape);

        var volume = PostProcessVolume.Default with { Extents = new(2f, 2f, 2f), BlendRadius = 0f };

        // The corner of a two-metre box is inside it, and would not be inside a two-metre sphere.
        Assert.Equal(1f, volume.Falloff(new(1.9f, 1.9f, 1.9f)), 5);
    }

    /// <summary>A sphere is the same extents read as radii, and its corner is outside.</summary>
    /// <remarks>
    ///     The case that shows the two shapes are actually different: a point a box contains and a
    ///     sphere of the same extents does not.
    /// </remarks>
    [Fact]
    public void A_sphere_volume_excludes_the_corner_a_box_contains() {
        var volume = PostProcessVolume.Default with {
            Extents = new(2f, 2f, 2f),
            BlendRadius = 0f,
            Shape = PostProcessShapeKind.Sphere
        };

        Assert.Equal(1f, volume.Falloff(new(1.9f, 0f, 0f)), 5);
        Assert.Equal(0f, volume.Falloff(new(1.9f, 1.9f, 1.9f)), 5);
    }

    /// <summary>The sphere's falloff is exact: a metre outside a two-metre radius is a metre.</summary>
    /// <remarks>
    ///     ⚠ Exact for uniform radii and a lower bound otherwise — see
    ///     <see cref="SpherePostProcessShape" />. This asserts the exact half, because the
    ///     approximation being wrong on the common case would be invisible: a grade that fades a
    ///     little early looks like a grade.
    /// </remarks>
    [Fact]
    public void The_spheres_falloff_is_measured_from_its_surface() {
        var volume = PostProcessVolume.Default with {
            Extents = new(2f, 2f, 2f),
            BlendRadius = 2f,
            Shape = PostProcessShapeKind.Sphere
        };

        Assert.Equal(1f, volume.Falloff(Vector3.Zero), 5);
        Assert.Equal(0.5f, volume.Falloff(new(3f, 0f, 0f)), 5);
        Assert.Equal(0.5f, volume.Falloff(new(0f, 0f, 3f)), 5);
        Assert.Equal(0f, volume.Falloff(new(4f, 0f, 0f)), 5);
    }

    /// <summary>A zeroed sphere contains its own centre, exactly as a zeroed box does.</summary>
    /// <remarks>
    ///     ⚠ The degenerate case a division would turn into a NaN, and a NaN falloff propagates into
    ///     the overlay as a weight that is neither zero nor one — which is a frame of garbage rather
    ///     than a volume that does nothing.
    /// </remarks>
    [Fact]
    public void A_zero_radius_sphere_collapses_rather_than_dividing() {
        var volume = PostProcessVolume.Default with {
            Extents = Vector3.Zero,
            BlendRadius = 1f,
            Shape = PostProcessShapeKind.Sphere
        };

        Assert.Equal(1f, volume.Falloff(Vector3.Zero), 5);
        Assert.Equal(0f, volume.Falloff(new(0.5f, 0f, 0f)), 5);
        Assert.False(float.IsNaN(volume.Falloff(new(0.5f, 0f, 0f))));
    }

    /// <summary>A sphere volume folds through the system, placed and scaled like any other.</summary>
    [Fact]
    public void A_sphere_volume_folds_from_where_it_is_placed() {
        using var world = new World();
        var view = new RenderView("Camera") { Position = new(10f, 0f, 0f) };
        var system = new PostProcessVolumeSystem(view);

        Place(
            world,
            new(10f, 0f, 0f),
            new() { Saturation = 0.5f },
            priority: 0,
            extents: new(3f, 3f, 3f),
            blendRadius: 0f,
            shape: PostProcessShapeKind.Sphere
        );

        system.Fold(world);

        Assert.Equal(1, system.ContributingCount);
        Assert.Equal(0.5f, system.Overlay.Saturation!.Value.Value, 5);
    }

    /// <summary>
    ///     ⚠ A custom volume with nothing to resolve it reaches nothing rather than everything.
    /// </summary>
    /// <remarks>
    ///     The same choice a singular transform makes. A fallback to the box would grade a rectangle
    ///     around the lake while the inspector looked correct, which is the failure that costs an
    ///     afternoon.
    /// </remarks>
    [Fact]
    public void A_custom_volume_with_no_source_reaches_nothing() {
        using var world = new World();
        var view = new RenderView("Camera") { Position = Vector3.Zero };
        var system = new PostProcessVolumeSystem(view);

        Place(
            world,
            Vector3.Zero,
            new() { Saturation = 0.5f },
            priority: 0,
            shape: PostProcessShapeKind.Custom
        );

        system.Fold(world);

        Assert.Equal(1, system.VolumeCount);
        Assert.Equal(0, system.ContributingCount);
    }

    /// <summary>A supplied shape decides the volume, and the blend radius still fades it.</summary>
    /// <remarks>
    ///     What a water body will be: a shape this assembly cannot evaluate, asked for by entity, with
    ///     every other mechanic of a volume untouched.
    /// </remarks>
    [Fact]
    public void A_supplied_shape_decides_a_custom_volume() {
        using var world = new World();
        var view = new RenderView("Camera") { Position = new(0f, 1f, 0f) };
        var system = new PostProcessVolumeSystem(view) { Shapes = new BelowZero() };

        Place(
            world,
            Vector3.Zero,
            new() { Saturation = 0.5f },
            priority: 0,
            blendRadius: 2f,
            shape: PostProcessShapeKind.Custom
        );

        // A metre above the surface, fading over two: half.
        system.Fold(world);
        Assert.Equal(1, system.ContributingCount);
        Assert.Equal(0.5f, system.Overlay.Saturation!.Value.Weight, 5);

        // Below it, fully.
        view.Position = new(0f, -3f, 0f);
        system.Fold(world);
        Assert.Equal(1f, system.Overlay.Saturation!.Value.Weight, 5);

        // Well above it, not at all.
        view.Position = new(0f, 40f, 0f);
        system.Fold(world);
        Assert.Equal(0, system.ContributingCount);
    }

    // --- The fixture --------------------------------------------------------

    /// <summary>A stand-in for a water body: everything below y = 0 is inside.</summary>
    sealed class BelowZero : IPostProcessShapeSource, IPostProcessShape {
        public IPostProcessShape? ShapeFor(Entity entity) => this;

        public bool Contains(Vector3 world, out float distanceOutside) {
            distanceOutside = MathF.Max(world.Y, 0f);
            return distanceOutside <= 0f;
        }
    }

    static Entity Place(
        World world,
        Vector3 at,
        PostProcessSettings settings,
        int priority,
        Vector3? extents = null,
        float blendRadius = 1f,
        bool unbound = false,
        PostProcessShapeKind shape = PostProcessShapeKind.Box
    ) {
        var entity = world.Create();

        world.Add(
            entity,
            PostProcessVolume.Default with {
                Extents = extents ?? new Vector3(3f, 3f, 3f),
                BlendRadius = blendRadius,
                Priority = priority,
                Unbound = unbound,
                Shape = shape,
                Settings = settings
            }
        );

        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(at) });

        return entity;
    }

    static void Turn(World world, Entity entity, Quaternion rotation) =>
        world.Get<WorldTransform>(entity).Value = Matrix4x4.FromQuaternion(rotation);
}
