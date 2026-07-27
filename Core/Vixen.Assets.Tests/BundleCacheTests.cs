// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Hashing;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.Assets.Tests;

/// <summary>
///     The cache is the only part of the content system that has to survive things going wrong: a
///     connection that drops, a server that is not the one the catalog was built against, a phone that
///     is turned off half way through a 400 MB pack. These are the tests for what it does about each.
/// </summary>
public sealed class BundleCacheTests {
    /// <summary>The straightforward case: nothing cached, one download, a file that is right.</summary>
    [Fact]
    public async Task AFetchedBundleIsVerifiedAndCommitted() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);

        Assert.False(world.Cache.IsCached(bundle));

        var path = await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.True(world.Cache.IsCached(bundle));
        Assert.Equal(world.Contents("pack"), await world.Files.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        // The partial file is gone rather than left as a second copy of a large download.
        Assert.False(world.Files.Exists(world.Cache.PartialPathOf(bundle)));
    }

    /// <summary>
    ///     The reason the partial file exists. A download that dies at 1 KB of 4 KB resumes at 1 KB,
    ///     and the second attempt moves the remaining 3 KB rather than all 4 again. On the connections
    ///     this feature is for, without it a large pack never finishes at all.
    /// </summary>
    [Fact]
    public async Task ADownloadThatDiesResumesFromWhereItStopped() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);
        world.Transport.CutOffAfter = 1000;

        await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal(1000, world.Cache.ReceivedSoFar(bundle));
        Assert.False(world.Cache.IsCached(bundle));

        world.Transport.CutOffAfter = int.MaxValue;
        await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.True(world.Cache.IsCached(bundle));
        Assert.Equal([0, 1000], world.Transport.RequestedOffsets);

        // 4096 bytes of content moved by two attempts: the 1000 that arrived and the 3096 that
        // followed. Anything more means the resume did not resume.
        Assert.Equal(4096, world.Transport.BytesServed);
    }

    /// <summary>
    ///     A server is entitled to ignore a byte range and send the whole file. Appending that to a
    ///     partial one would produce a file that is too long and hashes to nothing, so the partial is
    ///     thrown away and the download starts again — slower, and correct.
    /// </summary>
    [Fact]
    public async Task AServerThatIgnoresTheRangeStartsAgainRatherThanCorrupting() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);
        world.Transport.CutOffAfter = 1000;

        await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken)
        );

        world.Transport.CutOffAfter = int.MaxValue;
        world.Transport.IgnoresRanges = true;

        var path = await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.Equal(
            world.Contents("pack"),
            await world.Files.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)
        );
    }

    /// <summary>
    ///     A server that answers from somewhere neither asked for nor the beginning is not something
    ///     to guess about — the bundle is refused and named, rather than a file being assembled out of
    ///     bytes from an unknown offset.
    /// </summary>
    [Fact]
    public async Task AServerAnsweringFromTheWrongOffsetIsRefused() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);

        // Seeds a partial the server will not honour: it is told to continue from 1000 and the
        // transport is rigged to answer from 500.
        using (var partial = world.Files.OpenWrite(world.Cache.PartialPathOf(bundle))) {
            partial.Write(world.Contents("pack").AsSpan(0, 1000));
        }

        world.Transport.AnswerFrom = 500;

        var failure = await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken)
        );

        Assert.Contains("byte 1000", failure.Message, StringComparison.Ordinal);
        Assert.Contains("byte 500", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Nothing is committed unverified. A bundle whose CRC does not match the catalog's is deleted
    ///     rather than kept — a resume against corrupt bytes would append good data to bad and fail the
    ///     same way for ever.
    /// </summary>
    [Fact]
    public async Task ABundleThatArrivesCorruptIsRejectedAndNotKept() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);

        // The same length and different content: the length check cannot catch this and the CRC can.
        var wrong = world.Contents("pack").ToArray();
        wrong[2000] ^= 0xFF;
        world.Transport.Serve(bundle.Url, wrong);

        var failure = await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken)
        );

        Assert.Contains("CRC", failure.Message, StringComparison.Ordinal);
        Assert.False(world.Cache.IsCached(bundle));
        Assert.False(world.Files.Exists(world.Cache.PartialPathOf(bundle)));
    }

    /// <summary>
    ///     A body that ends short of what the catalog says is refused before the CRC is even computed,
    ///     and its bytes go too — a short body that the server considers complete will be just as short
    ///     next time, so resuming from it would loop.
    /// </summary>
    [Fact]
    public async Task ADownloadThatEndsShortIsRefused() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);
        world.Transport.Serve(bundle.Url, world.Contents("pack").AsSpan(0, 3000).ToArray());

        var failure = await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken)
        );

        Assert.Contains("3000 bytes", failure.Message, StringComparison.Ordinal);
        Assert.False(world.Files.Exists(world.Cache.PartialPathOf(bundle)));
    }

    /// <summary>
    ///     Two loads that want the same pack share one download. Checking "is it cached" and then
    ///     fetching would fetch twice under exactly the concurrency the check exists for, and both
    ///     would be appending to the same partial file.
    /// </summary>
    [Fact]
    public async Task TwoCallersWantingOneBundleShareOneDownload() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);

        // Held, so both calls are genuinely in flight together. Without this the fake transport
        // answers synchronously, the first call finishes before the second starts, and the second is
        // an ordinary cache hit — which passes whether anything deduplicates or not.
        world.Transport.Hold();

        var first = world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);
        var second = world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.False(first.IsCompleted);
        Assert.Same(first, second);

        world.Transport.Release();
        var both = await Task.WhenAll(first, second);

        Assert.Equal(both[0], both[1]);
        Assert.Equal(1, world.Transport.Requests);
        Assert.Equal(4096, world.Transport.BytesServed);
    }

    /// <summary>A failed download is not remembered, so a retry is a new attempt and not the old failure.</summary>
    [Fact]
    public async Task AFailedDownloadCanBeRetried() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);
        world.Transport.CutOffAfter = 1000;

        await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken)
        );

        world.Transport.CutOffAfter = int.MaxValue;
        await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.True(world.Cache.IsCached(bundle));
    }

    /// <summary>A bundle that is already here costs nothing, which is the point of a cache.</summary>
    [Fact]
    public async Task ACachedBundleIsNotFetchedAgain() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);

        await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);
        await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, world.Transport.Requests);
    }

    /// <summary>
    ///     A committed file that is the wrong length is a miss, not a corrupt read. That is the window
    ///     the copy-then-delete commit leaves — a crash between the two writes — and the length check
    ///     is what closes it without hashing every cached byte at every load.
    /// </summary>
    [Fact]
    public async Task ATruncatedCachedFileIsAMissRatherThanACorruptRead() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);

        using (var torn = world.Files.OpenWrite(world.Cache.PathOf(bundle))) {
            torn.Write(world.Contents("pack").AsSpan(0, 2048));
        }

        Assert.False(world.Cache.IsCached(bundle));

        await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.True(world.Cache.IsCached(bundle));
    }

    /// <summary>
    ///     A partial longer than the bundle is not a resumable download. It is left over from
    ///     something else, and continuing from its end would ask the server for bytes past the end of
    ///     the resource.
    /// </summary>
    [Fact]
    public async Task APartialLongerThanTheBundleIsDiscardedRatherThanResumedFrom() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);

        using (var stale = world.Files.OpenWrite(world.Cache.PartialPathOf(bundle))) {
            stale.Write(new byte[5000]);
        }

        await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.Equal([0], world.Transport.RequestedOffsets);
        Assert.True(world.Cache.IsCached(bundle));
    }

    /// <summary>
    ///     Cancelling keeps what arrived. Someone who backgrounds the game half way through a pack
    ///     expects to carry on, not to start again.
    /// </summary>
    [Fact]
    public async Task CancellingLeavesWhatArrivedWhereAResumeWillFindIt() {
        var world = new CacheWorld(bufferSize: 1024);
        var bundle = world.Publish("pack", 8192);

        using var cancelling = new CancellationTokenSource();
        var progress = new Watching(report => {
            if (report.Received >= 2048) {
                cancelling.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await world.Cache.EnsureAsync(bundle, progress, cancelling.Token)
        );

        // Enough arrived to be worth keeping, and not all of it — which is the state a resume exists
        // to pick up from.
        Assert.InRange(world.Cache.ReceivedSoFar(bundle), 2048, 8191);
        Assert.False(world.Cache.IsCached(bundle));

        await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);

        Assert.True(world.Cache.IsCached(bundle));
        Assert.Equal(8192, world.Transport.BytesServed);
    }

    /// <summary>
    ///     Progress rises to the bundle's size and stops there, so a bar that follows it fills up
    ///     exactly once. Reporting bytes-this-attempt instead is the bug that makes a resumed download
    ///     start its bar at zero having already got most of the way.
    /// </summary>
    [Fact]
    public async Task ProgressRisesToTheBundlesSizeAndCountsWhatWasAlreadyThere() {
        var world = new CacheWorld(bufferSize: 1024);
        var bundle = world.Publish("pack", 4096);
        world.Transport.CutOffAfter = 2048;

        var interrupted = new Watching();

        await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await world.Cache.EnsureAsync(bundle, interrupted, TestContext.Current.CancellationToken)
        );

        Assert.Equal(0, interrupted.Reports[0].Received);
        Assert.Equal(2048, interrupted.Reports[^1].Received);

        world.Transport.CutOffAfter = int.MaxValue;
        var resumed = new Watching();
        await world.Cache.EnsureAsync(bundle, resumed, TestContext.Current.CancellationToken);

        // The resumed run starts where the first one stopped rather than at zero, and finishes at the
        // whole bundle rather than at the 2048 bytes this attempt actually moved.
        Assert.Equal(2048, resumed.Reports[0].Received);
        Assert.Equal(4096, resumed.Reports[^1].Received);
        Assert.Equal(1.0, resumed.Reports[^1].Fraction);
        Assert.All(resumed.Reports, report => Assert.Equal("pack", report.Bundle));

        for (var index = 1; index < resumed.Reports.Count; index++) {
            Assert.True(resumed.Reports[index].Received > resumed.Reports[index - 1].Received);
        }
    }

    /// <summary>Verifying re-hashes, which is the check a cache hit deliberately does not do.</summary>
    [Fact]
    public async Task VerifyingCatchesACachedFileThatWentBadWithoutChangingLength() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);

        await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);
        Assert.True(await world.Cache.VerifyAsync(bundle, TestContext.Current.CancellationToken));

        var rotted = world.Contents("pack").ToArray();
        rotted[100] ^= 0x01;

        using (var writing = world.Files.OpenWrite(world.Cache.PathOf(bundle))) {
            writing.Write(rotted);
        }

        // Still the right length, so an ordinary load would use it; only re-hashing finds this.
        Assert.True(world.Cache.IsCached(bundle));
        Assert.False(await world.Cache.VerifyAsync(bundle, TestContext.Current.CancellationToken));
    }

    /// <summary>Evicting takes the finished file and any half-finished one with it.</summary>
    [Fact]
    public async Task EvictingRemovesTheBundleAndAnythingPartial() {
        var world = new CacheWorld();
        var bundle = world.Publish("pack", 4096);

        await world.Cache.EnsureAsync(bundle, null, TestContext.Current.CancellationToken);

        using (var leftover = world.Files.OpenWrite(world.Cache.PartialPathOf(bundle))) {
            leftover.Write([1, 2, 3]);
        }

        Assert.True(world.Cache.Evict(bundle));
        Assert.False(world.Cache.IsCached(bundle));
        Assert.False(world.Files.Exists(world.Cache.PartialPathOf(bundle)));
        Assert.False(world.Cache.Evict(bundle));
    }

    /// <summary>What a settings screen shows and what a "free up space" button does.</summary>
    [Fact]
    public async Task TheCacheReportsAndClearsWhatItIsUsing() {
        var world = new CacheWorld();
        var first = world.Publish("one", 4096);
        var second = world.Publish("two", 2048);

        await world.Cache.EnsureAsync(first, null, TestContext.Current.CancellationToken);
        await world.Cache.EnsureAsync(second, null, TestContext.Current.CancellationToken);

        Assert.Equal(6144, world.Cache.TotalSize());
        Assert.Equal(2, world.Cache.Clear());
        Assert.Equal(0, world.Cache.TotalSize());
        Assert.False(world.Cache.IsCached(first));
    }

    /// <summary>
    ///     A bundle with no URL is one that shipped with the application, so finding it missing from a
    ///     cache means the catalog and the build disagree — which is what the message says rather than
    ///     reporting a network failure that never happened.
    /// </summary>
    [Fact]
    public async Task ABundleWithNoUrlSaysTheCatalogAndTheBuildDisagree() {
        var world = new CacheWorld();
        var local = new CatalogBundle("shipped", "", new(7, 7), 10, 0, CompressionMethod.Lz4, []);

        var failure = await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await world.Cache.EnsureAsync(local, null, TestContext.Current.CancellationToken)
        );

        Assert.Contains("no URL", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, world.Transport.Requests);
    }

    /// <summary>A URL nothing answers is a bundle that cannot be opened, named as such.</summary>
    [Fact]
    public async Task AUrlThatAnswersNothingBecomesABundleFailure() {
        var world = new CacheWorld();
        var missing = new CatalogBundle("gone", "https://example.invalid/gone", new(9, 9), 10, 0, CompressionMethod.Lz4, []);

        var failure = await Assert.ThrowsAsync<BundleUnavailableException>(
            async () => await world.Cache.EnsureAsync(missing, null, TestContext.Current.CancellationToken)
        );

        Assert.Equal("gone", failure.Bundle);
        Assert.IsType<ContentTransportException>(failure.InnerException);
    }

    /// <summary>
    ///     The cache key is the content hash, so a rebuilt bundle with the same name is a miss rather
    ///     than the old one being served for ever. This is the property that makes a content update
    ///     work at all.
    /// </summary>
    [Fact]
    public async Task ARebuiltBundleWithTheSameNameIsACacheMiss() {
        var world = new CacheWorld();
        var version1 = world.Publish("pack", 4096);

        await world.Cache.EnsureAsync(version1, null, TestContext.Current.CancellationToken);

        var version2 = world.Publish("pack", 4096, seed: 99);

        Assert.False(world.Cache.IsCached(version2));
        Assert.True(world.Cache.IsCached(version1));
    }

    /// <summary>
    ///     Records progress on the thread that reports it. <see cref="Progress{T}" /> posts to a
    ///     synchronisation context, so a test using it asserts on callbacks that have not run yet and
    ///     cancels downloads that have already finished.
    /// </summary>
    sealed class Watching(Action<BundleProgress>? onReport = null) : IProgress<BundleProgress> {
        public List<BundleProgress> Reports { get; } = [];

        public void Report(BundleProgress value) {
            Reports.Add(value);
            onReport?.Invoke(value);
        }
    }

    /// <summary>A cache, a transport and some bundles to fetch through them.</summary>
    sealed class CacheWorld {
        readonly Dictionary<string, byte[]> published = new(StringComparer.Ordinal);

        public VirtualFileSystem Files { get; } = new();
        public FakeContentTransport Transport { get; } = new();
        public BundleCache Cache { get; }

        /// <summary>Sets one up.</summary>
        /// <param name="bufferSize">How much moves at a time, which decides how often progress lands.</param>
        public CacheWorld(int bufferSize = 128 * 1024) {
            Files.Mount(new("/cache"), new MemoryFileProvider());
            Cache = new(Files, new("/cache"), Transport) { BufferSize = bufferSize };
        }

        /// <summary>Makes up a bundle of a given size and serves it.</summary>
        public CatalogBundle Publish(string name, int size, byte seed = 0) {
            var contents = new byte[size];

            for (var index = 0; index < size; index++) {
                contents[index] = (byte)((index * 31) + seed);
            }

            var url = $"https://content.example/{name}-{seed}.bundle";
            published[name] = contents;
            Transport.Serve(url, contents);

            return new(
                name,
                url,
                ContentHash.Compute(contents),
                size,
                Crc32.HashToUInt32(contents),
                CompressionMethod.Lz4,
                []
            );
        }

        public byte[] Contents(string name) => published[name];
    }
}
