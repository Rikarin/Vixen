// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Rendering;

/// <summary>How a composited group's result is mixed with the picture already under it.</summary>
/// <remarks>
///     <para>
///         CSS Compositing 1 § 5.1's sixteen modes, in the specification's own order, which is also
///         the order <c>UtilityFamilies</c> registers the classes in.
///     </para>
///     <para>
///         ⚠ <b><see cref="Normal" /> is zero and is the only value that changes nothing</b>, which is
///         the bargain <see cref="UiLayer.Blur" /> states for its own default: a consumer that ignores
///         this composites the group source-over, which is the picture the frame would have had
///         without the declaration rather than a wrong one.
///     </para>
/// </remarks>
public enum UiBlendMode {
    /// <summary>Source-over: the group replaces what is under it, in proportion to its alpha.</summary>
    Normal,

    /// <summary>The product. Darkens, and white is the identity.</summary>
    Multiply,

    /// <summary>The complement of the product of the complements. Lightens, and black is the identity.</summary>
    Screen,

    /// <summary><see cref="HardLight" /> with the operands the other way round.</summary>
    Overlay,

    /// <summary>The darker of the two, per channel.</summary>
    Darken,

    /// <summary>The lighter of the two, per channel.</summary>
    Lighten,

    /// <summary>Brightens the backdrop to reflect the source.</summary>
    ColorDodge,

    /// <summary>Darkens the backdrop to reflect the source.</summary>
    ColorBurn,

    /// <summary><see cref="Multiply" /> below a half and <see cref="Screen" /> above it.</summary>
    HardLight,

    /// <summary><see cref="HardLight" />'s shape with a softer knee, so it cannot clip to black or white.</summary>
    SoftLight,

    /// <summary>The absolute difference.</summary>
    Difference,

    /// <summary><see cref="Difference" /> with a lower contrast.</summary>
    Exclusion,

    /// <summary>The source's hue over the backdrop's saturation and luminosity.</summary>
    Hue,

    /// <summary>The source's saturation over the backdrop's hue and luminosity.</summary>
    Saturation,

    /// <summary>The source's hue and saturation over the backdrop's luminosity.</summary>
    Color,

    /// <summary>The source's luminosity over the backdrop's hue and saturation.</summary>
    Luminosity
}

/// <summary>CSS Compositing 1 § 5's blend functions, over premultiplied colour.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A blend is the one group-wide effect that is a function of <i>both</i> operands, and
///         that is what separates it from every other field on <see cref="UiLayer" />.</b> Fading a
///         surface in, blurring it, tinting it by a matrix or multiplying its coverage by a mask are
///         all functions of the surface alone, so an executor can produce the composite's colour
///         without ever looking at what it is landing on. A blend cannot: it needs the backdrop.
///         <c>SoftwareUiRasterizer</c> gets that for free, because it owns the destination buffer.
///     </para>
///     <para>
///         ⚠ <b>The whole feature is nevertheless expressible as a change of <i>source</i> colour
///         followed by an ordinary source-over</b> — which is the arrangement CSS itself specifies in
///         § 5.1, and the reason no executor needs a second blend state. <see cref="Apply" /> returns
///         the premultiplied colour to composite normally, so the one line in a rasteriser that reads
///         the destination is the only line that changes. On a device the same identity says the
///         backdrop may arrive as a <i>texture</i> rather than as a framebuffer read — the capture
///         <c>UiRenderer.Capture</c> already performs for <c>backdrop-filter</c> is exactly the
///         picture this function's <c>backdrop</c> argument wants.
///     </para>
///     <para>
///         ⚠ <b>The arithmetic is done on the values the surface holds, which in this engine are
///         linear, and CSS blends in the device's colour space, which in a browser is sRGB. That is a
///         stated divergence rather than an oversight.</b> <c>multiply</c> of two mid-greys is
///         perceptibly darker in linear than in sRGB. Converting here would mean encoding and
///         decoding a transfer function per pixel in the composite, on both executors, and writing
///         back into an attachment that is linear — which is a colour-management decision for the
///         whole interface rather than for this function. Recorded in
///         <c>docs/guide/ui/compositing.md</c>.
///     </para>
/// </remarks>
public static class UiBlend {
    /// <summary>Rec. 601's luma weights, which CSS Compositing 1 § 5.3 names <c>Lum</c>.</summary>
    /// <remarks>
    ///     ⚠ Not Rec. 709's, and not this engine's photometric luminance. The four non-separable modes
    ///     are defined against these three constants exactly; substituting a "better" luma makes
    ///     <c>mix-blend-luminosity</c> a different picture from every other implementation of it.
    /// </remarks>
    const float LumR = 0.3f;

    const float LumG = 0.59f;
    const float LumB = 0.11f;

