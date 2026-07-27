// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.IO;
using Vixen.Editor.Assets.Textures;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The importer that ties the pipeline to <c>Vixen.Core.Imaging</c>. Its input is a PNG written
///     from the specification by <see cref="MinimalPng" /> rather than by the library that decodes
///     it, so "the pixels came back" is a statement about the two halves agreeing with the format
///     instead of with each other.
/// </summary>
public sealed class TextureImporterTests {
    static readonly VirtualPath Source = new("/Assets/hero.png");

    [Fact]
    public async Task APngComesOutAsAKtx2WithTheSameSizeAndTheSamePixels() {
        var (context, pixels) = Png(new() { Compression = TextureCompression.None, GenerateMips = false });

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var texture = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(4, texture.Width);
        Assert.Equal(4, texture.Height);
        Assert.Equal(1, texture.LevelCount);
        Assert.Equal(pixels, texture.Level(0).ToArray());
    }

    /// <summary>
    ///     The artefact is what the runtime uploads, so its type has to be the one the loader looks
    ///     for and its sub-asset the main one. A texture has no parts.
    /// </summary>
    [Fact]
    public async Task TheArtefactIsTheMainSubAssetAndIsCalledTexture() {
        var (context, _) = Png(new());

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);
        var artifact = Assert.Single(result.Artifacts);

