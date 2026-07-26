// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Mathematics;

/// <summary>
///     Conversion between sRGB-encoded and linear values — the one piece of colour handling that is
///     wrong in most renderers and invisible until it is pointed out.
/// </summary>
/// <remarks>
///     <para>
///         <b>The engine works in linear space.</b> Lighting, blending and filtering are only
///         correct on values proportional to radiance; sRGB encoding is a storage format that
///         devotes more of its 8 bits to the dark end, where the eye is more sensitive. Adding two
///         sRGB values is not adding two lights.
///     </para>
///     <para>
///         <b>These functions are for the cases the hardware does not cover.</b> A texture sampled
///         through an sRGB view format is decoded by the sampler, for free and with correct
///         filtering; doing it in a shader instead is both slower and wrong, because the filtering
///         then happens before the decode. What is left for these: colours typed by a human into an
///         inspector, colours parsed from hex, and anything crossing the boundary on the CPU.
///     </para>
///     <para>
///         The exact piecewise transfer function, not the <c>pow(x, 2.2)</c> approximation. The two
///         differ by up to about 1% in the darks, which is precisely where banding is visible, and
///         the exact form costs one comparison.
///     </para>
/// </remarks>
public static class ColorSpace {
    /// <summary>Decodes one sRGB-encoded channel to linear.</summary>
    /// <param name="value">The encoded value, nominally in <c>[0, 1]</c>.</param>
    /// <returns>The linear value.</returns>
    public static float SrgbToLinear(float value) =>
        value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    /// <summary>Encodes one linear channel as sRGB.</summary>
    /// <param name="value">The linear value, nominally in <c>[0, 1]</c>.</param>
    /// <returns>The encoded value.</returns>
    public static float LinearToSrgb(float value) =>
        value <= 0.0031308f ? value * 12.92f : (1.055f * MathF.Pow(value, 1f / 2.4f)) - 0.055f;

    /// <summary>
    ///     The relative luminance of a linear colour, using the Rec. 709 primaries that sRGB shares.
    /// </summary>
    /// <param name="linear">The linear colour.</param>
    /// <returns>The luminance.</returns>
    /// <remarks>
    ///     Only meaningful on linear values. Applying these weights to sRGB-encoded numbers — which
    ///     a great deal of image-processing code does — gives something that is neither luminance
    ///     nor lightness.
    /// </remarks>
    public static float Luminance(Vector3 linear) =>
        (0.2126f * linear.X) + (0.7152f * linear.Y) + (0.0722f * linear.Z);
}
