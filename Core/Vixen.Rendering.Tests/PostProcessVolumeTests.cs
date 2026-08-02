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

    // --- The fixture --------------------------------------------------------

    static Entity Place(
        World world,
        Vector3 at,
        PostProcessSettings settings,
        int priority,
        Vector3? extents = null,
        float blendRadius = 1f,
        bool unbound = false
    ) {
        var entity = world.Create();

        world.Add(
            entity,
            PostProcessVolume.Default with {
                Extents = extents ?? new Vector3(3f, 3f, 3f),
                BlendRadius = blendRadius,
                Priority = priority,
                Unbound = unbound,
                Settings = settings
            }
        );

        world.Add(entity, new WorldTransform { Value = Matrix4x4.FromTranslation(at) });

        return entity;
    }

    static void Turn(World world, Entity entity, Quaternion rotation) =>
        world.Get<WorldTransform>(entity).Value = Matrix4x4.FromQuaternion(rotation);
}
