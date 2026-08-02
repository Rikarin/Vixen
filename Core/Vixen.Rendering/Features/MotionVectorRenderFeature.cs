// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering.Features;

/// <summary>Where each object was last frame, and getting that to the velocity pass.</summary>
/// <remarks>
///     <para>
///         <b>The half of a motion vector that a view cannot supply.</b>
///         <see cref="RenderView.PreviousViewProjection" /> catches a camera that moved; this catches
///         an object that moved while the camera did not. A pass with only the first is the
///         depth-reprojection trick, which is correct for static geometry and wrong for everything
///         motion vectors exist for.
///     </para>
///     <para>
///         <b>Its own sub-feature rather than two more fields on
///         <see cref="TransformRenderFeature" />, and the reason is that feature's records path.</b>
///         With <c>UseRecords</c> on — which is every device with
///         <see cref="GraphicsDeviceFeatures.HasDrawIndirectCount" /> — that feature pushes nothing at
///         all, because a push is per draw and a merged command has no point inside it at which to
///         push the second object's matrix. The velocity pass is not a merged pass and does need its
///         matrices pushed, so it gets a contributor that always pushes and a shader that always
///         reads a push constant.
///     </para>
///     <para>
///         ⚠ <b>Both matrices, not just the previous one.</b> When records are on nothing else pushes
///         <c>world</c> either, so this pass would draw every object at whatever record zero holds.
///         Pushing both is also what keeps the shader's contract one thing:
///         <c>MotionVectors.rvn</c> declares a 128-byte range covering exactly these two, which is
///         Vulkan's guaranteed push size and therefore the budget.
///     </para>
///     <para>
///         ⚠ <b>It contributes nothing to any other pass, and that is checked by offset rather than by
///         name.</b> <c>ForwardPlus</c> declares a 64-byte range, so offset 64 falls outside it and
///         this pushes nothing there — a push into a range a layout does not have is a validation
///         error on every draw, not a value quietly dropped.
///     </para>
///     <para>
///         <b>How the history is kept.</b> Two arrays and a swap per frame. Extraction writes this
///         frame's world matrices before <see cref="Prepare" /> runs, so <see cref="Prepare" /> moves
///         what it recorded last frame into the array <see cref="Draw" /> reads and then records this
///         frame's into the other. A slot with no history — an object drawn for the first time, or one
///         whose slot was recycled — takes its current matrix, which reports no motion. Leaving the
///         zero matrix there instead would project the object to the origin and hand the whole
///         silhouette a vector pointing at the middle of the screen.
///     </para>
/// </remarks>
public sealed class MotionVectorRenderFeature : SubRenderFeature, IDrawSubFeature {
    Matrix4x4[] previous = [];
    Matrix4x4[] recorded = [];
    int count;

    /// <inheritdoc />
    public override string Name => "MotionVectors";

    /// <summary>Where the current world matrices come from.</summary>
    /// <remarks>
    ///     ⚠ <b>Required.</b> Without it there is nothing to compare against and nothing to draw with,
    ///     so <see cref="Prepare" /> does nothing and the velocity pass reports every pixel as still —
    ///     which looks exactly like a scene that is not moving.
    /// </remarks>
    public TransformRenderFeature? Transforms { get; set; }

    /// <summary>Which stages the two matrices are pushed to.</summary>
    public ShaderStage Stages { get; set; } = ShaderStage.Vertex;

    /// <summary>Where the current matrix goes in the push block.</summary>
    public int Offset { get; set; }

    /// <summary>And where the previous one goes. Sixty-four bytes after it, which is one matrix.</summary>
    public int PreviousOffset { get; set; } = 64;

    /// <summary>How many slots had a matrix last frame, for a test or an inspector.</summary>
    public int RecordCount => count;

    /// <summary>What <see cref="Draw" /> will push as "where this object was".</summary>
    public ReadOnlySpan<Matrix4x4> Previous => previous.AsSpan(0, count);

    /// <inheritdoc />
    /// <remarks>
    ///     Always false: this contributes a push per draw, which is exactly what stops a run of nodes
    ///     merging into one command. The velocity pass is not a pass that merges — see the class
    ///     remarks — so saying otherwise would be claiming a saving that cannot be taken.
    /// </remarks>
    public bool IsRecording => true;

    /// <inheritdoc />
    protected internal override void Prepare(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);

        if (Transforms is not { } transforms) {
            count = 0;
            return;
        }

        var world = system.Objects.Data.Data(transforms.World)[..system.Objects.Count];

        // The swap is what makes this one frame of history rather than a copy of the present:
        // `recorded` holds what the world looked like when Prepare last ran, and that is precisely
        // "last frame". Reading `world` into `previous` directly would report no motion, for ever.
        (previous, recorded) = (recorded, previous);

        Grow(ref previous, world.Length);
        Grow(ref recorded, world.Length);

        for (var index = 0; index < world.Length; index++) {
            // ⚠ M44 rather than a parallel bool array, and it is exact rather than a tolerance: the
            // only way a stored matrix has a zero here is that nothing ever wrote it, because every
            // affine transform the engine produces has one. A new object therefore reports no motion
            // on its first frame instead of streaking in from the origin.
            if (previous[index].M44 == 0f) {
                previous[index] = world[index];
            }

            recorded[index] = world[index];
        }

        count = world.Length;
    }

    /// <inheritdoc />
    public void Draw(RenderSystem system, RenderDrawContext context, in RenderNode node) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(context);

        if (Transforms is not { } transforms || StagesFor(context) is not { } stages) {
            return;
        }

        var index = node.Object.Index;
        var world = system.Objects.Data.Data(transforms.World)[index];
        var was = index < count ? previous[index] : world;

        Push(context, stages, Offset, ref world);
        Push(context, stages, PreviousOffset, ref was);
    }

    static void Push(RenderDrawContext context, ShaderStage stages, int offset, ref Matrix4x4 matrix) =>
        context.CommandList.PushConstants(
            stages,
            offset,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref matrix, 1))
        );

    /// <summary>
    ///     The stages to push to, or null when this shader has no range covering both matrices.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It asks about <see cref="PreviousOffset" />, not about
    ///         <see cref="Offset" />.</b> Every geometry pass in the engine has a range covering byte
    ///         zero, so testing the first matrix would say yes to the shading pass — and pushing 128
    ///         bytes into its 64-byte range is a validation error on every draw of the frame. Only the
    ///         velocity pass declares a range that reaches 64, which makes the question "is this the
    ///         pass I am for" and the answer a fact about the shader rather than a name this feature
    ///         has to be told.
    ///     </para>
    ///     <para>
    ///         An effect with no set layouts is one with no reflection at all — a host feeding
    ///         hand-written modules — and it gets <see cref="Stages" />, the same tell
    ///         <see cref="TransformRenderFeature.Draw" /> reads for the same reason.
    ///     </para>
    /// </remarks>
    ShaderStage? StagesFor(RenderDrawContext context) {
        if (context.Effect is not { } effect || effect.SetLayouts.Length == 0) {
            return Stages;
        }

        foreach (var range in effect.PushConstants) {
            if (PreviousOffset >= range.Offset && PreviousOffset + 64 <= range.Offset + range.Size) {
                return range.Stages;
            }
        }

        return null;
    }

    static void Grow(ref Matrix4x4[] array, int length) {
        if (array.Length >= length) {
            return;
        }

        // Zeroed on growth, which is what the M44 test above reads as "no history" — so an object
        // landing in a slot the array has just grown into reports no motion rather than whatever the
        // allocator left there.
        Array.Resize(ref array, Math.Max(length, array.Length * 2));
    }
}
