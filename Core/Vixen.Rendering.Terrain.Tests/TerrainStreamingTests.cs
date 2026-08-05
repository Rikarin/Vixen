// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>The tile streamer — [docs/plan/31 § D13]'s consumer, which had none.</summary>
/// <remarks>
///     ⚠ <b>The claim under test is not "pages load".</b> <c>StreamingGridTests</c> and
///     <c>PageResidencyTests</c> already say that, and both passed for the whole time nothing in the
///     engine asked either of them for anything. What is asserted here is that a terrain <em>asks</em>
///     — that a frame's upload gets smaller because of it, and that a tile nobody has been near is
///     still drawn.
/// </remarks>
public sealed class TerrainStreamingTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();


    /// <summary>Services the streamer until something is true, or gives up with a message.</summary>
    /// <remarks>
    ///     ⚠ <b>A deadline rather than a fixed number of iterations</b>, because a page load goes
    ///     through <c>Task.Run</c> and how many frames it takes back is the thread pool's business. A
    ///     fixed count passes on an idle machine and fails on a busy one, which is a flaky test rather
    ///     than a slow one.
    /// </remarks>
    static void Settle(TerrainStreamer streamer, Func<bool> until) {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline) {
            Span<StreamingSource> sources = [new(new(16f, 0f, 16f), 40f)];

            streamer.Update(sources);

            if (until()) {
                return;
            }

            Thread.Sleep(2);
        }

        Assert.Fail("the streamer never reached the state the test is about.");
    }

    static TerrainDescription Shape(int tiles) =>
        TerrainDescription.Default with {
            TileSamples = 32, TilesX = tiles, TilesZ = tiles,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    TerrainRenderer Build(TerrainMap terrain) =>
        new(
            device,
            terrain,
            new(
                device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], "terrain.vs"),
                device.CreateShader(ShaderStage.Fragment, [1, 2, 3, 4], "terrain.fs")
            ),
            new([PixelFormat.Rgba8UNorm], PixelFormat.Depth32Float),
            TerrainLodRanges.Default with { NearRange = 32f },
            8
        );

    static TerrainView View(Vector3 at) {
        var view = Matrix4x4.LookAt(at, at + new Vector3(1f, -0.5f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI * 0.9f, 1f, 0.1f, 100_000f);

        return new(view * projection, at, new(view * projection));
    }

    /// <summary>A tile nobody has asked for is not resident, and one nearby is.</summary>
    [Fact]
    public void ASourceMakesTheTilesAroundItResidentAndNotTheRest() {
        var description = Shape(tiles: 8);
        var terrain = new TerrainMap(description);
        using var streamer = new TerrainStreamer(in description, new TerrainTileSource(terrain));

        Settle(streamer, () => streamer.IsResident(0, 0));

        Assert.True(streamer.IsResident(0, 0), "the tile the source is standing in never arrived.");

        // The far corner of an eight-tile terrain is 217 m away, and the radius plus the lead is 71.
        Assert.False(streamer.IsResident(7, 7), "a tile two hundred metres away was loaded anyway.");
    }

    /// <summary>A tile with no fine levels is drawn coarse rather than dropped.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole reason the tail is pinned.</b> The obvious implementation refuses to select
    ///     a node whose tile has not arrived, and the symptom is a hole in the distance on the frame a
    ///     camera turns — which reads as the terrain failing rather than as a tile loading.
    /// </remarks>
    [Fact]
    public void AnAbsentTilesLevelIsFlooredRatherThanTheNodeDropped() {
        var description = Shape(tiles: 8);
        var terrain = new TerrainMap(description);
        using var streamer = new TerrainStreamer(in description, new TerrainTileSource(terrain)) { CoarseLevel = 3 };

        Assert.Equal(3f, streamer.LevelOf(7, 7, 0f));
        Assert.Equal(4f, streamer.LevelOf(7, 7, 4f));
    }

    /// <summary>And a resident tile keeps whatever level the quadtree chose.</summary>
    [Fact]
    public void AResidentTilesLevelIsWhateverWasChosen() {
        var description = Shape(tiles: 4);
        var terrain = new TerrainMap(description);
        using var streamer = new TerrainStreamer(in description, new TerrainTileSource(terrain)) { CoarseLevel = 3 };

        Settle(streamer, () => streamer.IsResident(0, 0));

        Assert.True(streamer.IsResident(0, 0));
        Assert.Equal(0f, streamer.LevelOf(0, 0, 0f));
    }

    /// <summary>The first frame of a large terrain moves far fewer bytes with a streamer than without.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the number the whole feature exists for, and it is the one a "streaming works"
    ///     test that only asserted residency would not have read.</b> A renderer can hold a fully
    ///     serviced streamer and still copy every tile in full — that was the state the first draft of
    ///     this was in — and every counter would say the pages loaded.
    /// </remarks>
    [Fact]
    public void TheFirstFrameCopiesTheCoarseTailRatherThanEveryTileInFull() {
        var description = Shape(tiles: 8);

        var plain = new TerrainMap(description);
        using var without = Build(plain);

        var streamed = new TerrainMap(description);
        using var with = Build(streamed);
        using var streamer = new TerrainStreamer(in description, new TerrainTileSource(streamed)) { CoarseLevel = 2 };

        with.Streaming = streamer;

        // A source in the corner, so most of the terrain is outside its radius.
        with.StreamingSources.Add(new(new(4f, 0f, 4f), 8f));

        without.Upload(device.BeginCommandList(), View(new(10f, 40f, 10f)));
        with.Upload(device.BeginCommandList(), View(new(10f, 40f, 10f)));

        Assert.True(
            with.UploadedBytes < without.UploadedBytes,
            $"{with.UploadedBytes} streamed against {without.UploadedBytes} plain, which is not a saving."
        );
    }

    /// <summary>A tile the pool hands over is copied, and it is copied once.</summary>
    /// <remarks>
    ///     ⚠ <b>Draining is what makes an arrival reach the atlas.</b> A pool that stages and a
    ///     renderer that never asks is a streamer whose counters all read healthy and whose fine
    ///     levels never appear — the failure this whole file is arranged to catch.
    /// </remarks>
    [Fact]
    public async Task AnArrivalReachesTheAtlasAndIsHandedOverOnce() {
        var description = Shape(tiles: 4);
        var terrain = new TerrainMap(description);
        var source = new TerrainTileSource(terrain);
        var pages = new TerrainTilePages(source, slots: 4);

        var bytes = new byte[pages.PageSize];
        var read = await source.ReadAsync(1, 1, bytes, TestContext.Current.CancellationToken);

        Assert.True(pages.Place(new(TerrainStreamer.PageSource, (1 * 4) + 1), 0, bytes.AsSpan(0, read)));
        Assert.True(pages.IsResident(1, 1));

        var handed = 0;

        pages.Drain((x, z, chain) => {
            handed++;

            Assert.Equal((1, 1), (x, z));
            Assert.Equal(read, chain.Length);
        });

        Assert.Equal(1, handed);

        // Drained means handed over, not re-handed. A pool that kept its pending list would copy every
        // resident tile every frame, which is the upload the streamer was added to avoid.
        pages.Drain((_, _, _) => Assert.Fail("the same arrival was handed over twice."));
    }

    /// <summary>An evicted slot's pending hand-over goes with it.</summary>
    /// <remarks>
    ///     ⚠ <b>Otherwise the copy writes one tile's heights into another tile's block</b>, which is
    ///     not missing ground but ground from somewhere else — and it looks like a sculpt that landed
    ///     in the wrong place.
    /// </remarks>
    [Fact]
    public async Task EvictingASlotDropsWhatWasWaitingToBeCopiedFromIt() {
        var description = Shape(tiles: 4);
        var terrain = new TerrainMap(description);
        var source = new TerrainTileSource(terrain);
        var pages = new TerrainTilePages(source, slots: 2);

        var bytes = new byte[pages.PageSize];
        var read = await source.ReadAsync(0, 0, bytes, TestContext.Current.CancellationToken);

        pages.Place(new(TerrainStreamer.PageSource, 0), 0, bytes.AsSpan(0, read));
        pages.Evict(new(TerrainStreamer.PageSource, 0), 0);

        Assert.False(pages.IsResident(0, 0));
        Assert.Equal(0, pages.Pending);

        pages.Drain((_, _, _) => Assert.Fail("a tile evicted before it was copied was copied anyway."));
    }

    /// <summary>The pool refuses rather than overruns when a frame's hand-over is full.</summary>
    [Fact]
    public async Task APoolWithNoRoomLeftThisFrameSaysSo() {
        var description = Shape(tiles: 4);
        var terrain = new TerrainMap(description);
        var source = new TerrainTileSource(terrain);
        var pages = new TerrainTilePages(source, slots: 8) { MaxPending = 1 };

        var bytes = new byte[pages.PageSize];
        var read = await source.ReadAsync(0, 0, bytes, TestContext.Current.CancellationToken);

        Assert.True(pages.Place(new(TerrainStreamer.PageSource, 0), 0, bytes.AsSpan(0, read)));
        Assert.False(pages.Place(new(TerrainStreamer.PageSource, 1), 1, bytes.AsSpan(0, read)));
    }

    /// <summary>Two tiles read at the same time each get their own heights.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The source ran on the thread pool from the day it was written and was written as if
    ///         it did not.</b> <see cref="PageResidency.Service" /> dispatches up to <c>maxLoads</c>
    ///         reads through <c>Task.Run</c> with nothing serialising them, so one scratch array
    ///         belonging to the source is up to eight tiles building their chains into the same memory
    ///         — and what a person sees is a tile wearing its neighbour's hills, which reads as bad
    ///         content rather than as a race.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Gated rather than slept, and repeated rather than timed.</b> Both readers wait on
    ///         one <see cref="TaskCompletionSource" /> so they are released together; the repetition is
    ///         only there to widen the window a shared buffer would be caught in. Correct code passes on
    ///         the first pass and every pass, so nothing here can flake.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TwoTilesReadAtOnceDoNotShareABuffer() {
        var description = Shape(tiles: 4);
        var terrain = new TerrainMap(description);

        // Two tiles that could not be mistaken for each other: one raised, one left alone.
        var layer = terrain.AddLayer("hill");

        foreach (var (x, z) in Samples(description, tileX: 0, tileZ: 0)) {
            layer.AddDelta(x, z, 4000);
        }

        terrain.InvalidateAll();
        terrain.Resolve();

        var source = new TerrainTileSource(terrain);
        var size = (int)TerrainMips.ChainSamples(description.TileSamples) * sizeof(ushort);

        var raised = new byte[size];
        var flat = new byte[size];

        await source.ReadAsync(0, 0, raised, TestContext.Current.CancellationToken);
        await source.ReadAsync(2, 2, flat, TestContext.Current.CancellationToken);

        Assert.False(raised.AsSpan().SequenceEqual(flat), "the two tiles are indistinguishable, so nothing is proved.");

        for (var attempt = 0; attempt < 64; attempt++) {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var a = new byte[size];
            var b = new byte[size];

            var first = Task.Run(
                async () => {
                    await gate.Task;
                    await source.ReadAsync(0, 0, a, CancellationToken.None);
                },
                TestContext.Current.CancellationToken
            );

            var second = Task.Run(
                async () => {
                    await gate.Task;
                    await source.ReadAsync(2, 2, b, CancellationToken.None);
                },
                TestContext.Current.CancellationToken
            );

            gate.SetResult();

            await Task.WhenAll(first, second);

            Assert.True(a.AsSpan().SequenceEqual(raised), $"the raised tile came back wrong on attempt {attempt}.");
            Assert.True(b.AsSpan().SequenceEqual(flat), $"the flat tile came back wrong on attempt {attempt}.");
        }
    }

    /// <summary>Compositing is the frame thread's, and a read off the pool does not do it.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half of the same fault.</b> <c>ReadAsync</c> used to call
    ///     <see cref="TerrainMap.Resolve" />, which writes the composite, the dirty flags and the
    ///     per-tile height ranges — off the frame, while the renderer's own <c>Upload</c> was calling it
    ///     too. Moving it to <c>Prepare</c> is only a fix if the streamer actually calls it, and only a
    ///     fix if the read really has stopped, so both are asserted rather than one.
    /// </remarks>
    [Fact]
    public async Task TheStreamerCompositesAndTheReadDoesNot() {
        var description = Shape(tiles: 4);
        var terrain = new TerrainMap(description);
        var source = new TerrainTileSource(terrain);

        terrain.InvalidateAll();

        var bytes = new byte[(int)TerrainMips.ChainSamples(description.TileSamples) * sizeof(ushort)];
        await source.ReadAsync(0, 0, bytes, TestContext.Current.CancellationToken);

        Assert.Equal(description.TileCount, terrain.DirtyTileCount);

        using var streamer = new TerrainStreamer(in description, source);
        streamer.Update([new(new(16f, 0f, 16f), 40f)]);

        Assert.Equal(0, terrain.DirtyTileCount);
    }

    /// <summary>Every sample of one tile.</summary>
    static IEnumerable<(int X, int Z)> Samples(TerrainDescription description, int tileX, int tileZ) {
        var rect = description.SamplesOf(tileX, tileZ);

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                yield return (x, z);
            }
        }
    }

    /// <summary>The pool is never larger than the terrain it is over.</summary>
    /// <remarks>
    ///     ⚠ <b>A pool of a thousand slots over a sixteen-tile terrain is a budget that describes
    ///     nothing</b> — nine hundred and eighty-four of them can never be filled, so the number a
    ///     person tunes has no relationship to what is resident.
    /// </remarks>
    [Fact]
    public void ThePoolIsClampedToTheTerrainsTileCount() {
        var description = Shape(tiles: 2);
        var terrain = new TerrainMap(description);
        using var streamer = new TerrainStreamer(in description, new TerrainTileSource(terrain), budget: 1L << 30);

        Assert.Equal(4, streamer.Pages.SlotCount);
    }
}
