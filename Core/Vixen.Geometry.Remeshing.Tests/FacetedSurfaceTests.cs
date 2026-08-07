// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The surface a generator makes, which the block-out corpus does not reach.</summary>
/// <remarks>
///     <para>
///         <b>Every fixture in <c>RemesherTests</c> comes out of <see cref="MeshShapes" /> or a boolean
///         of two of them: flat walls, sharp rims, a few hundred triangles and groups that somebody
///         assigned.</b> An image-to-3D output is none of those — thirteen to twenty-five thousand
///         triangles, faceted at the scale of one of them, no crease anywhere and no material boundary
///         at all — and all sixteen of the ones this was measured on failed in ways nothing in the
///         corpus reached.
///     </para>
///     <para>
///         ⚠ <b>Two of the three failures were a stage that had a usable answer and threw it away.</b>
///         <see cref="PatchLayout" /> reached a fixed point where <c>Merge</c> and <c>Extend</c> undid
///         each other every round — 398 patches, 391 of them perfectly usable — and refused because a
///         repair was still being asked for; <see cref="Quantizer" /> solved the consistency system,
///         raised a lower bound to force eight collapsed patches open, found <i>that</i> unsolvable, and
///         refused all 312. Both now leave the patch out and say so, which is what every other repair in
///         those two stages already does.
///     </para>
/// </remarks>
public class FacetedSurfaceTests {
    /// <summary>One set of group ids, two readings, and only the assignment is a crease.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The defect, at the stage that caused it, and its fix in the same test.</b> § D4
    ///         lists a face-group boundary as one of five feature sources because it is a material
    ///         boundary. The mesh here is smooth everywhere — no dihedral angle anywhere near the
    ///         threshold — and is cut into two groups down the middle, so the ring between them is the
    ///         only thing that can produce a chain. Called an assignment it does; called the coplanarity
    ///         guess it must not, because that is the reading a mesh out of
    ///         <see cref="EditMesh.FromTriangles" /> arrives with.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Why the per-triangle grouping is not the fixture here, though it is the input that
    ///         found this.</b> With a group per triangle every vertex is a feature corner, so every
    ///         chain is one edge long and the prune loop clears all of them — the damage shows up in the
    ///         cross field and the layout rather than in the polyline count, which is exactly why it was
    ///         invisible until a whole model refused.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(MeshGroupSource.Assigned, true)]
    [InlineData(MeshGroupSource.Coplanarity, false)]
    public void A_group_boundary_is_a_crease_only_where_somebody_assigned_it(MeshGroupSource source, bool crease) {
        var mesh = Halved(24, 16);

        mesh.GroupSource = source;

        var features = FeatureDetector.Detect(FieldFixtures.Condition(mesh), new());
        var found = features.Polylines.Any(chain => (chain.Sources & FeatureSource.Group) != default);

        Assert.Equal(crease, found);
    }

    /// <summary>A faceted surface's own grouping produces no creases at all.</summary>
    /// <remarks>
    ///     The mesh is smooth, so § D4 has nothing to find on it — and what it used to find was the
    ///     coplanarity guess, on nearly every edge.
    /// </remarks>
    [Fact]
    public void A_faceted_surface_has_no_creases_on_it() {
        var mesh = Faceted(24, 16);

        Assert.Equal(MeshGroupSource.Coplanarity, mesh.GroupSource);

        var features = FeatureDetector.Detect(FieldFixtures.Condition(mesh), new());

        Assert.All(features.Polylines, chain => Assert.Equal(default, chain.Sources & FeatureSource.Group));
    }

