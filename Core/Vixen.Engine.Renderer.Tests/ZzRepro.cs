// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

// TEMPORARY reproduction harness for task 398. Deleted before the branch is finished.

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

public sealed class ZzRepro(ITestOutputHelper output) : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    static readonly AssetReference Bark = new(new AssetId(Guid.NewGuid()), SubAssetId.Main);

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Occupies the thread pool so a freshly queued work item waits on thread injection.</summary>
    sealed class Saturator : IDisposable {
        readonly ManualResetEventSlim release = new(false);
        int running;

        public int Running => Volatile.Read(ref running);

        public Saturator(int items) {
            ThreadPool.SetMinThreads(1, 1);

            for (var i = 0; i < items; i++) {
                ThreadPool.UnsafeQueueUserWorkItem(
                    _ => {
                        Interlocked.Increment(ref running);
                        release.Wait(TimeSpan.FromMinutes(5));
                    },
                    null
                );
            }
        }

        public void Dispose() {
            release.Set();
            ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount);
        }
    }

    /// <summary>
    ///     INSTRUMENT CHECK. The saturator has to actually delay a queued work item; if this passes
    ///     with a small delay the harness proves nothing about the test below it.
    /// </summary>
    [Fact]
    public void TheSaturatorReallyDelaysAQueuedWorkItem() {
        using var saturator = new Saturator(200);

        var started = Stopwatch.StartNew();
        var probe = new ManualResetEventSlim(false);
        var waited = TimeSpan.Zero;

        ThreadPool.UnsafeQueueUserWorkItem(
            _ => {
                waited = started.Elapsed;
                probe.Set();
            },
            null
        );

        Assert.True(probe.Wait(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken), "the probe never ran at all");

        output.WriteLine($"pool workers occupied: {saturator.Running}");
        output.WriteLine($"a queued work item waited {waited}");

        Assert.True(
            waited > Patience,
            $"the saturator only delayed a work item by {waited}, which is inside the {Patience} "
            + "this harness has to exceed to model CI"
        );
    }

    /// <summary>The loop exactly as it is on master, under a saturated pool.</summary>
    [Fact]
    public void TheWallClockLoopFailsUnderASaturatedPool() {
        using var source = new AssetTextureSource(device, Content(256, 256), 4 * 1024 * 1024);
        using var saturator = new Saturator(200);

        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < Patience && !(source.TryGet(Bark, out _) && source.StreamingSwaps > 0)) {
            source.Want(Bark, 32);

            Record(source);
            Thread.Sleep(1);
        }

        output.WriteLine($"after {waited.Elapsed}: swaps {source.StreamingSwaps}");

        Assert.True(
            source.TryGet(Bark, out var view) && view.IsValid,
            $"the texture was never viewable in {Patience}"
        );
    }

    /// <summary>
    ///     The REAL fixture, run under a saturated pool. Not a copy of it — the method below is the
    ///     one CI runs.
    /// </summary>
    [Fact]
    public void TheRealSizingTestSurvivesASaturatedPool() {
        using var saturator = new Saturator(200);
        using var real = new AssetTextureStreamingTests();

        var waited = Stopwatch.StartNew();

        real.SizingAStreamedTextureKeepsItBelowTheWholeFile();

        output.WriteLine($"the real test passed in {waited.Elapsed} with {saturator.Running} workers blocked");
    }

    void Record(AssetTextureSource source) {
        using var commands = device.BeginCommandList();

        source.Update(commands);
        commands.Finish();

        device.GraphicsQueue.Submit([commands]);
    }

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

    public void Dispose() => device.Dispose();
}
