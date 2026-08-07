// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Styling;

/// <summary>The space a <c>color-mix()</c> interpolates in.</summary>
/// <remarks>
///     ⚠ <b>Ignoring this keyword is the failure the whole colour programme exists to stop.</b>
///     <c>in srgb</c> and <c>in oklab</c> give visibly different midpoints — red to blue in sRGB
///     passes through a dark muddy purple, in Oklab through a bright one — and a parser that read the
///     keyword and then always used one space would look correct in every test that mixed a colour
///     with <c>transparent</c> and wrong in every gradient. Which is why <see cref="ColorFunctions" />
///     refuses an unrecognised space rather than defaulting to a familiar one.
/// </remarks>
enum InterpolationSpace : byte {
    /// <summary>Gamma-encoded sRGB. What <c>in srgb</c> means, and it is <i>not</i> the linear one.</summary>
    Srgb,

    /// <summary>Linear-light sRGB, the space everything past the cascade already works in.</summary>
    SrgbLinear,

    /// <summary>Oklab, rectangular. The default, and what Tailwind's opacity modifier asks for.</summary>
    Oklab,

    /// <summary>Oklab in polar form: lightness, chroma, hue.</summary>
    Oklch
}

/// <summary>Which way round the hue circle a polar interpolation travels.</summary>
enum HueInterpolation : byte {
    /// <summary>The shorter arc. CSS's default.</summary>
    Shorter,

    /// <summary>The longer arc — deliberately the long way round the wheel.</summary>
    Longer,

    /// <summary>Always increasing, however far that is.</summary>
    Increasing,

    /// <summary>Always decreasing.</summary>
    Decreasing
}

/// <summary>The CSS Color 4/5 colour functions that are more than a triple of channels.</summary>
/// <remarks>
///     <para>
///         The arithmetic half of <c>oklab()</c>, <c>oklch()</c> and <c>color-mix()</c>;
///         <see cref="StyleValueParser" /> owns the grammar. Split that way because the grammar is
///         fiddly and the arithmetic is specified — the percentage rules below are an algorithm with
///         a name in another specification, and reading them next to a tokeniser hides that.
///     </para>
///     <para>
///         <b>Everything here is in and out of linear RGB</b>, because that is what
///         <see cref="StyleValue.Color" /> holds and what the renderer wants. The interpolation space
///         exists only between the two conversions.
///     </para>
///     <para>
///         ⚠ <b>Nothing here clamps to a gamut, and that is a decision rather than an omission.</b>
///         <c>oklch(0.7 0.4 30)</c> is outside sRGB and its linear triple has a negative channel.
///         Clipping per channel would shift the hue — a vivid red clips towards orange — and the
///         correct repair is chroma reduction holding lightness and hue, against the gamut of the
///         <i>display</i> rather than of sRGB. That needs to know what the display is, which this
///         assembly does not; <c>docs/plan/43</c> § D4 owns it. So the out-of-gamut triple is carried
///         through intact, which is what <see cref="Oklab.ToLinear" /> already says it does and what
///         costs nothing to change later: an authored colour that survives to the display can still be
///         mapped there, whereas one clamped here has already lost the chroma the mapper needed.
///     </para>
/// </remarks>
static class ColorFunctions {
    /// <summary>Reads a <c>&lt;color-space&gt;</c> keyword.</summary>
    /// <param name="name">The keyword.</param>
    /// <param name="space">The space it names.</param>
    /// <returns>Whether it names one this engine can interpolate in.</returns>
    /// <remarks>
    ///     ⚠ <b>The four Vixen has the conversions for, and no more.</b> CSS lists a dozen —
    ///     <c>lab</c>, <c>lch</c>, <c>hsl</c>, <c>hwb</c>, <c>xyz</c> — and accepting a keyword whose
    ///     conversion does not exist would mean silently interpolating in the wrong space, which is
    ///     the one outcome worse than refusing. A refused mix is <see cref="StyleValue.Unknown" /> and
    ///     the property keeps its inherited or initial value, which is visible.
    /// </remarks>
    public static bool TrySpace(ReadOnlySpan<char> name, out InterpolationSpace space) {
        if (name.Equals("srgb", StringComparison.OrdinalIgnoreCase)) {
            space = InterpolationSpace.Srgb;
            return true;
        }

        if (name.Equals("srgb-linear", StringComparison.OrdinalIgnoreCase)) {
            space = InterpolationSpace.SrgbLinear;
            return true;
        }

        if (name.Equals("oklab", StringComparison.OrdinalIgnoreCase)) {
            space = InterpolationSpace.Oklab;
            return true;
        }

        if (name.Equals("oklch", StringComparison.OrdinalIgnoreCase)) {
            space = InterpolationSpace.Oklch;
            return true;
        }

        space = InterpolationSpace.Oklab;
        return false;
    }