    /// <summary>A faceted surface comes back as quads rather than as a refusal.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the whole verb, and thirteen of sixteen real meshes used to fail it.</b> The
    ///     result is allowed to have holes in it — a patch that could not be repaired and a patch that
    ///     would not quantize open are both left out, and the report says how many — but it may not be
    ///     nothing, and what does come out is all quads.
    /// </remarks>
    [Fact]
    public void A_faceted_surface_comes_back_as_quads() {
        var quads = Remesher.Remesh(Faceted(24, 16), new() { TargetQuads = 400 }, out var report);

        Assert.False(quads.IsEmpty, string.Join(" · ", report.Warnings));
        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));
        Assert.Equal(0, report.NonQuadCount);
    }

    /// <summary>A partition that still wants repairing at the cap is a result, not a refusal.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The layout half of the real-mesh failure, held at the stage rather than through the
    ///         verb.</b> Thirteen of sixteen image-to-3D meshes refused here: <c>Merge</c> dissolved a
    ///         small patch's longest arc, <c>Extend</c> walked the loose end straight back out along the
    ///         same edges, and the two undid each other every round while both reported progress —
    ///         measured, rounds four, five and six were identical at 398 patches with 391 of them
    ///         usable, and <c>IsUsable</c> came back false.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The corpus does not reproduce the oscillation and this test does not try to.</b>
    ///         What it holds is the rule that made the oscillation fatal: a flooded partition with
    ///         patches in it is usable. There is exactly one way to be unusable now — nothing came out
    ///         at all — and <see cref="A_mesh_with_nothing_in_it_still_names_the_stage_that_refused" />
    ///         is that side of it.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("cylinder")]
    [InlineData("sphere")]
    [InlineData("union")]
    [InlineData("difference")]
    public void A_flooded_partition_is_usable(string name) {
        var layout = RemesherTests.Layout(name, out _, out _);

        Assert.NotEmpty(layout.Patches);
        Assert.True(layout.IsUsable, string.Join(" · ", layout.Warnings));

        Assert.DoesNotContain(
            layout.Warnings,
            warning => warning.Contains("still had unusable patches", StringComparison.Ordinal)
        );
    }

    /// <summary>A quantization that cannot open every collapsed patch keeps the answer it had.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The quantize half, and the scale lever is what makes it reachable from the block-out
    ///         corpus.</b> <c>Quantizer.Solve</c>'s <c>scale</c> multiplies every arc's target, so a very
    ///         small one drives whole patches to zero in both directions — the same position two of the
    ///         sixteen real meshes reached on their own, where round zero solved cleanly with eight
    ///         collapsed patches out of 312 and round one, with the lower bounds raised to force them
    ///         open, came back unsolvable and refused all 312.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Feasible with holes rather than optimal.</b> The counts that come back satisfy every
    ///         consistency constraint, so the patches that did quantize extract exactly as they would
    ///         have; the ones that did not are skipped by <c>PatchExtractor</c> and counted. What must
    ///         not happen is the whole model coming back empty because a repair overshot.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("cylinder")]
    [InlineData("sphere")]
    [InlineData("union")]
    public void A_quantization_that_cannot_open_every_patch_is_still_an_answer(string name) {
        var layout = RemesherTests.Layout(name, out _, out _);
        var quantization = Quantizer.Solve(layout, QuantizeMode.Exact, 0.001f);

        Assert.True(quantization.IsFeasible, string.Join(" · ", quantization.Warnings));
        Assert.Equal(layout.Arcs.Count, quantization.Counts.Count);
    }

    /// <summary>A refusal still names its stage, so the one that cannot be remeshed says which.</summary>
    /// <remarks>
    ///     docs/plan/41's robustness criterion is "a valid all-quad result <i>or</i> a report naming the
    ///     stage that refused". Loosening two stages from refusing must not turn a refusal into silence,
    ///     so the empty mesh — which has nothing to condition — still comes back with a reason.
    /// </remarks>
    [Fact]
    public void A_mesh_with_nothing_in_it_still_names_the_stage_that_refused() {
        var quads = Remesher.Remesh(new(), new() { TargetQuads = 400 }, out var report);

        Assert.True(quads.IsEmpty);
        Assert.NotEmpty(report.Warnings);
    }

    /// <summary>A smooth dome cut into two groups down the middle, which is what a material looks like.</summary>
    static EditMesh Halved(int around, int up) {
        var mesh = Smooth(around, up);

        for (var face = 0; face < mesh.FaceCount; face++) {
            var middle = Vector3.Zero;
            var loop = mesh.CornersOf(face);

            foreach (var corner in loop) {
                middle += mesh.Positions[corner];
            }

            mesh.SetGroup(face, middle.X < 0f ? 3 : 7);
        }

        return mesh;
    }

    /// <summary>The same dome without the roughness, so that no dihedral angle is a feature.</summary>
    static EditMesh Smooth(int around, int up) => Dome(around, up, 0f);

    /// <summary>A dome as a triangle soup, roughened so that hardly any two neighbours are coplanar.</summary>
    /// <remarks>
    ///     ⚠ Built through <see cref="EditMesh.FromTriangles" />, because the coplanarity grouping that
    ///     starts the failure is what <c>FromTriangles</c> does on the way in. The roughness is a fixed
    ///     hash of the grid indices rather than a random number: a fixture that differs between runs is
    ///     a test that reports a different thing every time.
    /// </remarks>
    internal static EditMesh Faceted(int around, int up) => Dome(around, up, 0.02f);

    /// <summary>The dome both fixtures are built from, with the radial roughness as a parameter.</summary>
    static EditMesh Dome(int around, int up, float roughness) {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var ring = 0; ring <= up; ring++) {
            var phi = MathF.PI * 0.5f * ring / up;

            for (var step = 0; step < around; step++) {
                var theta = MathF.Tau * step / around;
                var noise = ((ring * 73856093) ^ (step * 19349663) ^ ((ring + step) * 83492791)) & 0xFFFF;
                var rough = 1f + (roughness * noise / 0xFFFF);

                positions.Add(
                    rough
                    * new Vector3(
                        MathF.Cos(theta) * MathF.Cos(phi),
                        MathF.Sin(phi),
                        MathF.Sin(theta) * MathF.Cos(phi)
                    )
                );
            }
        }

        for (var ring = 0; ring < up; ring++) {
            for (var step = 0; step < around; step++) {
                var next = (step + 1) % around;
                var low = ring * around;
                var high = (ring + 1) * around;

                indices.AddRange([low + step, high + step, high + next]);
                indices.AddRange([low + step, high + next, low + next]);
            }
        }

        return EditMesh.FromTriangles([.. positions], [.. indices]);
    }
}
