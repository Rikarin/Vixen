// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.TextureGraph;

/// <summary>What one image in a <see cref="TexturePlan" /> stores.</summary>
/// <remarks>
///     <para>
///         <b>Seven, and 32-bit float is one of the choices since a height and a position need it.</b>
///         Doc 48 § M1 used to refuse it on the ground that a material map needing more than half-float
///         precision had a mistake upstream. A height field refutes that: a half has eleven bits of
///         mantissa, so a slope across a 2K map stair-steps and a normal derived from it shows the
///         terraces. The cost it named is real, an intermediate at 4K being 128 MiB as
///         <see cref="Rgba16Float" /> and 256 MiB as <see cref="Rgba32Float" />, which is why a graph
///         asks for it (<see cref="TextureGraphSettings.BitDepth" />) and sixteen stays the default.
///     </para>
///     <para>
///         ⚠ <b><see cref="R8" /> and <see cref="Rg8" /> can be read and cannot be written</b>, which
///         is a fact about the target rather than about this enum — see
///         <see cref="TextureFormats.IsStorable" />.
///     </para>
/// </remarks>
public enum TextureFormat : byte {
    /// <summary>One eight-bit channel: a mask, a height, a roughness.</summary>
    R8 = 1,

    /// <summary>Two eight-bit channels: a tangent-space normal with its third lane reconstructed.</summary>
    Rg8 = 2,

    /// <summary>Four eight-bit channels, linear. The ordinary colour intermediate.</summary>
    Rgba8 = 3,

    /// <summary>One half-float channel, for a height field a blur has to stay smooth in.</summary>
    R16Float = 4,

    /// <summary>Four half-float channels. The ordinary intermediate.</summary>
    Rgba16Float = 5,

    /// <summary>One 32-bit float channel, for a height field whose slopes have to survive derivation.</summary>
    R32Float = 6,

    /// <summary>Four 32-bit float channels, for a position. The widest thing a plan may ask for.</summary>
    Rgba32Float = 7
}

