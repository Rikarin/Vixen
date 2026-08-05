// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
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
    ///     The floor. Nothing has asked for this texture at any size, and it is still sampleable —
    ///     at the resolution its pinned first page covers, and not at the one the file holds.
    /// </summary>
    [Fact]
    public void AStreamedTextureArrivesAtItsPinnedFloorBeforeAnythingWantsIt() {
        using var source = new AssetTextureSource(device, Content(256, 256), 4 * 1024 * 1024);

        Assert.NotNull(source.Streaming);

        Settle(source);

        Assert.Equal(1L, source.StreamingSwaps);

        var copies = device.Recorder!.OfKind(RecordedCommandKind.CopyBufferToTexture);

        // 65_536 bytes of R8 level data is the 128×128 tail and everything under it; the 256×256
        // level alone is one page over, so it is not here.
        Assert.Contains(copies, copy => copy is { D: 0, E: 128 });
        Assert.DoesNotContain(copies, copy => copy.E == 256);
        Assert.Equal(1, source.Streaming!.Textures);
    }

    /// <summary>
    ///     And it grows. A view says how wide it needs the texture to be, the pages arrive, and the
    ///     image is replaced by a larger complete one rather than patched in place.
    /// </summary>
    [Fact]
    public void WantingAStreamedTextureAtFullWidthSwapsItForTheLargerImage() {
        var assets = Content(256, 256);
        using var source = new AssetTextureSource(device, assets, 4 * 1024 * 1024);

        Settle(source);

        Assert.Equal(1L, source.StreamingSwaps);
        device.Recorder!.Clear();

        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < Patience) {
            source.Want(Bark, 256);

            Record(source);

            if (source.StreamingSwaps > 1) {
                var copies = device.Recorder.OfKind(RecordedCommandKind.CopyBufferToTexture);

                Assert.Contains(copies, copy => copy is { D: 0, E: 256 });
                Assert.True(source.TryGet(Bark, out var view) && view.IsValid);
                Assert.Equal(0L, source.StreamingRefusals);

                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"the texture never grew past its floor in {Patience}");
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
            source.Want(Bark, 256);

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
