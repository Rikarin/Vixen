// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     Blend shapes — <a href="../../docs/plan/33-character-creator.md">doc 33</a> § D4's storage and
///     its kernel.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every assertion here is about where a vertex ended up, and most of them are exact.</b>
///         Vertex data is where a silent wrong answer lives: a delta applied in the wrong space, at
///         the wrong stride, or with the weight folded in twice renders <em>plausibly</em>, and a
///         suite that only checked "the mesh still draws" would pass on all three.
///     </para>
///     <para>
///         ⚠ <b>Exact is available here because the fixtures choose deltas the quantiser cannot
///         round.</b> <see cref="Unit" /> is <c>2⁻¹⁵</c>, so a delta whose components are integer
///         multiples of it and whose largest is <c>32767·2⁻¹⁵</c> has a step of exactly <c>2⁻¹⁵</c> —
///         a power of two — and every decode is a float multiplied by a power of two, which is exact.
///         A fixture with round decimal deltas would have to assert to a tolerance, and a tolerance is
///         where an off-by-one-quantum bug hides.
///     </para>
/// </remarks>
public class MorphTargetTests {
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

    // --- The headline claim -------------------------------------------------

    /// <summary>
    ///     ⚠ At a weight of one, the vertex the target names is at <c>base + Δ</c> and nothing else has
    ///     moved.
    /// </summary>
    /// <remarks>
    ///     The assertion the whole feature reduces to. It is exact rather than approximate — see the
    ///     class remarks — and it names the <em>other three</em> vertices too, because a scatter that
    ///     wrote at the wrong stride would move a vertex to a plausible place and this is what
    ///     distinguishes the two.
    /// </remarks>
    [Fact]
    public void A_weight_of_one_moves_the_vertex_to_where_the_target_says() {
        var mesh = Mesh();
        var delta = Exact(32767, -16384, 8192);

        var target = MorphTargetData.Encode("jawOpen", [2], [delta], []);
        var morphed = new SurfaceVertex[mesh.Length];

        MorphKernel.Apply(mesh, [target], [1f], morphed);

        Same(mesh[2].Position + delta, morphed[2].Position, "the moved vertex");

        Same(mesh[0].Position, morphed[0].Position, "vertex 0");
        Same(mesh[1].Position, morphed[1].Position, "vertex 1");
        Same(mesh[3].Position, morphed[3].Position, "vertex 3");
    }

    /// <summary>Half a weight is half the delta, exactly, because the delta halves exactly.</summary>
    [Fact]
    public void Half_a_weight_moves_it_half_way() {
        var mesh = Mesh();
        var delta = Exact(32767, -16384, 8192);

        var target = MorphTargetData.Encode("jawOpen", [2], [delta], []);
        var morphed = new SurfaceVertex[mesh.Length];

        MorphKernel.Apply(mesh, [target], [0.5f], morphed);

        Same(mesh[2].Position + (delta * 0.5f), morphed[2].Position, "the half-open jaw");
    }

    /// <summary>A weight of zero leaves the mesh alone, byte for byte.</summary>
    /// <remarks>
    ///     ⚠ Not a triviality. The dispatcher skips a zero-weight target entirely, so a reference that
    ///     added its deltas anyway would disagree with the device on every inactive shape — which is
    ///     most of them, most of the time.
    /// </remarks>
    [Fact]
    public void A_zero_weight_touches_nothing() {
        var mesh = Mesh();
        var target = MorphTargetData.Encode("jawOpen", [2], [Exact(32767, 0, 0)], []);
        var morphed = new SurfaceVertex[mesh.Length];

        MorphKernel.Apply(mesh, [target], [0f], morphed);

        for (var index = 0; index < mesh.Length; index++) {
            Same(mesh[index].Position, morphed[index].Position, $"vertex {index}");
            Same(mesh[index].Normal, morphed[index].Normal, $"normal {index}");
        }
    }

    /// <summary>Two shapes that move one vertex both move it, and the answer is the sum.</summary>
    /// <remarks>
    ///     The case a corrective <em>is</em>, and the reason the dispatch is one per target with a
    ///     barrier between rather than one over the concatenation: two invocations
    ///     read-modify-writing the same vertex in one dispatch would produce whichever landed last,
    ///     which on a fixture this small would still look right about half the time.
    /// </remarks>
    [Fact]
    public void Two_targets_that_share_a_vertex_both_move_it() {
        var mesh = Mesh();
        var first = Exact(16384, 0, 0);
        var second = Exact(0, -8192, 0);

        var targets = new[] {
            MorphTargetData.Encode("smileLeft", [2], [first], []),
            MorphTargetData.Encode("smileRight", [2], [second], [])
        };

        var morphed = new SurfaceVertex[mesh.Length];

        MorphKernel.Apply(mesh, targets, [1f, 1f], morphed);

        Same(mesh[2].Position + first + second, morphed[2].Position, "the shared vertex");
    }

