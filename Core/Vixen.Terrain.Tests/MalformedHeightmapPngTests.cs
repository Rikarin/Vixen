// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>PNGs that are not, and the one exception type an importer is allowed to see.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The type is the whole assertion, and it is not pedantry.</b> A heightmap arrives from
///         somebody else's tool or from a download, and <c>HeightmapImporter</c> catches
///         <see cref="ArgumentException" /> to turn a bad file into a line in the import log.
///         Everything else — an <c>OverflowException</c> from a dimension nobody bounded, an
///         <c>EndOfStreamException</c> or a <c>ZLibException</c> from image data that does not
///         inflate — goes straight past it and out of the importer, which is a person's asset import
///         crashing on a file they were given.
///     </para>
///     <para>
///         <b>The eight bytes of IHDR are the cheapest allocation request in the format</b>, so the
///         size cases weigh the refusal rather than only naming its type: 500000×500000 is half a
///         terabyte asked for by a forty-byte file, and refusing it after allocating it is not
///         refusing it.
///     </para>
/// </remarks>
public sealed class MalformedHeightmapPngTests {
    static byte[] Valid() => TerrainHeightmapPng.Encode(8, 8, new ushort[64]);

    /// <summary>The IHDR body starts at 16: width, height, then depth, colour and the rest.</summary>
    static byte[] WithSize(uint width, uint height) {
        var file = Valid();
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(20), height);

        return file;
    }

    static long Weigh(Action action) {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>A chunk length above 2^31 was a negative <c>int</c> handed straight to <c>Slice</c>.</summary>
    [Theory]
    [InlineData(0x80000000u)]
    [InlineData(0xFFFFFFF0u)]
    public void AChunkLengthThatDoesNotFitIsRefusedByName(uint length) {
        var file = Valid();
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), length);

        var failure = Assert.Throws<ArgumentException>(() => TerrainHeightmapPng.Decode(file));

        // The message names the file rather than repeating "Specified argument was out of the range
        // of valid values", which is what the slice said and is what an importer used to report.
        Assert.Contains("chunk", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Dimensions past the cap are refused without allocating what they ask for.</summary>
    [Theory]
    [InlineData(500_000u, 500_000u)]
    [InlineData(0x80000000u, 8u)]
    [InlineData(8u, 0xFFFFFFFFu)]
    public void ASizeNoFileCouldHoldIsRefusedWithoutAllocatingIt(uint width, uint height) {
        var file = WithSize(width, height);

        var allocated = Weigh(() => Assert.Throws<ArgumentException>(() => TerrainHeightmapPng.Decode(file)));

        Assert.True(allocated < 256 * 1024, $"Refusing a {file.Length}-byte file cost {allocated:N0} bytes.");
    }

    /// <summary>A zero axis decoded to an empty heightmap rather than being refused.</summary>
    /// <remarks>
    ///     It came back as a 0×0 image with no samples, which is not a heightmap and is diagnosed two
    ///     layers away as a file whose dimensions could not be worked out.
    /// </remarks>
    [Theory]
    [InlineData(0u, 8u)]
    [InlineData(8u, 0u)]
    public void AZeroAxisIsRefusedRatherThanDecodedToNothing(uint width, uint height) =>
        Assert.Throws<ArgumentException>(() => TerrainHeightmapPng.Decode(WithSize(width, height)));

    /// <summary>A size the IDAT could not possibly produce is refused before the row buffer exists.</summary>
    /// <remarks>
    ///     ⚠ <b>The check a cap on the dimensions cannot make.</b> 4096² is a legal heightmap and a
    ///     33 MB row buffer, and it is allocated from the header before a byte of image data is read
    ///     — so a forty-byte file declaring it costs 33 MB whatever the cap is. No deflate stream
    ///     expands more than 1032×, which is what makes the claim checkable against the bytes present.
    /// </remarks>
    [Fact]
    public void ASizeTheImageDataCouldNotProduceIsRefusedBeforeTheBufferExists() {
        var file = WithSize(4096, 4096);

        var allocated = Weigh(() => Assert.Throws<ArgumentException>(() => TerrainHeightmapPng.Decode(file)));

        Assert.True(allocated < 256 * 1024, $"Refusing a {file.Length}-byte file cost {allocated:N0} bytes.");
    }

    /// <summary>Image data that does not inflate is an ArgumentException like everything else here.</summary>
    /// <remarks>
    ///     ⚠ <b>Three different exception types reach this, which is why the first fix missed one.</b>
    ///     A truncated stream is an <c>EndOfStreamException</c>, a corrupt one an
    ///     <c>InvalidDataException</c>, and one the native inflater rejects a <c>ZLibException</c> —
    ///     and the third is an <c>IOException</c> rather than either of the first two. The fuzzer
    ///     found it in sixty thousand cases; the committed input is in
    ///     <c>Vixen.Fuzz.Tests/Corpus/heightmap</c>.
    /// </remarks>
    [Fact]
    public void ImageDataThatDoesNotInflateIsRefusedByName() {
        var truncated = Valid()[..40];
        Assert.Throws<ArgumentException>(() => TerrainHeightmapPng.Decode(truncated));

        var corrupt = Valid();
        corrupt.AsSpan(45).Fill(0x55);
        Assert.Throws<ArgumentException>(() => TerrainHeightmapPng.Decode(corrupt));
    }

    /// <summary>The exact bytes the fuzzer found, which the inflater rejects outright.</summary>
    [Fact]
    public void TheFuzzersZLibExceptionIsRefusedByName() {
        byte[] file = [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x10, 0x00, 0x00, 0x00, 0x00, 0x6A, 0xEE, 0x47,
            0x16, 0x00, 0x00, 0x00, 0x0B, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x7D, 0xB7, 0x7B, 0x31, 0x04, 0xB1,
            0x01, 0x09, 0x00, 0x83, 0x0C, 0x54, 0x51, 0xF3,
            0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
            0xAE, 0x42, 0x60, 0x82
        ];

        Assert.Throws<ArgumentException>(() => TerrainHeightmapPng.Decode(file));
    }

    /// <summary>No malformed file of any shape gets an exception the importer does not catch.</summary>
    /// <remarks>
    ///     A sweep rather than a case, over the byte positions that decide a length or a size. It is
    ///     the same assertion the fuzz target makes and is here so that the property is checked by
    ///     the project that owns it.
    /// </remarks>
    [Fact]
    public void NoSingleFieldCanProduceAnExceptionAnImporterDoesNotCatch() {
        foreach (var offset in new[] { 8, 16, 20, 24, 33, 37, 41, 45 }) {
            foreach (var value in new uint[] { 0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF }) {
                var file = Valid();

                if (offset + 4 > file.Length) {
                    continue;
                }

                BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(offset), value);

                try {
                    TerrainHeightmapPng.Decode(file);
                } catch (ArgumentException) {
                    // The documented refusal.
                }
            }
        }
    }

    [Fact]
    public void AWellFormedHeightmapStillDecodes() {
        var samples = new ushort[37 * 23];

        for (var index = 0; index < samples.Length; index++) {
            samples[index] = (ushort)(index * 137);
        }

        var decoded = TerrainHeightmapPng.Decode(TerrainHeightmapPng.Encode(37, 23, samples));

        Assert.Equal(37, decoded.Width);
        Assert.Equal(23, decoded.Height);
        Assert.Equal(samples, decoded.Samples);
    }
}
