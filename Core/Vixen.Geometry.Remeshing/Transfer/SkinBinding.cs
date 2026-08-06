// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Geometry.Remeshing;

/// <summary>One bone's hold on one vertex.</summary>
/// <param name="Bone">Which bone, indexed as the caller's skeleton is. Never interpreted here.</param>
/// <param name="Weight">How much of the vertex it moves, in <c>[0, 1]</c>.</param>
/// <remarks>
///     ⚠ <b>The bone index is opaque and that is deliberate.</b> A remesher that understood a
///     skeleton would need one, and <c>docs/plan/41-automatic-retopology.md</c> § D2 puts this
///     assembly below anything that has one. A weight is a number attached to an integer; the
///     integer means whatever the caller's rig says it means, and it survives the remesh unchanged.
/// </remarks>
public readonly record struct SkinInfluence(int Bone, float Weight);

/// <summary>Skinning weights for a whole mesh, as a flat block with a fixed stride.</summary>
/// <remarks>
///     <para>
///         <b><see cref="EditMesh" /> has no weight channel, so the transfer returns one
///         alongside the mesh rather than inside it.</b> That is the choice
///         <c>docs/plan/41-automatic-retopology.md</c> § D12 leaves open and it is made here:
///         parallel arrays, indexed by the mesh's own position indices, so a caller holds the mesh
///         and the binding together and neither one knows about the other. Adding a channel to the
///         mesh kernel instead would put a rig's vocabulary into doc 24's geometry types, which have
///         four consumers that have never heard of a bone.
///     </para>
///     <para>
///         ⚠ <b>Per <i>position</i>, not per corner — unlike normals and texture coordinates.</b> A
///         seam is two corners at one position carrying different coordinates, which is a statement
///         about the surface's parameterisation. A weight is a statement about where the surface
///         <i>goes</i>, and two corners at one position that moved differently would tear the mesh.
///     </para>
///     <para>
///         ⚠ <b>Flat with a stride rather than jagged, because a jagged array is a heap allocation
///         per vertex</b> and because the stride <i>is</i> the influence limit a target declares.
///         Unused slots are <c>(0, 0)</c> and a reader stops at the first zero weight — every
///         producer here sorts by descending weight, so the zeros are always at the end.
///     </para>
/// </remarks>
public sealed record SkinBinding {
    /// <summary>Every influence, <see cref="Stride" /> of them per vertex, in vertex order.</summary>
    public required IReadOnlyList<SkinInfluence> Influences { get; init; }

    /// <summary>How many influences each vertex has room for. Four is the usual answer.</summary>
    public required int Stride { get; init; }

    /// <summary>How many vertices the block covers.</summary>
    public int VertexCount => Stride > 0 ? Influences.Count / Stride : 0;

    /// <summary>One vertex's influences, including the zero-weight padding.</summary>
    /// <param name="vertex">Which vertex.</param>
    /// <returns>Exactly <see cref="Stride" /> influences, sorted by descending weight.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The vertex is not in the block.</exception>
    public IEnumerable<SkinInfluence> At(int vertex) {
        ArgumentOutOfRangeException.ThrowIfNegative(vertex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(vertex, VertexCount);

        for (var slot = 0; slot < Stride; slot++) {
            yield return Influences[(vertex * Stride) + slot];
        }
    }

    /// <summary>What one vertex's weights add up to.</summary>
    /// <param name="vertex">Which vertex.</param>
    /// <returns>The sum, which is one for a bound vertex and zero for an unbound one.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The vertex is not in the block.</exception>
    /// <remarks>
    ///     ⚠ <b>Zero is a real answer and means "not bound to anything", not "an error".</b> A mesh
    ///     with a rigged body and an unrigged prop in it produces both, and a transfer that
    ///     normalised the prop's zeros into an arbitrary bone would attach it to the pelvis.
    /// </remarks>
    public float Total(int vertex) {
        ArgumentOutOfRangeException.ThrowIfNegative(vertex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(vertex, VertexCount);

        var sum = 0f;

        for (var slot = 0; slot < Stride; slot++) {
            sum += Influences[(vertex * Stride) + slot].Weight;
        }

        return sum;
    }
}
