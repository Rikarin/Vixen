// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using StbImageSharp;
using Vixen.Core.Imaging;
using Vixen.Graphics;

namespace Vixen.Editor.Assets.Textures;

/// <summary>Reads the authoring formats artists actually save.</summary>
/// <remarks>
///     <para>
///         <b>This is not the ImageSharp doc 01 specified, and the reason is in
///         <c>Directory.Packages.props</c>:</b> ImageSharp 4.0.0 fails the build without a purchased
///         licence key, which an Apache-2.0 engine cannot ask of a contributor who wants to compile
///         the editor. Doc 01 already named StbImageSharp as the fallback and this is it — public
///         domain, and covering more of doc 08's importer table than ImageSharp did, because it
///         reads Radiance HDR.
///     </para>
///     <para>
///         <b>HDR goes to float and everything else to eight-bit RGBA.</b> A Radiance file holds
///         radiance, which has no upper bound, and narrowing it to a byte at the front door would
///         discard exactly the range an environment map exists for — the sun being ten thousand
///         times the sky is the whole content of the image.
///     </para>
///     <para>
///         <b>TGA's origin bit is honoured, and that is not a detail.</b> Bit 5 of a TGA's image
///         descriptor says whether row zero is the top or the bottom, both are legal, and a decoder
///         that ignores it reads half the world's TGAs upside down — where a flipped albedo and a
///         flipped normal map both render <i>plausibly</i>. StbImageSharp gets it right; nothing
///         asserted that until <c>TgaOrientationTests</c>, which builds the same picture both ways
///         up from the format and requires the same pixels back.
///     </para>
///     <para>
///         <b>Not read here:</b> <c>.exr</c>, <c>.tif</c> and <c>.webp</c>, which doc 08's table
///         asks for and which have no licence-clean managed reader worth adding on spec.
///         <c>.dds</c> is read, by <see cref="DdsDecoder" /> — doc 01 named Pfim for it, and it
///         turned out to need a header parser rather than a codec.
///     </para>
/// </remarks>
public sealed class StbImageDecoder : IImageDecoder {
    /// <summary>Radiance HDR, the one format here that carries more range than a byte.</summary>
    public const string HighDynamicRangeExtension = ".hdr";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".psd", ".gif", HighDynamicRangeExtension
    ];

    /// <inheritdoc />
    public TextureData Decode(Stream stream, string extension) {
        ArgumentNullException.ThrowIfNull(stream);

        return extension.Equals(HighDynamicRangeExtension, StringComparison.OrdinalIgnoreCase)
            ? DecodeFloat(stream)
            : DecodeBytes(stream);
    }

    static TextureData DecodeBytes(Stream stream) {
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha)
            ?? throw new InvalidDataException("The decoder read the file and produced nothing.");

        var texture = new TextureData(PixelFormat.Rgba8UNorm, image.Width, image.Height, levelCount: 1);
        image.Data.AsSpan(0, texture.ByteLength).CopyTo(texture.PixelSpan());

        return texture;
    }

    static TextureData DecodeFloat(Stream stream) {
        var image = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha)
            ?? throw new InvalidDataException("The decoder read the file and produced nothing.");

        var texture = new TextureData(PixelFormat.Rgba32Float, image.Width, image.Height, levelCount: 1);
        var pixels = texture.PixelSpan();

        for (var index = 0; index < image.Data.Length; index++) {
            BinaryPrimitives.WriteSingleLittleEndian(pixels[(index * 4)..], image.Data[index]);
        }

        return texture;
    }
}

/// <summary>Reads a KTX2 file, which is already what the engine ships.</summary>
/// <remarks>
///     An artist handing the pipeline a <c>.ktx2</c> has usually done the compression themselves,
///     with a tool that does it better than
///     <see cref="Vixen.Core.Imaging.BlockCompression.BlockCompressor" /> can. The importer passes a
///     compressed one straight through rather than decoding and re-encoding it, because a second
///     round of lossy compression only ever loses.
/// </remarks>
public sealed class Ktx2Decoder : IImageDecoder {
    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".ktx2"];

    /// <inheritdoc />
    public TextureData Decode(Stream stream, string extension) {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return Ktx2.Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }
}
