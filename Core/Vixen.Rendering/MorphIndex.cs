// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>
///     A mesh's blend shapes re-indexed by vertex, so a vertex can be morphed by a gather instead of
///     by a scatter.
/// </summary>
/// <remarks>
///     <para>
///         <b>The form <c>MorphTargetData</c> is not, and the reason is the paged path.</b> A target
///         is a run of <c>(vertex, Δ)</c> sorted by vertex, which is what a scatter wants: one
///         invocation per entry, writing the vertex it names. <c>MorphRenderFeature</c> dispatches
///         exactly that, once per active shape, into a vertex buffer it owns per instance.
///     </para>
///     <para>
///         ⚠ <b>A virtualized mesh has no such buffer and cannot be given one.</b> Its vertices live
///         in <c>MeshletPageSet</c>'s pages, and a page is <em>per mesh</em> — every instance of a
///         head reads the same bytes out of the same pool slot, because that sharing is what makes
///         streaming a hundred thousand clusters affordable. Weights are per instance. So there is
///         nowhere for a per-instance scatter to write, short of giving every instance a private copy
///         of every resident page it touches, and that is the one property the whole phase is built
///         on. See <a href="../../docs/plan/22-virtualized-geometry.md">doc 22</a> phase 2.
///     </para>
///     <para>
///         <b>What the paged path does instead is what it already does for skinning: a gather, in the
///         shader, per instance.</b> <c>ClusterRaster</c> decodes a page vertex and then transforms it
///         by that instance's bone palette — shared bytes, per-instance parameters, no intermediate
///         buffer. Morphing is the same shape, and this is the table it needs: given a mesh vertex,
///         which shapes move it and by how much.
///     </para>
///     <para>
///         ⚠ <b>A gather has no race and needs no barrier, which is the one thing it is strictly
///         better at.</b> <c>MorphKernel</c> has to dispatch per target because two shapes may move
///         one vertex and there is no float atomic to arbitrate them; a gather sums a vertex's own
///         shapes in one invocation and the question never arises. What it pays for that is the
///         re-indexing — this type — and doing the sum again in every stage that decodes a vertex,
///         which for the paged path is three shaders rather than the classic path's one pre-pass.
///     </para>
///     <para>
///         <b>Built at registration rather than at import, and that is deliberate.</b> Everything here
///         is derived from <c>MeshData.MorphTargets</c>, which a build already ships; deriving it
///         offline would be a second artefact, a second version fence and a second thing that can be
///         stale against the first. It is O(entries) once per mesh, on the frame the mesh is
///         registered, against deltas that were just deserialised.
///     </para>
/// </remarks>
public sealed class MorphIndex {
    /// <summary>How many 32-bit words one entry occupies, which is <c>MorphKernel.EntryWords</c>.</summary>
    /// <remarks>
    ///     Four, and the same four: the last three words are <c>MorphKernel.Pack</c>'s quantised
    ///     components, bit for bit. Only the first word differs — a scatter entry names the
    ///     <em>vertex</em> it writes and a gather entry names the <em>shape</em> it came from, because
    ///     the thing the reader already knows is the other one.
    /// </remarks>
    public const int EntryWords = MorphKernel.EntryWords;

    MorphIndex(uint[] runs, uint[] entries, float[] positionSteps, float[] normalSteps, int vertexCount) {
        Runs = runs;
        Entries = entries;
        PositionSteps = positionSteps;
        NormalSteps = normalSteps;
        VertexCount = vertexCount;
    }

    /// <summary>
    ///     Where each vertex's entries start, with a trailing total — <c>VertexCount + 1</c> of them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A prefix rather than a packed <c>(first, count)</c> pair, which costs a second adjacent
    ///         read on the device and buys the absence of a limit. Packing a count into eight bits
    ///         and a first into twenty-four would work for every mesh anyone has, and the mesh it did
    ///         not work for would be silently wrong rather than refused — a vertex touched by a
    ///         two-hundred-and-fifty-sixth shape simply losing it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A vertex no shape moves has <c>Runs[v] == Runs[v + 1]</c> and costs nothing but
    ///         the two reads.</b> That is most of a face and all of a body, and it is why the table is
    ///         indexed by vertex rather than searched: the common answer is "none" and it is one
    ///         comparison away.
    ///     </para>
    /// </remarks>
    public uint[] Runs { get; }

