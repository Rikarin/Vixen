// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Engine.Renderer.Tests;

public sealed class TextureStreamingTests {
    /// <summary>A page size small enough that a test texture is many pages.</summary>
    const int PageSize = 256;

    [Fact]
    public void ATextureWhoseWholeChainFitsInOnePageIsNotStreamedAtAll() {
        var files = new Files();
        files.Add(0, PixelFormat.R8UNorm, 8, 8);

        using var streamer = new TextureStreamer(files, 64 * PageSize, PageSize);

        Assert.False(streamer.Register(0, files.Layout(0)));
        Assert.False(streamer.IsRegistered(0));
        Assert.Equal(0, streamer.Textures);
    }

    /// <summary>
    ///     The floor the whole degradation story rests on: a registered texture has its first page
    ///     pinned, so it always has a complete tail to sample even before anything has asked for it.
    /// </summary>
    [Fact]
    public void ARegisteredTextureIsResidentAtItsPinnedLevelBeforeAnythingWantsIt() {
        var files = new Files();
        files.Add(0, PixelFormat.R8UNorm, 64, 64);

        using var streamer = new TextureStreamer(files, 64 * PageSize, PageSize);

        Assert.True(streamer.Register(0, files.Layout(0)));

        Drain(streamer);

        // 8×8 and smaller is 85 bytes, which is the most that fits in one 256-byte page.
        Assert.Equal(3, streamer.PinnedLevel(0));
        Assert.Equal(3, streamer.ResidentLevel(0));
        Assert.Equal(1, streamer.Loads);
    }

    [Fact]
    public void WantingATextureAtItsFullWidthBringsEveryLevelInAndTheBytesAreTheFiles() {
        var files = new Files();
        files.Add(0, PixelFormat.R8UNorm, 64, 64);

        using var streamer = new TextureStreamer(files, 64 * PageSize, PageSize);
        streamer.Register(0, files.Layout(0));

        streamer.Want(0, 64);
        Drain(streamer);

        Assert.Equal(0, streamer.ResidentLevel(0));

        var layout = files.Layout(0);
        var copied = new byte[layout.DataLength];

        Assert.Equal(layout.DataLength, streamer.CopyTail(0, 0, copied));
        Assert.Equal(files.Data(0, layout.DataOffset, layout.DataLength), copied);
    }

    /// <summary>
    ///     A tail is the levels the pool holds and no more, in the file's own smallest-first order —
    ///     which is what an upload reads and un-reverses.
    /// </summary>
    [Fact]
    public void APartiallyResidentTextureCopiesOutOnlyTheLevelsItHas() {
        var files = new Files();
        files.Add(0, PixelFormat.R8UNorm, 64, 64);

        using var streamer = new TextureStreamer(files, 64 * PageSize, PageSize);
        streamer.Register(0, files.Layout(0));

        streamer.Want(0, 16);
        Drain(streamer);

        var layout = files.Layout(0);

        Assert.Equal(2, streamer.ResidentLevel(0));

        var tail = new byte[layout.TailLength(2)];

        Assert.Equal(tail.Length, streamer.CopyTail(0, 2, tail));
        Assert.Equal(files.Data(0, layout.DataOffset, tail.Length), tail);

        // And what is not resident says so rather than answering with a short or stale run.
        Assert.Equal(-1, streamer.CopyTail(0, 0, new byte[layout.DataLength]));
    }

    /// <summary>
    ///     The criterion the whole thing is judged on. A scene several times over budget holds the
    ///     budget, draws something coarser, and counts the requests it could not meet — a manager
    ///     that treated its budget as a target would report a number nobody can plan against.
    /// </summary>
    [Fact]
    public void ABudgetTooSmallForTheSceneIsHeldAndTheRefusalsAreCounted() {
        var files = new Files();

        for (var texture = 0; texture < 4; texture++) {
            files.Add(texture, PixelFormat.R8UNorm, 64, 64);
        }

        // Four pages for four textures: every byte of the budget is a pinned first page, so there is
        // nothing left to evict and every further request has to be refused.
        using var streamer = new TextureStreamer(files, 4 * PageSize, PageSize);

        for (var texture = 0; texture < 4; texture++) {
            streamer.Register(texture, files.Layout(texture));
        }

        Drain(streamer);

        for (var frame = 0; frame < 8; frame++) {
            for (var texture = 0; texture < 4; texture++) {
                streamer.Want(texture, 64);
            }

            streamer.Service(64);
            Assert.True(streamer.ResidentBytes <= streamer.Budget);
        }

        Drain(streamer);

        Assert.Equal(4 * PageSize, streamer.Budget);
        Assert.True(streamer.ResidentBytes <= streamer.Budget);
        Assert.True(streamer.Rejections > 0, $"nothing was refused; {streamer.Rejections}");

        for (var texture = 0; texture < 4; texture++) {
            Assert.Equal(streamer.PinnedLevel(texture), streamer.ResidentLevel(texture));
        }
    }

