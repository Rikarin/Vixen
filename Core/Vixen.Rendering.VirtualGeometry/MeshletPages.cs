// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>Where one cluster's geometry sits inside a page, and how to decode it.</summary>
/// <remarks>
///     <para>
///         Deliberately <em>not</em> the cluster record. <see cref="Meshlet" /> carries what a
///         traversal needs to decide whether to draw a cluster — bounds, cone, error, parent error —
///         and all of that stays resident, because a traversal has to test a cluster before it can
///         know whether it wants its geometry. This is the other half: where the bytes are, which
///         page they are in, and the origin they are quantized against. It is meaningless until that
///         page is resident, which is exactly the distinction the whole phase turns on.
///     </para>
/// </remarks>
[DataContract("MeshletPageCluster")]
public readonly record struct MeshletPageCluster {
    /// <summary>An unplaced cluster, for the serializer.</summary>
    public MeshletPageCluster() { }

    /// <summary>Which page holds this cluster's geometry.</summary>
    public int Page { get; init; }

    /// <summary>Where its vertices start, in bytes from the page's own start.</summary>
    public int VertexOffset { get; init; }

    /// <summary>Where its triangle corners start, in bytes from the page's own start.</summary>
    /// <remarks>
    ///     After the vertices rather than in a section of their own. A cluster is placed, streamed
    ///     and evicted as one run of bytes, so splitting it into two runs would double every
    ///     placement's bookkeeping to save nothing.
    /// </remarks>
    public int TriangleOffset { get; init; }

    /// <summary>
    ///     The grid coordinate its quantized positions are relative to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Integer coordinates on the mesh-wide grid, not a position. That is what makes the
    ///         encoding exact: a vertex's global grid coordinate is <c>Origin + stored</c> in whole
    ///         numbers, so two clusters sharing a boundary vertex decode it to the same bits however
    ///         far apart their origins are.
    ///     </para>
    ///     <para>
    ///         See <see cref="MeshletPageSet.QuantizationStep" /> for why the grid is the mesh's
    ///         rather than each cluster's, which is the one decision in this format that cracks the
    ///         mesh if it is made the obvious way.
    ///     </para>
    /// </remarks>
    public Int3 Origin { get; init; }
}

/// <summary>One fixed-size page: a run of whole clusters' geometry, streamed and evicted together.</summary>
[DataContract("MeshletPage")]
public readonly record struct MeshletPage {
    /// <summary>An empty page, for the serializer.</summary>
    public MeshletPage() { }

    /// <summary>Where the page's bytes start in <see cref="MeshletPageSet.Data" />.</summary>
    public int Offset { get; init; }

    /// <summary>How many bytes it actually uses, which is at most the page size.</summary>
    /// <remarks>
    ///     The tail of the last page of a mesh is short, and streaming the padding would be reading
    ///     bytes nothing decodes. The <em>slot</em> a page occupies in the pool is the full page
    ///     size, because a pool of fixed slots is what makes eviction a free list rather than a
    ///     defragmenter.
    /// </remarks>
    public int Size { get; init; }

    /// <summary>The first cluster it holds.</summary>
    public int FirstCluster { get; init; }

    /// <summary>How many clusters it holds.</summary>
    public int ClusterCount { get; init; }

    /// <summary>The coarsest level any of its clusters is at.</summary>
    /// <remarks>
    ///     What a residency policy prioritises by when it has to choose. A coarse page is wanted by
    ///     every view that can see the object at all; a fine one only by a view that is close to it.
    /// </remarks>
    public int CoarsestLevel { get; init; }
}

/// <summary>A cluster DAG packed into pages: the runtime form of <see cref="MeshletMesh" />.</summary>
/// <remarks>
///     <para>
///         <b>Phase 2 of <c>docs/plan/22-virtualized-geometry.md</c>, the offline half.</b> What phase 1
///         produced is a DAG whose clusters index one mesh-wide vertex array — relocatable, but not
///         streamable: nothing in it can be loaded without loading all of it. This is the same DAG
///         with its geometry cut into fixed-size pages that can be.
///     </para>
///     <para>
///         <b>The records stay resident and only the geometry streams.</b>
///         <see cref="MeshletMesh.Meshlets" /> is the hierarchy — bounds, cones, errors, the group
///         links — and a traversal has to walk it to find out which clusters it wants, so it cannot
///         itself be paged. It is also small: sixty-odd bytes against a cluster's two kilobytes of
///         geometry, which is the ratio that makes the split obviously right rather than a
///         compromise.
///     </para>
///     <para>
///         <b>Page zero holds the roots and is pinned.</b> That is what makes a never-streamed
///         object draw: an object whose pages have not arrived, or whose budget will not stretch to
///         them, is drawn at its coarsest level rather than not at all. It is also what makes
///         streaming <em>degrade</em> rather than fail — see
///         <c>MeshletCut.SelectByError</c>'s residency-aware overload.
///     </para>
/// </remarks>
[DataContract("MeshletPageSet")]
public sealed record MeshletPageSet {
    /// <summary>The pages, coarsest first.</summary>
    public MeshletPage[] Pages { get; init; } = [];

