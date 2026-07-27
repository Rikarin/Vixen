// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Textures;

/// <summary>What a texture's bytes mean, which is the one thing a file cannot say for itself.</summary>
/// <remarks>
///     <para>
///         A normal map, an albedo map and a packed roughness-metalness mask are all four channels of
///         eight-bit RGBA in a PNG. Nothing in the file distinguishes them, and every decision
///         downstream depends on which one it is: whether to apply a transfer function, whether to
///         renormalise, and which compressed format holds it without ruining it.
///     </para>
///     <para>
///         Called content rather than usage because <see cref="Vixen.Graphics.TextureUsage" /> is
///         already the GPU's word for how a texture will be bound, and the two would sit in the same
///         file constantly.
///     </para>
/// </remarks>
public enum TextureContent {
    /// <summary>Colour, seen by a person. sRGB in the file and in the sampler, filtered in linear light.</summary>
    Colour,

    /// <summary>
    ///     Data, read by a shader — roughness, metalness, occlusion, a packed mask. No transfer
    ///     function anywhere: applying one to a roughness map bends the whole material response.
    /// </summary>
    Linear,

    /// <summary>A tangent-space normal map. Linear, and its mips are averaged as directions.</summary>
    NormalMap
}

/// <summary>Which compressed format to ship a texture in.</summary>
public enum TextureCompression {
    /// <summary>Pick from the content. See <see cref="TextureImporter" /> for what it picks and why.</summary>
    Automatic,

    /// <summary>None. Four bytes a texel, and the right answer for a small UI atlas or a lookup table.</summary>
    None,

    /// <summary>BC1: four bits a texel, one bit of alpha. The cheapest thing that is still colour.</summary>
    Bc1,

    /// <summary>BC3: eight bits a texel, with a real alpha channel.</summary>
    Bc3,

    /// <summary>BC4: one channel. A mask, an occlusion map, a height field.</summary>
    Bc4,

    /// <summary>BC5: two channels, which is what a tangent-space normal map needs.</summary>
    Bc5,

    /// <summary>BC7: eight bits a texel and the best quality BC offers for colour.</summary>
    Bc7
}

/// <summary>How one texture is imported.</summary>
/// <remarks>
///     Every field here can be overridden per build target by the <c>.meta</c> file's target section,
///     which is how one texture ships uncompressed on a phone and as BC7 on a desktop without two
///     copies of the source existing.
/// </remarks>
[DataContract("TextureImporter")]
public sealed record TextureImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;

    /// <summary>What the bytes mean.</summary>
    public TextureContent Content { get; init; } = TextureContent.Colour;

    /// <summary>Which compressed format to ship in.</summary>
    public TextureCompression Compression { get; init; } = TextureCompression.Automatic;

    /// <summary>Whether to build a mip chain. Off costs bandwidth and aliases; on costs a third more memory.</summary>
    public bool GenerateMips { get; init; } = true;

    /// <summary>
    ///     Whether the alpha channel is transparency rather than packed data. When it is, the mip
    ///     filter weights colour by it, which is what stops a cut-out's edge bleeding towards
    ///     whatever was painted under the transparent part.
    /// </summary>
    /// <remarks>
    ///     Only consulted for <see cref="TextureContent.Colour" />: "alpha is transparency" is not a
    ///     statement that means anything about a packed mask, and weighting a mask's channels by one
    ///     of themselves would be a strange thing to do quietly.
    /// </remarks>
    public bool AlphaIsTransparency { get; init; } = true;

    /// <summary>The largest the texture may ship at, or zero for no limit.</summary>
    /// <remarks>
    ///     Halving, not resampling: a texture over the limit is reduced through the same filter that
    ///     builds its mip chain until it fits. That means a 2048 with a limit of 1000 ships at 512
    ///     rather than at 1000, which is the behaviour every engine has and is worth knowing about.
    /// </remarks>
    public int MaxSize { get; init; }
}