/// <summary>What each <see cref="TextureFormat" /> is to a device, to Raven and to a reader.</summary>
public static class TextureFormats {
    /// <summary>The device format an image of this kind is created as.</summary>
    /// <param name="format">The plan's format.</param>
    /// <returns>The pixel format.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format" /> is not one of the seven.</exception>
    public static PixelFormat Pixel(TextureFormat format) =>
        format switch {
            TextureFormat.R8 => PixelFormat.R8UNorm,
            TextureFormat.Rg8 => PixelFormat.Rg8UNorm,
            TextureFormat.Rgba8 => PixelFormat.Rgba8UNorm,
            TextureFormat.R16Float => PixelFormat.R16Float,
            TextureFormat.Rgba16Float => PixelFormat.Rgba16Float,
            TextureFormat.R32Float => PixelFormat.R32Float,
            TextureFormat.Rgba32Float => PixelFormat.Rgba32Float,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    /// <summary>How many bytes one texel takes.</summary>
    /// <param name="format">The plan's format.</param>
    /// <returns>The size in bytes.</returns>
    public static int BytesPerTexel(TextureFormat format) => Pixel(format).BlockSize();

    /// <summary>Whether a kernel may write to an image of this kind.</summary>
    /// <param name="format">The plan's format.</param>
    /// <returns><see langword="true" /> when a storage image can be declared for it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>False for <see cref="TextureFormat.R8" /> and <see cref="TextureFormat.Rg8" />,
    ///         and this refutes a line in doc 48 § M1 and in issue #566.</b> Both list R8 and RG8
    ///         beside the other three as though the five were interchangeable. They are not:
    ///         <c>Raven/Vixen.Raven/Symbols/ImageFormats.cs</c> admits sixteen storage-image formats
    ///         and neither <c>r8</c> nor <c>rg8</c> is among them — and that table is not an
    ///         oversight, because Vulkan's list of formats an implementation <em>must</em> support
    ///         for <c>STORAGE_IMAGE</c> does not contain <c>R8_UNORM</c> or <c>R8G8_UNORM</c> either.
    ///         So a kernel writing one would be a kernel that fails to create a pipeline on a
    ///         conformant device.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>But the five that are storable are not all mandatory either, and
    ///         <see cref="TextureFormat.R16Float" /> is the one that is not.</b> Vulkan's required
    ///         storage set covers <c>rgba8</c>, <c>rgba16f</c>, <c>r32f</c> and <c>rgba32f</c>; <c>r16f</c> is in the
    ///         <em>extended</em> list, so a plan computing in <c>R16Float</c> — which is what a
    ///         height field wants — needs <c>shaderStorageImageExtendedFormats</c>. It is set on
    ///         every adapter this engine has been developed on, which is exactly why nothing said so
    ///         until #714. Raven declares SPIR-V's <c>StorageImageExtendedFormats</c> for it now, so
    ///         a device without the feature refuses the module rather than being undefined.
    ///     </para>
    ///     <para>
    ///         <b>Reading one is fine</b>, which is why they stay in the enum rather than being
    ///         deleted: an imported bitmap is sampled, and <c>Load</c> hands back
    ///         <c>(r, 0, 0, 1)</c> whatever the storage was. A plan may therefore take an R8 map in
    ///         and must compute in one of the five storable formats.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A 32-bit image need not be filterable, and that costs nothing here.</b> Vulkan does
    ///         not require linear filtering of <c>r32f</c> or <c>rgba32f</c>, and the evaluator binds no
    ///         samplers: every kernel reads with <c>Load</c>.
    ///     </para>
    /// </remarks>
    public static bool IsStorable(TextureFormat format) =>
        format is TextureFormat.Rgba8
            or TextureFormat.R16Float
            or TextureFormat.Rgba16Float
            or TextureFormat.R32Float
            or TextureFormat.Rgba32Float;

    /// <summary>The <c>[Format("…")]</c> string a kernel writing this image declares.</summary>
    /// <param name="format">The plan's format.</param>
    /// <returns>The Raven layout name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format cannot be written — see <see cref="IsStorable" />.</exception>
    public static string RavenName(TextureFormat format) =>
        format switch {
            TextureFormat.Rgba8 => "rgba8",
            TextureFormat.R16Float => "r16f",
            TextureFormat.Rgba16Float => "rgba16f",
            TextureFormat.R32Float => "r32f",
            TextureFormat.Rgba32Float => "rgba32f",
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "Raven declares no storage image of this format, so no kernel can write one."
            )
        };

    /// <summary>Every format a kernel can write, which is what its variants are built for.</summary>
    public static IReadOnlyList<TextureFormat> Storable { get; } =
        [
            TextureFormat.Rgba8,
            TextureFormat.R16Float,
            TextureFormat.Rgba16Float,
            TextureFormat.R32Float,
            TextureFormat.Rgba32Float
        ];

    /// <summary>The storage one number of channels is kept in, at a bit depth.</summary>
    /// <param name="channels">Grey or colour.</param>
    /// <param name="bitDepth">Sixteen or thirty-two, as <see cref="TextureGraphSettings.BitDepthOf" /> answers.</param>
    /// <returns>The format.</returns>
    internal static TextureFormat Of(TextureChannels channels, int bitDepth) =>
        (channels, bitDepth) switch {
            (TextureChannels.Grey, 32) => TextureFormat.R32Float,
            (_, 32) => TextureFormat.Rgba32Float,
            (TextureChannels.Grey, _) => TextureFormat.R16Float,
            _ => TextureFormat.Rgba16Float
        };

    /// <summary>How many channels an image of this format holds, as the compiler counts them.</summary>
    /// <param name="format">The plan's format.</param>
    /// <returns>Grey for one channel, and colour for any other.</returns>
    internal static TextureChannels ChannelsOf(TextureFormat format) =>
        format is TextureFormat.R8 or TextureFormat.R16Float or TextureFormat.R32Float
            ? TextureChannels.Grey
            : TextureChannels.Colour;
}
