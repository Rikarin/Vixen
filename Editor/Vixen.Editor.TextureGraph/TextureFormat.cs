// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Editor.TextureGraph;

/// <summary>What one image in a <see cref="TexturePlan" /> stores.</summary>
/// <remarks>
///     <para>
///         Five, and <b>32-bit float is deliberately not one of them</b> — doc 48 § M1. A texture
///         graph produces material maps, and a material map that needs more than half-float precision
///         is a map whose author has made a mistake somewhere upstream. The saving is not small: an
///         intermediate at 4K is 16 MB as <see cref="Rgba16Float" /> and 32 MB as four 32-bit floats,
///         and a plan holds several of them at once.
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

    /// <summary>Four half-float channels. The widest thing a plan may ask for.</summary>
    Rgba16Float = 5
}

/// <summary>What each <see cref="TextureFormat" /> is to a device, to Raven and to a reader.</summary>
public static class TextureFormats {
    /// <summary>The device format an image of this kind is created as.</summary>
    /// <param name="format">The plan's format.</param>
    /// <returns>The pixel format.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format" /> is not one of the five.</exception>
    public static PixelFormat Pixel(TextureFormat format) =>
        format switch {
            TextureFormat.R8 => PixelFormat.R8UNorm,
            TextureFormat.Rg8 => PixelFormat.Rg8UNorm,
            TextureFormat.Rgba8 => PixelFormat.Rgba8UNorm,
            TextureFormat.R16Float => PixelFormat.R16Float,
            TextureFormat.Rgba16Float => PixelFormat.Rgba16Float,
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
    ///         <b>Reading one is fine</b>, which is why they stay in the enum rather than being
    ///         deleted: an imported bitmap is sampled, and <c>Load</c> hands back
    ///         <c>(r, 0, 0, 1)</c> whatever the storage was. A plan may therefore take an R8 map in
    ///         and must compute in one of the three storable formats.
    ///     </para>
    /// </remarks>
    public static bool IsStorable(TextureFormat format) =>
        format is TextureFormat.Rgba8 or TextureFormat.R16Float or TextureFormat.Rgba16Float;

    /// <summary>The <c>[Format("…")]</c> string a kernel writing this image declares.</summary>
    /// <param name="format">The plan's format.</param>
    /// <returns>The Raven layout name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The format cannot be written — see <see cref="IsStorable" />.</exception>
    public static string RavenName(TextureFormat format) =>
        format switch {
            TextureFormat.Rgba8 => "rgba8",
            TextureFormat.R16Float => "r16f",
            TextureFormat.Rgba16Float => "rgba16f",
            _ => throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "Raven declares no storage image of this format, so no kernel can write one."
            )
        };

    /// <summary>Every format a kernel can write, which is what its variants are built for.</summary>
    public static IReadOnlyList<TextureFormat> Storable { get; } =
        [TextureFormat.Rgba8, TextureFormat.R16Float, TextureFormat.Rgba16Float];
}
