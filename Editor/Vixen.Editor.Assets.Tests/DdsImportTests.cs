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
///     <para>
///         The join between a <c>.dds</c> on disk and the KTX2 a device uploads. Its input is a
///         header written from DirectDraw's own field order by <see cref="MinimalDds" /> rather than
///         by the code that reads it.
///     </para>
///     <para>
///         Half of these assert a refusal. That is the point: doc 08's table promises DDS, DDS can
///         express a cube map, an array and a volume, and the alternative to refusing those by name
///         is reading a cube map's six element-major mip chains as one level-major one and shipping
///         a texture whose faces are interleaved into the wrong levels.
///     </para>
/// </summary>
public sealed class DdsImportTests {
    static readonly VirtualPath Source = new("/Assets/rock.dds");

    static ImportContext Context(byte[] file, TextureImportSettings? settings = null) {
        var files = new MemoryFileProvider();
        files.Seed(Source, file);

        return new(AssetId.New(), Source, settings ?? new(), files, "TextureImporter", "Windows");
    }

    static byte[] Counting(int length) {
        var bytes = new byte[length];

        for (var index = 0; index < length; index++) {
            bytes[index] = (byte)((index * 13) + 1);
        }

        return bytes;
    }

    /// <summary>
    ///     The one whose failure was silent, which is why it comes first: an unclaimed extension does
    ///     not error. It falls to <see cref="RawImporter" /> and becomes a byte blob under a type
    ///     name no runtime texture loader resolves, so an artist drops a normal map in and gets an
    ///     address that binds to nothing, with a green build.
    /// </summary>
    [Fact]
    public void TheRegistryResolvesADdsToTheTextureImporterAndNotToTheFallback() {
        var registry = new ImporterRegistry().Add(new TextureImporter()).AddFallback(new RawImporter());

        Assert.True(registry.TryGetForFile("/Assets/rock.dds", out var importer));
        Assert.IsType<TextureImporter>(importer);
    }

    /// <summary>
    ///     BC7 is what a DDS out of a modern texture tool holds, and the engine already speaks it, so
    ///     the payload goes through untouched — a second round of lossy compression only ever loses.
    /// </summary>
    [Fact]
    public async Task ABc7DdsShipsAsBc7WithItsBytesUntouched() {
        var blocks = Counting(16);

        var result = await new TextureImporter().ImportAsync(
            Context(MinimalDds.Write(4, 4, 98, blocks), new() { Content = TextureContent.Linear }),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Succeeded);
        var texture = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(PixelFormat.Bc7RgbaUNorm, texture.Format);
        Assert.Equal(4, texture.Width);
        Assert.Equal(4, texture.Height);
        Assert.Equal(blocks, texture.Level(0).ToArray());
    }

    /// <summary>
    ///     <para>
    ///         And the mip chain comes with it. This is the one thing a DDS carries that a PNG cannot
    ///         and that re-deriving would throw away: the levels an artist's tool encoded.
    ///     </para>
    ///     <para>
    ///         A 4×4 BC7 is three levels of one block each, and the assertion is on all three
    ///         separately, because concatenation is exactly what a level-table bug preserves.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task ACompressedMipChainArrivesLevelForLevel() {
        var blocks = Counting(48);

        var result = await new TextureImporter().ImportAsync(
            Context(MinimalDds.Write(4, 4, 98, blocks, mipCount: 3), new() { Content = TextureContent.Linear }),
            TestContext.Current.CancellationToken
        );

        var texture = Ktx2.Read(Assert.Single(result.Artifacts).Content.Span);

