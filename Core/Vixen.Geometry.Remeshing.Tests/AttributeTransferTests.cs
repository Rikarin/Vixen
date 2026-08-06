// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D12: the output carries the input, or it is useless.</summary>
/// <remarks>
///     <b>What R5 delivers, measured rather than claimed.</b> The material rule is the headline and it
///     is the one that looks like a micro-optimisation and is not — the numbers are in
///     <see cref="A_material_boundary_comes_out_a_chain_and_the_nearest_face_rule_shreds_it" />. Skin
///     weights round-trip a ramp to three decimal places and clamp deterministically. Normals are not
///     averaged across a crease. Every one of them is asserted at a thousandth and a thousand times
///     scale, because four absolute-tolerance bugs were found in this repository in one day.
/// </remarks>
public class AttributeTransferTests {
    /// <summary>The headline: a material boundary is a chain, and the rejected rule is a sawtooth.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D12's one emphatic sentence, turned into a measurement.</b>
    ///         "Nearest-face assignment shreds along a material boundary — every other quad flips —
    ///         and produces a mesh with a sawtooth material seam that looks like a UV bug." Both
    ///         rules run on the <i>same</i> source and the <i>same</i> target here, so the difference
    ///         is the rule and nothing else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two metrics, because "sawtooth" is two claims.</b> The <i>shape</i> claim is that
    ///         the boundary is a chain — every vertex on it carries exactly two boundary edges, and
    ///         three or four means it branched. The <i>length</i> claim is the one the plan's phrase
    ///         "every other quad flips" actually describes, and it is the sharper of the two: a
    ///         straight boundary across a fourteen-quad sheet is fourteen edges and nothing can beat
    ///         that, so the area rule hitting the floor exactly and the rejected rule missing it is a
    ///         measurement with no slack in it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured here: the area rule gives the floor of 14 edges branching twice, and
    ///         the nearest-face rule gives 17 branching twice.</b> The rejected rule zigzags by three
    ///         quads — twenty-one percent longer than a boundary that is <i>known</i> to be straight
    ///         — and it does so on a target whose only irregularity is a jitter smaller than half a
    ///         quad. It did <i>not</i> branch on this fixture; a single alternating column of quads
    ///         is a zigzag rather than a junction, and reporting it as one would be overclaiming.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_material_boundary_comes_out_a_chain_and_the_nearest_face_rule_shreds_it() {
        const int Cells = 14;

        var source = TransferFixtures.TwoGroups(64, 2f);
        var byArea = TransferFixtures.Grid(Cells, 2f, _ => 0, 0.4f);
        var byNearest = TransferFixtures.Grid(Cells, 2f, _ => 0, 0.4f);

        AttributeTransfer.Transfer(source, SourceAttributes.None, byArea, new());
        TransferFixtures.NearestFace(source, byNearest);

        var chain = TransferFixtures.Boundary(byArea);
        var sawtooth = TransferFixtures.Boundary(byNearest);
        var branching = TransferFixtures.Branching(byArea, chain);

        Assert.True(chain.Count > 0, "Neither group survived, so there is no boundary to measure.");

        Assert.True(
            branching <= 2,
            $"The area rule branched {branching} ways at a vertex: it is not a chain. "
            + $"{chain.Count} boundary edges against the nearest-face rule's {sawtooth.Count}."
        );

        // The floor: a straight cut across the sheet crosses exactly one edge per row.
        Assert.Equal(Cells, chain.Count);

