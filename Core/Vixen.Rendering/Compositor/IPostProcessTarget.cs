// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.Compositor;

/// <summary>A node whose parameters a post-process volume may lay an opinion over.</summary>
/// <remarks>
///     <para>
///         <b>The seam a volume reaches the frame through.</b> <c>PostProcessVolumeSystem</c> folds
///         the volumes the camera is inside into one <see cref="PostProcessOverlay" />;
///         <see cref="GraphicsCompositor.Apply" /> walks the frame and hands it to every node that
///         implements this. Which fields a node takes is the node's own business — a tonemap takes
///         the grade and the exposure compensation, a bloom takes the intensity and the threshold,
///         and neither has to know the other exists.
///     </para>
///     <para>
///         <b>Implemented downstream, in <c>Vixen.Rendering.PostFx</c></b> — the same direction
///         <c>ISceneRendererFactory</c> already sends knowledge, and for the same reason: the
///         compositor cannot name those node types without a reference cycle.
///     </para>
///     <para>
///         ⚠ <b>Applied over the node's <em>authored</em> value, never over last frame's.</b> A node
///         that accumulated would drift — two frames standing still inside one volume would apply the
///         same offset twice, and walking out would not undo it. So an implementation keeps what the
///         document gave it and treats the overlay as an overlay: <c>Blended.Over(authored)</c>, every
///         frame, from the same starting point.
///     </para>
///     <para>
///         ⚠ <b>Every node that wants a field gets it, and no name matching happens.</b> A frame with
///         two <c>!Bloom</c> nodes has both brightened by a volume that says the bloom is stronger
///         here, which is what "in this room the glow is stronger" means. Matching by node name was
///         the other option and it is worse: it makes a volume depend on what a document happened to
///         call its nodes, so renaming a node in a compositor would silently unwire every volume in
///         every level.
///     </para>
/// </remarks>
public interface IPostProcessTarget {
    /// <summary>Takes whatever of this frame's overlay applies to this node.</summary>
    /// <param name="overlay">
    ///     The fold of every volume reaching the camera. <see cref="PostProcessOverlay.IsEmpty" />
    ///     for a frame with no volumes, which is the case an implementation should make free.
    /// </param>
    void Apply(in PostProcessOverlay overlay);
}
