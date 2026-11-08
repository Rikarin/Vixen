// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using StbImageSharp;
using Vixen.Core.Imaging;
using Xunit;

namespace Vixen.Interchange.Tests;

/// <summary>
///     The authoring-format writers, each read back by a decoder nobody here wrote: StbImageSharp for
///     TGA, JPEG and Radiance HDR, libwebp's <c>dwebp</c> for WebP and macOS ImageIO through
///     <c>sips</c> for TIFF.
/// </summary>
/// <remarks>
///     ⚠ A writer checked by a reader written alongside it agrees with its own misreading of the
///     specification. Where the outside reader is a command line that this machine may not have,
///     the test skips and says so, and <c>VIXEN_REQUIRE_EXTERNAL_TOOLS=1</c> makes that a failure.
/// </remarks>
public sealed class ImageWriterTests {
    /// <summary>A picture with smooth gradients, hard edges, flat runs and noise, so every coding path is used.</summary>
    static byte[] Picture(int width, int height, int channels) {
        var samples = new byte[width * height * channels];
        var random = new Random(7);

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                for (var lane = 0; lane < channels; lane++) {
                    var at = ((y * width) + x) * channels + lane;
                    samples[at] = (y / 8) switch {
                        0 => 200,
                        1 => (byte)(x * 255 / (width - 1)),
                        2 => (byte)(((x / 4) + lane) % 2 == 0 ? 30 : 220),
                        _ => (byte)random.Next(256)
                    };
                }
            }
        }

        return samples;
    }

    static ImageResult Stb(byte[] file, ColorComponents components) => ImageResult.FromMemory(file, components);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void TgaReadsBackExactlyAndTheRightWayUp(int channels) {
        var samples = Picture(23, 37, channels);

        var read = Stb(TgaWriter.Encode(23, 37, channels, samples), Components(channels));

        Assert.Equal((23, 37), (read.Width, read.Height));
        Assert.Equal(samples, read.Data);
    }

    /// <summary>
    ///     JPEG is lossy, so the bound is on the error. At 95 a smooth picture comes back within a
    ///     few levels; a wrong Huffman table or quantisation order comes back as noise, hundreds of
    ///     levels off, and a missing byte stuff stops the decoder.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void JpegReadsBackCloseToWhatWasWritten(int channels) {
        var samples = Picture(61, 29, channels);

        var read = Stb(JpegWriter.Encode(61, 29, channels, samples, 95), Components(channels));

        Assert.Equal((61, 29), (read.Width, read.Height));
        var smooth = 61 * 16 * channels;
        var worst = Enumerable.Range(0, smooth).Max(at => Math.Abs(samples[at] - read.Data[at]));
        Assert.InRange(worst, 0, 12);
        Assert.InRange(Mean(samples, read.Data), 0.0, 8.0);
    }

    [Fact]
    public void JpegQualityTradesSizeForError() {
        var samples = Picture(64, 64, 3);

        var low = JpegWriter.Encode(64, 64, 3, samples, 20);
        var high = JpegWriter.Encode(64, 64, 3, samples, 95);

        Assert.True(low.Length < high.Length);
        Assert.True(Mean(samples, Stb(low, ColorComponents.RedGreenBlue).Data) > Mean(samples, Stb(high, ColorComponents.RedGreenBlue).Data));
    }

    /// <summary>RGBE keeps about eight bits relative to the brightest channel, over any range.</summary>
    [Fact]
    public void HdrReadsBackWithinItsPrecisionOverAWideRange() {
        const int width = 40;
        const int height = 3;
        var samples = new float[width * height * 3];

        for (var at = 0; at < samples.Length; at++) {
            samples[at] = MathF.Pow(2f, (at % 37) - 18) * (1 + (at % 5 * 0.1f));
        }

        var file = HdrWriter.Encode(new(width, height, 3, samples));
        var read = ImageResultFloat.FromMemory(file, ColorComponents.RedGreenBlue);

        Assert.Equal((width, height), (read.Width, read.Height));

        for (var texel = 0; texel < width * height; texel++) {
            var top = Enumerable.Range(0, 3).Max(lane => samples[(texel * 3) + lane]);

            for (var lane = 0; lane < 3; lane++) {
                Assert.InRange(Math.Abs(read.Data[(texel * 3) + lane] - samples[(texel * 3) + lane]), 0f, top / 100f);
            }
        }
    }

    /// <summary>A flat scanline is runs, a noisy one literals; both have to come back.</summary>
    [Fact]
    public void HdrRunsAndLiteralsBothReadBack() {
        var samples = new float[64 * 2 * 3];

        for (var at = 64 * 3; at < samples.Length; at++) {
            samples[at] = (at * 7919 % 101) / 101f;
        }

        Array.Fill(samples, 0.5f, 0, 64 * 3);

        var read = ImageResultFloat.FromMemory(HdrWriter.Encode(new(64, 2, 3, samples)), ColorComponents.RedGreenBlue);

        for (var at = 0; at < samples.Length; at++) {
            Assert.InRange(Math.Abs(read.Data[at] - samples[at]), 0f, 0.01f);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(1)]
    public void WebPReadsBackExactlyThroughLibwebp(int channels) {
        var tool = Locate("dwebp");

        if (tool is null) {
            Missing("dwebp", "brew install webp");
            return;
        }

        var samples = Picture(45, 50, channels);
        var folder = Directory.CreateTempSubdirectory("vixen-webp");

        try {
            var file = Path.Combine(folder.FullName, "picture.webp");
            var decoded = Path.Combine(folder.FullName, "picture.pam");
            File.WriteAllBytes(file, WebPWriter.Encode(45, 50, channels, samples));

            var (code, output) = Run(tool, file, "-pam", "-o", decoded);
            Assert.True(code == 0, output);

            var (width, height, pixels) = Pam(File.ReadAllBytes(decoded));
            Assert.Equal((45, 50), (width, height));

            for (var texel = 0; texel < width * height; texel++) {
                for (var lane = 0; lane < 4; lane++) {
                    var expected = lane == 3 ? channels == 4 ? samples[(texel * 4) + 3] : (byte)255
                        : channels == 1 ? samples[texel] : samples[(texel * channels) + lane];
                    Assert.Equal(expected, pixels[(texel * 4) + lane]);
                }
            }
        } finally {
            folder.Delete(true);
        }
    }

    /// <summary>
    ///     libtiff decodes the strips, predictor and all, and prints the bytes: an exact oracle at
    ///     every depth. (ImageIO is not one for float, since it colour-manages a float TIFF as
    ///     linear light into its display space.)
    /// </summary>
    [Theory]
    [InlineData(8, 1)]
    [InlineData(8, 2)]
    [InlineData(8, 3)]
    [InlineData(8, 4)]
    [InlineData(16, 3)]
    [InlineData(16, 4)]
    [InlineData(32, 1)]
    [InlineData(32, 3)]
    public void TiffReadsBackExactlyThroughLibtiff(int bits, int channels) {
        var tool = Locate("tiffinfo");

        if (tool is null) {
            Missing("tiffinfo", "brew install libtiff");
            return;
        }

        var samples = Picture(33, 40, channels);
        var expected = new List<byte>();
        byte[] file;

        switch (bits) {
            case 8:
                file = TiffWriter.Encode8(33, 40, channels, samples);
                expected.AddRange(samples);
                break;
            case 16:
                ushort[] wide = [.. samples.Select(static (s, at) => (ushort)((s * 257) ^ (at & 0xF)))];
                file = TiffWriter.Encode16(33, 40, channels, wide);
                expected.AddRange(wide.SelectMany(BitConverter.GetBytes));
                break;
            default:
                float[] floats = [.. samples.Select(static s => (s / 255f) - 0.25f)];
                file = TiffWriter.EncodeFloat(new(33, 40, channels, floats));
                expected.AddRange(floats.SelectMany(BitConverter.GetBytes));
                break;
        }

        var folder = Directory.CreateTempSubdirectory("vixen-tiff");

        try {
            var tiff = Path.Combine(folder.FullName, "picture.tif");
            File.WriteAllBytes(tiff, file);

            var (code, output) = Run(tool, "-d", tiff);
            Assert.True(code == 0, output);

            byte[] decoded = [
                ..output.Split('\n')
                    .Where(static line => System.Text.RegularExpressions.Regex.IsMatch(line, "^( [0-9a-f]{2})+$"))
                    .SelectMany(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Select(static hex => Convert.ToByte(hex, 16))
            ];
            Assert.Equal(expected, decoded);
        } finally {
            folder.Delete(true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void EightBitTiffReadsBackThroughImageIo(int channels) {
        var tool = Locate("sips");

        if (tool is null) {
            Missing("sips", "It is macOS's, and nothing else has it.");
            return;
        }

        var samples = Picture(33, 40, channels);
        var folder = Directory.CreateTempSubdirectory("vixen-tiff");

        try {
            var tiff = Path.Combine(folder.FullName, "picture.tif");
            var png = Path.Combine(folder.FullName, "picture.png");
            File.WriteAllBytes(tiff, TiffWriter.Encode8(33, 40, channels, samples));

            var (code, output) = Run(tool, "-s", "format", "png", tiff, "--out", png);
            Assert.True(code == 0 && File.Exists(png), output);

            var read = Stb(File.ReadAllBytes(png), Components(channels));
            Assert.Equal((33, 40), (read.Width, read.Height));
            Assert.Equal(samples, read.Data);
        } finally {
            folder.Delete(true);
        }
    }

    [Fact]
    public void ShapesTheFormatsCannotHoldAreRefused() {
        Assert.Throws<ArgumentException>(() => JpegWriter.Encode(2, 2, 4, new byte[16]));
        Assert.Throws<ArgumentException>(() => TgaWriter.Encode(2, 2, 2, new byte[8]));
        Assert.Throws<ArgumentException>(() => WebPWriter.Encode(20000, 1, 3, new byte[60000]));
        Assert.Throws<ArgumentException>(() => TiffWriter.Encode8(2, 2, 3, new byte[5]));
        Assert.Throws<ArgumentException>(() => HdrWriter.Encode(new(1, 1, 2, new float[2])));
    }

    static ColorComponents Components(int channels) =>
        channels switch {
            1 => ColorComponents.Grey,
            3 => ColorComponents.RedGreenBlue,
            _ => ColorComponents.RedGreenBlueAlpha
        };

    static double Mean(byte[] expected, byte[] actual) =>
        expected.Zip(actual).Average(static pair => Math.Abs(pair.First - pair.Second));

    static (int Width, int Height, byte[] Pixels) Pam(byte[] file) {
        var header = System.Text.Encoding.ASCII.GetString(file, 0, Math.Min(file.Length, 200));
        var end = header.IndexOf("ENDHDR\n", StringComparison.Ordinal) + "ENDHDR\n".Length;
        var fields = header[..end].Split('\n')
            .Select(static line => line.Split(' '))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(static parts => parts[0], static parts => parts[1]);
        Assert.Equal("4", fields["DEPTH"]);
        return (int.Parse(fields["WIDTH"]), int.Parse(fields["HEIGHT"]), file[end..]);
    }

    static string? Locate(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator)
        .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"])
        .Select(folder => Path.Combine(folder, name))
        .FirstOrDefault(File.Exists);

    static void Missing(string tool, string how) {
        Assert.False(
            Environment.GetEnvironmentVariable("VIXEN_REQUIRE_EXTERNAL_TOOLS") == "1",
            $"VIXEN_REQUIRE_EXTERNAL_TOOLS=1 and {tool} is not available. {how}"
        );
        Assert.Skip($"{tool} is not on this machine, so nothing outside checked this writer. {how}");
    }

    static (int Code, string Output) Run(string path, params string[] arguments) {
        var start = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }
}
