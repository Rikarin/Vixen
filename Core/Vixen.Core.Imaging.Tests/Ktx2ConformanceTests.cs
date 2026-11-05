// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>What Khronos's own validator makes of the files <see cref="Ktx2.Write" /> produces.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every file this suite writes was rejected the first time it ran.</b> Twenty-two of
///         twenty-two, on five distinct defects, every one of which the hand-computed fixtures in
///         <see cref="Ktx2Tests" /> had been agreeing with since they were written — because those
///         fixtures encode the same reading of the specification as the code. That is the whole
///         argument for this suite existing, and it is worth stating as a result rather than as a
///         methodology note.
///     </para>
///     <para>
///         The suite writes one file per format and per container shape — mip chains, array layers,
///         cube maps, an array of cube maps, a 3D texture — and asks <c>ktx validate</c> about each,
///         with <c>--warnings-as-errors</c>. Nothing here asserts a byte; the assertion is that
///         somebody else's parser is happy, which is the one thing the rest of this assembly cannot
///         say.
///     </para>
///     <para>
///         <b>The corpus is the shapes the engine can produce, not the shapes the format allows.</b>
///         Its worth depends on that staying true, which is why the shape that was missing when it
///         was first written is called out in <c>Shaped</c>: <c>Vixen.Editor.Assets</c>'s
///         <c>CubeLut</c> builds a 3D <c>Rgba16Float</c> texture and nothing was validating one. A
///         format or a shape added to the engine and not to this list is a hole, and the count
///         assertion below is what makes adding one deliberate.
///     </para>
///     <para>
///         <b>Supercompression is not covered because there is none.</b> <see cref="Ktx2.Write" />
///         writes <c>supercompressionScheme</c> as zero and refuses anything else on read; a build
///         that wants smaller bundles compresses the chunk the texture lives in. There is nothing to
///         validate.
///     </para>
/// </remarks>
public sealed class Ktx2ConformanceTests {
    /// <summary>Every format <see cref="VkFormats" /> knows a number for, in a plain 2D texture.</summary>
    /// <remarks>
    ///     ETC2 and ASTC are here even though <see cref="BlockCompression.BlockCompressor" /> cannot
    ///     produce their payloads. The container does not care what the block bytes mean, and two of
    ///     the three wrong <c>VkFormat</c> numbers this suite found were ETC2's — the formats nothing
    ///     writes are exactly the ones a round-trip test cannot check.
    /// </remarks>
    public static TheoryData<PixelFormat> Formats {
        get {
            var data = new TheoryData<PixelFormat>();

            foreach (var format in VkFormats.Supported) {
                data.Add(format);
            }

            return data;
        }
    }

    /// <summary>The container shapes, on the one format everything supports.</summary>
    public static TheoryData<string> Shapes => [
        "mips", "layers", "cube", "cube-array", "cube-mips", "block-mips", "single-texel",
        "non-square", "volume", "r16-mips", "rg16-mips", "rgba16-mips", "half-mips", "float-mips", "bc6h-signed-mips",
        "bc7-srgb-mips", "bc1-srgb-tiny", "bc3-srgb-odd"
    ];

