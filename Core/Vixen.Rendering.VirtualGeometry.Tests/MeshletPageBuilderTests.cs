// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.VirtualGeometry;

/// <summary>
///     The page format: fixed-size pages, one quantization grid, and the roots in page zero.
/// </summary>
/// <remarks>
///     <para>
///         Phase 2's offline half. Phase 1 spent its whole effort making a group's locked boundary
///         <em>bit-identical</em> between a parent and its children — collapsing onto existing
///         vertices rather than onto a quadric's optimum, precisely so the lock would hold exactly.
///         This is the step that can throw all of that away in one line, by quantizing each cluster
///         against its own bound, and the tests that matter here are the ones that would catch it.
///     </para>
///     <para>
///         What such a mistake looks like is worth stating, because it is why it needs a test rather
///         than a reading: nothing throws, the pages are the right size, every cluster decodes to
///         something within half a step of where it should be, and the mesh has a hairline crack at
///         every group boundary at every distance. It is a picture, seen from the right angle.
///     </para>
/// </remarks>
public class MeshletPageBuilderTests {
    static MeshletPageSet Pack(MeshletMesh mesh, MeshletBuildInput input, int pageSize = 8 * 1024) =>
        MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = pageSize });

    /// <summary>Every cluster's geometry is somewhere, and inside the page it says it is in.</summary>
    [Fact]
    public void Every_cluster_is_placed_inside_its_page() {
        var input = Shapes.Sphere(4);
        var mesh = MeshletBuilder.Build(input);
        var pages = Pack(mesh, input);

        Assert.NotEmpty(pages.Pages);
        Assert.Equal(mesh.Meshlets.Length, pages.Clusters.Length);

        for (var i = 0; i < mesh.Meshlets.Length; i++) {
            var meshlet = mesh.Meshlets[i];
            var placement = pages.Clusters[i];
            var page = pages.Pages[placement.Page];

            var end = placement.TriangleOffset + (meshlet.TriangleCount * 3);

            Assert.InRange(placement.Page, 0, pages.Pages.Length - 1);
            Assert.True(placement.VertexOffset < placement.TriangleOffset);
            Assert.True(end <= page.Size, $"Cluster {i} runs past the end of page {placement.Page}.");
            Assert.True(page.Size <= pages.PageSize);
        }
    }

    /// <summary>
    ///     The roots are in page zero, which is what makes a never-streamed object draw.
    /// </summary>
    /// <remarks>
    ///     Not luck and not a sort that happens to work out: clusters are packed coarsest level
    ///     first, and a root is by definition at the coarsest level there is. The whole degradation
    ///     story rests on this one page being pinnable.
    /// </remarks>
    [Fact]
    public void The_roots_are_in_page_zero() {
        var input = Shapes.Sphere(4);
        var mesh = MeshletBuilder.Build(input);
        var pages = Pack(mesh, input);

        Assert.All(mesh.Roots, root => Assert.Equal(0, pages.Clusters[root].Page));
    }

    /// <summary>Pages are coarsest first, so an early page is a page every view can want.</summary>
    [Fact]
    public void Pages_are_ordered_coarsest_first() {
        var input = Shapes.Sphere(4);
        var mesh = MeshletBuilder.Build(input);
        var pages = Pack(mesh, input);

        for (var i = 1; i < pages.Pages.Length; i++) {
            Assert.True(
                pages.Pages[i].CoarsestLevel <= pages.Pages[i - 1].CoarsestLevel,
                $"Page {i} is coarser than page {i - 1}, so a page's index says nothing about its level."
            );
        }
    }

    /// <summary>
    ///     A vertex two clusters share decodes to the same bits in both of them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The test the format exists for.</b> A locked boundary vertex is referenced by a
    ///         cluster on each side; the two clusters have different bounds; and quantizing against
    ///         those bounds — the obvious way to spend sixteen bits well — rounds the same position
    ///         to two different numbers. What that produces is a slit along every group boundary that
    ///         no amount of correct DAG-building can close, because the DAG was right and the last
    ///         step was not.
    ///     </para>
    ///     <para>
    ///         Bit-identical, asserted as equality rather than as a tolerance. "Within a rounding
    ///         error" is exactly what a crack is.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_shared_vertex_decodes_identically_in_every_cluster_that_holds_it() {
        var input = Shapes.Sphere(4);
        var mesh = MeshletBuilder.Build(input);
        var pages = Pack(mesh, input);

        // Source vertex -> what the first cluster to reference it decoded to.
        var seen = new Dictionary<int, Vector3>();
        var shared = 0;

        for (var i = 0; i < mesh.Meshlets.Length; i++) {
            var meshlet = mesh.Meshlets[i];
            var decoded = new Vector3[meshlet.VertexCount];
            pages.GetPositions(i, meshlet.VertexCount, decoded);

            for (var v = 0; v < meshlet.VertexCount; v++) {
                var source = mesh.Vertices[meshlet.VertexOffset + v];

                if (seen.TryGetValue(source, out var first)) {
                    Assert.True(
                        first == decoded[v],
                        $"Source vertex {source} decodes to {first} in one cluster and {decoded[v]} in {i}."
                    );

                    shared++;
                    continue;
                }

                seen[source] = decoded[v];
            }
        }

        // Not a vacuous pass: a DAG whose clusters shared no vertices would satisfy the loop above
        // trivially, and would also not be a DAG of a closed mesh.
        Assert.True(shared > 100, $"Only {shared} shared references — the fixture is not exercising the property.");
    }

    /// <summary>
    ///     Quantization moves a vertex by at most half a grid step, and the set says how much that is.
    /// </summary>
    /// <remarks>
    ///     A number worth reporting rather than assuming, because it is a floor under every level's
    ///     error — including level zero's, which phase 1 reports as exactly zero on the grounds that
    ///     level zero <em>is</em> the original mesh. After this it is not, by up to this much.
    /// </remarks>
    [Fact]
    public void No_vertex_moves_further_than_half_a_step() {
        var input = Shapes.Sphere(4);
        var mesh = MeshletBuilder.Build(input);
        var pages = Pack(mesh, input);

        var worst = 0f;

        for (var i = 0; i < mesh.Meshlets.Length; i++) {
            var meshlet = mesh.Meshlets[i];
            var decoded = new Vector3[meshlet.VertexCount];
            pages.GetPositions(i, meshlet.VertexCount, decoded);

            for (var v = 0; v < meshlet.VertexCount; v++) {
                var source = input.Positions[mesh.Vertices[meshlet.VertexOffset + v]];
                worst = MathF.Max(worst, (decoded[v] - source).Length());
            }
        }

        // Half a step per axis, so the worst a three-dimensional offset can be is √3 halves. Stated
        // as the diagonal rather than as the reported error, which is the per-axis number.
        Assert.True(
            worst <= pages.QuantizationError * MathF.Sqrt(3f) * 1.001f,
            $"A vertex moved {worst}, and half a step is {pages.QuantizationError}."
        );

        // And the error is far below the finest level that is not level zero, or the pages would be
        // changing what the DAG says it drew.
        var finest = mesh.Meshlets.Where(m => m.Error > 0f).Min(m => m.Error);
        Assert.True(
            pages.QuantizationError < finest * 0.1f,
            $"Quantization costs {pages.QuantizationError} against a finest level error of {finest}."
        );
    }

    /// <summary>The corners survive the round trip, byte for byte.</summary>
    [Fact]
    public void Triangle_corners_round_trip() {
        var input = Shapes.Sphere(3);
        var mesh = MeshletBuilder.Build(input);
        var pages = Pack(mesh, input);

        for (var i = 0; i < mesh.Meshlets.Length; i++) {
            var meshlet = mesh.Meshlets[i];
            var packed = pages.GetCorners(i, meshlet.TriangleCount);
            var original = mesh.Triangles.AsSpan(meshlet.TriangleOffset * 3, meshlet.TriangleCount * 3);

            Assert.True(packed.SequenceEqual(original), $"Cluster {i}'s corners did not round-trip.");
        }
    }

    /// <summary>Attributes are copied through verbatim, at the vertex they belong to.</summary>
    /// <remarks>
    ///     Only the position is this format's business. An attribute blob that arrived reordered, or
    ///     shifted by a vertex, would be a mesh whose normals belong to its neighbours — which shades
    ///     as a mesh with the wrong smoothing rather than as anything obviously broken.
    /// </remarks>
    [Fact]
    public void Attributes_travel_with_their_vertex() {
        var input = Shapes.Sphere(3);
        var mesh = MeshletBuilder.Build(input);

        // One recognisable int per vertex, so a misplacement is a wrong number rather than a subtly
        // wrong normal.
        var attributes = new byte[input.VertexCount * 4];

        for (var v = 0; v < input.VertexCount; v++) {
            BitConverter.TryWriteBytes(attributes.AsSpan(v * 4), v * 7);
        }

        var pages = MeshletPageBuilder.Build(
            mesh,
            input.Positions,
            attributes,
            new() { PageSize = 8 * 1024, AttributeStride = 4 }
        );

        Assert.Equal(MeshletPageBuilder.PositionSize + 4, pages.VertexStride);

        for (var i = 0; i < mesh.Meshlets.Length; i++) {
            var meshlet = mesh.Meshlets[i];
            var placement = pages.Clusters[i];
            var start = pages.Pages[placement.Page].Offset + placement.VertexOffset;

            for (var v = 0; v < meshlet.VertexCount; v++) {
                var source = mesh.Vertices[meshlet.VertexOffset + v];
                var at = start + (v * pages.VertexStride) + MeshletPageBuilder.PositionSize;

                Assert.Equal(source * 7, BitConverter.ToInt32(pages.Data, at));
            }
        }
    }

    /// <summary>A page size no cluster fits in is refused, rather than producing pages nothing reads.</summary>
    [Fact]
    public void A_cluster_that_cannot_fit_a_page_is_refused() {
        var input = Shapes.Sphere(3);
        var mesh = MeshletBuilder.Build(input);

        var thrown = Assert.Throws<ArgumentException>(
            () => MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 1024 })
        );

        Assert.Contains("page is 1024", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A degenerate mesh gets a grid rather than a division by zero.</summary>
    [Fact]
    public void A_mesh_with_no_extent_still_packs() {
        var positions = new Vector3[3];
        var input = new MeshletBuildInput { Positions = positions, Indices = [0, 1, 2] };
        var mesh = MeshletBuilder.Build(input);

        var pages = MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 2048 });

        Assert.True(pages.QuantizationStep > 0f);
        Assert.NotEmpty(pages.Pages);
    }
}