    /// <summary>Where each cluster's geometry is, indexed as <see cref="MeshletMesh.Meshlets" /> is.</summary>
    public MeshletPageCluster[] Clusters { get; init; } = [];

    /// <summary>Every page's bytes, concatenated. One page is <see cref="PageSize" /> bytes of it.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>How many bytes one page occupies in the pool.</summary>
    public int PageSize { get; init; }

    /// <summary>How many bytes one page vertex occupies: six of position, then the attributes.</summary>
    public int VertexStride { get; init; }

    /// <summary>
    ///     Where a vertex's four bone influences start, in bytes from the vertex, or −1 for a mesh
    ///     that has none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A per-mesh offset rather than a fixed one, because a static mesh must not pay for
    ///         it.</b> Influences are eight bytes — four indices and four weights — on a vertex that
    ///         is otherwise sixteen, so charging every mesh for them would be half again the bytes of
    ///         every page of every rock in the project to carry zeros. The stride is already per
    ///         mesh, so this costs one more integer beside it.
    ///     </para>
    ///     <para>
    ///         <b>And it cannot be a shader permutation, which is what makes it a record.</b> A
    ///         classic pass picks its skinned variant per draw — see <c>ShadowCaster</c>'s
    ///         <c>Skinned</c>. One indirect draw covers every cluster of every mesh in the scene, so
    ///         there is no per-draw anything to pick with, and "is this vertex skinned" has to be a
    ///         question the vertex's own mesh record answers.
    ///     </para>
    /// </remarks>
    public int InfluenceOffset { get; init; } = -1;

    /// <summary>Whether the vertices carry bone influences.</summary>
    public bool IsSkinned => InfluenceOffset >= 0;

    /// <summary>The corner of the mesh-wide quantization grid.</summary>
    public Vector3 QuantizationOrigin { get; init; }

    /// <summary>
    ///     How far apart the grid's points are, in object space.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>One grid for the mesh, not one per cluster, and that is the whole of why this
    ///         format does not crack.</b> Quantizing each cluster against its own bound is the
    ///         obvious way to spend the bits well, and it is wrong: a vertex on a locked boundary is
    ///         referenced by a cluster on each side, the two clusters have different bounds, and the
    ///         same position rounds to two different numbers. Phase 1 went to some trouble to make
    ///         that boundary <em>bit-identical</em> between a parent and its children — collapsing
    ///         onto existing vertices rather than onto a quadric's optimum, precisely so the lock
    ///         would hold exactly — and a per-cluster grid throws that away in the last step before
    ///         the device sees it.
    ///     </para>
    ///     <para>
    ///         Uniform across the three axes, so the error a vertex picks up does not depend on
    ///         which way the mesh happens to be oriented. Sixteen bits across the mesh's longest
    ///         extent, which is what makes every cluster's local coordinates fit in sixteen bits by
    ///         construction: the coarsest cluster there can be spans the whole mesh, and the whole
    ///         mesh is 65535 steps.
    ///     </para>
    /// </remarks>
    public float QuantizationStep { get; init; }

    /// <summary>
    ///     The most a vertex can have moved by being quantized: half a step, in object space.
    /// </summary>
    /// <remarks>
    ///     A floor under every level's error, including level zero's — which phase 1 reports as
    ///     exactly zero, because level zero <em>is</em> the original mesh. After this it is not, by
    ///     up to this much. A cut asked for a threshold below it is asking for a precision the pages
    ///     do not carry, and <see cref="MeshletCut" /> says so rather than pretending.
    /// </remarks>
    public float QuantizationError => QuantizationStep * 0.5f;

    /// <summary>How many bytes the pages occupy in total.</summary>
    public long TotalBytes => (long)Pages.Length * PageSize;

    /// <summary>Whether the geometry is here, as opposed to in a blob beside it.</summary>
    /// <remarks>
    ///     False for the set a build ships and a runtime loads — see <see cref="WithoutData" />. The
    ///     decoders below are the offline half and need the bytes; the traversal is the runtime half
    ///     and needs none of them, which is the split the whole phase is about.
    /// </remarks>
    public bool HasData => Data.Length > 0;

