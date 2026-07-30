// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>
///     Each view's matrix from the frame a depth pyramid was built in, which is the only matrix its
///     rectangle may be projected with.
/// </summary>
/// <remarks>
///     <para>
///         <b>Its own type because there are two consumers of it.</b> The object cull
///         (<see cref="GpuVisibilityGroup" />) and the cluster traversal
///         (<see cref="GpuClusterVisibility" />) both test spheres against the same pyramid with the
///         same matrix, and both have to answer the same awkward question first: is this view one whose
///         matrix I remember? Two copies of that rule is two places for it to be relaxed — and the way
///         it gets relaxed is by projecting a view's bound with a matrix that belonged to a different
///         view, which occludes geometry by arithmetic that was never about it.
///     </para>
///     <para>
///         <b>Dropped whole whenever the number of views changes.</b> "Index two" meaning "the second
///         shadow cascade" is a convention of the host's, and a frame that inserted a view has
///         renumbered everything after it — so a remembered matrix at an index is only about the same
///         view if the shape of the list has not moved. That is conservative in the right direction:
///         the frame after a view list changes is frustum-only rather than wrong.
///     </para>
/// </remarks>
public sealed class OccluderProjections {
    Matrix4x4[] matrices = [];
    bool[] usable = [];
    int remembered = -1;

    /// <summary>How many views were remembered, or <c>-1</c> if none have been.</summary>
    public int Count => remembered;

    /// <summary>Whether this many views is the shape that was remembered.</summary>
    /// <param name="viewCount">How many views the frame has.</param>
    /// <remarks>
    ///     What a caller checks before it decides a frame can be occlusion tested at all. A frame whose
    ///     view list changed shape has nothing usable here, whatever the pyramid says.
    /// </remarks>
    public bool Matches(int viewCount) => remembered == viewCount;

    /// <summary>Records the matrices the next frame's occlusion test will project with.</summary>
    /// <param name="frameViews">This frame's views, in this frame's order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frameViews" /> is null.</exception>
    public void Remember(IReadOnlyList<RenderView> frameViews) {
        ArgumentNullException.ThrowIfNull(frameViews);

        if (matrices.Length < frameViews.Count) {
            Array.Resize(ref matrices, frameViews.Count);
            Array.Resize(ref usable, frameViews.Count);
        }

        for (var i = 0; i < frameViews.Count; i++) {
            matrices[i] = frameViews[i].ViewProjection;

            // A view whose matrix was never set — a caller that supplied a frustum and nothing else —
            // leaves identity behind, which projects a scene into a rectangle that means nothing.
            // Better to skip occlusion for it than to occlude by arithmetic about nowhere.
            usable[i] = matrices[i] != Matrix4x4.Identity;
        }

        remembered = frameViews.Count;
    }

    /// <summary>The matrix a view's bound may be projected with, if there is one.</summary>
    /// <param name="viewIndex">Which view.</param>
    /// <param name="projection">Its matrix from the frame the pyramid was built in.</param>
    /// <returns>Whether this view can be occlusion tested at all.</returns>
    public bool TryGet(int viewIndex, out Matrix4x4 projection) {
        if (viewIndex >= 0 && viewIndex < usable.Length && usable[viewIndex]) {
            projection = matrices[viewIndex];
            return true;
        }

        projection = default;

        return false;
    }

    /// <summary>Forgets everything, so the next frame is frustum-only.</summary>
    public void Forget() {
        remembered = -1;
        Array.Clear(usable);
    }
}
