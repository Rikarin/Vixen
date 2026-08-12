// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
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

        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < Patience) {
            Assert.True(source.TryGet(Bark, out var view) && view.IsValid);

            Record(source);

            if (source.Streaming.ResidentLevel(0) == 0 && source.StreamingSwaps > 0) {
                Assert.Contains(
                    device.Recorder!.OfKind(RecordedCommandKind.CopyBufferToTexture),
                    copy => copy is { D: 0, E: 256 }
                );

                Assert.Equal(0L, source.StreamingRefusals);

                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"the texture never reached its full resolution in {Patience}");
    }

    /// <summary>
    ///     And a caller that knows how big it needs the texture to be narrows that. Sixty frames of
    ///     asking for 32 texels never brings the 256×256 level in, whatever the pool could hold.
    /// </summary>
    [Fact]
    public void SizingAStreamedTextureKeepsItBelowTheWholeFile() {
        using var source = new AssetTextureSource(device, Content(256, 256), 4 * 1024 * 1024);

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

        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < Patience && source.Streaming!.Rejections == 0) {
            source.TryGet(Bark, out _);

            Record(source);

            Thread.Sleep(1);
        }

        Assert.True(source.Streaming!.Rejections > 0, "nothing was refused by a one-page pool");
        Assert.True(source.Streaming.ResidentBytes <= source.Streaming.Budget);
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

        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < Patience && log.Lines.Count == 0) {
            source.TryGet(Bark, out _);

            Record(source);

            Thread.Sleep(1);
        }

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

    /// <summary>Asks and updates until the texture is viewable, or fails saying it never was.</summary>
    void Settle(AssetTextureSource source) {
        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < Patience) {
            var ready = source.TryGet(Bark, out var view);

            Record(source);

            if (ready && view.IsValid) {
                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"the texture was never viewable in {Patience}");
    }

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

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
