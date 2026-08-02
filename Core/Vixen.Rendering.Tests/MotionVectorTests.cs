// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Xunit;

namespace Tests;

/// <summary>One frame of history, which is the whole of what a motion vector needs and nothing keeps.</summary>
/// <remarks>
///     <para>
///         The half of a velocity pass that lives outside the shader. Where a pixel <em>is</em> is a
///         fact the frame already has; where it <em>was</em> is a fact somebody has to remember, and
///         neither the transform component nor the render object does — both are overwritten in place
///         by whatever moved them.
///     </para>
///     <para>
///         Every case here is one where getting it wrong produces a picture rather than an error: no
///         motion at all, motion pointing at the origin, or motion that is one frame stale.
///     </para>
/// </remarks>
public sealed class MotionVectorTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    /// <summary>
    ///     ⚠ An object that has not moved reports no motion, and an object that has reports where it was.
    /// </summary>
    /// <remarks>
    ///     The first assertion is the one that catches the obvious mistake: reading the current world
    ///     matrix into the previous array during the same <c>Prepare</c> that draws from it. That
    ///     version passes any test where nothing moves and reports zero motion for ever.
    /// </remarks>
    [Fact]
    public void The_previous_matrix_is_last_frames() {
        using var system = new RenderSystem();
        var (transforms, motion, id) = Rig(system);

        Place(system, transforms, id, new(1f, 0f, 0f));
        system.Prepare();

        // Nothing has moved yet, so the first frame's history is the first frame — which is what
        // makes a newly spawned object still rather than streaking in from the origin.
        Assert.Equal(1f, motion.Previous[id.Index].M41);

        Place(system, transforms, id, new(5f, 0f, 0f));
        system.Prepare();

        Assert.Equal(1f, motion.Previous[id.Index].M41);

        Place(system, transforms, id, new(9f, 0f, 0f));
        system.Prepare();

        Assert.Equal(5f, motion.Previous[id.Index].M41);
    }

    /// <summary>
    ///     ⚠ A slot nothing has written yet takes its current matrix rather than the zero one.
    /// </summary>
    /// <remarks>
    ///     A zero matrix projects to the origin, so an object drawn for the first time would hand its
    ///     whole silhouette a vector pointing at the middle of the screen — a smear across the frame
    ///     every time anything spawns. The test is <c>M44 == 0</c> because every affine transform the
    ///     engine produces has a one there, so the check is exact rather than a tolerance.
    /// </remarks>
    [Fact]
    public void A_new_object_has_no_motion_rather_than_motion_from_the_origin() {
        using var system = new RenderSystem();
        var (transforms, motion, first) = Rig(system);

        Place(system, transforms, first, new(1f, 0f, 0f));
        system.Prepare();
        system.Prepare();

        var second = Add(system, transforms);
        Place(system, transforms, second, new(40f, 0f, 0f));
        system.Prepare();

        Assert.Equal(40f, motion.Previous[second.Index].M41);
    }

    /// <summary>Without a transform feature there is nothing to compare, and it says so with nothing.</summary>
    [Fact]
    public void With_no_transforms_it_records_nothing() {
        using var system = new RenderSystem();
        var motion = new MotionVectorRenderFeature();
        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = new EffectPipelineDescriber(device) };

        meshes.Add(motion);
        system.AddFeature(meshes);
        system.Prepare();

        Assert.Equal(0, motion.RecordCount);
    }

    /// <summary>
    ///     ⚠ The view's history is advanced explicitly, and advancing twice does not lose a frame.
    /// </summary>
    /// <remarks>
    ///     <see cref="RenderView.ViewProjection" />'s setter runs whenever anything touches the matrix,
    ///     which in a frame with a jittered projection is more than once — so a previous matrix
    ///     maintained there would mean "earlier in this same frame" and report a camera that moved as
    ///     nearly still. <see cref="RenderView.Advance" /> is called by whoever owns the per-frame
    ///     update, and this holds that it does what its name says.
    /// </remarks>
    [Fact]
    public void A_view_remembers_exactly_one_frame() {
        var view = new RenderView("Camera") { ViewProjection = Matrix4x4.FromTranslation(new(1f, 0f, 0f)) };

        // Never advanced: zero, which MotionVectors.rvn's host contract treats as no history.
        Assert.Equal(0f, view.PreviousViewProjection.M44);

        view.Advance();
        view.ViewProjection = Matrix4x4.FromTranslation(new(2f, 0f, 0f));

        Assert.Equal(1f, view.PreviousViewProjection.M41);
        Assert.Equal(2f, view.ViewProjection.M41);

        view.Advance();
        view.ViewProjection = Matrix4x4.FromTranslation(new(3f, 0f, 0f));

        Assert.Equal(2f, view.PreviousViewProjection.M41);
    }

    /// <summary>
    ///     ⚠ The previous view-projection reaches the block at the offset the shader reads it from.
    /// </summary>
    /// <remarks>
    ///     Set 1 is a contract between shaders rather than any one shader's business, and this member
    ///     went on the end of it. A byte out and <c>MotionVectors.rvn</c> reprojects against a matrix
    ///     made of a view position and some padding, which is a frame of plausible garbage.
    /// </remarks>
    [Fact]
    public void The_previous_matrix_lands_at_byte_one_hundred_and_forty_four() {
        using var constants = new ViewConstants(device);

        var member = constants.Members.Single(entry => entry.Key == ViewConstants.PreviousViewProjection);

        Assert.Equal(144, member.Offset);
        Assert.Equal(64, member.Size);
        Assert.Equal(208, constants.Size);
    }

    // --- The fixture --------------------------------------------------------

    (TransformRenderFeature Transforms, MotionVectorRenderFeature Motion, RenderObjectId Id) Rig(RenderSystem system) {
        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = new EffectPipelineDescriber(device) };
        var transforms = new TransformRenderFeature { Device = device };
        var motion = new MotionVectorRenderFeature { Transforms = transforms };

        meshes.Add(transforms);
        meshes.Add(motion);
        system.AddFeature(meshes);

        return (transforms, motion, Add(system, transforms));
    }

    static RenderObjectId Add(RenderSystem system, TransformRenderFeature transforms) {
        var id = system.Objects.Add(new() { Bounds = new(Vector3.Zero, 1f) });

        system.Objects.Data.Data(transforms.World)[id.Index] = Matrix4x4.Identity;
        return id;
    }

    static void Place(RenderSystem system, TransformRenderFeature transforms, RenderObjectId id, Vector3 at) =>
        system.Objects.Data.Data(transforms.World)[id.Index] = Matrix4x4.FromTranslation(at);
}
