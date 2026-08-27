// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     The gather form of a mesh's blend shapes — <see cref="MorphIndex" />, which is what the paged
///     path morphs with because it has no per-instance buffer to scatter into.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every claim here is "the gather agrees with the scatter".</b> <c>MorphKernel</c> is the
///         reference the device kernel is already held to, so holding this to <c>MorphKernel</c> makes
///         all three agree with one third party rather than with each other in a ring.
///     </para>
///     <para>
///         ⚠ <b>Exact rather than approximate, and that is bought by the fixtures.</b>
///         <see cref="Unit" /> is <c>2⁻¹⁵</c>, so a delta whose components are integer multiples of it
///         and whose largest is <c>32767·2⁻¹⁵</c> quantises to a step that is a power of two — and
///         every decode is then a float multiplied by a power of two. The one place this suite does
///         <em>not</em> claim exactness is where the two forms sum the same terms in different orders,
///         and that case is called out where it arises.
///     </para>
/// </remarks>
public class MorphIndexTests {
    /// <summary>The quantiser's own step for a target whose range is <c>32767·2⁻¹⁵</c>.</summary>
    const float Unit = 1f / 32768f;

    /// <summary>A delta whose every component survives quantisation bit-for-bit.</summary>
    static Vector3 Exact(int x, int y, int z) => new(x * Unit, y * Unit, z * Unit);

    /// <summary>Four vertices at four corners, normals up, so a change is attributable.</summary>
    static SurfaceVertex[] Mesh() => [
        new() { Position = new(0f, 0f, 0f), Normal = Vector3.UnitY },
        new() { Position = new(1f, 0f, 0f), Normal = Vector3.UnitY },
        new() { Position = new(1f, 0f, 1f), Normal = Vector3.UnitY },
        new() { Position = new(0f, 0f, 1f), Normal = Vector3.UnitY }
    ];

    static void Same(Vector3 expected, Vector3 actual, string what) {
        Assert.True(
            expected.X == actual.X && expected.Y == actual.Y && expected.Z == actual.Z,
            $"{what}: expected {expected}, got {actual}"
        );
    }

    /// <summary>Morphs a whole mesh the gather way, one vertex at a time.</summary>
    static SurfaceVertex[] Gathered(MorphIndex index, SurfaceVertex[] mesh, params float[] weights) {
        var morphed = (SurfaceVertex[])mesh.Clone();

        for (var vertex = 0; vertex < mesh.Length; vertex++) {
            var position = morphed[vertex].Position;
            var normal = morphed[vertex].Normal;

            index.Apply(vertex, weights, ref position, ref normal);

            morphed[vertex] = morphed[vertex] with { Position = position, Normal = normal };
        }

        return morphed;
    }

    // --- The headline claim -------------------------------------------------

    /// <summary>
    ///     ⚠ The gather and the scatter put every vertex of a multi-shape mesh in the same place.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The assertion this whole type reduces to, and the fixture is chosen to make it able to
    ///         fail. Three shapes over four vertices, with vertex 2 moved by <em>two</em> of them —
    ///         that is a corrective, and it is the case a re-indexing gets wrong by keeping only the
    ///         last entry it wrote for a vertex.
    ///     </para>
    ///     <para>
    ///         Exact, because each vertex here is summed in the same order by both forms: the shapes
    ///         are declared in slot order and the counting sort is stable within a vertex, so the two
    ///         add <c>smileLeft</c> before <c>smileRight</c> alike.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_gather_puts_every_vertex_where_the_scatter_does() {
        var mesh = Mesh();

        MorphTargetData[] targets = [
            MorphTargetData.Encode("jawOpen", [0, 2], [Exact(32767, -16384, 8192), Exact(-4096, 2048, 0)], []),
            MorphTargetData.Encode("smileLeft", [2], [Exact(8192, 8192, -8192)], []),
            MorphTargetData.Encode("browRaise", [1, 3], [Exact(0, 32767, 0), Exact(0, -32767, 0)], [])
        ];

        float[] weights = [1f, 0.5f, -0.25f];

        var scattered = new SurfaceVertex[mesh.Length];
        MorphKernel.Apply(mesh, targets, weights, scattered);

        var index = MorphIndex.Build(targets, mesh.Length);
        Assert.NotNull(index);

        var gathered = Gathered(index, mesh, weights);