    /// <summary>The premultiplied source colour to composite source-over, once a mode is applied.</summary>
    /// <param name="mode">Which blend function.</param>
    /// <param name="source">The group's own colour at this pixel, premultiplied.</param>
    /// <param name="backdrop">What is already at this pixel, premultiplied.</param>
    /// <returns>
    ///     A premultiplied colour with <paramref name="source" />'s alpha, to be composited
    ///     source-over exactly as an unblended group's would be.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A transparent backdrop returns the source unchanged, and that is the specification
    ///         rather than a guard against dividing by zero.</b> § 5.1 weights the blend by the
    ///         backdrop's alpha — <c>(1 − αb)·Cs + αb·B(Cb, Cs)</c> — so where nothing is behind the
    ///         group there is nothing to mix with and every mode degenerates to <c>normal</c>. The
    ///         early return is that identity, and it happens to make the un-premultiply safe.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both operands are un-premultiplied and clamped to [0, 1] before the function
    ///         runs.</b> Every formula in § 5.1 is written for straight alpha, and several of them —
    ///         <c>color-dodge</c>, <c>color-burn</c>, <c>soft-light</c> — are only defined on the unit
    ///         interval and produce nonsense outside it. <see cref="Color4" /> is deliberately
    ///         unbounded above, so an interface drawn in an HDR frame can hand this a component past
    ///         one; clamping is the honest answer for a function whose spec has no meaning there.
    ///     </para>
    /// </remarks>
    public static Color4 Apply(UiBlendMode mode, Color4 source, Color4 backdrop) {
        if (mode == UiBlendMode.Normal || backdrop.A <= 0f || source.A <= 0f) {
            return source;
        }

        var cs = Unpremultiply(source);
        var cb = Unpremultiply(backdrop);
        var blended = Blend(mode, cb, cs);

        // § 5.1's weighting, then back to premultiplied. The alpha is untouched: a blend mode changes
        // what colour the group lands in, never how much of it lands.
        var mixed = ((1f - backdrop.A) * cs) + (backdrop.A * blended);

        return new Color4(mixed.X * source.A, mixed.Y * source.A, mixed.Z * source.A, source.A);
    }

    /// <summary>One mode's <c>B(Cb, Cs)</c>, over straight-alpha colour in [0, 1].</summary>
    /// <param name="mode">Which blend function.</param>
    /// <param name="cb">The backdrop colour.</param>
    /// <param name="cs">The source colour.</param>
    /// <returns>The blended colour, before § 5.1's weighting by the backdrop's alpha.</returns>
    public static Vector3 Blend(UiBlendMode mode, Vector3 cb, Vector3 cs) =>
        mode switch {
            UiBlendMode.Multiply => cb * cs,
            UiBlendMode.Screen => Screen(cb, cs),
            UiBlendMode.Overlay => HardLight(cs, cb),
            UiBlendMode.Darken => new Vector3(
                MathF.Min(cb.X, cs.X),
                MathF.Min(cb.Y, cs.Y),
                MathF.Min(cb.Z, cs.Z)
            ),
            UiBlendMode.Lighten => new Vector3(
                MathF.Max(cb.X, cs.X),
                MathF.Max(cb.Y, cs.Y),
                MathF.Max(cb.Z, cs.Z)
            ),
            UiBlendMode.ColorDodge => new Vector3(Dodge(cb.X, cs.X), Dodge(cb.Y, cs.Y), Dodge(cb.Z, cs.Z)),
            UiBlendMode.ColorBurn => new Vector3(Burn(cb.X, cs.X), Burn(cb.Y, cs.Y), Burn(cb.Z, cs.Z)),
            UiBlendMode.HardLight => HardLight(cb, cs),
            UiBlendMode.SoftLight => new Vector3(Soft(cb.X, cs.X), Soft(cb.Y, cs.Y), Soft(cb.Z, cs.Z)),
            UiBlendMode.Difference => new Vector3(
                MathF.Abs(cb.X - cs.X),
                MathF.Abs(cb.Y - cs.Y),
                MathF.Abs(cb.Z - cs.Z)
            ),
            UiBlendMode.Exclusion => cb + cs - (2f * cb * cs),
            UiBlendMode.Hue => SetLum(SetSat(cs, Sat(cb)), Lum(cb)),
            UiBlendMode.Saturation => SetLum(SetSat(cb, Sat(cs)), Lum(cb)),
            UiBlendMode.Color => SetLum(cs, Lum(cb)),
            UiBlendMode.Luminosity => SetLum(cb, Lum(cs)),

            // `Normal` and anything a future spec adds. Returning the source is what
            // `B(Cb, Cs) = Cs` means, and it is the value § 5.1 gives for `normal` outright.
            _ => cs
        };

