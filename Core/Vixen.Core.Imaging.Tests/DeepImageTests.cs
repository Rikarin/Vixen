// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using CsCheck;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>Sixteen-bit PNG and EXR, which a bake needs because eight bits is not enough of a measurement.</summary>
public sealed class DeepImageTests {
    /// <summary>A file the writer made, read back by the reader, at each channel count.</summary>
    [Fact]
    [Trait("Category", "Property")]
    public void A_sixteen_bit_png_survives_a_round_trip() =>
        Gen.Select(Gen.Int[1, 40], Gen.Int[1, 40], Gen.OneOf(1, 3, 4))
            .SelectMany(static (width, height, channels) =>
                Gen.UShort.Array[width * height * channels].Select(samples => (width, height, channels, samples))
            )
            .Sample(
                static image => {
                    var file = PngCodec.Encode16(image.width, image.height, image.channels, image.samples);
                    var read = PngCodec.Decode16(file);

                    Assert.Equal(image.width, read.Width);
                    Assert.Equal(image.height, read.Height);
                    Assert.Equal(image.channels, read.Channels);
                    Assert.Equal(image.samples, read.Samples);
                },
                iter: 200
            );

    /// <summary>A 16-bit file's first sample is big-endian, which the format fixes.</summary>
    /// <remarks>
    ///     ⚠ A round trip through our own reader cannot see a byte swap, since the two would agree. This
    ///     reads the bytes directly, and the conformance test below reads them with someone else's code.
    /// </remarks>
    [Fact]
    public void Sixteen_bit_samples_are_big_endian_in_the_file() {
        var file = PngCodec.Encode16(1, 1, 1, [0x1234]);
        var inflated = Inflate(file);

        Assert.Equal(new byte[] { 0, 0x12, 0x34 }, inflated);
    }

    static byte[] Inflate(byte[] png) {
        var at = 8;
        using var compressed = new MemoryStream();

        while (at < png.Length) {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];

            if (png[at + 4] == 'I' && png[at + 5] == 'D') {
                compressed.Write(png, at + 8, length);
            }

            at += 12 + length;
        }

        compressed.Position = 0;
        using var zlib = new System.IO.Compression.ZLibStream(
            compressed,
            System.IO.Compression.CompressionMode.Decompress
        );
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>An 8-bit file reads through the deep decoder with its levels filling the range.</summary>
    [Fact]
    public void An_eight_bit_png_reads_as_sixteen() {
        var read = PngCodec.Decode16(PngCodec.Encode(new(1, 1, [255, 0, 128, 255])));

        Assert.Equal(4, read.Channels);
        Assert.Equal(new ushort[] { 65535, 0, 32896, 65535 }, read.Samples);
    }

    [Theory]
    [InlineData(ExrPixels.Float)]
    [InlineData(ExrPixels.Half)]
    public void An_exr_survives_a_round_trip_at_each_width(ExrPixels pixels) =>
        Gen.Select(Gen.Int[1, 70], Gen.Int[1, 70], Gen.OneOf(1, 3, 4))
            .SelectMany(static (width, height, channels) => Gen.Single[-10f, 10f]
                    .Array[width * height * channels]
                    .Select(samples => new FloatImage(width, height, channels, samples))
            )
            .Sample(
                image => {
                    var read = ExrCodec.Decode(ExrCodec.Encode(image, pixels));

                    Assert.Equal(image.Width, read.Width);
                    Assert.Equal(image.Height, read.Height);
                    Assert.Equal(image.Channels, read.Channels);

                    for (var at = 0; at < image.Samples.Length; at++) {
                        var expected = pixels == ExrPixels.Half ? (float)(Half)image.Samples[at] : image.Samples[at];

                        Assert.Equal(expected, read.Samples[at]);
                    }
                },
                iter: 100
            );

    /// <summary>A flat picture compresses, so the ZIP path is the one that ran.</summary>
    /// <remarks>Random samples do not shrink and take the stored path, which the round trip above covers.</remarks>
    [Fact]
    public void A_smooth_exr_is_smaller_than_its_pixels() {
        var samples = new float[256 * 256 * 3];

        Array.Fill(samples, 0.25f);

        var file = ExrCodec.Encode(new(256, 256, 3, samples));

        Assert.True(file.Length < samples.Length * sizeof(float) / 50, $"{file.Length} bytes.");
        Assert.Equal(samples, ExrCodec.Decode(file).Samples);
    }