    /// <summary>
    ///     The same set with the geometry removed, for a build that ships the bytes separately.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What makes streaming possible at all.</b> A set carrying its own
    ///         <see cref="Data" /> is a single artefact, and deserialising it reads every page — which
    ///         is the one thing paging exists to avoid. So a build writes this beside the bytes: the
    ///         records, which are small and stay resident, and a blob a page source seeks into.
    ///     </para>
    ///     <para>
    ///         <see cref="OffsetOf" /> is what makes the blob seekable without an index, and the
    ///         reason <see cref="MeshletPageBuilder" /> pads every page out to
    ///         <see cref="PageSize" /> rather than packing them tight.
    ///     </para>
    /// </remarks>
    public MeshletPageSet WithoutData() => this with { Data = [] };

    /// <summary>Where a page's bytes start in the blob, whether or not the blob is here.</summary>
    /// <param name="page">Which page.</param>
    /// <remarks>
    ///     Arithmetic rather than a lookup, because <see cref="MeshletPageBuilder" /> pads each page
    ///     out to a whole slot — so a file of pages has the same shape as the pool they load into and
    ///     a source needs no table to find one. <see cref="MeshletPage.Offset" /> agrees with this for
    ///     a set that still has its data; a set that does not still answers, which is the point.
    /// </remarks>
    public long OffsetOf(int page) {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, Pages.Length);

        return (long)page * PageSize;
    }

    /// <summary>The bytes of one page, ready to be handed to a pool.</summary>
    /// <param name="page">Which page.</param>
    /// <returns>Its used bytes, which may be shorter than <see cref="PageSize" /> for the last one.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such page.</exception>
    /// <exception cref="InvalidOperationException">The geometry is not here — see <see cref="HasData" />.</exception>
    public ReadOnlySpan<byte> BytesOf(int page) {
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, Pages.Length);

        if (!HasData) {
            throw new InvalidOperationException(
                "This page set carries no geometry, so a page's bytes have to be read from the blob it "
                + "was built beside. See MeshletPageSet.WithoutData."
            );
        }

        return Data.AsSpan(Pages[page].Offset, Pages[page].Size);
    }

    /// <summary>
    ///     One cluster's positions, decoded back to object space.
    /// </summary>
    /// <param name="cluster">Which cluster, indexed as <see cref="MeshletMesh.Meshlets" /> is.</param>
    /// <param name="vertexCount">How many vertices it has, from its <see cref="Meshlet" />.</param>
    /// <param name="positions">Where to write them.</param>
    /// <exception cref="ArgumentException">The span is too small.</exception>
    /// <remarks>
    ///     The arithmetic a resolve shader will do per vertex, written once here so the shader can be
    ///     checked against it rather than against a second derivation of the same encoding — the same
    ///     argument <see cref="MeshletCut.PixelError" /> makes.
    /// </remarks>
    public void GetPositions(int cluster, int vertexCount, Span<Vector3> positions) {
        ArgumentOutOfRangeException.ThrowIfNegative(cluster);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cluster, Clusters.Length);

        if (positions.Length < vertexCount) {
            throw new ArgumentException("Too small to hold the cluster's positions.", nameof(positions));
        }

        var placement = Clusters[cluster];
        var start = Pages[placement.Page].Offset + placement.VertexOffset;

        for (var i = 0; i < vertexCount; i++) {
            var at = start + (i * VertexStride);

            var x = placement.Origin.X + BitConverter.ToUInt16(Data, at);
            var y = placement.Origin.Y + BitConverter.ToUInt16(Data, at + 2);
            var z = placement.Origin.Z + BitConverter.ToUInt16(Data, at + 4);

            positions[i] = QuantizationOrigin + (new Vector3(x, y, z) * QuantizationStep);
        }
    }

    /// <summary>
    ///     One cluster's bone influences, decoded.
    /// </summary>
    /// <param name="cluster">Which cluster, indexed as <see cref="MeshletMesh.Meshlets" /> is.</param>
    /// <param name="vertexCount">How many vertices it has, from its <see cref="Meshlet" />.</param>
    /// <param name="influences">Where to write them.</param>
    /// <exception cref="ArgumentException">The span is too small.</exception>
    /// <exception cref="InvalidOperationException">The mesh is not skinned — see <see cref="IsSkinned" />.</exception>
    /// <remarks>
    ///     <see cref="GetPositions" />'s counterpart and it exists for the same reason: this is the
    ///     arithmetic the raster and the resolve each do per vertex, written once so both can be
    ///     checked against it rather than against each other. Two shaders that agree with a third
    ///     party agree with each other; two that are only ever compared with each other agree on
    ///     whatever they both get wrong.
    /// </remarks>
    public void GetInfluences(int cluster, int vertexCount, Span<VertexInfluence> influences) {
        ArgumentOutOfRangeException.ThrowIfNegative(cluster);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cluster, Clusters.Length);

        if (!IsSkinned) {
            throw new InvalidOperationException(
                "This page set carries no bone influences, so its vertices have none to decode. See "
                + "MeshletPageSet.IsSkinned."
            );
        }

        if (influences.Length < vertexCount) {
            throw new ArgumentException("Too small to hold the cluster's influences.", nameof(influences));
        }

        var placement = Clusters[cluster];
        var start = Pages[placement.Page].Offset + placement.VertexOffset + InfluenceOffset;

        for (var i = 0; i < vertexCount; i++) {
            var at = start + (i * VertexStride);

            influences[i] = new(
                new(Data[at], Data[at + 1], Data[at + 2], Data[at + 3]),
                new Vector4(Data[at + 4], Data[at + 5], Data[at + 6], Data[at + 7]) / byte.MaxValue
            );
        }
    }

    /// <summary>One cluster's triangle corners, as indices into its own vertex list.</summary>
    /// <param name="cluster">Which cluster.</param>
    /// <param name="triangleCount">How many triangles it has, from its <see cref="Meshlet" />.</param>
    /// <returns>Three bytes per triangle.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such cluster.</exception>
    public ReadOnlySpan<byte> GetCorners(int cluster, int triangleCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(cluster);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cluster, Clusters.Length);

        var placement = Clusters[cluster];
        return Data.AsSpan(Pages[placement.Page].Offset + placement.TriangleOffset, triangleCount * 3);
    }
}

