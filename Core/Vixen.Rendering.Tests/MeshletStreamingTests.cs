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
    ///     <para>
    ///         Frames rather than milliseconds, and the difference is the whole reason this class
    ///         exists rather than a <c>Task.Delay</c>. A wall-clock delay makes the same test flaky on
    ///         a loaded build agent and vacuous on a fast one — which is not a hypothetical: the first
    ///         version of this was a delay, and it failed once in a full-suite run and passed twenty
    ///         times on its own.
    ///     </para>
    ///     <para>
    ///         So a read completes when the <em>frame loop</em> says so. <see cref="Advance" /> is
    ///         called once per frame and releases the loads that have waited long enough, which makes
    ///         "this page was missing for three frames" a fact about the test rather than about the
    ///         machine.
    ///     </para>
    /// </remarks>
    sealed class DelayedSource(MeshletPageSet pages, int frames) : IMeshletPageSource {
        readonly List<(TaskCompletionSource<int> Completion, Memory<byte> Destination, int Index, int Due)> waiting = [];
        readonly Lock gate = new();

        int frame;

        public int Reads { get; private set; }

        public ValueTask<int> ReadAsync(PageKey key, Memory<byte> destination, CancellationToken cancellation) {
            cancellation.ThrowIfCancellationRequested();
            Reads++;

            if (frames <= 0) {
                return ValueTask.FromResult(Fill(destination, key.Index));
            }

            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (gate) {
                waiting.Add((completion, destination, key.Index, frame + frames));
            }

            return new(completion.Task);
        }

        /// <summary>How many reads are still waiting for a frame to release them.</summary>
        /// <remarks>
        ///     What tells the frame loop apart from a load that is <em>meant</em> to be outstanding and
        ///     one that has been released and is on its way. Without it the loop cannot know whether
        ///     waiting is correct or is waiting for something that will not come until it advances
        ///     again, which is the difference between a fast test and a hundred-second one.
        /// </remarks>
        public int Waiting {
            get {
                lock (gate) {
                    return waiting.Count;
                }
            }
        }

        /// <summary>Ends a frame, completing every read that has waited its stated number of them.</summary>
        public void Advance() {
            lock (gate) {
                frame++;

                for (var i = waiting.Count - 1; i >= 0; i--) {
                    if (waiting[i].Due > frame) {
                        continue;
                    }

                    var (completion, destination, index, _) = waiting[i];
                    waiting.RemoveAt(i);
                    completion.SetResult(Fill(destination, index));
                }
            }
        }

        int Fill(Memory<byte> destination, int index) {
            var bytes = pages.BytesOf(index);
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
        var source = new DelayedSource(pages, frames: 2);

        using var pool = new MeshletPagePool(device, source, slots, pages.PageSize);
        using var residency = new PageResidency(pool, (long)slots * pages.PageSize);

        residency.Pin(new(0, 0));

        var budget = residency.Budget;
        Assert.True(pages.TotalBytes >= budget * 4, $"The scene is {pages.TotalBytes} bytes against a budget of {budget}.");

        foreach (var distance in Path()) {
            Frame(residency, pool, source, mesh, pages, distance);

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
        var source = new DelayedSource(pages, frames: 2);

        using var pool = new MeshletPagePool(device, source, slots, pages.PageSize);
        using var residency = new PageResidency(pool, (long)slots * pages.PageSize);

        residency.Pin(new(0, 0));
        Settle(residency, pool, source);

        var missed = 0;

        foreach (var distance in Path()) {
            var cut = Frame(residency, pool, source, mesh, pages, distance);

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
        var source = new DelayedSource(pages, frames: 2);

        using var pool = new MeshletPagePool(device, source, slots, pages.PageSize);
        using var residency = new PageResidency(pool, (long)slots * pages.PageSize);

        residency.Pin(new(0, 0));

        foreach (var distance in Path()) {
            var cut = Frame(residency, pool, source, mesh, pages, distance);

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

        var source = new DelayedSource(pages, frames: 1);

        using var pool = new MeshletPagePool(device, source, pages.Pages.Length, pages.PageSize);
        using var residency = new PageResidency(pool, pages.TotalBytes);

        residency.Pin(new(0, 0));

        const float Distance = 1.5f;
        var wanted = MeshletCut.SelectByError(mesh, Threshold(Distance));

        int[] cut = [];

        for (var frame = 0; frame < 400; frame++) {
            cut = Frame(residency, pool, source, mesh, pages, Distance);

            if (cut.SequenceEqual(wanted)) {
                return;
            }
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

        var source = new DelayedSource(pages, frames: 0);

        using var pool = new MeshletPagePool(device, source, 1, pages.PageSize);
        using var residency = new PageResidency(pool, pages.PageSize);

        residency.Pin(new(0, 0));
        Settle(residency, pool, source);

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
        DelayedSource source,
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
        source.Advance();

        // The loads this frame released have to reach the pool before the cut is chosen, or the frame
        // is deciding against a residency one frame stale. A device would have the same boundary; here
        // it is a thread hand-off.
        Handoff(residency, source);

        residency.Service();
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
    void Settle(PageResidency residency, MeshletPagePool pool, DelayedSource source, int frames = 400) {
        for (var frame = 0; frame < frames; frame++) {
            residency.Service();
            Flush(pool);
            source.Advance();

            if (residency.PendingRequests == 0 && residency.Loading == 0) {
                residency.Service();
                Flush(pool);
                return;
            }

            // The completion is deterministic; the hand-off from the loading task to this one is a
            // thread schedule, so it is waited for rather than assumed. What is never waited on is a
            // *delay* — how long a page is missing for is decided by Advance above.
            Handoff(residency, source);
        }
    }

    /// <summary>
    ///     A page read out of a blob is the page the builder wrote, and reading one reads only it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a shipping build actually does.</b> The records are deserialised whole at load and
    ///         the geometry is a blob beside them, so the source seeks — and a set that has been through
    ///         <see cref="MeshletPageSet.WithoutData" /> has no bytes of its own to check against, which
    ///         is exactly the arrangement this has to work in.
    ///     </para>
    ///     <para>
    ///         Two claims, and the second is the one that would rot silently. Reading the <em>right</em>
    ///         bytes is checked against the in-memory source, page for page. Reading <em>only</em> those
    ///         bytes is checked by counting what the stream was asked for: a source that read the blob
    ///         from the start every time would return identical, correct pages and defeat the entire
    ///         point of paging.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task A_streamed_page_is_the_page_the_builder_wrote() {
        var (_, _, pages) = Scene();
        var shipped = pages.WithoutData();

        Assert.False(shipped.HasData);
        Assert.Throws<InvalidOperationException>(() => shipped.BytesOf(0));

        using var counted = new CountingStream(new MemoryStream(pages.Data));
        using var streamed = new StreamMeshletPageSource();

        streamed.Add(0, shipped, counted);

        var buffer = new byte[pages.PageSize];

        for (var page = 0; page < pages.Pages.Length; page++) {
            var read = await streamed.ReadAsync(new(0, page), buffer, CancellationToken.None);

            Assert.Equal(pages.Pages[page].Size, read);
            Assert.True(pages.BytesOf(page).SequenceEqual(buffer.AsSpan(0, read)), $"Page {page} differs.");
        }

        // Every page read exactly its own bytes and no more — the blob is far larger than the sum, since
        // a page's used size is at most a slot and the last one is short.
        Assert.Equal(pages.Pages.Sum(page => (long)page.Size), counted.BytesRead);
        Assert.True(counted.BytesRead < pages.Data.Length, "A source that read the whole blob would pass every other assertion here.");
    }

    /// <summary>
    ///     The offsets a data-less set reports are the ones its pages were written at.
    /// </summary>
    /// <remarks>
    ///     <see cref="MeshletPageSet.OffsetOf" /> is arithmetic and <see cref="MeshletPage.Offset" /> is
    ///     what the builder recorded, and a streamed source trusts the first because the second belongs
    ///     to a blob that is no longer attached. They agree because the builder pads each page out to a
    ///     whole slot — which is a property of the packer that nothing else asserts, and which a change
    ///     to pack pages tightly would break here rather than in a frame.
    /// </remarks>
    [Fact]
    public void A_pages_offset_is_its_index_times_the_page_size() {
        var (_, _, pages) = Scene();

        for (var page = 0; page < pages.Pages.Length; page++) {
            Assert.Equal(pages.Pages[page].Offset, pages.OffsetOf(page));
            Assert.Equal((long)page * pages.PageSize, pages.OffsetOf(page));
        }

        Assert.Equal(pages.TotalBytes, pages.Data.Length);
    }

    /// <summary>A stream that says how much of it was actually read.</summary>
    sealed class CountingStream(Stream inner) : Stream {
        public long BytesRead { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            var read = inner.Read(buffer);
            BytesRead += read;

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush() => inner.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            if (disposing) {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    ///     Waits for the loads this frame released to hand their bytes back, and for nothing else.
    /// </summary>
    /// <remarks>
    ///     Not a delay and not an assumption about one: <see cref="DelayedSource.Advance" /> decides
    ///     <em>which</em> loads are due, and this waits only for the thread that carries those to run.
    ///     The comparison is against what the source is still holding, so a load that is meant to be
    ///     outstanding for two more frames is not waited for — which is the difference between a test
    ///     that runs in a second and one that spends its timeout on every frame.
    /// </remarks>
    static void Handoff(PageResidency residency, DelayedSource source) =>
        SpinWait.SpinUntil(() => residency.Loading <= source.Waiting, 250);

    /// <summary>Records and submits the copies a frame's placements need.</summary>
    void Flush(MeshletPagePool pool) {
        using var list = device.BeginCommandList(QueueKind.Compute);

        pool.Flush(list);
        list.Finish();
        device.ComputeQueue.Submit([list]);
    }
}