    /// <summary>Every entry, four words each, grouped by the vertex they move.</summary>
    /// <remarks>
    ///     Word zero is the shape's slot in <c>MeshData.MorphTargets</c> — which is the slot
    ///     <c>BlendShapeWeights.Weights</c> is indexed by, and not the ordinal the source file used.
    ///     Words one to three are the quantised deltas exactly as <c>MorphKernel.Pack</c> writes them,
    ///     so the two forms cannot round differently.
    /// </remarks>
    public uint[] Entries { get; }

    /// <summary>What one quantised position unit is worth, per shape.</summary>
    /// <remarks>
    ///     <c>MorphKernel.Step</c> of each target's <c>PositionScale</c>, computed on the host for the
    ///     reason that one gives: one division here is one float both processors then agree about.
    /// </remarks>
    public float[] PositionSteps { get; }

    /// <summary>What one quantised normal unit is worth, per shape.</summary>
    public float[] NormalSteps { get; }

    /// <summary>How many vertices the mesh has, which is <see cref="Runs" />'s length less one.</summary>
    public int VertexCount { get; }

    /// <summary>How many shapes it has.</summary>
    public int ShapeCount => PositionSteps.Length;

    /// <summary>How many entries there are in total.</summary>
    public int EntryCount => Entries.Length / EntryWords;

    /// <summary>
    ///     How far the mesh's shapes can move a vertex, if every one of them were at full weight.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The sum over shapes of the largest position delta any of them has, which is a bound on
    ///         <c>|Σ wᵢ·Δpᵢ|</c> for weights in <c>[-1, 1]</c>. What it is for is
    ///         <c>Meshlet.Bounds</c>: a traversal culls and picks a level by a cluster's rest-pose
    ///         bound, and a jaw that drops out of that bound is culled with its mouth open.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A weight past one is applied and is not bounded by this.</b> Every weight is
    ///         applied, including one past one — an animator overshooting a corrective relies on it —
    ///         and a bound computed at full weight does not cover it. The failure is a cluster culled
    ///         a frame early at the silhouette, not corruption, and the alternative is an unbounded
    ///         inflation that costs every frame for a case no exporter produces.
    ///     </para>
    /// </remarks>
    public float MaxDisplacement { get; private set; }

    /// <summary>Re-indexes a mesh's shapes by vertex.</summary>
    /// <param name="targets">The mesh's blend shapes, in <c>MeshData.MorphTargets</c> order.</param>
    /// <param name="vertexCount">How many vertices the mesh has.</param>
    /// <returns>The table, or null when there is nothing to morph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="targets" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="vertexCount" /> is negative.</exception>
    /// <exception cref="ArgumentException">A target moves a vertex the mesh does not have.</exception>
    /// <remarks>
    ///     ⚠ <b>Null for a mesh with no shapes, or with shapes that move nothing.</b> The caller's
    ///     branch is "is this mesh morphed at all", and a table of zero entries would answer yes and
    ///     then cost every vertex of every instance two buffer reads to say no.
    /// </remarks>
    public static MorphIndex? Build(IReadOnlyList<MorphTargetData> targets, int vertexCount) {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);

        var total = 0;

        foreach (var target in targets) {
            total += target.Count;
        }

        if (total == 0) {
            return null;
        }

        // Counting sort by vertex, which is what makes this linear: a target is already sorted by
        // vertex and the union of them is not, so the alternative is sorting entries entries times.
        var runs = new uint[vertexCount + 1];

        foreach (var target in targets) {
            foreach (var vertex in target.Indices) {
                if ((uint)vertex >= (uint)vertexCount) {
                    throw new ArgumentException(
                        $"'{target.Name}' moves vertex {vertex} and the mesh has {vertexCount}. The "
                        + "target belongs to a different mesh, or to a version of it from before an "
                        + "edit changed the vertex count.",
                        nameof(targets)
                    );
                }

                runs[vertex + 1]++;
            }
        }

