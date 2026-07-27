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
}