        for (var vertex = 0; vertex < mesh.Length; vertex++) {
            Same(scattered[vertex].Position, gathered[vertex].Position, $"vertex {vertex} position");
            Same(scattered[vertex].Normal, gathered[vertex].Normal, $"vertex {vertex} normal");
        }
    }

    /// <summary>Normal deltas travel too, and are not renormalised on either side.</summary>
    /// <remarks>
    ///     ⚠ The normal is the half a re-indexing drops silently: the position is wrong in a way a
    ///     picture shows and a normal is wrong in a way that reads as a lighting bug. It also pins the
    ///     word layout — the two triples are packed six shorts across three words and the normal
    ///     starts in the <em>high</em> half of the middle one, so a reader that took both triples from
    ///     the same parity would read the position's <c>z</c> as the normal's <c>x</c>.
    /// </remarks>
    [Fact]
    public void A_normal_delta_arrives_and_is_left_unnormalised() {
        var mesh = Mesh();
        var position = Exact(16384, 0, -8192);
        var normal = Exact(0, -32767, 16384);

        MorphTargetData[] targets = [MorphTargetData.Encode("crease", [1], [position], [normal])];

        var scattered = new SurfaceVertex[mesh.Length];
        MorphKernel.Apply(mesh, targets, [1f], scattered);

        var index = MorphIndex.Build(targets, mesh.Length);
        Assert.NotNull(index);

        var gathered = Gathered(index, mesh, 1f);

        Same(scattered[1].Position, gathered[1].Position, "the creased vertex");
        Same(scattered[1].Normal, gathered[1].Normal, "its normal");

        // The whole point of not renormalising: the result's length is not one and nobody fixed it.
        Assert.NotEqual(1f, gathered[1].Normal.Length(), 4);
    }

    // --- The table's own shape ----------------------------------------------

    /// <summary>A vertex no shape moves has an empty run, and that is the common case.</summary>
    [Fact]
    public void An_untouched_vertex_has_an_empty_run() {
        MorphTargetData[] targets = [MorphTargetData.Encode("jawOpen", [2], [Exact(32767, 0, 0)], [])];

        var index = MorphIndex.Build(targets, 4);
        Assert.NotNull(index);

        Assert.Equal(index.Runs[0], index.Runs[1]);
        Assert.Equal(index.Runs[1], index.Runs[2]);
        Assert.Equal(1u, index.Runs[3] - index.Runs[2]);
        Assert.Equal(index.Runs[3], index.Runs[4]);
    }

    /// <summary>
    ///     ⚠ The runs are a prefix: monotone, starting at zero and ending at the entry count.
    /// </summary>
    /// <remarks>
    ///     What every reader assumes and nothing else asserts. A counting sort whose prefix pass was
    ///     off by one produces a table that is right for most vertices and reads one neighbour's entry
    ///     for the rest — a face with one twitching eyelid, which nobody would call a bug in a table.
    /// </remarks>
    [Fact]
    public void The_runs_are_a_prefix_over_every_vertex() {
        MorphTargetData[] targets = [
            MorphTargetData.Encode("a", [0, 3], [Exact(1, 0, 0), Exact(2, 0, 0)], []),
            MorphTargetData.Encode("b", [3], [Exact(3, 0, 0)], []),
            MorphTargetData.Encode("c", [1, 2, 3], [Exact(4, 0, 0), Exact(5, 0, 0), Exact(6, 0, 0)], [])
        ];

        var index = MorphIndex.Build(targets, 4);
        Assert.NotNull(index);

        Assert.Equal(5, index.Runs.Length);
        Assert.Equal(0u, index.Runs[0]);
        Assert.Equal((uint)index.EntryCount, index.Runs[4]);
        Assert.Equal(6, index.EntryCount);

        for (var vertex = 0; vertex < 4; vertex++) {
            Assert.True(index.Runs[vertex] <= index.Runs[vertex + 1], $"run {vertex} goes backwards");
        }

        // Vertex 3 is moved by all three shapes, and every one of them is in its run.
        var shapes = new List<uint>();

        for (var entry = index.Runs[3]; entry < index.Runs[4]; entry++) {
            shapes.Add(index.Entries[(int)entry * MorphIndex.EntryWords]);
        }

        Assert.Equal([0u, 1u, 2u], shapes);
    }

    /// <summary>A mesh with no shapes has no table at all.</summary>
    /// <remarks>
    ///     ⚠ Null and not an empty table, because the caller's branch is "is this mesh morphed" and an
    ///     empty table answers yes — then charges every vertex of every instance two reads to say no.
    /// </remarks>
    [Fact]
    public void A_mesh_with_no_shapes_has_no_table() {
        Assert.Null(MorphIndex.Build([], 4));
        Assert.Null(MorphIndex.Build([MorphTargetData.Encode("still", [], [], [])], 4));
    }

    /// <summary>A weight of zero is skipped, exactly as the scatter skips a zero-weight dispatch.</summary>
    [Fact]
    public void A_zero_weight_touches_nothing() {
        var mesh = Mesh();

        MorphTargetData[] targets = [
            MorphTargetData.Encode("jawOpen", [0, 1, 2, 3], [
                Exact(32767, 0, 0), Exact(0, 32767, 0), Exact(0, 0, 32767), Exact(-32767, 0, 0)
            ], [])
        ];

        var index = MorphIndex.Build(targets, mesh.Length);
        Assert.NotNull(index);

        var gathered = Gathered(index, mesh, 0f);

        for (var vertex = 0; vertex < mesh.Length; vertex++) {
            Same(mesh[vertex].Position, gathered[vertex].Position, $"vertex {vertex}");
        }
    }

    /// <summary>A weight slot the caller did not supply reads as zero rather than throwing.</summary>
    /// <remarks>
    ///     <c>BlendShapeWeights</c>'s own rule — an entity that only ever opens its jaw carries one
    ///     number — and it has to hold here too, because this is what the device reads with a
    ///     per-instance weight range that may be shorter than the mesh's shape count.
    /// </remarks>
    [Fact]
    public void A_missing_weight_is_at_rest() {
        var mesh = Mesh();

        MorphTargetData[] targets = [
            MorphTargetData.Encode("jawOpen", [1], [Exact(32767, 0, 0)], []),
            MorphTargetData.Encode("browRaise", [1], [Exact(0, 32767, 0)], [])
        ];

        var index = MorphIndex.Build(targets, mesh.Length);
        Assert.NotNull(index);

        var gathered = Gathered(index, mesh, 1f);

        Same(mesh[1].Position + Exact(32767, 0, 0), gathered[1].Position, "only the jaw applied");
    }

    // --- What the bound is for ----------------------------------------------

    /// <summary>
    ///     ⚠ <see cref="MorphIndex.MaxDisplacement" /> covers every vertex at every weight in
    ///     <c>[-1, 1]</c>.
    /// </summary>
    /// <remarks>
    ///     The number a cluster's bound is inflated by, so a jaw that drops is not culled with its
    ///     mouth open. A bound that is too small is the silent kind of wrong — the geometry is there
    ///     and the traversal declines to draw it — so this asserts it against the worst case rather
    ///     than against a plausible one.
    /// </remarks>
    [Fact]
    public void The_displacement_bound_covers_the_worst_weights() {
        var mesh = Mesh();

        MorphTargetData[] targets = [
            MorphTargetData.Encode("jawOpen", [0, 2], [Exact(32767, 0, 0), Exact(16384, 0, 0)], []),
            MorphTargetData.Encode("browRaise", [0, 1], [Exact(0, 32767, 0), Exact(0, 8192, 0)], [])
        ];

        var index = MorphIndex.Build(targets, mesh.Length);
        Assert.NotNull(index);

        foreach (var weights in new[] { new[] { 1f, 1f }, [-1f, 1f], [1f, -1f], [-1f, -1f] }) {
            var gathered = Gathered(index, mesh, weights);

            for (var vertex = 0; vertex < mesh.Length; vertex++) {
                var moved = (gathered[vertex].Position - mesh[vertex].Position).Length();

                Assert.True(
                    moved <= index.MaxDisplacement + 1e-5f,
                    $"vertex {vertex} moved {moved} and the bound is {index.MaxDisplacement}"
                );
            }
        }
    }

    /// <summary>A target that names a vertex the mesh does not have is refused, loudly.</summary>
    /// <remarks>
    ///     ⚠ The failure this replaces is a read past the end of the runs array during registration,
    ///     which is a different mesh's table or a crash depending on the allocator.
    /// </remarks>
    [Fact]
    public void A_target_from_another_mesh_is_refused() {
        MorphTargetData[] targets = [MorphTargetData.Encode("jawOpen", [7], [Exact(1, 0, 0)], [])];

        var failure = Assert.Throws<ArgumentException>(() => MorphIndex.Build(targets, 4));

        Assert.Contains("moves vertex 7", failure.Message, StringComparison.Ordinal);
    }
}
