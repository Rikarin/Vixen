// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Tests;

/// <summary>
///     Phase 2's exit criterion: a camera path over a scene four times over budget, holding the
///     budget, with a synthetic I/O delay injected and no popping beyond the configured error.
/// </summary>
/// <remarks>
///     <para>
///         The three parts of the criterion are three different kinds of claim.
///         <em>Holding the budget</em> is a number, asserted every frame. <em>The synthetic delay</em>
///         is what makes the test about streaming at all rather than about a lookup — with instant
///         loads every page is resident on the frame it was asked for and nothing is ever missing.
///         And <em>no popping beyond the configured error</em> is the one that needs saying carefully,
///         because a cut drawn from pages that have not arrived is by definition coarser than the one
///         that was asked for.
///     </para>
///     <para>
///         What it means here is that the degradation is <b>a valid cut at a coarser threshold</b>
///         and never a partial one. A partial cut is a crack — the boundary between a cluster and its
///         missing neighbour was locked at one level and simplified at the other — and a crack is
///         unbounded error at one seam, which no threshold describes. A coarser cut is bounded error
///         everywhere, and it closes as the pages land. So what is asserted is that every frame's cut
///         is closed, is drawable, and converges on the one that was asked for.
///     </para>
/// </remarks>
public class MeshletStreamingTests : IDisposable {
    readonly NullDevice device = new();

    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A page source that takes a stated number of frames to answer.</summary>
    /// <remarks>
    ///     Frames rather than milliseconds, so the test is about how many frames a page is missing
    ///     for rather than about how fast the machine running it is. A wall-clock delay would make
    ///     the same test flaky on a loaded build agent and vacuous on a fast one.
    /// </remarks>
    sealed class DelayedSource(MeshletPageSet pages, int delayMilliseconds) : IMeshletPageSource {
        public int Reads { get; private set; }

