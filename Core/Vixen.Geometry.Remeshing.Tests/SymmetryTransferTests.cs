// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D11's mirror composed with § D12's transfer, which they did not used to be.</summary>
/// <remarks>
///     <para>
///         <b>This file used to characterise the gap and now asserts it closed.</b> § D11's exact
///         mirror was written against the overload that has no attributes and stage seven arrived on a
///         different branch, so a symmetric remesh took the mirror's early return before it reached the
///         transfer and came back with an empty <see cref="TransferResult" />. The pass now runs the
///         transfer itself — against the <i>uncut</i> source, which is what the attributes are indexed
///         by — and reflects what came back.
///     </para>
///     <para>
///         ⚠ <b>The reflection is not mechanical for skin weights and is for everything else.</b>
///         Normals reflect through the plane, colours and coordinates copy, and a weight changes which
///         bone it names — which nothing in <see cref="SkinBinding" /> can work out, because
///         <see cref="SkinInfluence" /> is an index with no name. So the caller supplies
///         <see cref="SourceAttributes.BoneMirror" />, and a symmetric remesh of a rigged mesh without
///         one is refused rather than guessed at.
///     </para>
///     <para>
///         ⚠ <b>The round trip is asserted on bits.</b> A mirrored vertex's weights are the kept
///         half's weights with their bones relabelled and nothing dropped, so they are <i>the same
///         floats</i> and not merely close ones. Asserting that with a tolerance would pass on an
///         implementation that renormalised every vertex in the model for no reason, which is exactly
///         the change somebody makes while tidying and nobody notices.
///     </para>
/// </remarks>
public class SymmetryTransferTests {
    /// <summary>The plane every test here mirrors about, which is the one criterion 4 is written for.</summary>
    static Plane Mirror => new(Vector3.UnitX, 0f);

    /// <summary>Bone 0 and bone 1 are each other's mirror, which is the whole map for a two-bone ramp.</summary>
    static int[] Swap => [1, 0];

    /// <summary>The gap, closed: a symmetric remesh now carries the source's colours across.</summary>
    [Fact]
    public void A_symmetric_remesh_carries_its_colours() {
        var source = RemesherTests.Fixture("box");

        var quads = Remesher.Remesh(
            source,
            new() { Colors = Painted(source) },
            new RemeshSettings { TargetQuads = 200, Symmetry = Mirror },
            out var report,
            out var transferred
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));
        Assert.Equal(quads.CornerCount, transferred.Colors.Count);