        Assert.Equal(3, texture.LevelCount);
        Assert.Equal(blocks[..16], texture.Level(0).ToArray());
        Assert.Equal(blocks[16..32], texture.Level(1).ToArray());
        Assert.Equal(blocks[32..], texture.Level(2).ToArray());
    }

    /// <summary>
    ///     <para>
    ///         <b>The silent one.</b> DXGI states the transfer function in the format number, and a
    ///         compressed payload passes straight through — so the file's answer is final and nothing
    ///         downstream gets a second chance to label it. Mapping <c>BC7_UNORM_SRGB</c> onto the
    ///         linear format would ship an albedo the sampler never converts, which is a scene that
    ///         looks washed out with a clean build log.
    ///     </para>
    ///     <para>
    ///         Both directions, because a decision that is always the same is not a decision.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(98u, PixelFormat.Bc7RgbaUNorm)]
    [InlineData(99u, PixelFormat.Bc7RgbaUNormSrgb)]
    [InlineData(71u, PixelFormat.Bc1RgbaUNorm)]
    [InlineData(72u, PixelFormat.Bc1RgbaUNormSrgb)]
    [InlineData(77u, PixelFormat.Bc3RgbaUNorm)]
    [InlineData(78u, PixelFormat.Bc3RgbaUNormSrgb)]
    public void TheHeadersTransferFunctionSurvivesTheImport(uint dxgi, PixelFormat expected) {
        var texture = DdsDecoder.Read(MinimalDds.Write(4, 4, dxgi, Counting(16)));

        Assert.Equal(expected, texture.Format);
    }

    /// <summary>
    ///     BC4 and BC5 are half-size blocks, so getting their block size wrong is a length error
    ///     rather than a wrong picture — which is the good kind, and worth pinning anyway.
    /// </summary>
    [Theory]
    [InlineData(80u, PixelFormat.Bc4RUNorm, 8)]
    [InlineData(83u, PixelFormat.Bc5RgUNorm, 16)]
    [InlineData(95u, PixelFormat.Bc6HRgbUFloat, 16)]
    public void TheBlockSizedFormatsAreReadAtTheirOwnSize(uint dxgi, PixelFormat expected, int blockBytes) {
        var texture = DdsDecoder.Read(MinimalDds.Write(4, 4, dxgi, Counting(blockBytes)));

        Assert.Equal(expected, texture.Format);
        Assert.Equal(blockBytes, texture.Level(0).Length);
    }

    /// <summary>
    ///     Every DDS written before D3D10 says what it is with a four-character code and no extension
    ///     header, and that is still most of the DDS files in the world.
    /// </summary>
    [Theory]
    [InlineData("DXT1", PixelFormat.Bc1RgbaUNorm, 8)]
    [InlineData("DXT5", PixelFormat.Bc3RgbaUNorm, 16)]
    [InlineData("ATI1", PixelFormat.Bc4RUNorm, 8)]
    [InlineData("ATI2", PixelFormat.Bc5RgUNorm, 16)]
    [InlineData("BC5U", PixelFormat.Bc5RgUNorm, 16)]
    public void ALegacyFourCharacterCodeIsReadWithoutAnExtensionHeader(
        string fourCc,
        PixelFormat expected,
        int blockBytes
    ) {
        var texture = DdsDecoder.Read(MinimalDds.WriteFourCc(4, 4, fourCc, Counting(blockBytes)));

        Assert.Equal(expected, texture.Format);
        Assert.Equal(blockBytes, texture.Level(0).Length);
    }

    /// <summary>
    ///     <para>
    ///         <b>Which way up, and which way round.</b> DDS has no origin flag: row zero is the top,
    ///         always. That is already the pipeline's order — PNG's rows arrive top first and come
    ///         back unchanged — so the pixels are copied through with no flip, and a fixture that is
    ///         asymmetric in both axes is what says so rather than a comment.
    ///     </para>
    ///     <para>
    ///         The channel order is asserted in the same test because it is the other half of the
    ///         same mistake: B8G8R8A8 is what a DDS out of an older tool holds, and reading it as
    ///         RGBA gives a picture that is recognisably the right picture with the red and blue
    ///         swapped — which, on a normal map, is a surface that leans the wrong way and looks fine.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(28u, 0, 1, 2)]      // R8G8B8A8_UNORM: red first in the file.
    [InlineData(87u, 2, 1, 0)]      // B8G8R8A8_UNORM: blue first in the file.
    public void AnUncompressedDdsKeepsItsRowOrderAndGetsItsChannelsTheRightWayRound(
        uint dxgi,
        int red,
        int green,
        int blue
    ) {
        // Three rows of four, red climbing left to right and green climbing top to bottom, so a
        // vertical flip, a horizontal flip and a transpose are each a different failure.
        var file = new byte[12 * 4];

        for (var y = 0; y < 3; y++) {
            for (var x = 0; x < 4; x++) {
                var texel = ((y * 4) + x) * 4;
                file[texel + red] = (byte)(16 + (x * 32));
                file[texel + green] = (byte)(16 + (y * 64));
                file[texel + blue] = 7;
                file[texel + 3] = 255;
            }
        }

        var texture = DdsDecoder.Read(MinimalDds.Write(4, 3, dxgi, file));

        Assert.Equal(PixelFormat.Rgba8UNorm, texture.Format);
        Assert.Equal(4, texture.Width);
        Assert.Equal(3, texture.Height);

        var pixels = texture.Level(0);

        for (var y = 0; y < 3; y++) {
            for (var x = 0; x < 4; x++) {
                var texel = ((y * 4) + x) * 4;

                Assert.Equal(16 + (x * 32), pixels[texel]);
                Assert.Equal(16 + (y * 64), pixels[texel + 1]);
                Assert.Equal(7, pixels[texel + 2]);
                Assert.Equal(255, pixels[texel + 3]);
            }
        }
    }

    /// <summary>The same picture through the masked legacy header, which is how a pre-D3D10 tool wrote BGRA.</summary>
    [Fact]
    public void ALegacyMaskedHeaderNamesItsChannelsByBitMaskAndIsReadTheSameWay() {
        byte[] file = [0x30, 0x20, 0x10, 0xFF, 0x31, 0x21, 0x11, 0x80];

        var texture = DdsDecoder.Read(
            MinimalDds.WriteMasked(2, 1, 32, (0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000), file)
        );

        Assert.Equal(PixelFormat.Rgba8UNorm, texture.Format);
        Assert.Equal<byte[]>([0x10, 0x20, 0x30, 0xFF, 0x11, 0x21, 0x31, 0x80], texture.Level(0).ToArray());
    }

    /// <summary>
    ///     An eight-bit single-channel DDS is what a mask or a height field ships as, and it widens
    ///     to opaque black plus the one channel rather than being refused.
    /// </summary>
    [Fact]
    public void ASingleChannelDdsWidensToOpaqueRgba() {
        var texture = DdsDecoder.Read(MinimalDds.Write(2, 1, 61, [0x40, 0x80]));

        Assert.Equal<byte[]>([0x40, 0, 0, 255, 0x80, 0, 0, 255], texture.Level(0).ToArray());
    }

    /// <summary>
    ///     <para>
    ///         <b>Claimed and refused, on <c>VideoImporter</c>'s precedent and for its reason.</b> An
    ///         artist who drops a cube map in and finds it silently became a 2D texture with its
    ///         faces folded into its mip levels has learned nothing; a build that stops and names the
    ///         shape has told them what to do.
    ///     </para>
    ///     <para>
    ///         All four spellings, because DDS can say each of these two different ways and a reader
    ///         that checks only the modern one passes every test written after 2008 and mis-reads
    ///         every file written before it.
    ///     </para>
    /// </summary>
    [Fact]
    public void ACubeMapIsRefusedByItsExtensionHeader() {
        var failure = Assert.Throws<NotSupportedException>(
            () => DdsDecoder.Read(MinimalDds.Write(4, 4, 98, Counting(16 * 6), arraySize: 1, miscFlag: 4))
        );

        Assert.Contains("cube map", failure.Message, StringComparison.Ordinal);
        Assert.Contains(".ktx2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACubeMapIsRefusedByItsLegacyCapsBits() {
        var failure = Assert.Throws<NotSupportedException>(
            () => DdsDecoder.Read(MinimalDds.Write(4, 4, 98, Counting(16 * 6), caps2: MinimalDds.CubeMapCaps))
        );

        Assert.Contains("cube map", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextureArrayIsRefusedByName() {
        var failure = Assert.Throws<NotSupportedException>(
            () => DdsDecoder.Read(MinimalDds.Write(4, 4, 98, Counting(16 * 4), arraySize: 4))
        );

        Assert.Contains("texture array", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4u, 1)]     // A 3D resource dimension in the extension header.
    [InlineData(3u, 4)]     // A depth in the base header.
    public void AVolumeIsRefusedByName(uint dimension, int depth) {
        var failure = Assert.Throws<NotSupportedException>(
            () => DdsDecoder.Read(MinimalDds.Write(4, 4, 98, Counting(16 * 4), dimension: dimension, depth: depth))
        );

        Assert.Contains("volume texture", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVolumeIsAlsoRefusedByItsLegacyCapsBit() {
        var failure = Assert.Throws<NotSupportedException>(
            () => DdsDecoder.Read(MinimalDds.Write(4, 4, 98, Counting(16 * 4), caps2: MinimalDds.VolumeCaps))
        );

        Assert.Contains("volume texture", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     BC2 has BC3's block size and BC3's colour half, so reading one as the other gives a
    ///     picture with garbage alpha — which looks like a texture with a bad mask rather than like
    ///     an importer bug, and would be found months later. It is refused by name instead.
    /// </summary>
    [Theory]
    [InlineData(74u)]
    [InlineData(75u)]
    public void Bc2IsRefusedRatherThanReadAsBc3(uint dxgi) {
        var failure = Assert.Throws<NotSupportedException>(
            () => DdsDecoder.Read(MinimalDds.Write(4, 4, dxgi, Counting(16)))
        );

        Assert.Contains("BC2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DxtThreeIsRefusedByTheSameArgument() {
        var failure = Assert.Throws<NotSupportedException>(
            () => DdsDecoder.Read(MinimalDds.WriteFourCc(4, 4, "DXT3", Counting(16)))
        );

        Assert.Contains("BC2", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A high-range surface stored uncompressed would have to be narrowed to a byte to fit the
    ///     path this decoder returns into, and narrowing it is the one thing a high-range image
    ///     exists to avoid. BC6H is read; this says so rather than quietly clipping the sun.
    /// </summary>
    [Theory]
    [InlineData(2u)]        // R32G32B32A32_FLOAT
    [InlineData(10u)]       // R16G16B16A16_FLOAT
    public void AnUncompressedHighRangeSurfaceIsRefusedRatherThanNarrowed(uint dxgi) {
        var failure = Assert.Throws<NotSupportedException>(
            () => DdsDecoder.Read(MinimalDds.Write(4, 4, dxgi, Counting(4 * 4 * 16)))
        );

        Assert.Contains("BC6H", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotADdsSaysSoRatherThanReadingGarbage() {
        var failure = Assert.Throws<InvalidDataException>(() => DdsDecoder.Read(Counting(200)));

        Assert.Contains("magic", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A header that promises more levels than follow it is a file something truncated, and
    ///     reading past the end of it would be the interesting kind of crash.
    /// </summary>
    [Fact]
    public void AMipChainThatStopsEarlyIsAnError() {
        var failure = Assert.Throws<InvalidDataException>(
            () => DdsDecoder.Read(MinimalDds.Write(4, 4, 98, Counting(16), mipCount: 3))
        );

        Assert.Contains("truncated", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeaderClaimingMoreLevelsThanTheExtentHasIsAnError() {
        var failure = Assert.Throws<InvalidDataException>(
            () => DdsDecoder.Read(MinimalDds.Write(4, 4, 98, Counting(16 * 8), mipCount: 8))
        );

        Assert.Contains("only has 3", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The whole round trip: an uncompressed DDS through the importer comes out as a KTX2 with a
    ///     mip chain the engine built, and the sRGB decision is the settings' — exactly as it is for
    ///     a PNG, because on this path the file has no say.
    /// </summary>
    [Theory]
    [InlineData(TextureContent.Colour, PixelFormat.Rgba8UNormSrgb)]
    [InlineData(TextureContent.Linear, PixelFormat.Rgba8UNorm)]
    public async Task AnUncompressedDdsLetsTheSettingsDecideTheTransferFunction(
        TextureContent content,
        PixelFormat expected
    ) {
        var result = await new TextureImporter().ImportAsync(
            Context(
                MinimalDds.Write(4, 4, 28, Counting(4 * 4 * 4)),
                new() { Content = content, Compression = TextureCompression.None }
            ),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.Succeeded);
        Assert.Equal(expected, Ktx2.Read(Assert.Single(result.Artifacts).Content.Span).Format);
    }

    /// <summary>
    ///     And when the file <i>does</i> have a say and disagrees with the usage, the build says so.
    ///     A linear BC7 used as colour is an albedo the sampler never converts, and the symptom is a
    ///     scene that looks washed out with nothing in the log — so there is now something in the log.
    /// </summary>
    [Theory]
    [InlineData(98u, TextureContent.Colour)]
    [InlineData(99u, TextureContent.Linear)]
    public async Task ACompressedSourceWhoseTransferFunctionContradictsItsUsageIsWarnedAbout(
        uint dxgi,
        TextureContent content
    ) {
        var result = await new TextureImporter().ImportAsync(
            Context(MinimalDds.Write(4, 4, dxgi, Counting(16)), new() { Content = content }),
            TestContext.Current.CancellationToken
        );

        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Warning);
        Assert.Contains("transfer function", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(98u, TextureContent.Linear)]
    [InlineData(99u, TextureContent.Colour)]
    public async Task AndWhenTheyAgreeThereIsNoWarning(uint dxgi, TextureContent content) {
        var result = await new TextureImporter().ImportAsync(
            Context(MinimalDds.Write(4, 4, dxgi, Counting(16)), new() { Content = content }),
            TestContext.Current.CancellationToken
        );

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Warning);
    }

    /// <summary>It reads its source and nothing else, which the enforcing provider is what would deny.</summary>
    [Fact]
    public async Task ItReadsItsSourceAndNothingElse() {
        var context = Context(MinimalDds.Write(4, 4, 98, Counting(16)));

        await new TextureImporter().ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal([Source], context.FileDependencies);
        Assert.Empty(context.AssetDependencies);
    }
}
