// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics;

namespace Vixen.TextureGraph;

/// <summary>One image of a bake, exactly as it is on the device, and what its bytes are.</summary>
/// <param name="Width">Its width in texels.</param>
/// <param name="Height">Its height in texels.</param>
/// <param name="Format">What each texel is.</param>
/// <param name="Texels">The bytes, tightly packed, top row first.</param>
/// <remarks>
///     What an export starts from. <see cref="ToTexture" /> is the step into
///     <see cref="MipChain" />, <see cref="Vixen.Core.Imaging.BlockCompression.BlockCompressor" />,
///     <see cref="Ktx2" /> and <see cref="DdsCodec" />, none of which need to know a texture graph
///     made it.
/// </remarks>
public sealed record TextureRawImage(int Width, int Height, PixelFormat Format, byte[] Texels) {
    /// <summary>Copies the image into level zero of a texture.</summary>
    /// <param name="levelCount">How many levels to allocate: one, or zero for a full chain to generate.</param>
    /// <returns>The texture, with anything below level zero still empty.</returns>
    public TextureData ToTexture(int levelCount = 1) {
        var texture = new TextureData(Format, Width, Height, levelCount);

        Texels.CopyTo(texture.LevelSpan(0));

        return texture;
    }
}