    /// <summary>
    ///     Least recently <em>used</em>: a texture nothing has wanted for a while gives its pages up
    ///     to one that is being drawn, and falls back to its pinned tail rather than to nothing.
    /// </summary>
    [Fact]
    public void ATextureNothingWantsGivesItsPagesToOneThatIsWanted() {
        var files = new Files();
        files.Add(0, PixelFormat.R8UNorm, 64, 64);
        files.Add(1, PixelFormat.R8UNorm, 64, 64);

        using var streamer = new TextureStreamer(files, 24 * PageSize, PageSize);
        streamer.Register(0, files.Layout(0));
        streamer.Register(1, files.Layout(1));

        streamer.Want(0, 64);
        Drain(streamer);

        Assert.Equal(0, streamer.ResidentLevel(0));
        Assert.Equal(0L, streamer.Evictions);

        // The camera turns. Nothing touches texture 0 from here on.
        for (var frame = 0; frame < 32; frame++) {
            streamer.Want(1, 64);
            streamer.Service(64);
            Thread.Sleep(1);
        }

        Drain(streamer);

        Assert.Equal(0, streamer.ResidentLevel(1));
        Assert.True(streamer.Evictions > 0, "nothing was evicted to make room");
        Assert.True(streamer.ResidentBytes <= streamer.Budget);

        // It gave levels up, and it never went below its pinned floor. Which level it stopped at is
        // whatever the budget had left over, and asserting a particular one would be asserting the
        // arithmetic of this test rather than the behaviour.
        var fallback = streamer.ResidentLevel(0);

        Assert.True(fallback > 0, "texture 0 kept every level it had");
        Assert.True(fallback <= streamer.PinnedLevel(0), $"texture 0 fell below its pinned floor: {fallback}");
    }

    /// <summary>
    ///     <c>textures.mipBias</c>, consumed. It is applied to what is asked for rather than to what
    ///     is sampled, because <c>SamplerDescription.LodBias</c> reaches the API on Vulkan alone.
    /// </summary>
    [Fact]
    public void APositiveMipBiasAsksForACoarserTail() {
        var files = new Files();
        files.Add(0, PixelFormat.R8UNorm, 64, 64);

        using var streamer = new TextureStreamer(files, 64 * PageSize, PageSize) { MipBias = 2f };
        streamer.Register(0, files.Layout(0));

        streamer.Want(0, 64);
        Drain(streamer);

        // Two levels of bias off a full-width request is the 16×16 level, not the 64×64 one.
        Assert.Equal(2, streamer.ResidentLevel(0));
        Assert.True(streamer.ResidentBytes < files.Layout(0).DataLength);
    }

    [Fact]
    public void TheWantedWidthFallsWithDistanceAndRisesWithTheViewport() {
        var near = TextureStreamer.WantedWidth(1f, 2f, 1080f, MathF.PI / 3f);
        var far = TextureStreamer.WantedWidth(1f, 20f, 1080f, MathF.PI / 3f);
        var tall = TextureStreamer.WantedWidth(1f, 2f, 2160f, MathF.PI / 3f);

        Assert.True(near > far);
        Assert.Equal(2 * near, tall, tolerance: 1);

        // Nothing behind the eye divides by zero, and nothing with no size asks for anything.
        Assert.True(TextureStreamer.WantedWidth(1f, 0f, 1080f, MathF.PI / 3f) > 0);
        Assert.Equal(0, TextureStreamer.WantedWidth(0f, 2f, 1080f, MathF.PI / 3f));
    }

    [Fact]
    public void AnUnregisteredTextureIsWantedWithoutComplaintAndWithoutCost() {
        var files = new Files();
        using var streamer = new TextureStreamer(files, 64 * PageSize, PageSize);

        streamer.Want(7, 1024);
        streamer.Service();

        Assert.Equal(0, streamer.PendingRequests);
        Assert.Equal(0L, streamer.Loads);
        Assert.False(streamer.IsRegistered(7));
    }

    /// <summary>Services until nothing is in flight and nothing is queued.</summary>
    static void Drain(TextureStreamer streamer) {
        for (var round = 0; round < 400; round++) {
            var placed = streamer.Service(256);

            if (placed == 0 && streamer.Loading == 0 && streamer.PendingRequests == 0) {
                return;
            }

            Thread.Sleep(1);
        }
    }

    /// <summary>A set of KTX2 files in memory, served as byte ranges.</summary>
    sealed class Files : ITextureStreamSource {
        readonly Dictionary<int, byte[]> files = [];
        readonly Dictionary<int, Ktx2Layout> layouts = [];

        public void Add(int texture, PixelFormat format, int width, int height) {
            var data = new TextureData(format, width, height);

            // A distinct byte per level, so a page holding the wrong level is visible rather than
            // plausible.
            for (var level = 0; level < data.LevelCount; level++) {
                data.LevelSpan(level).Fill((byte)(0x10 + level));
            }

            var file = Ktx2.Write(data);

            files[texture] = file;
            layouts[texture] = Ktx2.ReadLayout(file);
        }

        public Ktx2Layout Layout(int texture) => layouts[texture];

        public byte[] Data(int texture, long offset, long length) =>
            files[texture].AsSpan((int)offset, (int)length).ToArray();

        public ValueTask<int> ReadAsync(
            int texture,
            long offset,
            Memory<byte> destination,
            CancellationToken cancellation
        ) {
            var file = files[texture];
            var count = (int)Math.Min(destination.Length, file.Length - offset);

            file.AsSpan((int)offset, count).CopyTo(destination.Span);

            return ValueTask.FromResult(count);
        }
    }
}