/// <summary>The four bones one page vertex is weighted to, decoded.</summary>
/// <param name="Bones">Their indices into the mesh's skeleton, in the order the weights are.</param>
/// <param name="Weights">Their weights, as stored — which is to say not exactly summing to one.</param>
/// <remarks>
///     <para>
///         <b>A byte an index and a byte a weight, which is not a compromise.</b>
///         <c>Skinning.MaxBones</c> is 256 because 256 <c>mat4</c>s is exactly the 16 KB of uniform
///         range Vulkan guarantees — so a byte indexes the largest palette the engine has, exactly. And
///         a weight quantized to a 255th is finer than the position it is applied to, which a page
///         stores as a 65535th of the mesh's longest extent: a character two metres tall carries
///         positions to a thirtieth of a millimetre, and the weight error moves a vertex by less than
///         that.
///     </para>
///     <para>
///         Four influences, matching <c>BonePalette</c> and the glTF the importer reads. The fifth is
///         below the weight threshold in every asset that has one.
///     </para>
/// </remarks>
public readonly record struct VertexInfluence(Int4 Bones, Vector4 Weights) {
    /// <summary>The weights, normalised to sum to one.</summary>
    /// <remarks>
    ///     The mirror of <c>Skinning.NormalizeWeights</c>, and it is done rather than trusted for the
    ///     reason that one gives: quantized weights do not sum to exactly one, and the residue shows
    ///     as a uniform deflation toward the origin that reads as a scale bug.
    /// </remarks>
    public Vector4 Normalized {
        get {
            var total = Weights.X + Weights.Y + Weights.Z + Weights.W;
            return total > MathUtil.ZeroTolerance ? Weights / total : new(1f, 0f, 0f, 0f);
        }
    }

    /// <summary>The one matrix this vertex is transformed by.</summary>
    /// <param name="palette">Every skeleton's matrices, as the device sees them.</param>
    /// <param name="firstBone">Where this instance's own palette starts in it.</param>
    /// <returns>The blend.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An influence reaches outside the palette.</exception>
    /// <remarks>
    ///     Linear blend skinning, mirroring <c>Skinning.BlendMatrix</c> — interpolate the matrices,
    ///     then transform once. Written here so a test can ask whether the shaders agree with the
    ///     definition rather than with each other.
    /// </remarks>
    public Matrix4x4 Blend(ReadOnlySpan<Matrix4x4> palette, int firstBone) {
        var w = Normalized;

        var bone0 = palette[Bone(firstBone, Bones.X, palette.Length)];
        var bone1 = palette[Bone(firstBone, Bones.Y, palette.Length)];
        var bone2 = palette[Bone(firstBone, Bones.Z, palette.Length)];
        var bone3 = palette[Bone(firstBone, Bones.W, palette.Length)];

        return new(
            (bone0.Row1 * w.X) + (bone1.Row1 * w.Y) + (bone2.Row1 * w.Z) + (bone3.Row1 * w.W),
            (bone0.Row2 * w.X) + (bone1.Row2 * w.Y) + (bone2.Row2 * w.Z) + (bone3.Row2 * w.W),
            (bone0.Row3 * w.X) + (bone1.Row3 * w.Y) + (bone2.Row3 * w.Z) + (bone3.Row3 * w.W),
            (bone0.Row4 * w.X) + (bone1.Row4 * w.Y) + (bone2.Row4 * w.Z) + (bone3.Row4 * w.W)
        );
    }

    static int Bone(int first, int index, int count) {
        var at = first + index;
        ArgumentOutOfRangeException.ThrowIfNegative(at);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(at, count);

        return at;
    }
}
