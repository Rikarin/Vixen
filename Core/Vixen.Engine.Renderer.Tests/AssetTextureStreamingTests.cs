// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Assets;
using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Tests;

/// <summary>
///     A texture arrives at a floor resolution and grows into the one a view asked for — and with
///     streaming off, nothing about any of it happens.
/// </summary>
public sealed class AssetTextureStreamingTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    static readonly AssetReference Bark = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);

    /// <summary>
    ///     The degradation claim, and the reason it is worth a test rather than a sentence: with no
    ///     pool the class records the same four calls it recorded before streaming existed — a
    ///     transition in, one copy per level, a transition out — and builds no streamer, no ring and
    ///     no residency.
    /// </summary>
    [Fact]
    public void WithNoPoolATextureIsLoadedWholeExactlyAsItAlwaysWas() {
        using var source = new AssetTextureSource(device, Content(2, 2));

        Assert.Null(source.Streaming);

        Settle(source);

        var recorder = device.Recorder!;

        // Two levels of a 2×2, each copied once, between one barrier in and one barrier out.
        Assert.Equal(2, recorder.CountOf(RecordedCommandKind.Barrier));
        Assert.Equal(2, recorder.CountOf(RecordedCommandKind.CopyBufferToTexture));
        Assert.Equal(0, recorder.CountOf(RecordedCommandKind.CopyBuffer));

        var copies = recorder.OfKind(RecordedCommandKind.CopyBufferToTexture);

        Assert.Equal(0, copies[0].D);   // level 0
        Assert.Equal(2, copies[0].E);   // 2 texels wide
        Assert.Equal(1, copies[1].D);
        Assert.Equal(1, copies[1].E);

        Assert.Equal(0L, source.StreamingSwaps);
        Assert.Equal(0L, source.StreamingRefusals);
    }

    /// <summary>And it costs nothing per frame, which is the other half of "exactly as it was".</summary>
    [Fact]
    public void WithNoPoolAnUpdateAfterTheUploadRecordsNothingAtAll() {
        using var source = new AssetTextureSource(device, Content(2, 2));

        Settle(source);
        device.Recorder!.Clear();

        for (var frame = 0; frame < 60; frame++) {
            Record(source);
        }

        Assert.Equal(0, device.Recorder.Count);
    }

    /// <summary>
    ///     A pool that fits everything draws what the whole-file path drew. That is the reason the
    ///     default want is "complete": turning a pool on is then a decision about memory and never a
    ///     silent drop in quality.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Which resolution the first image is, is not asserted, and deliberately.</b> Both
    ///     pages of this file are asked for at once and an in-memory source returns them in the same
    ///     millisecond, so whether the texture is ever seen at its 128×128 floor is a race. What is
    ///     not a race is the floor itself — <see cref="TextureStreamer.PinnedLevel" /> — and where it
    ///     ends up.
    /// </remarks>
    [Fact]
    public void AStreamedTextureWithAPoolThatFitsItEndsUpAtTheWholeFile() {
        using var source = new AssetTextureSource(device, Content(256, 256), 4 * 1024 * 1024);

        Assert.NotNull(source.Streaming);

        Settle(source);

        Assert.Equal(1, source.Streaming!.Textures);

        // 65_536 bytes of R8 level data is the 128×128 tail and everything under it; the 256×256
        // level alone is one page over, so the pinned page cannot cover it.
        Assert.Equal(1, source.Streaming.PinnedLevel(0));

        Until(
            source,
            () => source.Streaming.ResidentLevel(0) == 0 && source.StreamingSwaps > 0,
            "the texture never reached its full resolution",
            frame => Assert.True(frame.TryGet(Bark, out var view) && view.IsValid)
        );

        Assert.Contains(
            device.Recorder!.OfKind(RecordedCommandKind.CopyBufferToTexture),
            copy => copy is { D: 0, E: 256 }
        );

        Assert.Equal(0L, source.StreamingRefusals);
    }

    /// <summary>
    ///     And a caller that knows how big it needs the texture to be narrows that. Sixty frames of
    ///     asking for 32 texels never brings the 256×256 level in, whatever the pool could hold.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The sixty frames used to have to be the decode's deadline as well as the claim,
    ///         and it failed as one</b> — measured 2026-08-19, one run in six on an <i>idle</i>
    ///         machine, at the last line: the texture was not viewable because sixty milliseconds is
    ///         not long enough for a thread pool to be certain to have run
    ///         <c>AssetTextureSource</c>'s decode task.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And when it failed that way it was failing kindly, because the assertion above it
    ///         had been passing vacuously.</b> "No copy of a 256-wide level was recorded" is trivially
    ///         true of a source that has not decoded anything yet, so on any run where the decode
    ///         landed late the negative claim was made about a handful of frames rather than sixty.
    ///         Waiting for the texture first fixes both: the wait asks for 32 texels every frame like
    ///         the loop it precedes, so every frame of it counts towards the negative claim, and the
    ///         sixty that follow are sixty frames of a texture that is really there.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SizingAStreamedTextureKeepsItBelowTheWholeFile() {
        using var source = new AssetTextureSource(device, Content(256, 256), 4 * 1024 * 1024);

        // Sized rather than left alone, because a source at its default asks to be complete — which
        // is the one thing this test exists to say does not happen. Every frame of the wait asks for
        // 32 texels like the sixty that follow it, so every one of them counts towards the negative
        // claim below.
        Settle(source, 32);

        for (var frame = 0; frame < 60; frame++) {
            source.Want(Bark, 32);
            source.TryGet(Bark, out _);

            Record(source);
            Thread.Sleep(1);
        }

        Assert.DoesNotContain(
            device.Recorder!.OfKind(RecordedCommandKind.CopyBufferToTexture),
            copy => copy.E == 256
        );

        Assert.True(source.TryGet(Bark, out var view) && view.IsValid);
    }

    /// <summary>
    ///     A pool too small to hold one texture's tail still draws it. The budget is the ceiling and
    ///     the pinned page is the floor, and between them there is nothing left to negotiate.
    /// </summary>
    [Fact]
    public void APoolTooSmallForTheTextureStillDrawsItAtTheFloor() {
        using var source = new AssetTextureSource(device, Content(256, 256), 64 * 1024);

        Settle(source);

        Assert.Equal(1L, source.StreamingSwaps);

        Until(
            source,
            () => source.Streaming!.Rejections > 0,
            "nothing was refused by a one-page pool",
            frame => frame.TryGet(Bark, out _)
        );

        Assert.True(source.Streaming!.ResidentBytes <= source.Streaming.Budget);
        Assert.True(source.TryGet(Bark, out var view) && view.IsValid);
        Assert.DoesNotContain(
            device.Recorder!.OfKind(RecordedCommandKind.CopyBufferToTexture),
            copy => copy.E == 256
        );
    }

    /// <summary>
    ///     And it says so, to the logger the host handed the source — through the chain a game builds
    ///     and not past it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The assertion the sibling test in <c>Vixen.Rendering.Tests</c> cannot make.</b>
    ///         <c>PageResidencyTests.Refusals_are_logged_once_and_a_healthy_frame_logs_nothing</c>
    ///         sets <c>PageResidency.Logger</c> itself, so what it proves is that the log call works
    ///         once somebody has already made the link — and for as long as event 4001 has existed,
    ///         nobody in a shipped game had. The residency was built by <see cref="TextureStreamer" />
    ///         and the streamer by <see cref="AssetTextureSource" />, and neither carried a logger, so
    ///         the one signal that says the streaming budget is too small for the scene reached a log
    ///         only in a test that reached past both.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing here touches the residency.</b> The only thing set is
    ///         <see cref="AssetTextureSource.Logger" />, which is what a host has. That is the whole
    ///         point of the test: if either forward is dropped it goes red, and an assertion made on
    ///         the residency would stay green with the chain cut.
    ///     </para>
    ///     <para>
    ///         The refusal is forced the way
    ///         <see cref="APoolTooSmallForTheTextureStillDrawsItAtTheFloor" /> forces it — a pool of one
    ///         page against a 256-square chain, where the pinned floor is already the whole budget.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ARefusalReachesTheLoggerTheHostHandedTheSource() {
        var log = new CaptureLogger();

        using var source = new AssetTextureSource(device, Content(256, 256), 64 * 1024) { Logger = log };

        // The first forward, before a frame has run: what a host set is what the streamer holds.
        Assert.Same(log, source.Streaming!.Logger);

        Settle(source);

        Until(
            source,
            () => log.Lines.Count > 0,
            "the refusal never reached the logger the host handed the source",
            frame => frame.TryGet(Bark, out _)
        );

        Assert.True(source.Streaming.Rejections > 0, "nothing was refused by a one-page pool");

        var refused = Assert.Single(log.Lines, line => line.Id == 4001);

        Assert.Equal(LogLevel.Warning, refused.Level);

        // The number that tells somebody which fix this is: the pool is one page and all of it pinned.
        Assert.Contains("1", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A source with no pool keeps what it was given rather than reading as a dropped link.</summary>
    /// <remarks>
    ///     There is nothing under it to log — no streamer, no residency and no refusal — so the value
    ///     goes nowhere, and answering null would be indistinguishable from a forward that failed.
    /// </remarks>
    [Fact]
    public void ASourceWithNoPoolStillRoundTripsTheLoggerItWasGiven() {
        var log = new CaptureLogger();

        using var source = new AssetTextureSource(device, Content(2, 2)) { Logger = log };

        Assert.Null(source.Streaming);
        Assert.Same(log, source.Logger);

        Settle(source);

        Assert.Empty(log.Lines);
    }

    /// <summary>Every line the chain wrote, with the id it wrote it under.</summary>
    sealed class CaptureLogger : ILogger {
        public List<(int Id, LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add((eventId.Id, logLevel, formatter(state, exception)));
    }

    /// <summary>Records one frame's uploads and submits them, which is what reaches the recorder.</summary>
    void Record(AssetTextureSource source) {
        using var commands = device.BeginCommandList();

        source.Update(commands);
        commands.Finish();

        device.GraphicsQueue.Submit([commands]);
    }

    /// <summary>
    ///     Runs frames until the texture is viewable, and gives up only when nothing is left that
    ///     could make it so.
    /// </summary>
    /// <param name="source">The source to drive.</param>
    /// <param name="want">The width to size it at each frame, or <c>-1</c> to leave the want alone.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>There is no deadline here, and that is the point of the method.</b> This used to
    ///         run frames for thirty seconds and fail if the texture had not arrived — and it failed
    ///         that way on CI on 2026-08-26 (run 33010276792, the Windows leg) with
    ///         <c>StreamingSwaps</c> still at zero. Nothing was slow. The reads this waits for are
    ///         <see cref="Task.Run(Action)" /> — the KTX2 header in
    ///         <see cref="AssetTextureSource" />, the pages in <c>PageResidency</c> — and the suite
    ///         runs every test project at once, so the pool inside one test host is saturated by other
    ///         collections sitting in settle loops of their own. A work item queued into a saturated
    ///         pool waits on .NET's thread injection, which adds about two threads a second; the delay
    ///         is therefore a property of how many workers the whole host has blocked and is unrelated
    ///         to the read, which is a memcpy out of a mounted bundle.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reproduced on macOS by blocking two hundred pool workers</b>, which delayed a
    ///         newly queued item by 1 m 45 s and produced the CI message exactly. Under it the loop
    ///         sat at <c>Loading == 1</c> for the whole 1 m 45 s and then went viewable in the frame
    ///         after the read was finally scheduled. So no number would have been right: thirty
    ///         seconds, sixty, two hundred — each is a guess about the host's scheduler, and the
    ///         previous guess is the one this file already paid for once.
    ///     </para>
    ///     <para>
    ///         So the giving-up condition is a fact about the source instead. When there is no read
    ///         outstanding, nothing loading and nothing queued, the source has done everything it is
    ///         going to do and another frame cannot change the answer — that is a real failure and it
    ///         is reported on the frame it becomes true, which is sooner than any deadline. While any
    ///         of the three is non-zero the work exists and is waited for, however long the pool takes
    ///         to run it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>All three, and <see cref="AssetTextureSource.Reading" /> is the one that is easy
    ///         to leave out.</b> Before the header read has been taken up the streamer has no texture
    ///         registered at all, so <c>Loading</c> and <c>PendingRequests</c> are both zero — the
    ///         predicate would be vacuously satisfied on the first frame and the settle would fail
    ///         before anything had been asked for.
    ///     </para>
    /// </remarks>
    void Settle(AssetTextureSource source, int want = -1) =>
        Until(
            source,
            () => source.TryGet(Bark, out var view) && view.IsValid,
            "the texture never became viewable",
            frame => {
                if (want >= 0) {
                    frame.Want(Bark, want);
                }

                // Asked before the frame as well, because that is what starts the read.
                frame.TryGet(Bark, out _);
            }
        );

    /// <summary>Runs frames until something is true, or until nothing could make it true.</summary>
    /// <param name="source">The source to drive.</param>
    /// <param name="done">What is being waited for.</param>
    /// <param name="never">What to say when the source runs out of things to do first.</param>
    /// <param name="ask">What each frame asks for, before it is recorded.</param>
    /// <remarks>
    ///     ⚠ The predicate is read <em>after</em> the frame, because the view a texture is answered
    ///     with is created inside <see cref="AssetTextureSource.Update" /> — a frame that finished the
    ///     job answers on that frame and not the next one.
    /// </remarks>
    void Until(AssetTextureSource source, Func<bool> done, string never, Action<AssetTextureSource> ask) {
        var quiet = 0;

        while (true) {
            var before = Working(source);

            ask(source);
            Record(source);

            if (done()) {
                return;
            }

            // ⚠ Consecutive frames, and reset on either end of one. A read that finishes part way
            // through a frame leaves nothing outstanding by the end of it and has not been taken up
            // yet — the take-up is in the next frame's ask — so a single idle observation says
            // nothing. Eight in a row with no read outstanding, nothing loading and nothing queued at
            // either end is a fact about the source: it has run out of things to do.
            quiet = before || Working(source) ? 0 : quiet + 1;

            Assert.True(
                quiet < 8,
                $"{never}, and for {quiet} frames the source has had no read outstanding, nothing "
                + "loading and nothing queued — so no number of further frames can change it"
            );

            // A yield rather than a budget: it hands the core to whatever is doing the reading, and
            // nothing above decides anything by how many of these have gone by.
            Thread.Sleep(1);
        }
    }

    /// <summary>Whether the source has anything outstanding that a further frame could take up.</summary>
    /// <remarks>
    ///     ⚠ <b>All three, and <see cref="AssetTextureSource.Reading" /> is the one that is easy to
    ///     leave out.</b> Before the header read has been taken up the streamer has no texture
    ///     registered at all, so <c>Loading</c> and <c>PendingRequests</c> are both zero — a settle
    ///     that looked only at those two would call the source idle on its first frame, before
    ///     anything had been asked for, and give up vacuously.
    /// </remarks>
    static bool Working(AssetTextureSource source) =>
        source.Reading(Bark) is not null
        || source.Streaming is { Loading: > 0 }
        || source.Streaming is { PendingRequests: > 0 };

    /// <summary>A content manager holding one KTX2 texture of a given size.</summary>
    /// <remarks>
    ///     Uncompressed, and that is a constraint rather than a convenience: a page is a byte range
    ///     of a mapped bundle, and an LZ4-packed chunk has no slice of the map that is the payload.
    ///     See <see cref="AssetTextureStreamSource" />.
    /// </remarks>
    static AssetManager Content(int width, int height) {
        var files = new VirtualFileSystem();
        var storage = new MemoryFileProvider();

        files.Mount(new("/store"), storage);
        files.Mount(new("/bundles"), storage);

        var backend = new FileOdbBackend(files, new("/store/odb"));
        var database = new ObjectDatabase(backend);

        var data = new TextureData(PixelFormat.R8UNorm, width, height);

        for (var level = 0; level < data.LevelCount; level++) {
            data.LevelSpan(level).Fill((byte)(0x10 + level));
        }

        var texture = database.WriteRaw(
            ContentHash.TypeId(typeof(TextureData)),
            [],
            Ktx2.Write(data),
            CompressionMethod.None
        );

        var bundle = new BundleWriter();
        bundle.AddAll(backend);

        using (var target = files.OpenWrite(new("/bundles/Main.bundle"))) {
            target.Write(bundle.Build());
        }

        var catalog = new ContentCatalog(
            CatalogFormat.Version,
            default,
            "Windows",
            [new("bark", texture, "Main", ContentProvider.Local, [], [], 0, Reference: Bark)],
            [new("Main", "", default, 0, 0, CompressionMethod.None, [])]
        );

        return new(catalog, new LocalBundleSource(files, new("/bundles")));
    }

    /// <inheritdoc />
    public void Dispose() => device.Dispose();
}
