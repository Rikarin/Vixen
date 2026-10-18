// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Textures;

namespace Vixen.Editor.Texturing;

/// <summary>Decodes a picture off the disk the way an editor that is not the file's only user must.</summary>
/// <remarks>
///     <para>
///         <b>The texturing half of <a href="https://github.com/Rikarin/Vixen/issues/1432">#1432</a>,
///         and it is <c>ThumbnailCache</c>'s two fixes (#1326, #1407) said once for both of this
///         plugin's decode sites</b> — <see cref="Painting.PaintAlphaSource" /> and
///         <see cref="TextureExternalImages" />, which had each copied the read before either fix
///         landed.
///     </para>
///     <para>
///         ⚠ <b>The file is read whole and closed before the codec sees a byte, and it is opened
///         sharing writes and deletes.</b> <c>File.OpenRead</c> shares for reading only and its
///         handle lived for the length of the decode, so on Windows — where a share mode is enforced
///         and not advisory — an artist saving over a brush alpha or a fill's picture in their paint
///         program while the editor decoded it got a sharing violation from the paint program.
///     </para>
///     <para>
///         ⚠ <b>Whatever the codec throws comes out as <see cref="InvalidDataException" />.</b>
///         StbImageSharp answers a PNG whose header is cut short with
///         <c>InvalidOperationException("unknown image type")</c>, which neither caller's filter
///         named, so a truncated file threw out of a panel's text box and out of a preview's fill.
///         The codec is third-party and reads bytes anybody could have written, so the set it throws
///         is not a documented one; translating at the codec — rather than widening each caller to
///         every exception — keeps a defect in the caller's own code a crash, which it should be.
///         The reading half keeps its own types, because <see cref="IOException" /> and
///         <see cref="UnauthorizedAccessException" /> are the ones a caller says something
///         different about.
///     </para>
/// </remarks>
static class PictureFile {
    /// <summary>Reads a file into memory and decodes it.</summary>
    /// <param name="decoder">The codec for <paramref name="extension" />.</param>
    /// <param name="path">The file, absolute.</param>
    /// <param name="extension">Its extension, for decoders that read more than one.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="IOException">The file would not open or read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    /// <exception cref="InvalidDataException">The bytes are not a picture the codec can decode.</exception>
    public static TextureData Decode(IImageDecoder decoder, string path, string extension) {
        ArgumentNullException.ThrowIfNull(decoder);

        using var bytes = Read(path);

        try {
            return decoder.Decode(bytes, extension);
        } catch (Exception failure) when (failure is not InvalidDataException) {
            throw new InvalidDataException(failure.Message, failure);
        }
    }

    /// <summary>Copies a whole file into memory without standing in the way of anybody saving it.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its bytes, positioned at the start, with the file already closed.</returns>
    static MemoryStream Read(string path) {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var bytes = new MemoryStream();

        file.CopyTo(bytes);
        bytes.Position = 0;

        return bytes;
    }
}
