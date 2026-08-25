// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.Imaging.BlockCompression;
using Vixen.Core.IO;
using Vixen.Editor.Assets.Textures;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     <para>
///         The join between a Radiance <c>.hdr</c> on disk and the KTX2 a device uploads. Its input
///         is a file written from the format by <see cref="MinimalHdr" /> rather than by the library
///         that reads it, so "the values came back" is a statement about the two halves agreeing
///         with the format instead of with each other.
///     </para>
///     <para>
///         ⚠ <b>Nothing covered this path, and it had never once worked.</b> The importer claims
///         <c>.hdr</c> and the guide's decoder table promises <c>Rgba32Float</c>; the uncompressed
///         path allocated four bytes a texel and copied sixteen into them, so every <c>.hdr</c> ever
///         dropped on the pipeline failed with "destination is too short". A high-range import is
///         also the kind that renders <i>plausibly</i> when it is wrong — it is a brightness error,
///         not a crash — so these assert values and not that nothing threw.
///     </para>
/// </summary>
public sealed class HdrImportTests {
    static readonly VirtualPath Source = new("/Assets/sky.hdr");

    /// <summary>
    ///     Texels whose exponents span eight stops, so the brightest is 128 times the dimmest and
    ///     every one of them is at or over one — which no eight-bit format can hold.
    /// </summary>
    static byte[] Varied(int width, int height) {
        var rgbe = new byte[width * height * 4];

        for (var texel = 0; texel < width * height; texel++) {
            rgbe[texel * 4] = (byte)(128 + (texel % 64));
            rgbe[(texel * 4) + 1] = (byte)(64 + (texel % 32));
            rgbe[(texel * 4) + 2] = (byte)(32 + (texel % 16));
            rgbe[(texel * 4) + 3] = (byte)(129 + (texel % 8));
        }

        return rgbe;
    }

    /// <summary>One value everywhere, so a lossy encoder's error is not what is being measured.</summary>
    static byte[] Flat(int width, int height, byte red, byte green, byte blue, byte exponent) {
        var rgbe = new byte[width * height * 4];

        for (var texel = 0; texel < width * height; texel++) {
            rgbe[texel * 4] = red;
            rgbe[(texel * 4) + 1] = green;
            rgbe[(texel * 4) + 2] = blue;
            rgbe[(texel * 4) + 3] = exponent;
        }

        return rgbe;
    }

    static ImportContext Context(byte[] file, TextureImportSettings settings, string target = "Windows") {
        var files = new MemoryFileProvider();
        files.Seed(Source, file);

        return new(AssetId.New(), Source, settings, files, "TextureImporter", target);
    }

    static ImportContext Png(TextureImportSettings settings) {
        var files = new MemoryFileProvider();
        files.Seed(new("/Assets/hero.png"), MinimalPng.Write(4, 4, new byte[4 * 4 * 4]));

        return new(AssetId.New(), new("/Assets/hero.png"), settings, files, "TextureImporter", "Windows");
    }

