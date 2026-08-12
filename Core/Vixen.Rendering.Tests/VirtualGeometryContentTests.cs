// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Graphics;
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

    /// <summary>
    ///     Releasing a mesh gives its pinned root page back, and the next level's mesh gets it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A level unload used to shrink the pool permanently.</b> Registering pins a root page
    ///         and a pinned page is never evicted, and there was no unregister anywhere: not on
    ///         <c>GpuClusterVisibility</c>, not on the feature, not on this system. So a project that
    ///         loaded a level and unloaded it kept one slot per mesh, for ever, and the only symptom was
    ///         that eventually meshes stopped drawing.
    ///     </para>
    ///     <para>
    ///         The pool is sized to exactly one mesh's root page here, so "the slot came back" is the
    ///         difference between the second registration working and it throwing.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Releasing_a_mesh_returns_its_pinned_page_to_the_pool() {
        using var device = new NullDevice();

        var (mesh, pages) = Build();

        // One slot: one mesh's root page is the whole pool, so "the slot came back" is the difference
        // between the second registration working and it refusing.
        using var geometry = new VirtualGeometrySystem(device, slots: 1, pageSize: 8 * 1024);

        var first = Load(geometry, 1, mesh, pages);

        Assert.Equal(0, first);
        Assert.Equal(1, geometry.Feature.RegisteredMeshes);

        // The root page is pinned before it has arrived, which is the point of pinning at load time.
        Settle(device, geometry);
        Assert.Equal(1, geometry.Residency.PinnedPages);

        Assert.True(geometry.Release(first));

        Assert.Equal(0, geometry.Feature.RegisteredMeshes);
        Assert.Equal(0, geometry.Residency.PinnedPages);
        Assert.Equal(0, geometry.Residency.ResidentPages);

        // The blob went with it, so a level nobody is drawing does not hold its pages open.
        var page = new byte[pages.PageSize];
        var read = await geometry.Source.ReadAsync(new(1, 0), page, TestContext.Current.CancellationToken);

        Assert.Equal(0, read);

        // Releasing twice is not an error and does not give the slot back twice.
        Assert.False(geometry.Release(first));

        // And the slot really came back: the next level's mesh registers, pins and becomes resident.
        var second = Load(geometry, 2, mesh, pages);

        Assert.Equal(1, second);
        Assert.Equal(1, geometry.Feature.RegisteredMeshes);

        Settle(device, geometry);
        Assert.Equal(1, geometry.Residency.PinnedPages);
        Assert.True(geometry.Residency.IsResident(new(2, 0)));

        // ⚠ The retired registration keeps its number and draws nothing, rather than being compacted
        // away — an object still holding index 0 must not find itself drawing index 1's geometry.
        Assert.Equal(2, geometry.MeshCount);
        Assert.Equal(0, geometry.Visibility.MeshAt(first).RootCount);
        Assert.Equal(0, geometry.Visibility.MeshAt(first).PageCount);
        Assert.True(geometry.Visibility.MeshAt(second).RootCount > 0);
        Assert.Equal(2, geometry.Visibility.MeshAt(second).Source);
    }

    /// <summary>
    ///     Registering more meshes than the pool can pin says so, rather than drawing nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A scene of ≥<c>slots</c> virtualized meshes, at one slot instead of 512.</b> Every
    ///     registration pins a root page and a pinned page is never evicted, so once the pool is full of
    ///     them every further mesh used to be dropped from the request queue in silence — resident never,
    ///     re-requested never, and <see cref="PageResidency.Rejections" /> reading zero.
    /// </remarks>
    [Fact]
    public void Registering_more_meshes_than_the_pool_can_pin_says_so() {
        using var device = new NullDevice();
        using var geometry = new VirtualGeometrySystem(device, slots: 1, pageSize: 8 * 1024);

        var (mesh, pages) = Build();

        Load(geometry, 1, mesh, pages);

        var refused = Assert.Throws<PageBudgetException>(() => Load(geometry, 2, mesh, pages));

        Assert.Equal(1, refused.Capacity);
        Assert.Equal(2, refused.Pinned);
        Assert.Contains("slot count", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Runs frames until the pinned root page has landed, or the patience runs out.</summary>
    /// <remarks>
    ///     ⚠ <b>Flushed like a frame, because the staging region is reclaimed at the flush.</b> The pool
    ///     stages one slot's bytes per pool slot, so a loop that only serviced would fill it and every
    ///     placement after the first would be refused — which is a real contract and not a test artefact.
    /// </remarks>
    static void Settle(IGraphicsDevice device, VirtualGeometrySystem geometry) {
        var waited = Stopwatch.StartNew();

        // Thirty rather than ten, the same as every other settle in the tree that a loaded runner has
        // caught out. A deadline only costs time on a build that is already failing.
        while (waited.Elapsed < TimeSpan.FromSeconds(30)) {
            geometry.Residency.Service();
            Flush(device, geometry.Pages);

            if (geometry.Residency.PendingRequests == 0 && geometry.Residency.Loading == 0) {
                geometry.Residency.Service();
                Flush(device, geometry.Pages);

                return;
            }

            Thread.Sleep(1);
        }
    }

    static void Flush(IGraphicsDevice device, MeshletPagePool pool) {
        using var list = device.BeginCommandList(QueueKind.Compute);

        pool.Flush(list);
        list.Finish();
        device.ComputeQueue.Submit([list]);
    }

    static int Load(VirtualGeometrySystem geometry, int id, MeshletMesh mesh, MeshletPageSet pages) =>
        geometry.Content(
            id,
            Serializer.ToBytes(mesh),
            Serializer.ToBytes(pages.WithoutData()),
            new MemoryStream(pages.Data)
        );

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
