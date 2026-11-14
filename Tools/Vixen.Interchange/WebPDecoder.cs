// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics;

namespace Vixen.Interchange;

/// <summary>Reads a lossless WebP through <see cref="WebPReader" />, as an importer reads any other picture.</summary>
/// <remarks>
///     Eight bits a lane, as the format holds, so <c>Rgba8UNorm</c>. A file that says it has no alpha is opaque, as
///     <see cref="StbImageDecoder" /> answers for a PNG without one. A lossy or animated WebP is refused with what it is.
/// </remarks>
public sealed class WebPDecoder : IImageDecoder {
    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [".webp"];

    /// <inheritdoc />
    public TextureData Decode(Stream stream, string extension) {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var (width, height, alpha, rgba) = WebPReader.DecodeRgba8(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        for (var at = 3; !alpha && at < rgba.Length; at += 4) {
            rgba[at] = byte.MaxValue;
        }

        var texture = new TextureData(PixelFormat.Rgba8UNorm, width, height, 1);
        rgba.CopyTo(texture.PixelSpan());
        return texture;
    }
}