        for (var vertex = 0; vertex < vertexCount; vertex++) {
            runs[vertex + 1] += runs[vertex];
        }

        var entries = new uint[total * EntryWords];
        var cursor = new uint[vertexCount];

        runs.AsSpan(0, vertexCount).CopyTo(cursor);

        var positionSteps = new float[targets.Count];
        var normalSteps = new float[targets.Count];
        var displacement = 0f;

        for (var shape = 0; shape < targets.Count; shape++) {
            var target = targets[shape];

            positionSteps[shape] = MorphKernel.Step(target.PositionScale);
            normalSteps[shape] = MorphKernel.Step(target.NormalScale);

            var reach = 0f;

            for (var entry = 0; entry < target.Count; entry++) {
                var at = (int)cursor[target.Indices[entry]]++ * EntryWords;

                entries[at + 0] = (uint)shape;

                // ⚠ The deltas are copied word for word out of the scatter's own packing rather than
                // re-derived from the shorts, so the gather and the scatter cannot disagree by a
                // rounding. MorphKernel.Pack is the one place the bits are decided.
                MorphKernel.PackEntry(target, entry, entries.AsSpan(at + 1, EntryWords - 1));

                reach = MathF.Max(reach, target.PositionDelta(entry).Length());
            }

            displacement += reach;
        }

        return new(runs, entries, positionSteps, normalSteps, vertexCount) { MaxDisplacement = displacement };
    }

    /// <summary>
    ///     Morphs one vertex by gathering its own shapes — the arithmetic the three cluster shaders do.
    /// </summary>
    /// <param name="vertex">Which mesh vertex.</param>
    /// <param name="weights">One per shape. Shorter is read as zero for the rest.</param>
    /// <param name="position">Its position, moved in place.</param>
    /// <param name="normal">Its normal, moved in place and not renormalised.</param>
    /// <exception cref="ArgumentOutOfRangeException">There is no such vertex.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Written here so the shaders can be checked against it</b> rather than against each
    ///         other — <c>MeshletPageSet.GetPositions</c>'s argument, and <c>MorphKernel</c>'s, and it
    ///         is the same argument: two shaders that agree with a third party agree with each other.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This and <c>MorphKernel.Apply</c> agree to within float summation order and not
    ///         exactly.</b> The scatter adds shape by shape across the whole mesh and the gather adds
    ///         shape by shape within one vertex, so the same terms are summed in a different order.
    ///         A test that asserts bit equality between the two paths is asserting something neither
    ///         path promises.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing renormalises the normal</b>, for <c>MorphKernel</c>'s two reasons: a shape
    ///         may cancel one exactly, and <c>Vector3.Normalize</c> gives up below an absolute
    ///         <c>1e-6</c>. The consumers already <c>SafeNormalize</c> at <c>1e-4</c>.
    ///     </para>
    /// </remarks>
    public void Apply(int vertex, ReadOnlySpan<float> weights, ref Vector3 position, ref Vector3 normal) {
        ArgumentOutOfRangeException.ThrowIfNegative(vertex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(vertex, VertexCount);

        var first = Runs[vertex];
        var last = Runs[vertex + 1];

        for (var index = first; index < last; index++) {
            var at = (int)index * EntryWords;
            var shape = (int)Entries[at];
            var weight = shape < weights.Length ? weights[shape] : 0f;

            // ⚠ Skipped rather than added, which is what MorphKernel.Apply does with an inactive
            // shape — so the reference does not quietly touch a vertex a zero-weighted shape names.
            if (weight == 0f) {
                continue;
            }

            // Six shorts across three words, MorphKernel.Pack's layout: the position occupies the
            // first word and a half and the normal the last word and a half.
            position += new Vector3(Low(at + 1), High(at + 1), Low(at + 2)) * PositionSteps[shape] * weight;
            normal += new Vector3(High(at + 2), Low(at + 3), High(at + 3)) * NormalSteps[shape] * weight;
        }
    }

    short Low(int word) => (short)(Entries[word] & 0xFFFF);

    short High(int word) => (short)(Entries[word] >> 16);
}