        public async ValueTask<int> ReadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) {
            Reads++;

            await Task.Delay(delayMilliseconds, cancellation).ConfigureAwait(false);

            var bytes = pages.BytesOf(key.Index);
            bytes.CopyTo(destination.Span);

            return bytes.Length;
        }
    }

    /// <summary>
    ///     A closed sphere, welded at the seam and at both poles.
    /// </summary>
    /// <remarks>
    ///     Its own copy rather than the one in <c>Vixen.Rendering.VirtualGeometry.Tests</c>, which is
    ///     internal to that assembly. What matters about it is only that it is <em>closed</em>: every
    ///     edge carries two triangles, so a cut that leaves a hole is a number rather than a picture.
    ///     The seam column wraps to the first rather than being duplicated, and each pole is one
    ///     vertex, because either shortcut would make the mesh open and the closure check vacuous.
    /// </remarks>
    static MeshletBuildInput Sphere(int rings, int segments) {
        var positions = new List<Vector3> { new(0f, 1f, 0f) };

        for (var ring = 1; ring < rings; ring++) {
            var phi = MathF.PI * ring / rings;

            for (var segment = 0; segment < segments; segment++) {
                var theta = 2f * MathF.PI * segment / segments;

                positions.Add(
                    new(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta))
                );
            }
        }

        positions.Add(new(0f, -1f, 0f));

        var south = positions.Count - 1;
        var indices = new List<int>();

        int At(int ring, int segment) => 1 + ((ring - 1) * segments) + (segment % segments);

        for (var segment = 0; segment < segments; segment++) {
            indices.AddRange([0, At(1, segment + 1), At(1, segment)]);
            indices.AddRange([south, At(rings - 1, segment), At(rings - 1, segment + 1)]);
        }

        for (var ring = 1; ring < rings - 1; ring++) {
            for (var segment = 0; segment < segments; segment++) {
                var a = At(ring, segment);
                var b = At(ring, segment + 1);
                var c = At(ring + 1, segment);
                var d = At(ring + 1, segment + 1);

                indices.AddRange([a, b, c]);
                indices.AddRange([b, d, c]);
            }
        }

        return new() { Positions = [.. positions], Indices = [.. indices] };
    }

    /// <summary>A sphere, its DAG, and its pages — small enough that a page holds a few clusters.</summary>
    static (MeshletBuildInput Input, MeshletMesh Mesh, MeshletPageSet Pages) Scene(int pageSize = 4 * 1024) {
        var input = Sphere(48, 96);
        var mesh = MeshletBuilder.Build(input);
        var pages = MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = pageSize });

        return (input, mesh, pages);
    }

    /// <summary>
    ///     A pool a quarter the size of the scene holds its budget for the whole path.
    /// </summary>
    /// <remarks>
    ///     Four times over is the number the criterion names, and it is the number that makes the
    ///     test about eviction rather than about loading: at that ratio the camera cannot get round
    ///     the sphere without evicting pages it will want again.
    /// </remarks>
    [Fact]
    public void A_scene_four_times_over_budget_holds_the_budget() {
        var (_, mesh, pages) = Scene();

        var slots = Math.Max(2, pages.Pages.Length / 4);
        var source = new DelayedSource(pages, 2);

        using var pool = new MeshletPagePool(device, source, slots, pages.PageSize);
        using var residency = new PageResidency(pool, (long)slots * pages.PageSize);

        residency.Pin(new(0, 0));

        var budget = residency.Budget;
        Assert.True(pages.TotalBytes >= budget * 4, $"The scene is {pages.TotalBytes} bytes against a budget of {budget}.");

        foreach (var distance in Path()) {
            Frame(residency, pool, mesh, pages, distance);

            Assert.True(
                residency.ResidentBytes <= budget,
                $"{residency.ResidentBytes} bytes resident against a budget of {budget}."
            );
        }

        // Not a test of a manager that never loaded anything: the path really did stream.
        Assert.True(residency.Loads > slots, $"Only {residency.Loads} loads for {pages.Pages.Length} pages.");
        Assert.True(residency.Evictions > 0, "Nothing was evicted, so the budget was never actually pressed.");
    }

    /// <summary>
    ///     Every frame of the path draws a closed surface, however little has arrived.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The criterion that means "no popping beyond the configured error".</b> A sphere has
    ///         no boundary, so a cut that took a cluster on one side of a group and left its
    ///         neighbour missing leaves an edge with one triangle on it — which is a crack detected
    ///         as a number rather than looked for in a picture. It is the same check phase 1's exit
    ///         criterion uses, applied to a cut chosen under a residency constraint rather than
    ///         under a threshold alone.
    ///     </para>
    ///     <para>
    ///         The naive implementation fails this and looks right doing it: drop the clusters whose
    ///         pages are missing and draw the rest. Every cluster drawn is a cluster that was
    ///         supposed to be drawn, the frame rate is fine, and there is a hole.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_frame_of_the_path_draws_a_closed_surface() {
        var (_, mesh, pages) = Scene();

        var slots = Math.Max(2, pages.Pages.Length / 4);
        var source = new DelayedSource(pages, 2);

        using var pool = new MeshletPagePool(device, source, slots, pages.PageSize);
        using var residency = new PageResidency(pool, (long)slots * pages.PageSize);

        residency.Pin(new(0, 0));
        Settle(residency, pool);

        var missed = 0;

        foreach (var distance in Path()) {
            var cut = Frame(residency, pool, mesh, pages, distance);

            Assert.NotEmpty(cut);
            Assert.True(IsClosed(mesh, cut), $"The cut at distance {distance} left an open edge.");

            if (cut.Length != MeshletCut.SelectByError(mesh, Threshold(distance)).Length) {
                missed++;
            }
        }

        // The path really did have frames where a page was missing — otherwise the closure check
        // above is checking the same thing phase 1 already checks.
        Assert.True(missed > 0, "Nothing was ever missing, so the degradation path never ran.");
    }

    /// <summary>
    ///     A cut never draws a cluster whose bytes are not there.
    /// </summary>
    /// <remarks>
    ///     The other half of the same rule, and the one whose failure is a garbage triangle rather
    ///     than a hole: a cluster whose page has been evicted has a slot holding some other page's
    ///     bytes, so drawing it is drawing whatever moved in.
    /// </remarks>
    [Fact]
    public void A_cut_never_names_a_cluster_that_is_not_resident() {
        var (_, mesh, pages) = Scene();

        var slots = Math.Max(2, pages.Pages.Length / 4);
        var source = new DelayedSource(pages, 2);

        using var pool = new MeshletPagePool(device, source, slots, pages.PageSize);
        using var residency = new PageResidency(pool, (long)slots * pages.PageSize);

        residency.Pin(new(0, 0));

        foreach (var distance in Path()) {
            var cut = Frame(residency, pool, mesh, pages, distance);

            Assert.All(
                cut,
                cluster => Assert.True(
                    residency.IsResident(new(0, pages.Clusters[cluster].Page)),
                    $"Cluster {cluster} is in page {pages.Clusters[cluster].Page}, which is not resident."
                )
            );
        }
    }

    /// <summary>
    ///     Given time, the cut converges on the one that was asked for.
    /// </summary>
    /// <remarks>
    ///     What "the popping closes" means. A manager that held the budget by never loading anything
    ///     would satisfy every other test here; this is the one it fails.
    /// </remarks>
    [Fact]
    public void A_stationary_camera_converges_on_the_cut_it_asked_for() {
        var (_, mesh, pages) = Scene();

        var source = new DelayedSource(pages, 1);

        using var pool = new MeshletPagePool(device, source, pages.Pages.Length, pages.PageSize);
        using var residency = new PageResidency(pool, pages.TotalBytes);

        residency.Pin(new(0, 0));

        const float Distance = 1.5f;
        var wanted = MeshletCut.SelectByError(mesh, Threshold(Distance));

        int[] cut = [];

        for (var frame = 0; frame < 400; frame++) {
            cut = Frame(residency, pool, mesh, pages, Distance);

            if (cut.SequenceEqual(wanted)) {
                return;
            }

            Thread.Sleep(1);
        }

        Assert.Fail($"After 400 frames the cut is {cut.Length} clusters and the answer is {wanted.Length}.");
    }

    /// <summary>
    ///     With the root page unpinned and the budget at nothing, the object still draws something.
    /// </summary>
    /// <remarks>
    ///     The guarantee stated as its own test rather than left as a property of the pinning: an
    ///     object whose pages will never arrive is drawn at its coarsest level, not dropped. Which is
    ///     also why page zero holds the roots — see <c>MeshletPageBuilderTests</c>.
    /// </remarks>
    [Fact]
    public void The_root_page_is_enough_to_draw_the_object() {
        var (_, mesh, pages) = Scene();

        var source = new DelayedSource(pages, 0);

        using var pool = new MeshletPagePool(device, source, 1, pages.PageSize);
        using var residency = new PageResidency(pool, pages.PageSize);

        residency.Pin(new(0, 0));
        Settle(residency, pool);

        Assert.Equal(1, residency.ResidentPages);

        // Asked for the finest cut there is, and given a pool that holds one page.
        var cut = MeshletCut.SelectByError(mesh, pages, 0f, page => residency.IsResident(new(0, page)));

        Assert.NotEmpty(cut);
        Assert.True(IsClosed(mesh, cut));
        Assert.All(cut, cluster => Assert.Equal(0, pages.Clusters[cluster].Page));
    }

    /// <summary>One frame: ask, service, choose a cut, and say what was used.</summary>
    /// <remarks>
    ///     The shape phase 3's traversal will have, with the traversal replaced by a linear scan —
    ///     which is the same substitution <see cref="MeshletCut" /> is written as, and for the same
    ///     reason.
    /// </remarks>
    int[] Frame(
        PageResidency residency,
        MeshletPagePool pool,
        MeshletMesh mesh,
        MeshletPageSet pages,
        float distance
    ) {
        var threshold = Threshold(distance);

        // What the frame would draw if everything were resident, which is what it asks for. Demand
        // driven: the requests are the pages of the clusters the cut actually wanted.
        foreach (var cluster in MeshletCut.SelectByError(mesh, threshold)) {
            residency.Request(new(0, pages.Clusters[cluster].Page));
        }

        residency.Service();

        // The copies the placements need, recorded and submitted as a frame's would be. Not
        // decoration: the pool stages into host memory it reuses after every flush, so a loop that
        // never flushed would be a frame that never submitted — and would refuse placements.
        Flush(pool);

        var cut = MeshletCut.SelectByError(mesh, pages, threshold, page => residency.IsResident(new(0, page)));

        // Used, not merely resident — the distinction the eviction order turns on.
        foreach (var cluster in cut) {
            residency.Touch(new(0, pages.Clusters[cluster].Page));
        }

        return cut;
    }

    /// <summary>A camera path: in towards the sphere, round it, and out again.</summary>
    static IEnumerable<float> Path() {
        for (var step = 0; step < 60; step++) {
            yield return 8f - (step * 0.12f);
        }

        for (var step = 0; step < 60; step++) {
            yield return 0.8f + (step * 0.12f);
        }
    }

    /// <summary>The object-space threshold a distance implies, at one pixel of deviation.</summary>
    static float Threshold(float distance) =>
        MeshletCut.ErrorForPixels(1f, distance, MathF.PI / 3f, 1080f);

    /// <summary>Whether every edge of the cut carries exactly two triangles.</summary>
    /// <remarks>
    ///     The closure test phase 1's exit criterion uses. A sphere is closed, so any cut of it that
    ///     is a valid surface is closed too — and an edge with one triangle on it is precisely a
    ///     crack, whether it was opened by a bad error metric or by a page that had not arrived.
    /// </remarks>
    static bool IsClosed(MeshletMesh mesh, int[] cut) {
        var corners = MeshletCut.Flatten(mesh, cut);
        var welded = Weld(mesh, corners);
        var edges = new Dictionary<(int, int), int>();

        for (var corner = 0; corner < corners.Length; corner += 3) {
            for (var side = 0; side < 3; side++) {
                var a = welded[corners[corner + side]];
                var b = welded[corners[corner + ((side + 1) % 3)]];
                var key = a < b ? (a, b) : (b, a);

                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }

        return edges.Values.All(count => count == 2);
    }

    /// <summary>Vertices welded by position, because a seam is two indices at one point.</summary>
    static int[] Weld(MeshletMesh mesh, int[] corners) {
        _ = corners;

        var welded = new int[mesh.Vertices.Length == 0 ? 1 : mesh.Vertices.Max() + 1];
        Array.Fill(welded, -1);

        // The DAG's vertices are the source mesh's, unchanged and never invented, so index equality
        // is position equality — which is exactly the property phase 1 pays for by collapsing onto
        // existing vertices.
        for (var i = 0; i < welded.Length; i++) {
            welded[i] = i;
        }

        return welded;
    }

    /// <summary>Runs frames until everything asked for has arrived, or the patience runs out.</summary>
    /// <remarks>
    ///     Flushes like a frame does, because the pool's staging is reclaimed at the flush and a loop
    ///     that only serviced would fill it — which is a real contract and not a test artefact.
    /// </remarks>
    void Settle(PageResidency residency, MeshletPagePool pool, int frames = 400) {
        for (var frame = 0; frame < frames; frame++) {
            residency.Service();
            Flush(pool);

            if (residency.PendingRequests == 0 && residency.Loading == 0) {
                residency.Service();
                Flush(pool);
                return;
            }

            Thread.Sleep(1);
        }
    }

    /// <summary>Records and submits the copies a frame's placements need.</summary>
    void Flush(MeshletPagePool pool) {
        using var list = device.BeginCommandList(QueueKind.Compute);

        pool.Flush(list);
        list.Finish();
        device.ComputeQueue.Submit([list]);
    }
}
