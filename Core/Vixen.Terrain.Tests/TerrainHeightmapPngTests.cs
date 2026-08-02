// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>Sixteen-bit greyscale PNG — [docs/plan/31 § T3]'s owed import and export.</summary>
public sealed class TerrainHeightmapPngTests {
    static ushort[] Ramp(int width, int height) {
        var samples = new ushort[width * height];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                samples[(y * width) + x] = (ushort)(((y * width) + x) * 7919 % 65536);
            }
        }

        return samples;
    }

    /// <summary>What is written comes back, to the sample.</summary>
    /// <remarks>
    ///     ⚠ <b>Sixteen bits, and this is the whole reason the file exists.</b> An eight-bit import is
    ///     a terrain quantised to 256 heights, which reads as a faint terrace on every slope and gets
    ///     blamed on the generator rather than on the import.
    /// </remarks>
    [Fact]
    public void ARoundTripIsLossless() {
        var samples = Ramp(37, 23);
        var decoded = TerrainHeightmapPng.Decode(TerrainHeightmapPng.Encode(37, 23, samples));

        Assert.Equal(37, decoded.Width);
        Assert.Equal(23, decoded.Height);
        Assert.Equal(samples, decoded.Samples);
    }

    /// <summary>A terrain's own heights round-trip too, composited first.</summary>
    [Fact]
    public void ATerrainsHeightsRoundTrip() {
        var terrain = new Terrain(
            new() { TileSamples = 32, TilesX = 2, TilesZ = 2, MetresPerQuad = 1f, MinHeight = -50f, MaxHeight = 50f }
        );

        var layer = terrain.AddLayer("Sculpt");

        TerrainSculpt.Sculpt(terrain, layer, TerrainBrush.Default with { Radius = 8f, Strength = 1f }, new(new(20f, 20f)), 20f);
        terrain.Resolve();

        var decoded = TerrainHeightmapPng.Decode(TerrainHeightmapPng.Encode(terrain));

        Assert.Equal(terrain.Description.SamplesX, decoded.Width);
        Assert.Equal(terrain.Description.SamplesZ, decoded.Height);
        Assert.Equal(terrain.Composite.Span.ToArray(), decoded.Samples);
    }

    /// <summary>The file is a PNG other tools will open: signature, chunks and CRCs.</summary>
    /// <remarks>
    ///     ⚠ <b>The CRC is over the type and the body and not over the length.</b> One computed over
    ///     the wrong range produces a file this library reads back happily and no other tool will
    ///     open — which is the worst possible way to find out.
    /// </remarks>
    [Fact]
    public void TheFileIsAWellFormedPng() {
        var file = TerrainHeightmapPng.Encode(8, 8, Ramp(8, 8));

        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], file[..8]);

        var at = 8;
        var kinds = new List<string>();

        while (at + 12 <= file.Length) {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            var kind = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            var stated = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 8 + length));

            Assert.Equal(Crc(file.AsSpan(at + 4, 4 + length)), stated);

            kinds.Add(kind);
            at += 12 + length;
        }

        Assert.Equal(["IHDR", "IDAT", "IEND"], kinds);
    }

    /// <summary>Every filter type decodes, because a real file picks one per row.</summary>
    /// <remarks>
    ///     ⚠ <b>A reader that handled only the filter it writes would refuse most real files.</b>
    ///     World Machine, Gaea and Photoshop all choose per row from five.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryFilterTypeDecodes(byte filter) {
        var samples = Ramp(16, 9);
        var decoded = TerrainHeightmapPng.Decode(Filtered(16, 9, samples, filter));

        Assert.Equal(samples, decoded.Samples);
    }

    /// <summary>An eight-bit PNG is refused rather than widened.</summary>
    [Fact]
    public void AnEightBitPngIsRefused() {
        var file = TerrainHeightmapPng.Encode(4, 4, Ramp(4, 4));

        // The bit depth is the ninth byte of the IHDR body, which starts at 8 + 8.
        file[16 + 8] = 8;

        var thrown = Assert.Throws<ArgumentException>(() => TerrainHeightmapPng.Decode(file));

        Assert.Contains("sixteen", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>And a colour one is refused rather than averaged.</summary>
    /// <remarks>
    ///     ⚠ <b>There is no defensible way to turn three channels into one height.</b> A luminance
    ///     weighting is a photographic convention and a heightfield is not a photograph; averaging
    ///     silently would make a terrain that is subtly wrong everywhere.
    /// </remarks>
    [Fact]
    public void AColourPngIsRefused() {
        var file = TerrainHeightmapPng.Encode(4, 4, Ramp(4, 4));

        file[16 + 9] = 2;

        Assert.Contains(
            "greyscale",
            Assert.Throws<ArgumentException>(() => TerrainHeightmapPng.Decode(file)).Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void SomethingThatIsNotAPngIsRefused() {
        Assert.Throws<ArgumentException>(() => TerrainHeightmapPng.Decode("not a png at all"u8));
    }

    /// <summary>The filter earns its keep: a heightfield compresses far better than raw.</summary>
    /// <remarks>
    ///     ⚠ <b>Filter 0 writes the heights themselves and a large terrain comes out several times
    ///     larger.</b> A terrain's rows differ from each other by a few metres and from nothing else,
    ///     so filtering against the row above leaves near-zero bytes that deflate eats.
    /// </remarks>
    [Fact]
    public void TheFileIsSmallerThanTheRawSamples() {
        var samples = new ushort[128 * 128];

        for (var y = 0; y < 128; y++) {
            for (var x = 0; x < 128; x++) {
                // A smooth hill, which is what a heightfield is and what raw r16 cannot exploit.
                samples[(y * 128) + x] = (ushort)(30000 + (int)(2000 * MathF.Sin(x * 0.05f) * MathF.Cos(y * 0.05f)));
            }
        }

        var file = TerrainHeightmapPng.Encode(128, 128, samples);

        Assert.True(
            file.Length < samples.Length * 2 / 2,
            $"{file.Length} bytes for {samples.Length * 2} of samples, which is not compression."
        );
    }

    /// <summary>Builds a PNG with one filter type on every row, to exercise the decoder.</summary>
    static byte[] Filtered(int width, int height, ushort[] samples, byte filter) {
        var bytes = width * 2;
        var stride = bytes + 1;
        var raw = new byte[stride * height];
        var previous = new byte[bytes];

        for (var y = 0; y < height; y++) {
            var row = new byte[bytes];

            for (var x = 0; x < width; x++) {
                row[x * 2] = (byte)(samples[(y * width) + x] >> 8);
                row[(x * 2) + 1] = (byte)samples[(y * width) + x];
            }

            raw[y * stride] = filter;

            for (var x = 0; x < bytes; x++) {
                var left = x >= 2 ? row[x - 2] : (byte)0;
                var above = previous[x];
                var corner = x >= 2 ? previous[x - 2] : (byte)0;

                raw[(y * stride) + 1 + x] = filter switch {
                    0 => row[x],
                    1 => (byte)(row[x] - left),
                    2 => (byte)(row[x] - above),
                    3 => (byte)(row[x] - ((left + above) / 2)),
                    _ => (byte)(row[x] - Paeth(left, above, corner))
                };
            }

            previous = row;
        }

        var compressed = new MemoryStream();

        using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) {
            deflate.Write(raw);
        }

        var file = new MemoryStream();

        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];

        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);

        header[8] = 16;

        Write(file, "IHDR"u8, header);
        Write(file, "IDAT"u8, compressed.ToArray());
        Write(file, "IEND"u8, []);

        return file.ToArray();
    }

    static byte Paeth(byte left, byte above, byte corner) {
        var estimate = left + above - corner;
        var dl = Math.Abs(estimate - left);
        var da = Math.Abs(estimate - above);
        var dc = Math.Abs(estimate - corner);

        return dl <= da && dl <= dc ? left : da <= dc ? above : corner;
    }

    static void Write(Stream into, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> body) {
        var length = new byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        into.Write(length);
        into.Write(kind);
        into.Write(body);

        var crc = new byte[4];
        var scratch = new byte[kind.Length + body.Length];

        kind.CopyTo(scratch);
        body.CopyTo(scratch.AsSpan(kind.Length));

        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(scratch));
        into.Write(crc);
    }

    static uint Crc(ReadOnlySpan<byte> data) {
        var crc = 0xFFFFFFFFu;

        foreach (var value in data) {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++) {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
