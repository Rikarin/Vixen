// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Uv;

/// <summary>How a region that failed its distortion bound is broken into smaller ones.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/42 § D3's plug point, and the only one this library has.</b> The charter is
///         PartUV's shape — decompose, flatten, accept or recurse, merge back — and the one part of
///         PartUV that cannot be taken is the top of it, because PartField is a learned decomposition.
///         So the recursion is kept and the decomposition is made replaceable, with a classical
///         concavity-driven default that owes nobody anything.
///     </para>
///     <para>
///         ⚠ <b>The default path never calls an implementation of this.</b> Leaving
///         <see cref="UvSettings.Decomposition" /> null selects the built-in decomposition, which is an
///         approximate convex split over the dual graph weighted by dihedral concavity and surface
///         occlusion. This interface exists so that a learned part field can be dropped in behind it
///         under § D14's rules — <i>it proposes and never decides</i>, because whatever comes back is
///         still flattened, still measured, and still has to pass
///         <see cref="UvSettings.DistortionThreshold" /> before it is kept. A bad decomposition costs
///         chart quality and can never cost validity.
///     </para>
///     <para>
///         ⚠ <b>An implementation may decline, and declining is not a failure.</b> Returning null, an
///         empty list, or anything that does not describe at least two non-empty parts falls back to
///         the built-in decomposition for that region alone. A proposer that only understands some
///         shapes is a useful proposer.
///     </para>
/// </remarks>
public interface IChartDecomposition {
    /// <summary>Breaks a region of faces into parts.</summary>
    /// <param name="mesh">The mesh the faces belong to.</param>
    /// <param name="faces">The region, as face indices in ascending order.</param>
    /// <param name="parts">
    ///     How many parts are wanted. The charter asks for two, because a recursion that halves is what
    ///     bounds its own depth — returning more is allowed and returning fewer declines.
    /// </param>
    /// <returns>
    ///     A part index in <c>[0, parts)</c> per entry of <paramref name="faces" />, or null to decline
    ///     and let the built-in decomposition handle this region.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Determinism is part of the contract, not a quality of the implementation.</b>
    ///     docs/plan/42 § D12 gates byte-identical output for the same input and settings at any thread
    ///     count on any platform, and this call sits upstream of every coordinate in the atlas. An
    ///     implementation that iterates a hash set, consults a clock or restarts randomly breaks that
    ///     gate for the whole library.
    /// </remarks>
    IReadOnlyList<int>? Decompose(EditMesh mesh, IReadOnlyList<int> faces, int parts);
}