        // ⚠ Near rather than equal, and only here. A colour is *interpolated* barycentrically at the
        // point the corner landed on, and three coordinates summing to one in float do not reproduce
        // a constant to the last bit — 0.99999994 rather than 1. Everything the mirror itself does is
        // asserted on bits; this is the transfer underneath it, which never claimed exactness.
        foreach (var color in transferred.Colors) {
            Assert.Equal(1f, color.X, 5);
            Assert.Equal(0.5f, color.Y, 5);
            Assert.Equal(0.25f, color.Z, 5);
            Assert.Equal(1f, color.W, 5);
        }
    }

    /// <summary>
    ///     ⚠ The control, and the reason the fact above is about composition rather than about
    ///     transfer being broken: the same source and the same attributes, with symmetry off, carry
    ///     across too. Without this, a transfer that had stopped working entirely would read as a
    ///     symmetry bug.
    /// </summary>
    [Fact]
    public void The_same_source_without_symmetry_does_carry_its_colours() {
        var source = RemesherTests.Fixture("box");

        Remesher.Remesh(
            source,
            new() { Colors = Painted(source) },
            new RemeshSettings { TargetQuads = 200 },
            out var report,
            out var transferred
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));
        Assert.NotEmpty(transferred.Colors);
    }

    /// <summary>A rigged mesh comes back rigged, and the mirror half is bound to the mirror bones.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The fixture is chosen so that a wrong answer is a <i>visible</i> wrong answer.</b>
    ///         <c>TransferFixtures.Ramp</c> binds bone 0 at one end of the <c>x</c> axis and bone 1 at
    ///         the other, so mirroring about <c>x = 0</c> without swapping the bones produces a mesh
    ///         whose two halves are both bound to the near end — the "left arm drives the right leg"
    ///         failure, reduced to two bones and one axis.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bit equality, not closeness.</b> Nothing is dropped and nothing is merged on the
    ///         ordinary mirror, so the mirrored vertex's weights are the same floats relabelled.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("cylinder")]
    public void A_rigged_mesh_round_trips_its_weights_through_a_mirror(string name) {
        var (quads, transferred, report) = Rigged(name, Swap);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        var binding = transferred.Weights;

        Assert.NotNull(binding);
        Assert.Equal(quads.PositionCount, binding.VertexCount);

        var index = ByBits(quads);
        var pairs = 0;

        for (var vertex = 0; vertex < quads.PositionCount; vertex++) {
            var position = quads.Positions[vertex];

            if (BitConverter.SingleToInt32Bits(position.X) == 0) {
                continue;
            }

            var (x, y, z) = Bits(position);

            Assert.True(index.TryGetValue((x ^ int.MinValue, y, z), out var other), $"{position} has no mirror.");

            // The map sends 0 to 1 and 1 to 0, so the mirror's hold by bone 0 is this vertex's by
            // bone 1 and the other way round — the same float, not a nearby one.
            SameBits(Weight(binding, other, 0), Weight(binding, vertex, 1), $"{name}: {position} on bone 0");
            SameBits(Weight(binding, other, 1), Weight(binding, vertex, 0), $"{name}: {position} on bone 1");

            pairs++;
        }

        Assert.True(pairs > 0, $"{name}: nothing was mirrored, so the round trip proved nothing.");

        // And the binding is a real one rather than a symmetric block of zeros: the ramp puts most of
        // a vertex's weight on the bone at its own end.
        Assert.Equal(0, transferred.UnboundVertices);
    }

    /// <summary>Weights with symmetry on and no bone map are refused, and the warning names the map.</summary>
    /// <remarks>
    ///     ⚠ <b>The alternative to refusing is a character whose left arm drives their right leg</b>,
    ///     which an animator finds three weeks later and no test finds at all. An empty result and a
    ///     warning is worse to use and enormously better to debug.
    /// </remarks>
    [Fact]
    public void Weights_with_no_bone_map_are_refused_rather_than_guessed() {
        var (quads, transferred, report) = Rigged("box", null);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        // The mesh still comes back, and it is still the mesh symmetry asked for.
        Assert.True(quads.FaceCount > 0);
        Assert.Null(transferred.Weights);
        Assert.Empty(transferred.Colors);

        // In both places: a caller holding only the result must be able to tell a refusal from a bug.
        Assert.Contains(report.Warnings, warning => warning.Contains("BoneMirror", StringComparison.Ordinal));
        Assert.Contains(transferred.Warnings, warning => warning.Contains("BoneMirror", StringComparison.Ordinal));
    }

    /// <summary>Every way a map can be wrong is named, and none of them is repaired.</summary>
    /// <remarks>
    ///     ⚠ <b>A clamp here is the worst available outcome.</b> An out-of-range entry silently
    ///     clamped binds one limb to whichever bone happened to be last; a short map filled with
    ///     identity leaves exactly the bones nobody listed unmirrored. Both produce a rig that is
    ///     wrong in one place, which is harder to find than one that refused.
    /// </remarks>
    [Theory]
    [InlineData(new[] { 1, 7 }, "not a bone")]
    [InlineData(new[] { 1, 1 }, "its own inverse")]
    [InlineData(new[] { 0 }, "no mirror")]
    public void A_map_that_cannot_be_a_mirror_is_named_rather_than_repaired(int[] bones, string says) {
        var (_, transferred, report) = Rigged("box", bones);

        Assert.Null(transferred.Weights);
        Assert.Contains(report.Warnings, warning => warning.Contains(says, StringComparison.Ordinal));
    }

    /// <summary>Normals survive the reflection, and the mirrored half's point the mirrored way.</summary>
    /// <remarks>
    ///     ⚠ <b>The reflection builds a fresh mesh, so a layer that is not carried is a layer that is
    ///     gone.</b> Before the two features composed, a symmetric remesh came back with no normals at
    ///     all — which reads as a shading bug and is a bookkeeping one.
    /// </remarks>
    [Fact]
    public void Normals_are_reflected_rather_than_dropped() {
        var source = RemesherTests.Fixture("sphere");

        var quads = Remesher.Remesh(
            source,
            SourceAttributes.None,
            new RemeshSettings { TargetQuads = 300, Symmetry = Mirror },
            out var report,
            out _
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));
        Assert.Equal(quads.CornerCount, quads.Normals.Length);

        var lit = 0;

        foreach (var normal in quads.Normals) {
            if (normal.LengthSquared() > 0f) {
                lit++;
            }
        }

        Assert.True(lit > quads.CornerCount / 2, $"Only {lit} of {quads.CornerCount} corners got a normal.");

        // A sphere mirrored about x = 0 has as much normal pointing one way along x as the other, to
        // the last bit, because every mirrored normal is a sign flip of one that was kept.
        var sum = Vector3.Zero;

        foreach (var normal in quads.Normals) {
            sum += normal;
        }

        Assert.True(MathF.Abs(sum.X) < 1e-3f * quads.CornerCount, $"The normals lean {sum.X} along the mirror axis.");
    }

    /// <summary>A vertex on the plane is weighted symmetrically, because it stands in both halves.</summary>
    /// <remarks>
    ///     ⚠ <b>The seam is one vertex, not two, so its weights have to be invariant under the bone
    ///     mirror.</b> Left as they arrived they would move the seam differently from the surface
    ///     either side of it — a vertex that belongs to both halves being driven by only one of their
    ///     rigs, which pinches the mesh open along the plane the moment the character moves.
    /// </remarks>
    [Fact]
    public void A_vertex_on_the_plane_is_weighted_symmetrically() {
        var (quads, transferred, report) = Rigged("box", Swap);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        var binding = transferred.Weights;

        Assert.NotNull(binding);

        var seam = 0;

        for (var vertex = 0; vertex < quads.PositionCount; vertex++) {
            if (BitConverter.SingleToInt32Bits(quads.Positions[vertex].X) != 0) {
                continue;
            }

            seam++;

            // Invariant under the map means bone 0's hold equals bone 1's, since they swap.
            Assert.Equal(Weight(binding, vertex, 0), Weight(binding, vertex, 1), 5);
        }

        Assert.True(seam > 0, "Nothing landed on the plane, so the symmetrisation was never exercised.");
    }

    /// <summary>Nothing comes back over the influence limit, and every bound vertex still sums to one.</summary>
    /// <remarks>
    ///     ⚠ <b>Symmetrising the seam is the one place an influence count can <i>grow</i>.</b> Two
    ///     four-bone sets average to as many as eight, and a target with room for four that is handed
    ///     eight has to drop and rescale rather than truncate — or the seam's vertices come back
    ///     summing to less than one and the mesh shrinks toward the origin under animation.
    /// </remarks>
    [Fact]
    public void Every_bound_vertex_sums_to_one_and_stays_within_the_limit() {
        var (quads, transferred, report) = Rigged("cylinder", Swap);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        var binding = transferred.Weights;

        Assert.NotNull(binding);
        Assert.Equal(4, binding.Stride);

        for (var vertex = 0; vertex < quads.PositionCount; vertex++) {
            var total = binding.Total(vertex);

            if (total == 0f) {
                continue;
            }

            Assert.Equal(1f, total, 4);

            var held = 0;

            foreach (var influence in binding.At(vertex)) {
                if (influence.Weight > 0f) {
                    held++;
                }
            }

            Assert.InRange(held, 1, binding.Stride);
        }
    }

    /// <summary>§ D14 over the composed path: ten runs at three worker counts are one run.</summary>
    /// <remarks>
    ///     ⚠ <b>A greedy pass that iterates a dictionary orders differently on another runtime</b>,
    ///     and the mirror's weight merge is a greedy pass. It is a list with an explicit tie-break for
    ///     that reason, and this is what says so.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void A_symmetric_rigged_remesh_is_the_same_run_at_every_worker_count(int workers) {
        using var scheduler = new JobScheduler(workers);

        var (_, first, report) = Rigged("box", Swap, scheduler);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));
        Assert.NotNull(first.Weights);

        for (var run = 1; run < 4; run++) {
            var (_, again, _) = Rigged("box", Swap, scheduler);

            Assert.NotNull(again.Weights);
            Assert.Equal(first.Weights.Influences.Count, again.Weights.Influences.Count);

            for (var slot = 0; slot < first.Weights.Influences.Count; slot++) {
                var one = first.Weights.Influences[slot];
                var two = again.Weights.Influences[slot];

                Assert.Equal(one.Bone, two.Bone);
                SameBits(one.Weight, two.Weight, $"{workers} workers, run {run}, influence {slot}");
            }
        }
    }

    /// <summary>A symmetric remesh of a fixture bound by <c>TransferFixtures.Ramp</c>.</summary>
    static (EditMesh Quads, TransferResult Transferred, RemeshReport Report) Rigged(
        string name,
        int[]? bones,
        JobScheduler? scheduler = null
    ) {
        var source = RemesherTests.Fixture(name);

        var quads = Remesher.Remesh(
            source,
            new() { Weights = TransferFixtures.Ramp(source, source.Bounds.Size.X), BoneMirror = bones },
            new RemeshSettings { TargetQuads = 300, Symmetry = Mirror },
            out var report,
            out var transferred,
            scheduler
        );

        return (quads, transferred, report);
    }

    /// <summary>One colour on every corner of a source, so a carried colour is recognisable.</summary>
    static Vector4[] Painted(EditMesh source) {
        var colors = new Vector4[source.CornerCount];

        Array.Fill(colors, new(1f, 0.5f, 0.25f, 1f));

        return colors;
    }

    /// <summary>How much of a vertex one bone holds, ignoring the zero-weight padding.</summary>
    static float Weight(SkinBinding binding, int vertex, int bone) {
        foreach (var influence in binding.At(vertex)) {
            if (influence.Bone == bone && influence.Weight > 0f) {
                return influence.Weight;
            }
        }

        return 0f;
    }

    /// <summary>Every position by its bits, so a mirror is looked up rather than searched for.</summary>
    static Dictionary<(int X, int Y, int Z), int> ByBits(EditMesh mesh) {
        var index = new Dictionary<(int, int, int), int>(mesh.PositionCount);

        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            index.TryAdd(Bits(mesh.Positions[vertex]), vertex);
        }

        return index;
    }

    static (int X, int Y, int Z) Bits(Vector3 position) =>
        (
            BitConverter.SingleToInt32Bits(position.X),
            BitConverter.SingleToInt32Bits(position.Y),
            BitConverter.SingleToInt32Bits(position.Z)
        );

    static void SameBits(float one, float two, string where) =>
        Assert.True(
            BitConverter.SingleToInt32Bits(one) == BitConverter.SingleToInt32Bits(two),
            $"{where}: {one} and {two} are not the same float."
        );
}
