// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Imaging.BlockCompression;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>What <see cref="DdsCodec" /> writes, read by itself and by something else.</summary>
public sealed class DdsCodecTests {
    /// <summary>Every format the table names.</summary>
    public static TheoryData<PixelFormat> Formats {
        get {
            var data = new TheoryData<PixelFormat>();

            foreach (var format in DdsCodec.Supported) {
                data.Add(format);
            }

            return data;
        }
    }

    /// <summary>⚠ The guard against a suite that quietly checked nothing.</summary>
    [Fact]
    public void TheCorpusIsTheSizeItIsMeantToBe() => Assert.Equal(25, Formats.Count);

    [Theory]
    [MemberData(nameof(Formats))]
    public void EveryFormatRoundTrips(PixelFormat format) {
        var texture = Filled(new(format, 37, 21));
        var back = DdsCodec.Read(DdsCodec.Write(texture));

        Assert.Equal(format, back.Format);
        Assert.Equal((37, 21, texture.LevelCount), (back.Width, back.Height, back.LevelCount));
        Assert.Equal(texture.Pixels.ToArray(), back.Pixels.ToArray());
    }

    /// <summary>
    ///     ⚠ A DDS is every level of one face and then the next, and a texture is the other way
    ///     round. A round trip alone would pass with neither direction transposing, so the first
    ///     bytes after face zero's whole chain are asserted to be face one's largest level.
    /// </summary>
    [Fact]
    public void ACubeMapIsStoredFaceByFaceAndComesBackLevelByLevel() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 4, 4, faceCount: 6);

        for (var level = 0; level < texture.LevelCount; level++) {
            var length = texture.Levels[level].Length / 6;

            for (var face = 0; face < 6; face++) {
                texture.LevelSpan(level).Slice(face * length, length).Fill((byte)(level * 16 + face));
            }
        }

        var file = DdsCodec.Write(texture);
        const int Payload = 148;
        var chain = 4 * 4 * 4 + 2 * 2 * 4 + 1 * 1 * 4;

        Assert.Equal(0x00, file[Payload]);
        Assert.Equal(0x10, file[Payload + 4 * 4 * 4]);
        Assert.Equal(0x20, file[Payload + 4 * 4 * 4 + 2 * 2 * 4]);
        Assert.Equal(0x01, file[Payload + chain]);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(128 + 8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(128 + 12)));

        var back = DdsCodec.Read(file);

        Assert.Equal(6, back.FaceCount);
        Assert.Equal(texture.Pixels.ToArray(), back.Pixels.ToArray());
    }

    [Fact]
    public void AnArrayRoundTrips() {
        var texture = Filled(new(PixelFormat.Bc7RgbaUNorm, 16, 16, layerCount: 3));
        var back = DdsCodec.Read(DdsCodec.Write(texture));

        Assert.Equal(3, back.LayerCount);
        Assert.Equal(texture.Pixels.ToArray(), back.Pixels.ToArray());
    }

    /// <summary>The header fields a strict reader checks, in the places the format puts them.</summary>
    [Fact]
    public void TheHeaderIsWhatTheFormatSaysItIs() {
        var file = DdsCodec.Write(new(PixelFormat.Bc7RgbaUNormSrgb, 32, 16, 1));

        Assert.Equal("DDS "u8.ToArray(), file[..4]);
        Assert.Equal(124u, Read(4));
        Assert.Equal(16u, Read(12));
        Assert.Equal(32u, Read(16));
        Assert.Equal(32u * 16u, Read(20));
        Assert.Equal(32u, Read(76));
        Assert.Equal("DX10"u8.ToArray(), file[84..88]);
        Assert.Equal(99u, Read(128));
        Assert.Equal(3u, Read(132));
        Assert.Equal(148 + 32 * 16, file.Length);

        uint Read(int at) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));
    }

    [Fact]
    public void AnythingElseIsRefusedByName() {
        Assert.Throws<DdsException>(static () => DdsCodec.Read(new byte[8]));
        Assert.Throws<DdsException>(static () => DdsCodec.Read(new byte[200]));
        Assert.Throws<DdsException>(static () =>
            DdsCodec.Write(new(PixelFormat.Rgba8UNorm, 4, 4, 1, 4))
        );
        Assert.Throws<DdsException>(static () => DdsCodec.Write(new(PixelFormat.Etc2Rgba8UNorm, 4, 4)));

        var legacy = DdsCodec.Write(new(PixelFormat.Rgba8UNorm, 4, 4));

        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(84), 0x35545844); // 'DXT5'

        Assert.Contains(
            "DdsDecoder",
            Assert.Throws<DdsException>(() => DdsCodec.Read(legacy)).Message,
            StringComparison.Ordinal
        );

        var cut = DdsCodec.Write(new(PixelFormat.Rgba8UNorm, 4, 4, 1));

        Assert.Throws<DdsException>(() => DdsCodec.Read(cut.AsSpan(0, cut.Length - 1)));
    }

    /// <summary>
    ///     FFmpeg shares no code with this and decodes the blocks itself. It is held to Vixen's own
    ///     decode of the same blocks, which is proved separately against a GPU, so this checks the
    ///     container: if the header, the size or the row order were wrong the picture would be too.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bc1RgbaUNorm, 4)]
    [InlineData(PixelFormat.Bc3RgbaUNorm, 4)]
    [InlineData(PixelFormat.Bc4RUNorm, 1)]
    [InlineData(PixelFormat.Bc5RgUNorm, 2)]
    public void FfmpegReadsWhatWeWroteTheWayWeDo(PixelFormat format, int channels) {
        if (ExternalTools.FfmpegTool is not { } ffmpeg) {
            ExternalTools.Missing("ffmpeg", "Install it: brew install ffmpeg.");

            return;
        }

        var source = ReferenceImages.FilteredNoise(64);
        var encoded = BlockCompressor.Encode(source, format);
        var ours = BlockCompressor.Decode(encoded).Pixels;

        var directory = ExternalTools.Scratch($"dds-{format}");
        var path = Path.Combine(directory, "texture.dds");
        var raw = Path.Combine(directory, "texture.rgba");

        File.WriteAllBytes(path, DdsCodec.Write(encoded));

        var (exitCode, output) = ExternalTools.Run(
            ffmpeg,
            "-v",
            "error",
            "-y",
            "-i",
            path,
            "-pix_fmt",
            "rgba",
            "-f",
            "rawvideo",
            raw
        );

        Assert.True(exitCode == 0, $"ffmpeg refused {format}:{Environment.NewLine}{output}");

        var theirs = File.ReadAllBytes(raw);

        Assert.Equal(64 * 64 * 4, theirs.Length);

        var worst = 0;

        for (var texel = 0; texel < 64 * 64; texel++) {
            for (var channel = 0; channel < channels; channel++) {
                worst = Math.Max(worst, Math.Abs(theirs[texel * 4 + channel] - ours[texel * 4 + channel]));
            }
        }

        // FFmpeg's fixed-point BC1 and BC4 interpolation rounds differently from the specification's
        // division by up to a level or two; a misread container would be off by hundreds.
        Assert.True(worst <= 3, $"{format}: FFmpeg's texel differs from Vixen's by {worst}");

        Directory.Delete(directory, true);
    }

    /// <summary>
    ///     ⚠ FFmpeg has no BC7 ("Unsupported DXGI format 98"), so ImageIO reads that one, and the
    ///     mid-chain levels and the odd-sized file besides: it is the one reader here that takes the
    ///     format this engine actually ships, through a code path Apple wrote.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bc7RgbaUNorm)]
    [InlineData(PixelFormat.Bc3RgbaUNorm)]
    [InlineData(PixelFormat.Bc1RgbaUNorm)]
    [InlineData(PixelFormat.Rgba8UNorm)]
    public void ImageIoReadsWhatWeWroteTheWayWeDo(PixelFormat format) {
        if (ExternalTools.SipsTool is not { } sips) {
            ExternalTools.Missing("sips", "It comes with macOS, and only there.");

            return;
        }

        var source = ReferenceImages.FilteredNoise(64);
        var encoded = format.IsCompressed() ? BlockCompressor.Encode(source, format) : source;
        var ours = format.IsCompressed() ? BlockCompressor.Decode(encoded).Pixels.ToArray() : source.Pixels.ToArray();

        var directory = ExternalTools.Scratch($"dds-imageio-{format}");
        var path = Path.Combine(directory, "texture.dds");
        var png = Path.Combine(directory, "texture.png");

        File.WriteAllBytes(path, DdsCodec.Write(encoded));

        var (exitCode, output) = ExternalTools.Run(sips, "-s", "format", "png", path, "--out", png);

        Assert.True(exitCode == 0 && File.Exists(png), $"sips refused {format}:{Environment.NewLine}{output}");

        var (width, height, channels, samples) = PngCodec.Decode16(File.ReadAllBytes(png));

        Assert.Equal((64, 64), (width, height));

        var worst = 0;

        for (var texel = 0; texel < 64 * 64; texel++) {
            for (var channel = 0; channel < 3; channel++) {
                var theirs = samples[texel * channels + channel] / 257;

                worst = Math.Max(worst, Math.Abs(theirs - ours[texel * 4 + channel]));
            }
        }

        Assert.True(worst <= 3, $"{format}: ImageIO's texel differs from Vixen's by {worst}");

        Directory.Delete(directory, true);
    }

    static TextureData Filled(TextureData texture) {
        new Random(texture.ByteLength).NextBytes(texture.PixelSpan());

        return texture;
    }
}
