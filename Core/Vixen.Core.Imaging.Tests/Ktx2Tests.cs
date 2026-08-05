// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

public sealed class Ktx2Tests {
    /// <summary>
    ///     The external check. Every byte of a 1×1 RGBA8 file is worked out from the specification
    ///     here and compared to what the writer produced — so a misread of the layout fails, where a
    ///     round trip through this same code would not. It does not catch a *misunderstanding* of the
    ///     spec, which is what running the Khronos validator would; that is owed and stated in the
    ///     class remarks.
    /// </summary>
    [Fact]
    public void ASingleTexelFileIsExactlyWhatTheSpecificationSaysItShouldBe() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 1, 1, levelCount: 1);
        new byte[] { 0x11, 0x22, 0x33, 0x44 }.CopyTo(texture.LevelSpan(0));

        var file = Ktx2.Write(texture);

        // 12 identifier + 68 header = 80; one level index entry of 24 puts the descriptor at 104.
        const int descriptorOffset = 104;
        // One basic block for four channels: 4 + 24 + 4*16 = 92.
        const int descriptorLength = 92;
        const int levelDataOffset = descriptorOffset + descriptorLength;

        Assert.Equal(levelDataOffset + 4, file.Length);
        Assert.Equal(Ktx2.Identifier.ToArray(), file[..12]);

        Assert.Equal(37u, Read32(file, 12));                     // vkFormat: R8G8B8A8_UNORM
        Assert.Equal(1u, Read32(file, 16));                      // typeSize
        Assert.Equal(1u, Read32(file, 20));                      // pixelWidth
        Assert.Equal(1u, Read32(file, 24));                      // pixelHeight
        Assert.Equal(0u, Read32(file, 28));                      // pixelDepth: 0, not 1, for a 2D texture
        Assert.Equal(0u, Read32(file, 32));                      // layerCount: 0 for a non-array texture
        Assert.Equal(1u, Read32(file, 36));                      // faceCount
        Assert.Equal(1u, Read32(file, 40));                      // levelCount
        Assert.Equal(0u, Read32(file, 44));                      // supercompressionScheme
        Assert.Equal((uint)descriptorOffset, Read32(file, 48));  // dfdByteOffset
        Assert.Equal((uint)descriptorLength, Read32(file, 52));  // dfdByteLength
        Assert.Equal(0u, Read32(file, 56));                      // kvdByteOffset
        Assert.Equal(0u, Read32(file, 60));                      // kvdByteLength
        Assert.Equal(0ul, Read64(file, 64));                     // sgdByteOffset
        Assert.Equal(0ul, Read64(file, 72));                     // sgdByteLength

        Assert.Equal((ulong)levelDataOffset, Read64(file, 80));  // level 0 byteOffset
        Assert.Equal(4ul, Read64(file, 88));                     // level 0 byteLength
        Assert.Equal(4ul, Read64(file, 96));                     // level 0 uncompressedByteLength

        Assert.Equal((uint)descriptorLength, Read32(file, descriptorOffset));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, file[levelDataOffset..]);
    }

    /// <summary>
    ///     The part of the format that reads like a mistake and is not: the level index runs largest
    ///     first, and the bytes it points at run the other way, so a streaming loader can read the
    ///     small mips off the front of the file and show something before the rest arrives.
    /// </summary>
    [Fact]
    public void LevelDataIsStoredSmallestFirstWhileTheIndexIsLargestFirst() {
        var texture = new TextureData(PixelFormat.R8UNorm, 4, 4);
        var file = Ktx2.Write(texture);

        Assert.Equal(3, texture.LevelCount);

        var offsets = new ulong[texture.LevelCount];

        for (var level = 0; level < texture.LevelCount; level++) {
            offsets[level] = Read64(file, Ktx2.HeaderLength + (level * Ktx2.LevelIndexEntryLength));
        }

        // Level 0 is the largest and sits last; the 1x1 tail sits first.
        Assert.True(offsets[0] > offsets[1]);
        Assert.True(offsets[1] > offsets[2]);
        Assert.Equal(16ul, Read64(file, Ktx2.HeaderLength + 8));
        Assert.Equal(1ul, Read64(file, Ktx2.HeaderLength + (2 * Ktx2.LevelIndexEntryLength) + 8));
    }

    [Fact]
    public void ATextureSurvivesBeingWrittenAndReadBack() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 8, 4);

        for (var level = 0; level < texture.LevelCount; level++) {
            texture.LevelSpan(level).Fill((byte)(level + 1));
        }

        var read = Ktx2.Read(Ktx2.Write(texture));

        Assert.Equal(texture.Format, read.Format);
        Assert.Equal(texture.Width, read.Width);
        Assert.Equal(texture.Height, read.Height);
        Assert.Equal(texture.LevelCount, read.LevelCount);
        Assert.Equal(texture.Pixels.ToArray(), read.Pixels.ToArray());
    }

    [Fact]
    public void ACubeMapKeepsItsSixFaces() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 4, 4, faceCount: 6);
        texture.PixelSpan().Fill(0x5A);

        var read = Ktx2.Read(Ktx2.Write(texture));

        Assert.Equal(6, read.FaceCount);
        Assert.Equal(texture.ByteLength, read.ByteLength);
        Assert.Equal(texture.Pixels.ToArray(), read.Pixels.ToArray());
    }

    [Fact]
    public void ABlockCompressedTextureRoundTrips() {
        var texture = new TextureData(PixelFormat.Bc7RgbaUNorm, 8, 8, levelCount: 1);
        texture.PixelSpan().Fill(0xC3);

        var read = Ktx2.Read(Ktx2.Write(texture));

        // 8x8 in 4x4 blocks of 16 bytes is four blocks: 64 bytes.
        Assert.Equal(64, read.ByteLength);
        Assert.Equal(PixelFormat.Bc7RgbaUNorm, read.Format);
    }

    [Fact]
    public void SomethingThatIsNotKtx2IsRefusedByItsIdentifier() {
        var failure = Assert.Throws<Ktx2Exception>(() => Ktx2.Read(new byte[128]));

        Assert.Contains("identifier", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SupercompressionIsRefusedRatherThanMisread() {
        var file = Ktx2.Write(new TextureData(PixelFormat.R8UNorm, 1, 1, levelCount: 1));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), 2);   // Zstd

        var failure = Assert.Throws<Ktx2Exception>(() => Ktx2.Read(file));

        Assert.Contains("supercompression", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A level whose declared length disagrees with what its extent and format imply is a
    ///     corrupt file, and reading it would fill a texture with whatever followed in memory.
    /// </summary>
    [Fact]
    public void ALevelWhoseLengthDisagreesWithItsFormatIsRefused() {
        var file = Ktx2.Write(new TextureData(PixelFormat.Rgba8UNorm, 2, 2, levelCount: 1));
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(Ktx2.HeaderLength + 8), 99);

        var failure = Assert.Throws<Ktx2Exception>(() => Ktx2.Read(file));

        Assert.Contains("99 bytes", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALevelPointingOutsideTheFileIsRefused() {
        var file = Ktx2.Write(new TextureData(PixelFormat.Rgba8UNorm, 2, 2, levelCount: 1));
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(Ktx2.HeaderLength), 1_000_000);

        Assert.Throws<Ktx2Exception>(() => Ktx2.Read(file));
    }

    /// <summary>
    ///     The reader's half of the smallest-first bargain: the tail of a file is one contiguous run
    ///     from the front of its level data, so a streamer reads it with one seek and one read and
    ///     never touches the large levels at all.
    /// </summary>
    [Fact]
    public void AMipTailIsOneContiguousRunAtTheFrontOfTheLevelData() {
        var texture = new TextureData(PixelFormat.R8UNorm, 16, 16);
        var layout = Ktx2.ReadLayout(Ktx2.Write(texture));

        Assert.Equal(5, layout.LevelCount);
        Assert.Equal(layout.Levels[^1].Offset, layout.DataOffset);
        Assert.Equal(256 + 64 + 16 + 4 + 1, layout.DataLength);

        // Levels 2, 3 and 4 are 16, 4 and 1 bytes, and they are the first 21 of the level data.
        Assert.Equal(21, layout.TailLength(2));
        Assert.Equal(layout.DataLength, layout.TailLength(0));
    }

    [Fact]
    public void ALayoutIsReadableFromTheHeadOfAFileAlone() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 8, 8);
        var file = Ktx2.Write(texture);

        var length = Ktx2.LayoutLength(file.AsSpan(0, Ktx2.HeaderLength));

        Assert.Equal(Ktx2.HeaderLength + (4 * Ktx2.LevelIndexEntryLength), length);

        var layout = Ktx2.ReadLayout(file.AsSpan(0, length));

        Assert.Equal(PixelFormat.Rgba8UNorm, layout.Format);
        Assert.Equal(8, layout.Width);
        Assert.Equal(4, layout.LevelCount);
        Assert.Equal(256, layout.Levels[0].Length);
    }

    /// <summary>
    ///     A tail is a whole smaller texture rather than a larger one with holes, which is what lets
    ///     a partially streamed texture be created and sampled by code that knows nothing about
    ///     streaming.
    /// </summary>
    [Fact]
    public void AMipTailDecodesToACompleteSmallerTexture() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 16, 8);

        for (var level = 0; level < texture.LevelCount; level++) {
            texture.LevelSpan(level).Fill((byte)(level + 1));
        }

        var file = Ktx2.Write(texture);
        var layout = Ktx2.ReadLayout(file);
        var tail = Ktx2.ReadTail(file, layout, 2);

        Assert.Equal(4, tail.Width);
        Assert.Equal(2, tail.Height);
        Assert.Equal(texture.LevelCount - 2, tail.LevelCount);

        // The tail's level 0 is the file's level 2, and it carries the file's bytes.
        Assert.Equal(texture.Level(2).ToArray(), tail.Level(0).ToArray());
        Assert.Equal(texture.Level(texture.LevelCount - 1).ToArray(), tail.Level(tail.LevelCount - 1).ToArray());
    }

    [Fact]
    public void ATailOfLevelZeroIsTheWholeTexture() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 8, 4);
        texture.PixelSpan().Fill(0x7E);

        var file = Ktx2.Write(texture);
        var tail = Ktx2.ReadTail(file, Ktx2.ReadLayout(file), 0);

        Assert.Equal(texture.Pixels.ToArray(), tail.Pixels.ToArray());
    }

    /// <summary>
    ///     The claim the whole partial reader rests on: reading a tail touches the tail's bytes and
    ///     no others. A stream that counts what was asked of it proves that in a way an assertion
    ///     about the result cannot.
    /// </summary>
    [Fact]
    public async Task ReadingATailFromAStreamNeverTouchesTheLargeLevels() {
        var texture = new TextureData(PixelFormat.R8UNorm, 64, 64);
        texture.PixelSpan().Fill(0x40);

        var file = Ktx2.Write(texture);
        await using var stream = new CountingStream(file);

        var layout = await Ktx2.ReadLayoutAsync(stream, TestContext.Current.CancellationToken);
        var head = stream.BytesRead;

        var tail = await Ktx2.ReadTailAsync(stream, layout, 3, TestContext.Current.CancellationToken);

        Assert.Equal(8, tail.Width);
        Assert.Equal(layout.TailLength(3), stream.BytesRead - head);

        // Level 0 alone is 4096 bytes; the whole tail from level 3 down is 85.
        Assert.True(stream.BytesRead < 4096);
        Assert.Equal(texture.Level(3).ToArray(), tail.Level(0).ToArray());
    }

    [Fact]
    public async Task OneLevelIsReadableOnItsOwn() {
        var texture = new TextureData(PixelFormat.R8UNorm, 8, 8);

        for (var level = 0; level < texture.LevelCount; level++) {
            texture.LevelSpan(level).Fill((byte)(0xA0 + level));
        }

        var file = Ktx2.Write(texture);
        await using var stream = new CountingStream(file);

        var layout = await Ktx2.ReadLayoutAsync(stream, TestContext.Current.CancellationToken);
        var buffer = new byte[layout.Levels[1].Length];

        var read = await Ktx2.ReadLevelAsync(stream, layout, 1, buffer, TestContext.Current.CancellationToken);

        Assert.Equal(16, read);
        Assert.Equal(texture.Level(1).ToArray(), buffer);
    }

    [Fact]
    public void TheTailThatFitsABudgetIsTheLargestOneThatDoes() {
        var layout = Ktx2.ReadLayout(Ktx2.Write(new TextureData(PixelFormat.R8UNorm, 16, 16)));

        Assert.Equal(0, layout.TailFor(long.MaxValue));
        Assert.Equal(2, layout.TailFor(21));
        Assert.Equal(3, layout.TailFor(20));

        // A budget under one 1×1 level still answers the smallest level: a texture with nothing
        // resident is one nothing can sample.
        Assert.Equal(layout.LevelCount - 1, layout.TailFor(0));
    }

    [Fact]
    public void ALayoutOfATruncatedIndexSaysSoRatherThanReadingPastIt() {
        var file = Ktx2.Write(new TextureData(PixelFormat.R8UNorm, 8, 8));

        var failure = Assert.Throws<Ktx2Exception>(() => Ktx2.ReadLayout(file.AsSpan(0, Ktx2.HeaderLength + 8)));

        Assert.Contains("level index", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStreamThatEndsInsideALevelSaysSo() {
        var file = Ktx2.Write(new TextureData(PixelFormat.R8UNorm, 8, 8));
        await using var whole = new CountingStream(file);

        var layout = await Ktx2.ReadLayoutAsync(whole, TestContext.Current.CancellationToken);
        await using var truncated = new CountingStream(file[..(file.Length - 32)]);

        await Assert.ThrowsAsync<Ktx2Exception>(
            async () => await Ktx2.ReadTailAsync(truncated, layout, 0, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public void EveryFormatTheTableClaimsRoundTripsThroughItsNumber() {
        foreach (var format in VkFormats.Supported) {
            Assert.Equal(format, VkFormats.To(VkFormats.From(format)));
        }
    }

    [Fact]
    public void AFormatNothingShipsSaysSoRatherThanWritingAWrongNumber() {
        var failure = Assert.Throws<Ktx2Exception>(() => VkFormats.From(PixelFormat.Depth32Float));

        Assert.Contains("VkFormats.Table", failure.Message, StringComparison.Ordinal);
    }

    static uint Read32(byte[] file, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset));

    static ulong Read64(byte[] file, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(offset));

    /// <summary>A seekable stream that counts what was asked of it.</summary>
    /// <remarks>
    ///     A partial reader's whole claim is about bytes it did <em>not</em> read, and only the
    ///     stream can testify to that.
    /// </remarks>
    sealed class CountingStream(byte[] bytes) : Stream {
        long position;

        public int BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position {
            get => position;
            set => position = value;
        }

        public override int Read(Span<byte> buffer) {
            var count = (int)Math.Min(buffer.Length, bytes.Length - position);

            if (count <= 0) {
                return 0;
            }

            bytes.AsSpan((int)position, count).CopyTo(buffer);
            position += count;
            BytesRead += count;

            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) {
            position = origin switch {
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => bytes.Length + offset,
                _ => offset
            };

            return position;
        }

        public override void Flush() { }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