    static Vector3 Unpremultiply(Color4 colour) {
        var inverse = 1f / colour.A;

        return new Vector3(
            Math.Clamp(colour.R * inverse, 0f, 1f),
            Math.Clamp(colour.G * inverse, 0f, 1f),
            Math.Clamp(colour.B * inverse, 0f, 1f)
        );
    }

    static Vector3 Screen(Vector3 cb, Vector3 cs) => cb + cs - (cb * cs);

    /// <summary>§ 5.1's <c>HardLight</c>, which every one of its neighbours is written in terms of.</summary>
    static Vector3 HardLight(Vector3 cb, Vector3 cs) =>
        new(Hard(cb.X, cs.X), Hard(cb.Y, cs.Y), Hard(cb.Z, cs.Z));

    static float Hard(float cb, float cs) =>
        cs <= 0.5f
            ? cb * (2f * cs)
            : (cb + ((2f * cs) - 1f)) - (cb * ((2f * cs) - 1f));

    static float Dodge(float cb, float cs) {
        // ⚠ The order of the three cases is the specification's and is not a chain of equivalent
        // guards. A backdrop of zero stays zero even where the source is one — which is the case that
        // makes `color-dodge` leave black alone rather than blowing it to white.
        if (cb <= 0f) {
            return 0f;
        }

        return cs >= 1f ? 1f : MathF.Min(1f, cb / (1f - cs));
    }

    static float Burn(float cb, float cs) {
        if (cb >= 1f) {
            return 1f;
        }

        return cs <= 0f ? 0f : 1f - MathF.Min(1f, (1f - cb) / cs);
    }

    static float Soft(float cb, float cs) {
        // § 5.1's D(Cb), which is a square root above a quarter and a cubic below it. The two halves
        // meet at 0.25 with matching value and slope, which is the whole reason the piecewise
        // definition is written that way rather than clamped.
        var d = cb <= 0.25f
            ? (((((16f * cb) - 12f) * cb) + 4f) * cb)
            : MathF.Sqrt(cb);

        return cs <= 0.5f
            ? cb - ((1f - (2f * cs)) * cb * (1f - cb))
            : cb + (((2f * cs) - 1f) * (d - cb));
    }

    static float Lum(Vector3 c) => (LumR * c.X) + (LumG * c.Y) + (LumB * c.Z);

    static Vector3 SetLum(Vector3 c, float lum) => ClipColor(c + new Vector3(lum - Lum(c)));

    /// <summary>§ 5.3's <c>ClipColor</c>: pulls an out-of-gamut colour back towards its own luma.</summary>
    /// <remarks>
    ///     ⚠ <b>Towards the luma and not towards the unit cube's faces, which is what a per-channel
    ///     clamp would do.</b> The four non-separable modes are defined to preserve luminosity
    ///     exactly, and a clamp changes it — so `mix-blend-color` on a saturated backdrop would shift
    ///     brightness as well as hue, which is the one thing the mode promises not to do.
    /// </remarks>
    static Vector3 ClipColor(Vector3 c) {
        var lum = Lum(c);
        var min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        var max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));

        if (min < 0f && lum - min > 1e-6f) {
            c = new Vector3(lum) + ((c - new Vector3(lum)) * (lum / (lum - min)));
        }

        if (max > 1f && max - lum > 1e-6f) {
            c = new Vector3(lum) + ((c - new Vector3(lum)) * ((1f - lum) / (max - lum)));
        }

        return c;
    }

    static float Sat(Vector3 c) =>
        MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z));

    /// <summary>§ 5.3's <c>SetSat</c>, stretching the mid channel between the min and the max.</summary>
    static Vector3 SetSat(Vector3 c, float sat) {
        Span<float> channels = [c.X, c.Y, c.Z];

        // The indices of the smallest, middle and largest channel. Found rather than sorted, because
        // the result has to be written back to the channel it came from — sorting loses which was
        // which, and `SetSat` is a map on channels rather than on a sorted triple.
        var min = 0;
        var max = 0;

        for (var i = 1; i < 3; i++) {
            if (channels[i] < channels[min]) {
                min = i;
            }

            if (channels[i] > channels[max]) {
                max = i;
            }
        }

        if (min == max) {
            // Every channel equal: a grey has no saturation to stretch, and § 5.3's formula would
            // divide by zero here.
            return Vector3.Zero;
        }

        var mid = 3 - min - max;

        if (channels[max] > channels[min]) {
            channels[mid] = (channels[mid] - channels[min]) * sat / (channels[max] - channels[min]);
            channels[max] = sat;
        } else {
            channels[mid] = 0f;
            channels[max] = 0f;
        }

        channels[min] = 0f;

        return new Vector3(channels[0], channels[1], channels[2]);
    }
}