    /// <summary>The normal delta lands on the normal, not on the position.</summary>
    /// <remarks>
    ///     ⚠ The two are twelve bytes apart in the vertex and both are a <c>Vector3</c>, so writing one
    ///     where the other goes compiles, draws, and looks like a shading bug rather than a stride bug.
    /// </remarks>
    [Fact]
    public void A_normal_delta_lands_on_the_normal() {
        var mesh = Mesh();
        var position = Exact(8192, 0, 0);
        var normal = Exact(0, -16384, 32767);

        var target = MorphTargetData.Encode("crease", [1], [position], [normal]);
        var morphed = new SurfaceVertex[mesh.Length];

        MorphKernel.Apply(mesh, [target], [1f], morphed);

        Same(mesh[1].Position + position, morphed[1].Position, "the position");
        Same(mesh[1].Normal + normal, morphed[1].Normal, "the normal");
    }

    /// <summary>
    ///     ⚠ A shape that exactly cancels a normal produces a zero normal, not a NaN.
    /// </summary>
    /// <remarks>
    ///     <b>The absolute-epsilon trap, put in front of the kernel deliberately.</b>
    ///     <c>Vector3.Normalize</c> gives up below an absolute <c>1e-6</c> and answers with infinities,
    ///     and a morphed normal is exactly the quantity that can reach zero — <c>Δn = −n</c> at full
    ///     weight is an authored shape, not a corrupt one. Nothing in the pre-pass normalises, which is
    ///     why this comes out as the zero vector and the fragment stage's <c>SafeNormalize</c> is what
    ///     has to cope with it. If somebody adds a normalise here, this is the test that says so.
    /// </remarks>
    [Fact]
    public void A_shape_that_cancels_a_normal_leaves_zero_rather_than_a_nan() {
        var mesh = Mesh();
        var target = MorphTargetData.Encode("flatten", [0], [Vector3.Zero], [-Vector3.UnitY]);
        var morphed = new SurfaceVertex[mesh.Length];

        MorphKernel.Apply(mesh, [target], [1f], morphed);

        Same(Vector3.Zero, morphed[0].Normal, "the cancelled normal");
        Assert.False(float.IsNaN(morphed[0].Normal.Y));
    }

    // --- Storage ------------------------------------------------------------

    /// <summary>A delta equal to the target's own range round-trips bit-exactly.</summary>
    /// <remarks>
    ///     Which is what <see cref="MorphTargetData.Quantum" /> being 32767 rather than 32768 buys, and
    ///     it is worth an assertion because the asymmetric form is the one everybody writes first: it
    ///     would land the authored extreme one quantum short, on every target, silently.
    /// </remarks>
    [Fact]
    public void A_delta_at_the_targets_own_range_round_trips_exactly() {
        var delta = new Vector3(0.75f, -0.75f, 0f);
        var target = MorphTargetData.Encode("extreme", [0], [delta], []);

        Assert.Equal(0.75f, target.PositionScale);
        Same(delta, target.PositionDelta(0), "the extreme");
    }

    /// <summary>Every other delta is within half a quantum of what was authored.</summary>
    [Fact]
    public void Quantisation_costs_at_most_half_a_step() {
        Vector3[] deltas = [
            new(0.3f, -0.11f, 0.0007f),
            new(-0.29f, 0.0031f, 0.13f),
            new(0.001f, 0.001f, -0.3f)
        ];

        var target = MorphTargetData.Encode("noisy", [0, 1, 2], deltas, []);
        var bound = (target.PositionScale / MorphTargetData.Quantum) * 0.5f;

        for (var entry = 0; entry < deltas.Length; entry++) {
            var apart = deltas[entry] - target.PositionDelta(entry);

            Assert.True(
                MathF.Abs(apart.X) <= bound && MathF.Abs(apart.Y) <= bound && MathF.Abs(apart.Z) <= bound,
                $"entry {entry} is {apart} away and the bound is {bound}"
            );
        }
    }

    /// <summary>
    ///     ⚠ A target whose deltas are all zero quantises to zeros, not to <c>NaN</c>.
    /// </summary>
    /// <remarks>
    ///     The range is the divisor, and a zero range divided into is what turns a whole mesh inside
    ///     out the moment somebody moves the slider off zero.
    /// </remarks>
    [Fact]
    public void A_target_that_moves_nothing_quantises_to_zeros() {
        var target = MorphTargetData.Encode("still", [0, 1], [Vector3.Zero, Vector3.Zero], []);

        Assert.Equal(0f, target.PositionScale);
        Assert.All(target.Positions, component => Assert.Equal(0, component));
        Same(Vector3.Zero, target.PositionDelta(0), "a still vertex");
    }

    /// <summary>Sparsifying keeps the vertices that move and drops the ones that do not.</summary>
    [Fact]
    public void Sparsify_keeps_only_the_vertices_that_move() {
        Vector3[] deltas = [
            Vector3.Zero,
            new(0.5f, 0f, 0f),
            new(1e-7f, 0f, 0f),
            new(0f, 0f, -0.25f)
        ];

        var target = MorphTargetData.Sparsify("browRaise", deltas, [], 1e-4f);

        Assert.Equal([1, 3], target.Indices);
        Assert.Equal(2, target.Count);
    }

