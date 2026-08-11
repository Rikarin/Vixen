// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging;

/// <summary>An 8-bit RGBA image in memory.</summary>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="Pixels">Its pixels, row-major, four bytes each.</param>
/// <remarks>
///     <para>
///         Deliberately smaller than <see cref="TextureData" /> rather than a special case of it. A
///         texture carries a format, a mip chain, layers and faces because a GPU needs all four; a
///         picture somebody is going to look at has none of them, and the only two operations it
///         supports — encode it, compare it against another one — want a flat array and a stride.
///     </para>
///     <para>
///         One format and no format field, because the alternative is every consumer switching on
///         one. A frame that came off the device in another layout is converted on the way in, at
///         the one place that knows what the device gave it.
///     </para>
/// </remarks>
public readonly record struct Bitmap(int Width, int Height, byte[] Pixels) {
    /// <summary>The byte offset of a pixel.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <returns>Where it starts in <see cref="Pixels" />.</returns>
    public int Offset(int x, int y) => ((y * Width) + x) * 4;
}