    /// <summary>Two runs write the same bytes, which a content-addressed cache rests on.</summary>
    [Fact]
    public void An_exr_is_the_same_bytes_each_time() {
        var samples = Enumerable.Range(0, 64 * 64).Select(static at => at * 0.001f).ToArray();
        var image = new FloatImage(64, 64, 1, samples);

        Assert.Equal(ExrCodec.Encode(image), ExrCodec.Encode(image));
    }

    /// <summary>What the writer does not write, the reader refuses.</summary>
    [Fact]
    public void A_file_that_is_not_an_exr_is_refused() =>
        Assert.Throws<InvalidDataException>(static () => ExrCodec.Decode(new byte[64]));

    /// <summary>Someone else's decoder agrees about every pixel, on both widths.</summary>
    /// <remarks>
    ///     ⚠ <b>The reader beside the writer proves nothing about the format</b>, since a shared misreading
    ///     round-trips. FFmpeg's decoder shares no code with this, and reading through it is what shows
    ///     the channel order, the predictor and the byte split are the format's and not ours.
    /// </remarks>
    [Theory]
    [InlineData(ExrPixels.Float, 3)]
    [InlineData(ExrPixels.Half, 3)]
    [InlineData(ExrPixels.Float, 1)]
    public void Ffmpeg_reads_what_was_written(ExrPixels pixels, int channels) {
        if (Tool("ffmpeg") is not { } ffmpeg) {
            ExternalTools.Missing("ffmpeg", "brew install ffmpeg");
            return;
        }

        const int Side = 37;
        var samples = new float[Side * Side * channels];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                for (var channel = 0; channel < channels; channel++) {
                    samples[(y * Side + x) * channels + channel] = (x * (channel + 1) + y) / 200f;
                }
            }
        }

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exr");

        File.WriteAllBytes(path, ExrCodec.Encode(new(Side, Side, channels, samples), pixels));

        try {
            var format = channels == 1 ? "grayf32le" : "gbrpf32le";

            using var process = Process.Start(
                new ProcessStartInfo(ffmpeg, ["-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", format, "-"]) {
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            )!;

            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);

            var bytes = output.ToArray();

            Assert.Equal(samples.Length * sizeof(float), bytes.Length);

            // Planar G, B, R, which is why the lanes are not in order here.
            int[] planes = channels == 1 ? [0] : [1, 2, 0];

            for (var plane = 0; plane < channels; plane++) {
                for (var pixel = 0; pixel < Side * Side; pixel++) {
                    var read = BitConverter.ToSingle(bytes, (plane * Side * Side + pixel) * sizeof(float));
                    var expected = samples[pixel * channels + planes[plane]];

                    Assert.True(MathF.Abs(expected - read) < 1e-3f, $"{expected} came back as {read}.");
                }
            }
        } finally {
            File.Delete(path);
        }
    }

    /// <summary>macOS's image reader opens both files, and agrees about their size and depth.</summary>
    [Fact]
    public void Sips_opens_a_deep_png_and_an_exr() {
        if (!OperatingSystem.IsMacOS()) {
            Assert.Skip("sips is a macOS tool.");
        }

        var samples = new ushort[8 * 4 * 3];
        var png = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        var exr = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exr");

        File.WriteAllBytes(png, PngCodec.Encode16(8, 4, 3, samples));
        File.WriteAllBytes(exr, ExrCodec.Encode(new(8, 4, 3, new float[8 * 4 * 3])));

        try {
            var (_, pngOutput) = ExternalTools.Run("/usr/bin/sips", "-g", "pixelWidth", "-g", "bitsPerSample", png);
            var (_, exrOutput) = ExternalTools.Run("/usr/bin/sips", "-g", "pixelWidth", "-g", "pixelHeight", exr);

            Assert.Contains("pixelWidth: 8", pngOutput, StringComparison.Ordinal);
            Assert.Contains("bitsPerSample: 16", pngOutput, StringComparison.Ordinal);
            Assert.Contains("pixelHeight: 4", exrOutput, StringComparison.Ordinal);
        } finally {
            File.Delete(png);
            File.Delete(exr);
        }
    }

    static string? Tool(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator)
        .Select(folder => Path.Combine(folder, name))
        .FirstOrDefault(File.Exists);
}