    /// <summary>Reads a <c>&lt;hue-interpolation-method&gt;</c> keyword.</summary>
    /// <param name="name">The keyword.</param>
    /// <param name="hue">The method it names.</param>
    /// <returns>Whether it names one.</returns>
    public static bool TryHue(ReadOnlySpan<char> name, out HueInterpolation hue) {
        hue = name switch {
            _ when name.Equals("shorter", StringComparison.OrdinalIgnoreCase) => HueInterpolation.Shorter,
            _ when name.Equals("longer", StringComparison.OrdinalIgnoreCase) => HueInterpolation.Longer,
            _ when name.Equals("increasing", StringComparison.OrdinalIgnoreCase) => HueInterpolation.Increasing,
            _ when name.Equals("decreasing", StringComparison.OrdinalIgnoreCase) => HueInterpolation.Decreasing,
            _ => (HueInterpolation) byte.MaxValue
        };

        return hue != (HueInterpolation) byte.MaxValue;
    }

    /// <summary>Builds a linear colour from Oklab components.</summary>
    /// <param name="l">Lightness, 0 to 1.</param>
    /// <param name="a">The green-red axis.</param>
    /// <param name="b">The blue-yellow axis.</param>
    /// <param name="alpha">Alpha, 0 to 1.</param>
    /// <returns>The colour, in linear RGB and unclamped.</returns>
    public static Color4 FromOklab(float l, float a, float b, float alpha) {
        var linear = new Oklab(l, a, b).ToLinear();
        return new Color4(linear.X, linear.Y, linear.Z, alpha);
    }

    /// <summary>Builds a linear colour from Oklch components.</summary>
    /// <param name="l">Lightness, 0 to 1.</param>
    /// <param name="chroma">Chroma, 0 upwards.</param>
    /// <param name="hue">Hue, in degrees.</param>
    /// <param name="alpha">Alpha, 0 to 1.</param>
    /// <returns>The colour, in linear RGB and unclamped.</returns>
    /// <remarks>
    ///     Oklch <i>is</i> Oklab — the same space read in polar coordinates — so this is one
    ///     conversion and not two. CSS Color 4 § 9.3.
    /// </remarks>
    public static Color4 FromOklch(float l, float chroma, float hue, float alpha) {
        var radians = hue * (MathF.PI / 180f);
        return FromOklab(l, chroma * MathF.Cos(radians), chroma * MathF.Sin(radians), alpha);
    }