    /// <summary>A shape that only re-shades survives, because the normal delta is tested too.</summary>
    /// <remarks>
    ///     Testing the position alone would throw a crease-only shape away entirely and leave an empty
    ///     target — which reads downstream as a file that had no shapes in it.
    /// </remarks>
    [Fact]
    public void Sparsify_keeps_a_vertex_that_only_reshades() {
        Vector3[] positions = [Vector3.Zero, Vector3.Zero];
        Vector3[] normals = [Vector3.Zero, new(0f, 0.4f, 0f)];

        var target = MorphTargetData.Sparsify("crease", positions, normals, 1e-4f);

        Assert.Equal([1], target.Indices);
        Assert.True(target.HasNormals);
    }

    /// <summary>What a target costs, in the bytes the plan's memory line is about.</summary>
    /// <remarks>
    ///     Sixteen an entry with normals, ten without. A head with twenty shapes each touching four
    ///     thousand vertices is 1.28 MB — resident, shared by every instance of the mesh, and the
    ///     number a project trades against.
    /// </remarks>
    [Fact]
    public void An_entry_costs_sixteen_bytes_with_normals_and_ten_without() {
        Vector3[] deltas = [new(1f, 0f, 0f), new(0f, 1f, 0f)];

        var shaded = MorphTargetData.Encode("shaded", [0, 1], deltas, deltas);
        var moved = MorphTargetData.Encode("moved", [0, 1], deltas, []);

        Assert.Equal(32L, shaded.SizeInBytes);
        Assert.Equal(20L, moved.SizeInBytes);
    }

    // --- The wire the device reads -----------------------------------------

    /// <summary>
    ///     ⚠ The packed entries decode to the deltas the record holds, component for component.
    /// </summary>
    /// <remarks>
    ///     The device never sees <see cref="MorphTargetData" />; it sees four words an entry. This is
    ///     the assertion that the packing preserves the sixteen bits — including the sign, which is the
    ///     half a mask-and-shift gets wrong, and which would show up as an eyelid that opens upward on
    ///     the device and downward on the host.
    /// </remarks>
    [Fact]
    public void The_packed_entries_carry_the_same_bits_the_record_does() {
        Vector3[] positions = [Exact(-32767, 16384, -8192), Exact(4096, -4096, 32767)];
        Vector3[] normals = [Exact(1, -1, 0), Exact(-32767, 32767, -16384)];

        var target = MorphTargetData.Encode("packed", [3, 9], positions, normals);
        var words = MorphKernel.Pack(target);

        Assert.Equal(target.Count * MorphKernel.EntryWords, words.Length);

        for (var entry = 0; entry < target.Count; entry++) {
            var at = entry * MorphKernel.EntryWords;

            Assert.Equal((uint)target.Indices[entry], words[at]);

            var position = new Vector3(
                Signed(words[at + 1]) * MorphKernel.Step(target.PositionScale),
                Signed(words[at + 1] >> 16) * MorphKernel.Step(target.PositionScale),
                Signed(words[at + 2]) * MorphKernel.Step(target.PositionScale)
            );

            var normal = new Vector3(
                Signed(words[at + 2] >> 16) * MorphKernel.Step(target.NormalScale),
                Signed(words[at + 3]) * MorphKernel.Step(target.NormalScale),
                Signed(words[at + 3] >> 16) * MorphKernel.Step(target.NormalScale)
            );

            Same(target.PositionDelta(entry), position, $"entry {entry}'s position");
            Same(target.NormalDelta(entry), normal, $"entry {entry}'s normal");
        }
    }

    /// <summary>A target with no normal deltas packs zeros into their half rather than garbage.</summary>
    [Fact]
    public void A_target_with_no_normals_packs_zeros_for_them() {
        var target = MorphTargetData.Encode("moved", [0], [Exact(32767, 0, 0)], []);
        var words = MorphKernel.Pack(target);

        Assert.Equal(0u, words[2] >> 16);
        Assert.Equal(0u, words[3]);
    }

    // --- Refusals -----------------------------------------------------------

    /// <summary>A target naming a vertex the mesh does not have is refused by name.</summary>
    /// <remarks>
    ///     Rather than written past the end of the stream, which on a device is somebody else's mesh
    ///     and on the host is an exception a long way from the cause.
    /// </remarks>
    [Fact]
    public void A_target_that_names_a_vertex_the_mesh_lacks_is_refused() {
        var mesh = Mesh();
        var target = MorphTargetData.Encode("wrongMesh", [9], [Exact(32767, 0, 0)], []);
        var morphed = new SurfaceVertex[mesh.Length];

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => MorphKernel.Apply(mesh, [target], [1f], morphed)
        );

        Assert.Contains("wrongMesh", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Deltas and indices that disagree in length are refused at encode time.</summary>
    [Fact]
    public void Indices_and_deltas_that_disagree_are_refused() =>
        Assert.Throws<ArgumentException>(
            () => MorphTargetData.Encode("bad", [0, 1], [Vector3.UnitX], [])
        );

    /// <summary>How the transliterated shader reads the low half of a word, for the packing check.</summary>
    static float Signed(uint bits) {
        var value = bits & 0xFFFFu;

        return value < 0x8000u ? value : (float)value - 65536f;
    }
}
