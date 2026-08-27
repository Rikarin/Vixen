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
    /// <summary>How many shapes may move one vertex.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The device's gather is a counted loop, and this is its bound.</b> A vertex whose
    ///         run were longer would have its remaining shapes silently dropped by the raster — right
    ///         about every mesh anyone has and wrong about exactly one, with nothing anywhere to say
    ///         so. So <see cref="Build" /> refuses instead, and names the vertex.
    ///     </para>
    ///     <para>
    ///         Two hundred and fifty-six, which is more shapes than a MetaHuman face carries in total
    ///         and far more than can move one vertex — a corrective moves the vertices two shapes
    ///         already moved, and the stack is two or three deep.
    ///     </para>
    /// </remarks>
    public const int MaxShapesPerVertex = 256;

    /// <summary>How many 32-bit words one entry occupies, which is <c>MorphKernel.EntryWords</c>.</summary>
    /// <remarks>
    ///     Four, and the same four: the last three words are <c>MorphKernel.Pack</c>'s quantised
    ///     components, bit for bit. Only the first word differs — a scatter entry names the
    ///     <em>vertex</em> it writes and a gather entry names the <em>shape</em> it came from, because
    ///     the thing the reader already knows is the other one.
    /// </remarks>
    public const int EntryWords = MorphKernel.EntryWords;

    MorphIndex(
        string[] names,
        uint[] runs,
        uint[] entries,
        float[] positionSteps,
        float[] normalSteps,
        float[] reaches,
        int vertexCount
    ) {
        Names = names;
        Runs = runs;
        Entries = entries;
        PositionSteps = positionSteps;
        NormalSteps = normalSteps;
        Reaches = reaches;
        VertexCount = vertexCount;
    }

    /// <summary>What the mesh calls each of its shapes, in slot order.</summary>
    /// <remarks>
    ///     ⚠ <b>The authoritative answer to "which slot is <c>jawOpen</c>" for a virtualized mesh</b>,
    ///     as <c>MorphRenderFeature.ShapesOf</c> is for a suballocated one. A clip binds a shape by
    ///     name and <c>BlendShapeWeights</c> is addressed by slot, and the ordinal a source file used
    ///     is not the ordinal <c>MeshData.MorphTargets</c> ended up with — <c>ReadMorphTargets</c>
    ///     drops a shape that moves nothing above the threshold and deduplicates the names of the
    ///     rest. Something on each path has to have seen both ends, and on this one it is this.
    /// </remarks>
    public string[] Names { get; }

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

    /// <summary>How far one shape moves the vertex it moves most, per shape.</summary>
    /// <remarks>
    ///     <para>
    ///         What an instance's bound is actually inflated by, once its weights are known:
    ///         <c>Σ |wᵢ|·Reaches[i]</c> rides in <c>CullInstance.MotionRadius</c>, which the traversal
    ///         already adds to every cluster radius for a skinned instance's pose. So a face holding
    ///         still inflates nothing and a face at full expression inflates by what it is actually
    ///         doing, rather than by what twenty shapes could do at once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per shape and summed per instance, rather than one number per mesh</b>, because
    ///         the mesh-wide sum is what <see cref="MaxDisplacement" /> is and it is loose by the
    ///         number of shapes. A twenty-shape head making one expression would be tested against a
    ///         bound twenty shapes wide, which draws clusters no camera can see.
    ///     </para>
    /// </remarks>
    public float[] Reaches { get; }

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

    /// <summary>
    ///     The first shape that this type would refuse, or null when the mesh can be re-indexed.
    /// </summary>
    /// <param name="targets">The mesh's blend shapes.</param>
    /// <param name="vertexCount">How many vertices the mesh has.</param>
    /// <returns>The offending shape's name and why, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="targets" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The same two rules <see cref="Build" /> throws for, asked rather than raised</b> — so
    ///         a build can refuse a mesh where a person is reading the log, instead of a load throwing
    ///         where a frame loop is running. <c>ModelImporter</c> asks it before it decides whether a
    ///         mesh gets a cluster hierarchy, because the paged path is the one that re-indexes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both rules are reachable without a corrupt file.</b> A shape naming a vertex the
    ///         mesh does not have is what retopology leaves behind — it replaces a mesh's vertices and
    ///         does not rewrite its shapes. A vertex moved by more than
    ///         <see cref="MaxShapesPerVertex" /> shapes is what a generated corrective stack does if
    ///         nobody bounds it.
    ///     </para>
    /// </remarks>
    public static string? Refused(IReadOnlyList<MorphTargetData> targets, int vertexCount) {
        ArgumentNullException.ThrowIfNull(targets);

        if (vertexCount <= 0) {
            return null;
        }

        var counts = new int[vertexCount];

        foreach (var target in targets) {
            foreach (var vertex in target.Indices) {
                if ((uint)vertex >= (uint)vertexCount) {
                    return $"'{target.Name}' moves vertex {vertex} and the mesh has {vertexCount}";
                }

                if (++counts[vertex] > MaxShapesPerVertex) {
                    return $"'{target.Name}' is the {counts[vertex]}th shape to move vertex {vertex}, "
                        + $"and the device's gather is a loop of at most {MaxShapesPerVertex}";
                }
            }
        }

        return null;
    }

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
            if (runs[vertex + 1] > MaxShapesPerVertex) {
                throw new ArgumentException(
                    $"Vertex {vertex} is moved by {runs[vertex + 1]} shapes and the device's gather is "
                    + $"a loop of at most {MaxShapesPerVertex}. Refused here rather than truncated "
                    + "there, because a raster that dropped the rest would be right about every other "
                    + "mesh and silently wrong about this one.",
                    nameof(targets)
                );
            }

            runs[vertex + 1] += runs[vertex];
        }

        var entries = new uint[total * EntryWords];
        var cursor = new uint[vertexCount];

        runs.AsSpan(0, vertexCount).CopyTo(cursor);

        var names = new string[targets.Count];
        var positionSteps = new float[targets.Count];
        var normalSteps = new float[targets.Count];
        var reaches = new float[targets.Count];
        var displacement = 0f;

        for (var shape = 0; shape < targets.Count; shape++) {
            var target = targets[shape];

            names[shape] = target.Name;
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

            reaches[shape] = reach;
            displacement += reach;
        }

        return new(names, runs, entries, positionSteps, normalSteps, reaches, vertexCount) {
            MaxDisplacement = displacement
        };
    }

    /// <summary>How far these weights can move any vertex, in object space.</summary>
    /// <param name="weights">One per shape. Shorter is read as zero for the rest.</param>
    /// <returns>The bound, which is zero for a mesh at rest.</returns>
    /// <remarks>
    ///     <para>
    ///         What goes in <c>CullInstance.MotionRadius</c>, added to whatever a pose already put
    ///         there. Every bound in the DAG is a rest-pose bound, so a traversal that tested them as
    ///         they stand culls a dropped jaw by where it is not — and the failure is silent, because
    ///         a cluster that is not drawn does not say so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The absolute weight, because a negative one displaces just as far.</b> An
    ///         exporter that authored a shape as the inverse of its neighbour produces exactly that,
    ///         and a bound that summed signed weights would shrink where it needs to grow.
    ///     </para>
    /// </remarks>
    public float Radius(ReadOnlySpan<float> weights) {
        var radius = 0f;

        for (var shape = 0; shape < Reaches.Length; shape++) {
            if (shape < weights.Length) {
                radius += MathF.Abs(weights[shape]) * Reaches[shape];
            }
        }

        return radius;
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
