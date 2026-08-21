// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;

namespace Vixen.Editor.Assets.Textures;

/// <summary>Turns an authoring image format into pixels the engine can work with.</summary>
/// <remarks>
///     <para>
///         <b>This interface is the licence boundary, and it has already earned itself.</b> Doc 01
///         specified ImageSharp; ImageSharp 4.0.0 turned out to fail the build without a purchased
///         key, and swapping to StbImageSharp was one class rather than a change to the importer,
///         the pipeline or anything downstream. A codec is one implementation of this, not a fact
///         about the codebase.
///     </para>
///     <para>
///         Everything decodes to a <see cref="TextureData" /> because that is what the rest of the
///         pipeline already speaks. A decoder returns one mip level of one face; chains, cube faces
///         and compression are the importer's business.
///     </para>
/// </remarks>
public interface IImageDecoder {
    /// <summary>Which extensions it reads, lowercase and with their leading dots.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Decodes an image.</summary>
    /// <param name="stream">The file's bytes.</param>
    /// <param name="extension">Which extension it arrived as, for decoders that read more than one.</param>
    /// <returns>One level of one face, in an uncompressed format — or a compressed one if the source was already engine-ready.</returns>
    TextureData Decode(Stream stream, string extension);
}

/// <summary>The decoders an importer uses when nobody hands it a different set.</summary>
public static class ImageDecoders {
    /// <summary>Every decoder that ships.</summary>
    public static IReadOnlyList<IImageDecoder> BuiltIn { get; } =
        [new StbImageDecoder(), new Ktx2Decoder(), new DdsDecoder()];

    /// <summary>Finds the decoder for an extension.</summary>
    /// <param name="decoders">The set to look in.</param>
    /// <param name="extension">The extension, with its leading dot.</param>
    /// <returns>The decoder, or <see langword="null" /> if nothing reads that.</returns>
    public static IImageDecoder? For(IReadOnlyList<IImageDecoder> decoders, string extension) {
        ArgumentNullException.ThrowIfNull(decoders);

        var wanted = extension.ToLowerInvariant();

        foreach (var decoder in decoders) {
            if (decoder.Extensions.Contains(wanted, StringComparer.Ordinal)) {
                return decoder;
            }
        }

        return null;
    }
}