    /// <summary>
    ///     ⚠ The guard against a suite that quietly checked nothing. A filter that matches no case,
    ///     or an enum entry added without a number, changes this count and fails here rather than
    ///     printing a green run over an empty corpus.
    /// </summary>
    [Fact]
    public void TheCorpusIsTheSizeItIsMeantToBe() {
        Assert.Equal(31, Formats.Count);
        Assert.Equal(18, Shapes.Count);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void KhronosAcceptsEveryFormatWeCanName(PixelFormat format) =>
        Validate($"format-{format}", Filled(new(format, 64, 64, levelCount: 1)));

    [Theory]
    [MemberData(nameof(Shapes))]
    public void KhronosAcceptsEveryContainerShapeWeCanWrite(string shape) =>
        Validate($"shape-{shape}", Filled(Shaped(shape)));

    static TextureData Shaped(string shape) =>
        shape switch {
            "mips" => new(PixelFormat.Rgba8UNorm, 64, 64),
            "layers" => new(PixelFormat.Rgba8UNorm, 64, 64, levelCount: 1, layerCount: 4),
            "cube" => new(PixelFormat.Rgba8UNorm, 64, 64, levelCount: 1, faceCount: 6),
            "cube-array" => new(PixelFormat.Rgba8UNorm, 64, 64, levelCount: 1, layerCount: 3, faceCount: 6),
            "cube-mips" => new(PixelFormat.Rgba8UNorm, 64, 64, faceCount: 6),
            // The shape that found the padding defect: a 1-byte-per-texel chain whose smallest
            // levels are 1, 4 and 16 bytes long, so every level after the first needs padding.
            "block-mips" => new(PixelFormat.R8UNorm, 64, 64),
            "single-texel" => new(PixelFormat.Rgba8UNorm, 1, 1, levelCount: 1),
            "non-square" => new(PixelFormat.Bc7RgbaUNorm, 64, 16, levelCount: 3),
            // The one shape a real importer writes that the first version of this corpus missed:
            // CubeLut.Parse builds exactly this, and pixelDepth is the header field with the
            // awkward rule — zero for a texture that is not 3D, and the extent when it is.
            "volume" => new(PixelFormat.Rgba16Float, 8, 8, levelCount: 1, depth: 8),
            // What a sixteen-bit or float export produces, and every BC form's padding at the small
            // end: the levels that are one block whatever their extent.
            "r16-mips" => new(PixelFormat.R16UNorm, 37, 13),
            "rg16-mips" => new(PixelFormat.Rg16UNorm, 64, 64),
            "rgba16-mips" => new(PixelFormat.Rgba16UNorm, 64, 32),
            "half-mips" => new(PixelFormat.Rgba16Float, 64, 64),
            "float-mips" => new(PixelFormat.Rgba32Float, 32, 64),
            "bc6h-signed-mips" => new(PixelFormat.Bc6HRgbSFloat, 64, 64),
            "bc7-srgb-mips" => new(PixelFormat.Bc7RgbaUNormSrgb, 37, 13),
            "bc1-srgb-tiny" => new(PixelFormat.Bc1RgbaUNormSrgb, 3, 1),
            "bc3-srgb-odd" => new(PixelFormat.Bc3RgbaUNormSrgb, 5, 9),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "No such shape.")
        };

    /// <summary>
    ///     The whole export path into one file: a sixteen-bit source, a chain, an encode at each BC
    ///     target, and the validator on the result. The theories above fill a container with noise;
    ///     this is the same container around blocks that mean something.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bc1RgbaUNormSrgb)]
    [InlineData(PixelFormat.Bc3RgbaUNormSrgb)]
    [InlineData(PixelFormat.Bc4RUNorm)]
    [InlineData(PixelFormat.Bc5RgUNorm)]
    [InlineData(PixelFormat.Bc7RgbaUNorm)]
    [InlineData(PixelFormat.Bc7RgbaUNormSrgb)]
    public void KhronosAcceptsAChainEncodedFromSixteenBits(PixelFormat target) {
        var source = new TextureData(PixelFormat.Rgba16UNorm, 37, 21);

        new Random(37).NextBytes(source.LevelSpan(0));
        MipChain.Generate(source, target.IsSrgb() ? MipOptions.Colour : MipOptions.Linear);

        var encoded = BlockCompression.BlockCompressor.Encode(
            source,
            target,
            new() { Quality = BlockCompression.BlockQuality.Fast, SourceIsSrgbEncoded = target.IsSrgb() }
        );

        Validate($"chain-{target}", encoded);
    }

    [Theory]
    [InlineData(PixelFormat.Bc6HRgbUFloat)]
    [InlineData(PixelFormat.Bc6HRgbSFloat)]
    public void KhronosAcceptsAFloatChainEncodedAsBc6H(PixelFormat target) {
        var source = new TextureData(PixelFormat.Rgba16Float, 32, 32);

        ReferenceImages.HighDynamicRange(32).Pixels.CopyTo(source.LevelSpan(0));
        MipChain.Generate(source);

        Validate($"chain-{target}", BlockCompression.BlockCompressor.Encode(source, target));
    }

    static TextureData Filled(TextureData texture) {
        var pixels = texture.PixelSpan();

        for (var i = 0; i < pixels.Length; i++) {
            pixels[i] = (byte)(((i * 37) + 3) & 0xFF);
        }

        return texture;
    }

    static void Validate(string name, TextureData texture) {
        if (ExternalTools.KtxTool is not { } ktx) {
            ExternalTools.Missing("the Khronos ktx tool", "Install KTX-Software: brew install ktx.");

            return;
        }

        var directory = ExternalTools.Scratch(name);
        var path = Path.Combine(directory, name + ".ktx2");

        File.WriteAllBytes(path, Ktx2.Write(texture));

        var (exitCode, output) = ExternalTools.Run(ktx, "validate", "--warnings-as-errors", path);

        Assert.True(exitCode == 0, $"ktx validate rejected {name}:{Environment.NewLine}{output}");

        Directory.Delete(directory, true);
    }
}