    /// <summary>Settles what two <c>color-mix()</c> percentages mean.</summary>
    /// <param name="first">The first percentage, 0 to 100, or null if it was omitted.</param>
    /// <param name="second">The second, likewise.</param>
    /// <param name="weights">Where the two weights, which sum to 1 or to 0, are written.</param>
    /// <param name="alphaMultiplier">What the mixed colour's alpha is scaled by afterwards.</param>
    /// <remarks>
    ///     <para>
    ///         <b>CSS Values 5 § "normalize mix percentages", with the force-normalization flag set</b>
    ///         — which is what <c>color-mix()</c> passes, and the reason the rules read oddly without
    ///         it. Six steps, and the three interesting outcomes fall out of them rather than being
    ///         special cases:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>One omitted.</b> Step 2 gives it <c>100% − </c>the other, so
    ///             <c>red 30%, blue</c> is 30/70. The obvious reading, and the only one of the three
    ///             that is obvious.
    ///         </item>
    ///         <item>
    ///             <b>Both given, not summing to 100.</b> Step 4 scales them to sum to 100 —
    ///             <c>red 20%, blue 60%</c> mixes 25/75, <i>not</i> 20/80 and not 25/75 with full
    ///             alpha. Step 5 then keeps the 20 points that went missing as <c>leftover</c>, and
    ///             the result's alpha is multiplied by <c>1 − leftover</c>, here 0.8. Two colours that
    ///             between them claim only 80% of the mix leave it 20% transparent, which is the part
    ///             a reasonable implementation invents wrongly. Over 100 there is no leftover: 60/60
    ///             scales to 50/50 at full alpha.
    ///         </item>
    ///         <item>
    ///             ⚠ <b>Both zero is not invalid.</b> An older CSS Color 5 draft said it was, and that
    ///             reading is still widely repeated. The current one produces <b>transparent black</b>
    ///             — <c>oklch(0% 0 none / 0)</c> in the specification's own example — and it needs no
    ///             code here to say so: step 4 does not fire because the total is not above zero,
    ///             step 5 makes <c>leftover</c> 100% and so the multiplier 0, and two zero weights
    ///             give zero components. The whole case is the algorithm run honestly.
    ///         </item>
    ///     </list>
    /// </remarks>
    public static void Normalize(float? first, float? second, Span<float> weights, out float alphaMultiplier) {
        // 1. The specified sum, each percentage clamped into range on the way in.
        var specified = 0f;
        var omitted = 0;

        if (first is { } one) {
            specified += Math.Clamp(one, 0f, 100f);
        } else {
            omitted++;
        }

        if (second is { } two) {
            specified += Math.Clamp(two, 0f, 100f);
        } else {
            omitted++;
        }

        // 2. What is left over, split evenly between the ones that said nothing. Both omitted is
        //    50/50, which is the commonest `color-mix()` there is and needs no rule of its own.
        var share = omitted == 0 ? 0f : (100f - Math.Min(specified, 100f)) / omitted;
        var p1 = first is { } a ? Math.Clamp(a, 0f, 100f) : share;
        var p2 = second is { } b ? Math.Clamp(b, 0f, 100f) : share;

        // 3.
        var total = p1 + p2;

        // 4. Force-normalized, so this fires whenever there is anything to scale — including the
        //    under-100 case, which without the flag would be left alone.
        if (total > 0f) {
            p1 *= 100f / total;
            p2 *= 100f / total;
        }

        // 5. Read off the *unscaled* total, which is the point: the scaling in step 4 has already
        //    thrown away the information that the two colours did not claim the whole mix, and this
        //    is where it is kept.
        var leftover = total < 100f ? (100f - total) / 100f : 0f;

        weights[0] = p1 / 100f;
        weights[1] = p2 / 100f;
        alphaMultiplier = 1f - leftover;
    }

    /// <summary>Mixes two colours.</summary>
    /// <param name="first">One colour, linear.</param>
    /// <param name="second">The other, linear.</param>
    /// <param name="weights">Their two weights, from <see cref="Normalize" />.</param>
    /// <param name="alphaMultiplier">The alpha scale, from <see cref="Normalize" />.</param>
    /// <param name="space">The space to interpolate in.</param>
    /// <param name="hue">How to cross the hue circle, if the space is polar.</param>
    /// <returns>The mixed colour, linear and unclamped.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Premultiplied, and that single detail is what makes the opacity modifier work.</b>
    ///         CSS Color 4 § 12.3 says colour components are multiplied by alpha before interpolating
    ///         and divided by the interpolated alpha afterwards. Take
    ///         <c>color-mix(in oklab, blue 50%, transparent)</c>: premultiplied, the transparent end
    ///         contributes a weighted <i>nothing</i> rather than a weighted black, so the halves are
    ///         blue-times-one and zero, the alpha is 0.5, and dividing back through gives blue at half
    ///         alpha — which is what the author asked for. Interpolated naïvely it gives a
    ///         <i>dark</i> blue at half alpha, because Oklab's origin is black and the mix travelled
    ///         halfway to it. That failure is invisible against a dark background and obvious against
    ///         a light one, which is the worst way for a bug to be visible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Hue is not premultiplied, and that is why the opacity modifier must say
    ///         <c>in oklab</c> and not <c>in oklch</c>.</b> An angle has no zero to scale towards, so
    ///         the specification leaves it out of the premultiplication — and <c>transparent</c> is
    ///         <c>rgb(0 0 0 / 0)</c>, whose hue is 0°. Mixing blue with transparent in <c>oklch</c>
    ///         therefore drags the hue half way from blue's 264° to 360°, and comes out a half-alpha
    ///         <i>purple</i>. Browsers do exactly the same thing; it is the reason Tailwind's own
    ///         emission names the rectangular space. Vixen implements the specified behaviour rather
    ///         than a kinder one, because a mix that quietly disagreed with a browser would be worse.
    ///     </para>
    /// </remarks>
    public static Color4 Mix(
        Color4 first,
        Color4 second,
        ReadOnlySpan<float> weights,
        float alphaMultiplier,
        InterpolationSpace space,
        HueInterpolation hue
    ) {
        var p1 = weights[0];
        var p2 = weights[1];
        var alpha = (p1 * first.A) + (p2 * second.A);

        var one = ToSpace(first, space);
        var two = ToSpace(second, space);

        Vector3 mixed;

        if (space == InterpolationSpace.Oklch) {
            var (h1, h2) = Arc(one.Z, two.Z, hue);

            mixed = new Vector3(
                (p1 * one.X * first.A) + (p2 * two.X * second.A),
                (p1 * one.Y * first.A) + (p2 * two.Y * second.A),
                (p1 * h1) + (p2 * h2)
            );

            if (alpha > 0f) {
                mixed = new Vector3(mixed.X / alpha, mixed.Y / alpha, mixed.Z);
            }
        } else {
            mixed = (one * (p1 * first.A)) + (two * (p2 * second.A));

            if (alpha > 0f) {
                mixed /= alpha;
            }
        }

        return FromSpace(mixed, alpha * alphaMultiplier, space);
    }

