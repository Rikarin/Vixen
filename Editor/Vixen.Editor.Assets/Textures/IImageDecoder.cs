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

    /// <summary>Decodes a picture off the disk the way an editor that is not the file's only user must.</summary>
    /// <param name="decoder">The codec for <paramref name="extension" />.</param>
    /// <param name="path">The file, absolute.</param>
    /// <param name="extension">Its extension, for decoders that read more than one.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="IOException">The file would not open or read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a picture the codec can decode.</exception>
    /// <remarks>
    ///     <para>
    ///         <b><c>ThumbnailCache</c>'s two fixes (#1326, #1407), said once for every editor decode
    ///         site.</b> It began as the texturing plugin's internal <c>PictureFile</c> (#1432) and
    ///         moved here for <a href="https://github.com/Rikarin/Vixen/issues/1471">#1471</a>, because
    ///         the import panel's preview and the CLI's bake had the same two defects and could not
    ///         see a plugin's internals — and a plugin may not be a friend assembly, which is what
    ///         <c>ModuleReferenceTests</c> guards. This assembly is the one all three already reference.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The file is read whole and closed before the codec sees a byte, and it is opened
    ///         sharing writes and deletes.</b> <c>File.OpenRead</c> shares for reading only and its
    ///         handle lived for the length of the decode, so on Windows — where a share mode is enforced
    ///         and not advisory — an artist saving over a picture in their paint program while the
    ///         editor decoded it got a sharing violation from the paint program.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Whatever the codec throws comes out as <see cref="InvalidDataException" />.</b>
    ///         StbImageSharp answers a PNG whose header is cut short with
    ///         <c>InvalidOperationException("unknown image type")</c>, which a caller's filter of the
    ///         documented types does not name. The codec is third-party and reads bytes anybody could
    ///         have written, so the set it throws is not a documented one; translating at the codec —
    ///         rather than widening each caller to every exception — keeps a defect in the caller's own
    ///         code a crash, which it should be. The reading half keeps its own types, because
    ///         <see cref="IOException" /> and <see cref="UnauthorizedAccessException" /> are the ones a
    ///         caller says something different about.
    ///     </para>
    /// </remarks>
    public static TextureData DecodeFile(IImageDecoder decoder, string path, string extension) {
        ArgumentNullException.ThrowIfNull(decoder);

        using var bytes = ReadShared(path);

        try {
            return decoder.Decode(bytes, extension);
        } catch (Exception failure) when (failure is not InvalidDataException) {
            throw new InvalidDataException(failure.Message, failure);
        }
    }

    /// <summary>Copies a whole file into memory without standing in the way of anybody saving it.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its bytes, positioned at the start, with the file already closed.</returns>
    static MemoryStream ReadShared(string path) {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var bytes = new MemoryStream();

        file.CopyTo(bytes);
        bytes.Position = 0;

        return bytes;
    }
}
