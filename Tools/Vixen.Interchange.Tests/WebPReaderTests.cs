// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Xunit;

namespace Vixen.Interchange.Tests;

/// <summary>
///     The lossless WebP reader, against the writer beside it and against files libwebp 1.6.0 wrote (through Pillow
///     12.3), so it reads the tools the writer does not use: the predictor and colour transforms, colour indexing at
///     each packing, the colour cache, the distance map's far codes and prefix codes per region.
/// </summary>
public sealed class WebPReaderTests {
    static byte[] Picture(int width, int height, int channels) =>
        [..Enumerable.Range(0, width * height * channels).Select(static at => (byte)(at * 37 % 251))];

    /// <summary>The picture each libwebp file holds: a pattern every lane of which is a different sum of x and y.</summary>
    static byte[] Pattern(int width, int height, bool alpha) {
        var lanes = alpha ? 4 : 3;
        var samples = new byte[width * height * lanes];
        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var at = (y * width + x) * lanes;
                samples[at] = (byte)(x * 37 + y * 11);
                samples[at + 1] = (byte)(x * 5 + y * 23);
                samples[at + 2] = (byte)(x * y);
                if (alpha) {
                    samples[at + 3] = (byte)(x + y * 3);
                }
            }
        }

        return samples;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void WhatTheWriterWritesReadsBack(int channels) {
        var samples = Picture(33, 40, channels);

        var (width, height, alpha, rgba) = WebPReader.DecodeRgba8(WebPWriter.Encode(33, 40, channels, samples));

        Assert.Equal((33, 40, channels == 4), (width, height, alpha));
        for (var at = 0; at < 33 * 40; at++) {
            var grey = channels == 1;
            Assert.Equal(samples[at * channels], rgba[at * 4]);
            Assert.Equal(samples[at * channels + (grey ? 0 : 1)], rgba[at * 4 + 1]);
            Assert.Equal(samples[at * channels + (grey ? 0 : 2)], rgba[at * 4 + 2]);
            Assert.Equal(channels == 4 ? samples[at * 4 + 3] : (byte)255, rgba[at * 4 + 3]);
        }
    }

    [Theory]
    [InlineData(PatternRgba, true)]
    [InlineData(PatternRgb, false)]
    public void LibwebpsLosslessFilesReadAsThePictureTheyHold(string file, bool alpha) {
        var read = WebPReader.Decode(Convert.FromBase64String(file));

        var expected = Pattern(37, 29, alpha);
        Assert.Equal((37, 29, alpha ? 4 : 3), (read.Width, read.Height, read.Channels));
        Assert.Equal(expected.Select(static s => s / 255f), read.Samples);
    }

    [Fact]
    public void ASmoothPictureReadsThroughItsPredictors() {
        var (width, height, _, rgba) = WebPReader.DecodeRgba8(Convert.FromBase64String(Smooth));

        Assert.Equal((64, 48), (width, height));
        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var at = (y * width + x) * 4;
                Assert.Equal(
                    ((byte)(x * 4), (byte)(y * 4), (byte)((x + y) * 2), (byte)255),
                    (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3])
                );
            }
        }
    }

    /// <summary>Two colours pack eight texels to a byte and four colours four, which the reader unpacks.</summary>
    [Theory]
    [InlineData(PaletteTwo, 2)]
    [InlineData(PaletteFour, 4)]
    public void APalettedPictureUnpacksItsIndices(string file, int colours) {
        byte[][] table = [[255, 0, 0], [0, 128, 255], [20, 200, 40], [250, 250, 10]];

        var (width, height, _, rgba) = WebPReader.DecodeRgba8(Convert.FromBase64String(file));

        Assert.Equal((33, 7), (width, height));
        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var at = (y * width + x) * 4;
                Assert.Equal(table[(x * 3 + y) % colours], rgba[at..(at + 3)]);
            }
        }
    }

    /// <summary>The built-in decoders read a WebP, as eight-bit RGBA, opaque where the file says it has no alpha.</summary>
    [Fact]
    public void TheBuiltInDecodersReadAWebP() {
        var decoder = Assert.IsType<WebPDecoder>(ImageDecoders.For(ImageDecoders.BuiltIn, ".WEBP"));

        var texture = decoder.Decode(new MemoryStream(Convert.FromBase64String(PatternRgb)), ".webp");

        Assert.Equal((37, 29, Vixen.Graphics.PixelFormat.Rgba8UNorm), (texture.Width, texture.Height, texture.Format));
        var expected = Pattern(37, 29, false);
        var pixels = texture.PixelSpan().ToArray();
        for (var at = 0; at < 37 * 29; at++) {
            Assert.Equal(expected[(at * 3)..(at * 3 + 3)], pixels[(at * 4)..(at * 4 + 3)]);
            Assert.Equal(byte.MaxValue, pixels[at * 4 + 3]);
        }
    }

    [Fact]
    public void ALossyFileIsRefusedForWhatItIs() {
        var refused = Assert.Throws<InvalidDataException>(() => WebPReader.Decode(Convert.FromBase64String(Lossy)));

        Assert.Contains("lossy", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileCutShortIsRefusedAndNotReadAsZeros() {
        var whole = Convert.FromBase64String(PatternRgb);

        Assert.Throws<InvalidDataException>(() => WebPReader.Decode(whole.AsSpan(0, whole.Length / 2)));
        // Cut short with sizes that agree, so only the stream itself running out can say so.
        var cut = whole[..(whole.Length / 2)];
        BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(4), (uint)(cut.Length - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(16), (uint)(cut.Length - 20));
        Assert.Throws<InvalidDataException>(() => WebPReader.Decode(cut));
        Assert.Throws<InvalidDataException>(() => WebPReader.Decode("RIFF0000WAVE"u8));
    }

    const string PatternRgba =
        "UklGRp4AAABXRUJQVlA4TJEAAAAvJAAHELmM6H8M1ARAwpDADPaPOD/ACGSTtjmn0qk0+p8gLomvAkSdsbiAl8Rx20aSNJdj6PzTVQQ1pZrZ4yUW"
        + "THF/HFMTSQrzQJva6N8fCtiB3IIAZI1BEIMhhkCMh1iIjTiIhdiIg/gihkIMgxgOMQLiTZRFtE2MQ7zrW8i/kf8g/xcxEmIUxGiIMZA1xkIAAA==";

    const string PatternRgb =
        "UklGRp4CAABXRUJQVlA4TJICAAAvJAAHAA11IaL/AQhtJEmS9h5jCP5QV9U10xAEAsko+fMN4DCS5DQSs8yQf3gyP8MJgpHbNo50cDLV3vKKTwgA"
        + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKADkUgkEonEvi2D/hEbOnbq3KVRY2LTZs1btGzVesJ06IVEIpFIJFLfl0H/F00OSKQ2JBKpQy9kMplM"
        + "JpP7vgz6T16QmzVvQSaT25DJ5A6UXikUCoVCofRjGTRQHoSyonQA9QoCgUAgEKgfy6ABBFocMdq0BYE6gHsFg8FgMBjcj2XQAAaDuzRqPBFazL62"
        + "YHAHCKQ3CAQCgUAg/VwGHWffyMrRDtIBCu0NCoVCoVBoP5fB07EzRztoBxisNxgMBoPBYDAYDNZfGAzWEbaAtTiytIN1gMPhvcPhcDi8H/B+HQYv"
        + "3+dI0r4DAoFAIPo8zvVr9s1HRCwQLVtNlPYdkEgksg8kEjkG/UYi+/fwpdfvMHk6oFC9oVAoFAqFQqH6jervBW1QKFQHNBqN7qNPNHoM+o1eoBfo"
        + "Vq3btEWjO2AwGAymT0zf5qZ+9+ddBgymAxaLxWL7xGL7jsX264LJ1LZd+w44HA6H63P29RPX7/7gFrjGTXA4XIuWuNZtBjwej8fj+8Lj+9HPfuGX"
        + "TJI9wA9UVFRUVFR9UfW9H/2cwYSYAdWaPoWampqampq6b33vRz+p+039tOUFGhoaGhoaGhoamhGaW2hpaWlp++yrb7TfwXGMdkF7QNuadqDrlY6u"
        + "jz776tuapruUXujpe3/iO30LQ28MBwwLhgVDs+YtGFq1bsPQrn0Hxl57653xURgvhJGxQy+9Mq0B0xowLZiat2jJxMTUlql9h1567a33Ppivppmb"
        + "MTOf0+0B";

    const string Smooth =
        "UklGRj4AAABXRUJQVlA4TDIAAAAvP8ALALmM6H8shASECf7vlSwoxID3fwKM/hjUpm3ApP3/ePYQCCRt8bY/YgKOt+fzHQ==";

    const string PaletteTwo = "UklGRiYAAABXRUJQVlA4TBkAAAAvIIABABASJEz3f7HO//z3n2CSptqOUU4+AA==";

    const string PaletteFour =
        "UklGRjwAAABXRUJQVlA4TC8AAAAvIIABAIWagGkYpCMvmnBAxy8QIASKck4gQEiQ6MR6/y+AoAli7vLdPawX7H1oDAA=";

    const string Lossy =
        "UklGRkAAAABXRUJQVlA4IDQAAABwAQCdASoQABAAAUAmJR3ADYgGUAD++0n+6/3iP6bP/7DWODOezIlRd9TAm3HcDci+hAAA";
}