    /// <summary>Turns a linear colour into the components the interpolation works on.</summary>
    static Vector3 ToSpace(Color4 colour, InterpolationSpace space) {
        var linear = new Vector3(colour.R, colour.G, colour.B);

        return space switch {
            // ⚠ `in srgb` is the *encoded* space. Interpolating in linear light and calling it sRGB
            // is the mistake that makes a red-to-blue mix come out too dark, and it is invisible
            // unless the two are compared side by side.
            InterpolationSpace.Srgb => new Vector3(
                ColorSpace.LinearToSrgb(linear.X),
                ColorSpace.LinearToSrgb(linear.Y),
                ColorSpace.LinearToSrgb(linear.Z)
            ),
            InterpolationSpace.SrgbLinear => linear,
            InterpolationSpace.Oklab => Components(Oklab.FromLinear(linear)),
            _ => Polar(Oklab.FromLinear(linear))
        };
    }

    /// <summary>And back again.</summary>
    static Color4 FromSpace(Vector3 components, float alpha, InterpolationSpace space) => space switch {
        InterpolationSpace.Srgb => new Color4(
            ColorSpace.SrgbToLinear(components.X),
            ColorSpace.SrgbToLinear(components.Y),
            ColorSpace.SrgbToLinear(components.Z),
            alpha
        ),
        InterpolationSpace.SrgbLinear => new Color4(components.X, components.Y, components.Z, alpha),
        InterpolationSpace.Oklab => FromOklab(components.X, components.Y, components.Z, alpha),
        _ => FromOklch(components.X, components.Y, components.Z, alpha)
    };

    static Vector3 Components(Oklab colour) => new(colour.L, colour.A, colour.B);

    /// <summary>Oklab read in polar coordinates: lightness, chroma, hue in degrees.</summary>
    static Vector3 Polar(Oklab colour) {
        var hue = MathF.Atan2(colour.B, colour.A) * (180f / MathF.PI);
        return new Vector3(colour.L, MathF.Sqrt((colour.A * colour.A) + (colour.B * colour.B)), hue < 0f ? hue + 360f : hue);
    }

    /// <summary>Moves two hues onto the arc the method asks for, before they are interpolated.</summary>
    /// <remarks>
    ///     CSS Color 4 § 12.4, transcribed. Each case adds a whole turn to one end so that plain
    ///     linear interpolation between the adjusted pair travels the intended way — which is why the
    ///     result may leave <c>[0, 360)</c> and why nothing here puts it back: a hue of 400° is 40°,
    ///     and <see cref="FromOklch" /> takes a cosine of it either way.
    /// </remarks>
    static (float First, float Second) Arc(float first, float second, HueInterpolation hue) {
        var delta = second - first;

        switch (hue) {
            case HueInterpolation.Shorter when delta > 180f:
                first += 360f;
                break;

            case HueInterpolation.Shorter when delta < -180f:
                second += 360f;
                break;

            case HueInterpolation.Longer when delta is > 0f and < 180f:
                first += 360f;
                break;

            case HueInterpolation.Longer when delta is > -180f and <= 0f:
                second += 360f;
                break;

            case HueInterpolation.Increasing when delta < 0f:
                second += 360f;
                break;

            case HueInterpolation.Decreasing when delta > 0f:
                first += 360f;
                break;

            default:
                break;
        }

        return (first, second);
    }
}