    /// <summary>
    ///     The instrument, before anything is measured with it: the fixture is a file the decoder
    ///     reads, and every texel it reads back is the value the format's own arithmetic says it is.
    /// </summary>
    [Theory]
    [InlineData(8, 8)]
    [InlineData(16, 3)]
    public void TheFixtureIsAFileTheDecoderReadsBackExactly(int width, int height) {
        var rgbe = Varied(width, height);

        using var stream = new MemoryStream(MinimalHdr.Write(width, height, rgbe));
        var texture = new StbImageDecoder().Decode(stream, ".hdr");

        Assert.Equal(PixelFormat.Rgba32Float, texture.Format);
        Assert.Equal(width, texture.Width);
        Assert.Equal(height, texture.Height);

        var floats = MemoryMarshal.Cast<byte, float>(texture.Level(0));

        for (var texel = 0; texel < width * height; texel++) {
            var exponent = rgbe[(texel * 4) + 3];

            for (var channel = 0; channel < 3; channel++) {
                Assert.Equal(MinimalHdr.ToFloat(rgbe[(texel * 4) + channel], exponent), floats[(texel * 4) + channel]);
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>The one that used to fail, and the reason for the file.</b> Asking for no compression
    ///     is asking for the decoded radiance, and every float has to arrive bit-identical: this is
    ///     the assertion that catches a narrowing to bytes, which would render as a picture rather
    ///     than as an error.
    /// </summary>
    [Fact]
    public async Task WithoutCompressionTheFloatsArriveExactly() {
        var rgbe = Varied(8, 8);

        var result = await new TextureImporter().ImportAsync(
            Context(
                MinimalHdr.Write(8, 8, rgbe),
                new() { Content = TextureContent.Linear, Compression = TextureCompression.None, GenerateMips = false }
            ),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Succeeded);
        var texture = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(PixelFormat.Rgba32Float, texture.Format);
        Assert.Equal(8, texture.Width);
        Assert.Equal(8, texture.Height);
        Assert.Equal(1, texture.LevelCount);

        var floats = MemoryMarshal.Cast<byte, float>(texture.Level(0));

        for (var texel = 0; texel < 8 * 8; texel++) {
            var exponent = rgbe[(texel * 4) + 3];

            for (var channel = 0; channel < 3; channel++) {
                Assert.Equal(MinimalHdr.ToFloat(rgbe[(texel * 4) + channel], exponent), floats[(texel * 4) + channel]);
            }
        }

        // ⚠ Not a restatement of the loop. The loop would still pass on a fixture an eight-bit
        // texture could have held, which would make the whole file agree with the bug it exists to
        // catch; this is the claim that the picture is one no byte can express.
        var brightest = 0f;

        foreach (var value in floats) {
            brightest = MathF.Max(brightest, value);
        }

        Assert.True(brightest > 100f, $"The fixture has to carry values well over one, and its brightest is {brightest}.");
    }

    /// <summary>
    ///     Automatic picks BC6H, which is the only block format that holds a value over one — and
    ///     the value comes back over one, which is the whole point and is not implied by the format.
    /// </summary>
    [Fact]
    public async Task AutomaticShipsAsBc6HAndTheRangeSurvivesIt() {
        // 128 × 2^(136 − 136), so a hundred and twenty-eight times the brightest thing an eight-bit
        // texture can say, and comfortably inside half precision's range on the way through BC6H.
        var rgbe = Flat(8, 8, 128, 64, 32, 136);

        var result = await new TextureImporter().ImportAsync(
            Context(
                MinimalHdr.Write(8, 8, rgbe),
                new() { Content = TextureContent.Linear, GenerateMips = false }
            ),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Succeeded);
        var texture = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(PixelFormat.Bc6HRgbUFloat, texture.Format);
        Assert.Equal(8, texture.Width);
        Assert.Equal(8, texture.Height);

        var decoded = BlockCompressor.Decode(texture);
        var halves = MemoryMarshal.Cast<byte, Half>(decoded.Level(0));

        for (var texel = 0; texel < 8 * 8; texel++) {
            Assert.Equal(128f, (float)halves[texel * 4], tolerance: 2f);
            Assert.Equal(64f, (float)halves[(texel * 4) + 1], tolerance: 1f);
            Assert.Equal(32f, (float)halves[(texel * 4) + 2], tolerance: 1f);
        }
    }

    /// <summary>
    ///     <para>
    ///         The transfer function, said out loud. Radiance is linear by definition and no float
    ///         format has an sRGB form, so a usage of <see cref="TextureContent.Colour" /> — which is
    ///         the default, and what an artist importing a sky will leave alone — cannot be honoured
    ///         and must not be silently half-honoured.
    ///     </para>
    ///     <para>
    ///         ⚠ Getting this wrong is invisible: a texture the hardware converts twice and one it
    ///         never converts are both pictures, and neither logs anything.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task AColourUsageIsReportedAsNotApplyingAndTheFormatStaysLinear() {
        var result = await new TextureImporter().ImportAsync(
            Context(
                MinimalHdr.Write(8, 8, Varied(8, 8)),
                new() { Content = TextureContent.Colour, Compression = TextureCompression.None, GenerateMips = false }
            ),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Succeeded);
        Assert.False(Ktx2.Read(Assert.Single(result.Artifacts).Content.Span).Format.IsSrgb());

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Information
                && entry.Message.Contains("no float format has an sRGB form", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     The two things the eight-bit path would have done and this one cannot: the mip chain and
    ///     the size limit both run through a filter that reads eight-bit channels. Reported rather
    ///     than approximated, and reported rather than left for somebody to notice in a frame.
    /// </summary>
    [Fact]
    public async Task MipsAndTheSizeLimitAreWarnedAboutRatherThanApplied() {
        var result = await new TextureImporter().ImportAsync(
            Context(
                MinimalHdr.Write(16, 16, Varied(16, 16)),
                new() { Content = TextureContent.Linear, Compression = TextureCompression.None, MaxSize = 8 }
            ),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Succeeded);
        var texture = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(1, texture.LevelCount);
        Assert.Equal(16, texture.Width);
        Assert.Equal(16, texture.Height);

        var warnings = result.Diagnostics.Where(entry => entry.Severity == ImportSeverity.Warning).ToList();

        Assert.Contains(warnings, entry => entry.Message.Contains("ships with one level", StringComparison.Ordinal));
        Assert.Contains(warnings, entry => entry.Message.Contains("over the 8 limit", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A phone samples neither BC7 nor BC6H, so a high-range texture ships as floats there and
    ///     says how much that costs, the way the eight-bit path already does.
    /// </summary>
    [Fact]
    public async Task AHighRangeTextureShipsUncompressedOnAPhoneAndSaysSo() {
        var result = await new TextureImporter().ImportAsync(
            Context(
                MinimalHdr.Write(8, 8, Varied(8, 8)),
                new() { Content = TextureContent.Linear, GenerateMips = false },
                "Android/Vulkan"
            ),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Succeeded);
        Assert.Equal(PixelFormat.Rgba32Float, Ktx2.Read(Assert.Single(result.Artifacts).Content.Span).Format);

        Assert.Contains(
            result.Diagnostics,
            entry => entry.Severity == ImportSeverity.Warning
                && entry.Message.Contains("does not sample BC", StringComparison.Ordinal)
        );
    }

    /// <summary>
    ///     Every eight-bit block format clamps at one, so shipping a high-range source in one would
    ///     produce a low-range picture under a high-range name — refused, and refused by the
    ///     importer rather than by the encoder, whose own message advises the mistake being made.
    /// </summary>
    [Theory]
    [InlineData(TextureCompression.Bc1)]
    [InlineData(TextureCompression.Bc3)]
    [InlineData(TextureCompression.Bc4)]
    [InlineData(TextureCompression.Bc5)]
    [InlineData(TextureCompression.Bc7)]
    public async Task AnEightBitBlockFormatIsRefusedForAHighRangeSource(TextureCompression compression) {
        var failure = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await new TextureImporter().ImportAsync(
                Context(
                    MinimalHdr.Write(8, 8, Varied(8, 8)),
                    new() { Content = TextureContent.Linear, Compression = compression, GenerateMips = false }
                ),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("Ask for Bc6H", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And the mirror of it, because the setting is now askable of anything: BC6H on an
    ///     eight-bit source drops the alpha and spends its precision above one, where an eight-bit
    ///     source has nothing.
    /// </summary>
    [Fact]
    public async Task Bc6HIsRefusedForAnEightBitSource() {
        var failure = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await new TextureImporter().ImportAsync(
                Png(new() { Content = TextureContent.Linear, Compression = TextureCompression.Bc6H }),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("BC7 is the eight-bit equivalent", failure.Message, StringComparison.Ordinal);
    }
}
