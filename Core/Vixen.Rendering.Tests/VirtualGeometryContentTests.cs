// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Tests;

/// <summary>
///     From the bytes a build wrote to a mesh a frame can draw.
/// </summary>
/// <remarks>
///     <para>
///         <b>The link that was missing, and it was missing in a way nothing could report.</b> The
///         importer wrote three artefacts per mesh and nothing outside its own tests read any of them
///         back; <c>VirtualGeometryRenderFeature.Register</c> took a hierarchy and a page set from a
///         caller that did not exist. Every stage from import to shaded pixel was tested, and the two
///         halves had never been introduced.
///     </para>
///     <para>
///         What is asserted here is the join rather than either side of it: that the records
///         deserialise into a pair that agrees with itself, that a mismatched pair is refused rather
///         than drawn, and that one call leaves the mesh registered <em>and</em> its blob reachable —
///         because doing those separately is how a mesh comes to be registered against a blob nobody
///         added, which draws its root page and nothing below it.
///     </para>
/// </remarks>
public sealed class VirtualGeometryContentTests {
    /// <summary>The two record artefacts deserialise into a mesh that agrees with itself.</summary>
    [Fact]
    public void The_records_read_back_as_the_mesh_that_was_built() {
        var (mesh, pages) = Build();

        var asset = VirtualGeometryContent.Read(Serializer.ToBytes(mesh), Serializer.ToBytes(pages.WithoutData()));

        Assert.Equal(mesh.Meshlets.Length, asset.Hierarchy.Meshlets.Length);
        Assert.Equal(pages.Clusters.Length, asset.Pages.Clusters.Length);
        Assert.Equal(pages.VertexStride, asset.Pages.VertexStride);
        Assert.Equal(pages.QuantizationStep, asset.Pages.QuantizationStep);

        // And the geometry is not in it, which is the whole reason the blob is a separate artefact: a
        // set carrying its own data is a single artefact whose deserialisation reads every page.
        Assert.False(asset.Pages.HasData);
        Assert.True(asset.Pages.Pages.Length > 0);
    }

    /// <summary>
    ///     Two artefacts from different builds are refused rather than decoded.
    /// </summary>
    /// <remarks>
    ///     The failure this prevents does not look like a failure. A placement per cluster indexed as
    ///     the hierarchy's clusters are is meaningless when the two counts differ — every offset lands
    ///     somewhere, and what comes out is a mesh, just not this one. A cache that kept one artefact
    ///     across a re-import is the ordinary way to get here.
    /// </remarks>
    [Fact]
    public void Artefacts_from_different_builds_are_refused() {
        var coarse = Build(8);
        var fine = Build(24);

        Assert.NotEqual(coarse.Mesh.Meshlets.Length, fine.Mesh.Meshlets.Length);

        var thrown = Assert.Throws<ArgumentException>(
            () => VirtualGeometryContent.Read(
                Serializer.ToBytes(coarse.Mesh),
                Serializer.ToBytes(fine.Pages.WithoutData())
            )
        );

        Assert.Contains("different builds", thrown.Message, StringComparison.Ordinal);

        // An empty artefact is the other half of the same question, and is what a missing sub-asset
        // looks like from here.
        Assert.Throws<ArgumentException>(() => VirtualGeometryContent.Read([], Serializer.ToBytes(coarse.Pages)));
        Assert.Throws<ArgumentException>(() => VirtualGeometryContent.Read(Serializer.ToBytes(coarse.Mesh), []));
    }

    /// <summary>
    ///     One call registers the mesh and makes its pages reachable, and a page comes back.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The end-to-end claim of the loader: after this, a draw naming the returned index reaches
    ///         geometry. The page read is what proves the blob was wired to the same source id the
    ///         registration used — the two are one argument here precisely so they cannot disagree.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task One_call_registers_the_mesh_and_reaches_its_pages() {
        using var device = new NullDevice();
        using var geometry = new VirtualGeometrySystem(device, slots: 16, pageSize: 8 * 1024);

        var (mesh, pages) = Build();

        var index = geometry.Content(
            7,
            Serializer.ToBytes(mesh),
            Serializer.ToBytes(pages.WithoutData()),
            new MemoryStream(pages.Data)
        );

        Assert.Equal(0, index);
        Assert.Equal(1, geometry.MeshCount);
        Assert.Equal(pages.Pages.Length, geometry.Visibility.PageCount);

        // The blob answers for the id the registration used. A source that had never been given the
        // bytes returns nothing here and the mesh draws at its coarsest level for ever, which is a
        // working frame showing the wrong thing.
        var page = new byte[pages.PageSize];
        var read = await geometry.Source.ReadAsync(new(7, 0), page, TestContext.Current.CancellationToken);

        Assert.Equal(pages.Pages[0].Size, read);
        Assert.Equal(pages.BytesOf(0).ToArray(), page.AsSpan(0, pages.Pages[0].Size).ToArray());
    }

    /// <summary>
    ///     The stack wires every pass to every other, which is what a host cannot be asked to do.
    /// </summary>
    /// <remarks>
    ///     Six objects and eleven references between them. A host that set ten of the eleven gets a
    ///     frame that draws nothing and reports no reason — which is the failure this class exists to
    ///     make impossible rather than unlikely.
    /// </remarks>
    [Fact]
    public void The_stack_is_wired_to_itself() {
        using var device = new NullDevice();
        using var geometry = new VirtualGeometrySystem(device, slots: 16, pageSize: 8 * 1024);

        Assert.Same(geometry.Visibility, geometry.Feature.Visibility);
        Assert.Same(geometry.Pages, geometry.Feature.Pages);
        Assert.Same(geometry.Residency, geometry.Visibility.Residency);
        Assert.Same(geometry.Visibility, geometry.Raster.Visibility);
        Assert.Same(geometry.Pages, geometry.Raster.Pages);
        Assert.Same(geometry.Visibility, geometry.Tiles.Visibility);
        Assert.Same(geometry.Visibility, geometry.Resolve.Visibility);
        Assert.Same(geometry.Tiles, geometry.Resolve.Tiles);
        Assert.Same(geometry.Pages, geometry.Resolve.Pages);

        // The budget is the pool, which is what makes a scene's streaming cost one number.
        Assert.Equal(16L * 8 * 1024, geometry.Residency.Budget);
    }

    static (MeshletMesh Mesh, MeshletPageSet Pages) Build(int segments = 16) {
        var input = Grid(segments);
        var mesh = MeshletBuilder.Build(input);

        return (mesh, MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 8 * 1024 }));
    }

    /// <summary>A tessellated quad: enough triangles for a hierarchy with something under its roots.</summary>
    static MeshletBuildInput Grid(int segments) {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var y = 0; y <= segments; y++) {
            for (var x = 0; x <= segments; x++) {
                positions.Add(new((float)x / segments, 0f, (float)y / segments));
            }
        }

        for (var y = 0; y < segments; y++) {
            for (var x = 0; x < segments; x++) {
                var a = (y * (segments + 1)) + x;
                var b = a + 1;
                var c = a + segments + 1;
                var d = c + 1;

                indices.AddRange([a, c, b]);
                indices.AddRange([b, c, d]);
            }
        }

        return new() { Positions = [.. positions], Indices = [.. indices] };
    }
}