        Assert.Equal("Texture", artifact.Type);
        Assert.Equal(SubAssetId.Main, artifact.SubAsset);
        Assert.Empty(result.SubAssets);
    }

    /// <summary>
    ///     <para>
    ///         The sRGB flag is the one decision no file can make for itself, and getting it wrong is
    ///         the bug that produces a scene which looks washed out or crushed and takes a week to
    ///         diagnose. A colour texture ships in an sRGB format so the hardware converts on the way
    ///         out of the sampler; a roughness map or a mask must not, because a transfer function
    ///         applied to data bends the whole material response.
    ///     </para>
    ///     <para>
    ///         Both directions are asserted, because a decision that is always the same is not a
    ///         decision.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(TextureContent.Colour, PixelFormat.Rgba8UNormSrgb)]
    [InlineData(TextureContent.Linear, PixelFormat.Rgba8UNorm)]
    [InlineData(TextureContent.NormalMap, PixelFormat.Rgba8UNorm)]
    public async Task WhatTheBytesMeanDecidesWhetherTheHardwareConverts(TextureContent content, PixelFormat expected) {
        var (context, _) = Png(new() { Content = content, Compression = TextureCompression.None });

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(expected, Ktx2.Read(Assert.Single(result.Artifacts).Content.Span).Format);
    }

    /// <summary>
    ///     And what the bytes mean also decides the compressed format. A normal map goes to BC5's two
    ///     channels because the third is reconstructed in the shader and storing it costs precision
    ///     the other two want; colour goes to BC7, in its sRGB form so the pair stay consistent.
    /// </summary>
    [Theory]
    [InlineData(TextureContent.Colour, PixelFormat.Bc7RgbaUNormSrgb)]
    [InlineData(TextureContent.Linear, PixelFormat.Bc7RgbaUNorm)]
    [InlineData(TextureContent.NormalMap, PixelFormat.Bc5RgUNorm)]
    public async Task AutomaticCompressionIsChosenFromWhatTheBytesMean(TextureContent content, PixelFormat expected) {
        var (context, _) = Png(new() { Content = content });

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(expected, Ktx2.Read(Assert.Single(result.Artifacts).Content.Span).Format);
    }

    /// <summary>
    ///     A colour texture cannot be squeezed into a two-channel format: BC5 has no sRGB form, so
    ///     the hardware would never apply the transfer function and the texture would ship looking
    ///     wrong with nothing in the build log. One of the two settings is a mistake and saying so is
    ///     better than picking one.
    /// </summary>
    [Fact]
    public async Task AColourTextureCannotBeSqueezedIntoTwoChannels() {
        var (context, _) = Png(new() { Content = TextureContent.Colour, Compression = TextureCompression.Bc5 });

        var failure = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken)
        );

        Assert.Contains("no sRGB form", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <para>
    ///         BC is a desktop and console format: Android's baseline is ETC2 and iOS is ASTC only,
    ///         and a BC7 texture on either is one the driver refuses to sample. Shipping it anyway
    ///         would produce a build that fails on the device rather than in the log.
    ///     </para>
    ///     <para>
    ///         So a mobile target gets uncompressed and a warning that names what is missing. That is
    ///         four times the memory and it is the honest answer until <c>astcenc</c> is bound — the
    ///         alternative is a texture that does not work.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("Android/Vulkan")]
    [InlineData("iOS")]
    public async Task AMobileTargetGetsUncompressedAndAWarningThatNamesTheEncoder(string target) {
        var (context, _) = Png(new(), target: target);

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(PixelFormat.Rgba8UNormSrgb, Ktx2.Read(Assert.Single(result.Artifacts).Content.Span).Format);

        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Warning);
        Assert.Contains("astcenc", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADesktopTargetGetsNoSuchWarning() {
        var (context, _) = Png(new());

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Warning);
    }

    /// <summary>
    ///     Halving rather than resampling, which means a limit of three on an 8×8 gives 2×2 and not
    ///     3×3. Every engine behaves this way and it surprises everyone once, so the importer says
    ///     what it did.
    /// </summary>
    [Fact]
    public async Task ASizeLimitHalvesUntilItFitsAndSaysSo() {
        var (context, _) = Png(new() { MaxSize = 3, Compression = TextureCompression.None }, width: 8, height: 8);

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);
        var texture = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(2, texture.Width);
        Assert.Equal(2, texture.Height);

        var note = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Severity == ImportSeverity.Information
        );

        Assert.Contains("halved 2 times", note.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The bottom of the chain. A limit of one reduces all the way to a single texel, which is
    ///     the only case that runs the halving loop to its last level — every other limit stops
    ///     earlier because the size condition is already satisfied. Without it the loop's bound was
    ///     untested: moving it by one left every other test in this file green.
    /// </summary>
    [Fact]
    public async Task ALimitOfOneReducesAllTheWayToASingleTexel() {
        var (context, _) = Png(new() { MaxSize = 1, Compression = TextureCompression.None }, width: 8, height: 8);

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);
        var texture = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(1, texture.Width);
        Assert.Equal(1, texture.Height);
    }

    [Fact]
    public async Task ATextureUnderTheLimitIsLeftAlone() {
        var (context, _) = Png(new() { MaxSize = 64, Compression = TextureCompression.None });

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(4, Ktx2.Read(Assert.Single(result.Artifacts).Content.Span).Width);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 1)]
    public async Task MipsAreBuiltOrNotAsAsked(bool generate, int expected) {
        var (context, _) = Png(
            new() { GenerateMips = generate, Compression = TextureCompression.None },
            width: 4,
            height: 4
        );

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(expected, Ktx2.Read(Assert.Single(result.Artifacts).Content.Span).LevelCount);
    }

    /// <summary>
    ///     <para>
    ///         What the bytes mean reaches the mip filter too, and this is the only test that says
    ///         so. A checkerboard of black and white halves to 188 when the texture is colour, because
    ///         colour is averaged in the light it stands for, and to 128 when it is data. The mip
    ///         filter's own tests prove it can do both; this proves the importer asks for the right
    ///         one.
    ///     </para>
    ///     <para>
    ///         The importer wiring is the part worth testing separately, because passing
    ///         <c>MipOptions.Linear</c> everywhere would leave every other test in this file green:
    ///         nothing else here looks below level zero.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(TextureContent.Colour, 188)]
    [InlineData(TextureContent.Linear, 128)]
    public async Task WhatTheBytesMeanReachesTheMipFilter(TextureContent content, int expected) {
        var pixels = new byte[2 * 2 * 4];

        for (var texel = 0; texel < 4; texel++) {
            var white = texel % 2 == 0;

            for (var channel = 0; channel < 3; channel++) {
                pixels[(texel * 4) + channel] = white ? (byte)255 : (byte)0;
            }

            pixels[(texel * 4) + 3] = 255;
        }

        var files = new MemoryFileProvider();
        files.Seed(Source, MinimalPng.Write(2, 2, pixels));

        var context = new ImportContext(
            AssetId.New(),
            Source,
            new TextureImportSettings { Content = content, Compression = TextureCompression.None },
            files,
            "TextureImporter",
            "Windows"
        );

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);
        var texture = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(2, texture.LevelCount);
        Assert.Equal(expected, texture.Level(1)[0]);
    }

    /// <summary>
    ///     And a normal map's mips come back unit length, which the plain filter would not give.
    /// </summary>
    [Fact]
    public async Task ANormalMapsMipsAreStillUnitLength() {
        var pixels = new byte[2 * 2 * 4];
        ReadOnlySpan<(byte X, byte Y)> leaning = [(218, 128), (36, 128), (128, 218), (128, 36)];

        for (var texel = 0; texel < 4; texel++) {
            pixels[texel * 4] = leaning[texel].X;
            pixels[(texel * 4) + 1] = leaning[texel].Y;
            pixels[(texel * 4) + 2] = 218;
            pixels[(texel * 4) + 3] = 255;
        }

        var files = new MemoryFileProvider();
        files.Seed(Source, MinimalPng.Write(2, 2, pixels));

        var context = new ImportContext(
            AssetId.New(),
            Source,
            new TextureImportSettings {
                Content = TextureContent.NormalMap,
                Compression = TextureCompression.None
            },
            files,
            "TextureImporter",
            "Windows"
        );

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);
        var reduced = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span).Level(1);

        var x = (reduced[0] / 255f * 2f) - 1f;
        var y = (reduced[1] / 255f * 2f) - 1f;
        var z = (reduced[2] / 255f * 2f) - 1f;

        Assert.Equal(1f, MathF.Sqrt((x * x) + (y * y) + (z * z)), tolerance: 0.02f);
    }

    /// <summary>
    ///     A texture an artist already compressed with a better encoder is copied rather than decoded
    ///     and re-encoded. A second round of lossy compression only ever loses, and the bytes coming
    ///     out being the bytes going in is the only way to say that without qualification.
    /// </summary>
    [Fact]
    public async Task AnAlreadyCompressedSourcePassesThroughUntouched() {
        var original = new TextureData(PixelFormat.Bc7RgbaUNorm, 8, 8, levelCount: 1);

        for (var index = 0; index < original.ByteLength; index++) {
            original.PixelSpan()[index] = (byte)(index * 7);
        }

        var path = new VirtualPath("/Assets/baked.ktx2");
        var files = new MemoryFileProvider();
        files.Seed(path, Ktx2.Write(original));

        var context = new ImportContext(
            AssetId.New(),
            path,
            new TextureImportSettings(),
            files,
            "TextureImporter",
            "Windows"
        );

        var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);
        var texture = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(PixelFormat.Bc7RgbaUNorm, texture.Format);
        Assert.Equal(original.Pixels.ToArray(), texture.Pixels.ToArray());
    }

    /// <summary>
    ///     An importer that reads a sibling without declaring it produces an artefact that is stale
    ///     for ever. This one reads its source and nothing else, and the enforcing provider is what
    ///     would say otherwise.
    /// </summary>
    [Fact]
    public async Task ItReadsItsSourceAndNothingElse() {
        var (context, _) = Png(new());

        await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal([Source], context.FileDependencies);
        Assert.Empty(context.AssetDependencies);
    }

    /// <summary>
    ///     The same input has to give the same bytes, because the content build's cache key is a hash
    ///     of the input and the catalogue's is a hash of the output. A non-deterministic importer
    ///     makes every incremental build ship a changed bundle.
    /// </summary>
    [Fact]
    public async Task TheSameSourceImportsToTheSameBytes() {
        var first = await Import(new());
        var second = await Import(new());

        Assert.Equal(first, second);

        async Task<byte[]> Import(TextureImportSettings settings) {
            var (context, _) = Png(settings);
            var result = await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);

            return Assert.Single(result.Artifacts).Content.ToArray();
        }
    }

    [Fact]
    public async Task AnExtensionNothingDecodesNamesWhatIsMissing() {
        var path = new VirtualPath("/Assets/hero.exr");
        var files = new MemoryFileProvider();
        files.Seed(path, "not really an exr");

        var context = new ImportContext(
            AssetId.New(),
            path,
            new TextureImportSettings(),
            files,
            "TextureImporter",
            "Windows"
        );

        var failure = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken)
        );

        Assert.Contains(".exr", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItClaimsTheFormatsItsDecodersRead() {
        var importer = new TextureImporter();

        Assert.Contains(".png", importer.Extensions);
        Assert.Contains(".hdr", importer.Extensions);
        Assert.Contains(".ktx2", importer.Extensions);
        Assert.Equal("TextureImporter", importer.Name);
    }

    /// <summary>
    ///     Every extension the importer claims has to have a decoder behind it, or an artist gets a
    ///     failed import for a file the pipeline advertised it could read.
    /// </summary>
    [Fact]
    public void EveryExtensionItClaimsHasADecoder() {
        foreach (var extension in new TextureImporter().Extensions) {
            Assert.NotNull(ImageDecoders.For(ImageDecoders.BuiltIn, extension));
        }
    }

    /// <summary>
    ///     And the registry resolves a file to it, which is the join between "this class exists" and
    ///     "the pipeline will actually run it".
    /// </summary>
    [Fact]
    public void TheRegistryResolvesAnImageToIt() {
        var registry = new ImporterRegistry().Add(new TextureImporter()).AddFallback(new RawImporter());

        Assert.True(registry.TryGetForFile("/Assets/hero.png", out var forPng));
        Assert.IsType<TextureImporter>(forPng);

        Assert.True(registry.TryGetForFile("/Assets/sky.hdr", out var forHdr));
        Assert.IsType<TextureImporter>(forHdr);

        Assert.True(registry.TryGetForFile("/Assets/notes.txt", out var forText));
        Assert.IsType<RawImporter>(forText);
    }

    static (ImportContext Context, byte[] Pixels) Png(
        TextureImportSettings settings,
        int width = 4,
        int height = 4,
        string target = "Windows"
    ) {
        var pixels = new byte[width * height * 4];

        for (var texel = 0; texel < width * height; texel++) {
            pixels[texel * 4] = (byte)(texel * 11);
            pixels[(texel * 4) + 1] = (byte)(255 - (texel * 5));
            pixels[(texel * 4) + 2] = (byte)(texel * 3);
            pixels[(texel * 4) + 3] = 255;
        }

        var files = new MemoryFileProvider();
        files.Seed(Source, MinimalPng.Write(width, height, pixels));

        return (
            new(AssetId.New(), Source, settings, files, "TextureImporter", target),
            pixels
        );
    }
}