        Assert.True(
            sawtooth.Count > chain.Count,
            $"The nearest-face rule gave {sawtooth.Count} boundary edges and the area rule gave "
            + $"{chain.Count}; this fixture cannot tell the two rules apart."
        );
    }

    /// <summary>And the same on the real thing, end to end through the remesher.</summary>
    /// <remarks>
    ///     ⚠ <b>The synthetic case above jitters its target on purpose; this one does not have
    ///     to.</b> An extraction's quads wobble because a Coons interior projected onto a surface and
    ///     relaxed eight times does not land on a lattice, which is the condition the jitter
    ///     reproduces — so the end-to-end assertion is the one that says the rule matters in the
    ///     pipeline rather than in a fixture.
    /// </remarks>
    [Fact]
    public void The_remeshers_own_output_carries_a_material_boundary_that_is_a_chain() {
        var source = TransferFixtures.TwoGroups(48, 2f);
        var quads = Remesher.Remesh(source, new() { TargetQuads = 200, KeepGroups = true }, out var report);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        var groups = new HashSet<int>();

        for (var face = 0; face < quads.FaceCount; face++) {
            groups.Add(quads.Faces[face].Group);
        }

        Assert.True(groups.Count >= 2, "Both source groups should survive onto the output.");

        var boundary = TransferFixtures.Boundary(quads);

        Assert.True(
            TransferFixtures.Branching(quads, boundary) <= 2,
            $"The output's material boundary branches: {boundary.Count} edges, "
            + $"{TransferFixtures.Branching(quads, boundary)}-way at its worst."
        );
    }

    /// <summary>Normals are not interpolated through a smoothing boundary.</summary>
    /// <remarks>
    ///     ⚠ <b>Interpolating across one is exactly the artefact smoothing groups exist to
    ///     prevent.</b> A right-angle fold has two faces meeting at a shared position; the corners on
    ///     each side must keep their own side's normal, and a single averaged normal there is the
    ///     rounded-off box that makes a remesh look like it lost the hard surface — which § D4 says
    ///     the whole layout was arranged to keep.
    /// </remarks>
    [Theory]
    [InlineData(1e-3f)]
    [InlineData(1f)]
    [InlineData(1e+3f)]
    public void A_crease_keeps_a_different_normal_on_each_of_its_sides(float scale) {
        var source = TransferFixtures.Scaled(Fold(12), scale);
        var target = TransferFixtures.Scaled(Fold(5), scale);

        AttributeTransfer.Transfer(source, SourceAttributes.None, target, new());

        var normals = target.Normals;
        var found = false;

        for (var position = 0; position < target.PositionCount; position++) {
            var point = target.Positions[position];

            // The crease is the line where the wall meets the floor — x = 0 *and* z = 0 — and not
            // the whole of the wall, every vertex of which also has x = 0.
            if (MathF.Abs(point.X) > 1e-4f * scale || MathF.Abs(point.Z) > 1e-4f * scale) {
                continue;
            }

            var seen = new List<Vector3>();

            foreach (var face in target.FacesAt(position)) {
                var entry = target.Faces[face];
                var loop = target.CornersOf(face);

                for (var index = 0; index < loop.Length; index++) {
                    if (loop[index] == position) {
                        seen.Add(normals[entry.Start + index]);
                    }
                }
            }

            if (seen.Count < 2) {
                continue;
            }

            found = true;

            var apart = seen.Max(one => seen.Max(two => Vector3.Distance(one, two)));

            Assert.True(
                apart > 0.5f,
                $"At {point} the crease's corners agree to within {apart}: the fold was smoothed over."
            );
        }

        Assert.True(found, "No shared position on the crease was found, so nothing was asserted.");
    }

    /// <summary>A source with no normal layer still produces unit normals rather than zeros.</summary>
    /// <remarks>
    ///     ⚠ <b>A zero that means "off".</b> A boolean result, a marching-cubes surface and an
    ///     <c>.obj</c> with no <c>vn</c> lines all arrive without normals, and a transfer that read
    ///     the absent layer as black would write a mesh whose every shading normal is degenerate.
    /// </remarks>
    [Fact]
    public void A_source_with_no_normals_still_gives_the_output_some() {
        var source = TransferFixtures.Grid(8, 2f, _ => 0);
        var target = TransferFixtures.Grid(3, 2f, _ => 0);

        Assert.Empty(source.Normals.ToArray());

        AttributeTransfer.Transfer(source, SourceAttributes.None, target, new());

        Assert.Equal(target.CornerCount, target.Normals.Length);

        foreach (var normal in target.Normals) {
            Assert.InRange(normal.Length(), 0.99f, 1.01f);
            Assert.True(float.IsFinite(normal.X) && float.IsFinite(normal.Y) && float.IsFinite(normal.Z));
        }
    }

    /// <summary>A rigged mesh round-trips: the ramp comes out the ramp.</summary>
    /// <remarks>
    ///     <b>§ D12: this is what makes doc 33's characters remeshable</b>, and a generated humanoid
    ///     riggable. The fixture binds two bones by a linear ramp across the sheet, so the correct
    ///     answer at every output vertex is a closed form and the assertion is against it rather than
    ///     against a golden.
    /// </remarks>
    [Theory]
    [InlineData(1e-3f)]
    [InlineData(1f)]
    [InlineData(1e+3f)]
    public void Skinning_weights_round_trip_a_rigged_mesh(float scale) {
        var source = TransferFixtures.Scaled(TransferFixtures.Grid(24, 2f, _ => 0), scale);
        var target = TransferFixtures.Scaled(TransferFixtures.Grid(9, 2f, _ => 0), scale);
        var binding = TransferFixtures.Ramp(source, 2f * scale);

        var result = AttributeTransfer.Transfer(
            source,
            new() { Weights = binding },
            target,
            new()
        );

        Assert.NotNull(result.Weights);
        Assert.Equal(0, result.UnboundVertices);
        Assert.Equal(target.PositionCount, result.Weights.VertexCount);

        for (var vertex = 0; vertex < target.PositionCount; vertex++) {
            Assert.InRange(result.Weights.Total(vertex), 0.999f, 1.001f);

            var wanted = Math.Clamp((target.Positions[vertex].X / (2f * scale)) + 0.5f, 0f, 1f);
            var got = 0f;

            foreach (var influence in result.Weights.At(vertex)) {
                if (influence.Bone == 1) {
                    got = influence.Weight;
                }
            }

            Assert.InRange(got, wanted - 1e-3f, wanted + 1e-3f);
        }
    }

    /// <summary>Five influences into a target with room for four drops the smallest, every time.</summary>
    /// <remarks>
    ///     ⚠ <b>§ D12's clamp, and the reason it is not optional.</b> A target handed more than it
    ///     can hold silently loses one, and <i>which</i> one depends on the order the interpolation
    ///     accumulated them in — so the same asset re-imported after an unrelated change comes back
    ///     bound differently. Sorting by descending weight with the bone index as the tie-break makes
    ///     the survivor a function of the input.
    /// </remarks>
    [Fact]
    public void An_over_full_influence_set_is_clamped_to_the_targets_limit() {
        var source = TransferFixtures.Grid(4, 2f, _ => 0);
        var influences = new SkinInfluence[source.PositionCount * 8];

        for (var vertex = 0; vertex < source.PositionCount; vertex++) {
            // Eight bones, weights 8/36 … 1/36, so the four that must survive are bones 0 to 3 and
            // their renormalised total is (8+7+6+5)/26.
            for (var bone = 0; bone < 8; bone++) {
                influences[(vertex * 8) + bone] = new(bone, (8 - bone) / 36f);
            }
        }

        var target = TransferFixtures.Grid(2, 2f, _ => 0);

        var result = AttributeTransfer.Transfer(
            source,
            new() { Weights = new() { Influences = influences, Stride = 8 } },
            target,
            new() { MaxInfluences = 4 }
        );

        Assert.NotNull(result.Weights);
        Assert.Equal(4, result.Weights.Stride);

        for (var vertex = 0; vertex < target.PositionCount; vertex++) {
            var kept = result.Weights.At(vertex).ToList();

            Assert.Equal([0, 1, 2, 3], kept.Select(influence => influence.Bone));
            Assert.InRange(result.Weights.Total(vertex), 0.999f, 1.001f);
            Assert.InRange(kept[0].Weight, (8f / 26f) - 1e-4f, (8f / 26f) + 1e-4f);
        }
    }

    /// <summary>Weights that add to nothing stay nothing rather than being invented.</summary>
    /// <remarks>
    ///     ⚠ <b>A zero that means "off".</b> An unrigged prop inside a rigged mesh is a real input,
    ///     and normalising its zeros would divide by zero or — worse — attach it to bone zero, which
    ///     on a humanoid is the pelvis.
    /// </remarks>
    [Fact]
    public void An_unbound_source_comes_back_unbound_rather_than_attached_to_bone_zero() {
        var source = TransferFixtures.Grid(6, 2f, _ => 0);
        var target = TransferFixtures.Grid(3, 2f, _ => 0);
        var influences = new SkinInfluence[source.PositionCount * 4];

        var result = AttributeTransfer.Transfer(
            source,
            new() { Weights = new() { Influences = influences, Stride = 4 } },
            target,
            new()
        );

        Assert.NotNull(result.Weights);
        Assert.Equal(target.PositionCount, result.UnboundVertices);

        for (var vertex = 0; vertex < target.PositionCount; vertex++) {
            Assert.Equal(0f, result.Weights.Total(vertex));
        }
    }

    /// <summary>Colours interpolate, and an absent colour channel stays absent.</summary>
    [Fact]
    public void Colours_are_interpolated_and_an_absent_channel_stays_absent() {
        var source = TransferFixtures.Grid(16, 2f, _ => 0);
        var target = TransferFixtures.Grid(5, 2f, _ => 0);
        var colors = new Vector4[source.CornerCount];

        for (var face = 0; face < source.FaceCount; face++) {
            var entry = source.Faces[face];
            var loop = source.CornersOf(face);

            for (var index = 0; index < loop.Length; index++) {
                var t = (source.Positions[loop[index]].X / 2f) + 0.5f;

                colors[entry.Start + index] = new(t, 0f, 0f, 1f);
            }
        }

        var painted = AttributeTransfer.Transfer(source, new() { Colors = colors }, target, new());

        Assert.Equal(target.CornerCount, painted.Colors.Count);

        for (var face = 0; face < target.FaceCount; face++) {
            var entry = target.Faces[face];
            var loop = target.CornersOf(face);

            for (var index = 0; index < loop.Length; index++) {
                var wanted = (target.Positions[loop[index]].X / 2f) + 0.5f;

                Assert.InRange(painted.Colors[entry.Start + index].X, wanted - 0.05f, wanted + 0.05f);
                Assert.Equal(1f, painted.Colors[entry.Start + index].W, 3);
            }
        }

        var bare = AttributeTransfer.Transfer(source, SourceAttributes.None, TransferFixtures.Grid(5, 2f, _ => 0), new());

        Assert.Empty(bare.Colors);
        Assert.Null(bare.Weights);
    }

    /// <summary>The whole transfer gives the same answer at a thousandth and a thousand times scale.</summary>
    /// <remarks>
    ///     ⚠ <b>Four absolute-tolerance bugs were found in this repository in one day, all of them
    ///     <c>MathUtil.ZeroTolerance</c> guarding a quantity that carries the model's units.</b> A
    ///     cross product carries them squared and a barycentric denominator to the fourth, so the
    ///     scale at which a guard fires moves by six orders for a factor of ten in the model. The
    ///     answers compared here are scale-free by construction — a normal, a group index, a weight —
    ///     so anything that moves is a tolerance that should have been relative.
    /// </remarks>
    [Fact]
    public void The_transfer_answers_the_same_at_a_thousandth_and_a_thousand_times_scale() {
        var baseline = Answer(1f);

        foreach (var scale in (float[]) [1e-3f, 1e+3f]) {
            var scaled = Answer(scale);

            Assert.Equal(baseline.Groups, scaled.Groups);

            for (var at = 0; at < baseline.Normals.Length; at++) {
                Assert.True(
                    Vector3.Distance(baseline.Normals[at], scaled.Normals[at]) < 1e-4f,
                    $"Corner {at}'s normal moved from {baseline.Normals[at]} to {scaled.Normals[at]} at {scale}×."
                );
            }

            for (var at = 0; at < baseline.Weights.Length; at++) {
                Assert.Equal(baseline.Weights[at].Bone, scaled.Weights[at].Bone);
                Assert.InRange(scaled.Weights[at].Weight, baseline.Weights[at].Weight - 1e-4f, baseline.Weights[at].Weight + 1e-4f);
            }
        }
    }

    /// <summary>A source triangle with no area produces no <c>NaN</c> anywhere.</summary>
    /// <remarks>
    ///     ⚠ <b>The interior barycentric solution divides by zero there</b>, which is why
    ///     <c>ClosestTriangle</c> guarantees its weights sum to one on a degenerate triangle and why
    ///     nothing in the transfer renormalises them.
    /// </remarks>
    [Fact]
    public void A_source_with_a_degenerate_triangle_produces_no_nan() {
        var source = new EditMesh();
        var a = source.AddPosition(new(-1f, -1f, 0f));
        var b = source.AddPosition(new(1f, -1f, 0f));
        var c = source.AddPosition(new(1f, 1f, 0f));
        var d = source.AddPosition(new(-1f, 1f, 0f));

        source.AddFace([a, b, c, d]);

        // Three corners at one point: no area, no plane, no interior solution.
        var e = source.AddPosition(new(0f, 0f, 0f));

        source.AddFace([e, e, e]);

        var target = TransferFixtures.Grid(3, 2f, _ => 0);

        AttributeTransfer.Transfer(source, SourceAttributes.None, target, new());

        foreach (var normal in target.Normals) {
            Assert.False(float.IsNaN(normal.X) || float.IsNaN(normal.Y) || float.IsNaN(normal.Z));
        }
    }

    /// <summary>Everything one run of the transfer produced, at one scale, for comparison.</summary>
    static (int[] Groups, Vector3[] Normals, SkinInfluence[] Weights) Answer(float scale) {
        var source = TransferFixtures.Scaled(TransferFixtures.TwoGroups(32, 2f), scale);
        var target = TransferFixtures.Scaled(TransferFixtures.Grid(9, 2f, _ => 0, 0.3f), scale);
        var binding = TransferFixtures.Ramp(source, 2f * scale);

        var result = AttributeTransfer.Transfer(source, new() { Weights = binding }, target, new());
        var groups = new int[target.FaceCount];

        for (var face = 0; face < target.FaceCount; face++) {
            groups[face] = target.Faces[face].Group;
        }

        return (groups, target.Normals.ToArray(), [.. result.Weights!.Influences]);
    }

    /// <summary>Two sheets meeting at a right angle: one crease, and a smoothing group each side.</summary>
    static EditMesh Fold(int cells) {
        var mesh = new EditMesh();
        var indices = new int[(2 * cells) + 1, cells + 1];

        for (var i = 0; i <= 2 * cells; i++) {
            for (var j = 0; j <= cells; j++) {
                var t = ((float) i / cells) - 1f;
                var y = ((float) j / cells) - 0.5f;

                // t < 0 climbs the wall at x = 0; t >= 0 runs out along the floor.
                indices[i, j] = mesh.AddPosition(t < 0f ? new(0f, y, -t) : new(t, y, 0f));
            }
        }

        for (var i = 0; i < 2 * cells; i++) {
            for (var j = 0; j < cells; j++) {
                mesh.AddFace(
                    [indices[i, j], indices[i + 1, j], indices[i + 1, j + 1], indices[i, j + 1]],
                    0,
                    i < cells ? 1 : 2
                );
            }
        }

        return mesh;
    }
}
