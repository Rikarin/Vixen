// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Mathematics;

/// <summary>
///     Brings a colour that is outside a display's gamut into it, by CSS Color 4's binary search with
///     local MINDE — reducing chroma while holding lightness and hue, rather than clipping channels.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Per-channel clipping is not a cheaper version of this, it is a different answer.</b>
///         Clamping each channel to <c>[0, 1]</c> moves the hue: a vivid red runs out of red first and
///         keeps whatever green and blue it had, so it clips <i>towards orange</i>. Reducing chroma at
///         constant lightness and hue desaturates instead, which is the error a viewer forgives.
///     </para>
///     <para>
///         ⚠ <b>The destination is the display's gamut, not always sRGB.</b> On a P3 panel
///         <c>oklch(0.7 0.3 30)</c> is showable and must not be touched at all; mapping it to sRGB
///         anyway throws away exactly the chroma the panel was bought for. This is why the repair
///         belongs at presentation, where the swapchain's colour space is known, and why nothing
///         earlier in the pipeline should clamp — see <see cref="Oklab.ToLinear" />.
///     </para>
///     <para>
///         <b>Everything here is in linear sRGB primaries, including the results.</b> A colour outside
///         sRGB is carried as linear values outside <c>[0, 1]</c> — which is what
///         <c>VK_COLOR_SPACE_EXTENDED_SRGB_LINEAR_EXT</c> means, and what keeps the engine's working
///         space unchanged. <see cref="Map(Vector3, ColorGamut)" /> returns a colour still in those
///         coordinates, but guaranteed to land inside <c>[0, 1]</c> once
///         <see cref="FromLinearSrgb" /> rebases it onto the destination's primaries.
///     </para>
///     <para>
///         Implements <a href="https://www.w3.org/TR/css-color-4/#binsearch">CSS Color 4 §14.2.1-2</a>.
///         The specification now offers three algorithms — this one, EdgeSeeker, and a ray-trace
///         variant — and lets an implementation pick among them; this is the one with reference
///         implementations to check against, and the only one whose constants the prose pins down.
///     </para>
/// </remarks>
public static class GamutMap {
    /// <summary>One just-noticeable difference in Oklab, as CSS Color 4 fixes it.</summary>
    /// <remarks>
    ///     The specification's value, and its reasoning: in CIE Lab, where lightness runs 0–100, one
    ///     JND under ΔE2000 is 2. Oklab's lightness runs 0–1, so the same threshold is a hundred times
    ///     smaller.
    /// </remarks>
    public const float JustNoticeableDifference = 0.02f;

    /// <summary>Where the chroma search stops narrowing.</summary>
    public const float SearchEpsilon = 0.0001f;

    // Derived once from published chromaticities rather than copied as digits, because a colour
    // matrix transcribed by hand is wrong in a way that looks plausible: every channel is close, the
    // white point still comes out white, and only saturated colours drift. The derivation below is
    // the standard one and is checked by round-trip and white-point tests.
    static readonly Matrix3x3 SrgbToP3;
    static readonly Matrix3x3 P3ToSrgb;
    static readonly Matrix3x3 SrgbToRec2020;
    static readonly Matrix3x3 Rec2020ToSrgb;

    static GamutMap() {
        // xy chromaticities. sRGB is Rec. 709; Display P3 is DCI-P3's primaries on D65 rather than
        // DCI's own white, which is the whole difference between the two P3s and the reason a
        // "P3" matrix taken from a cinema reference is the wrong one for a Mac display.
        var srgb = RgbToXyz(0.640, 0.330, 0.300, 0.600, 0.150, 0.060);
        var p3 = RgbToXyz(0.680, 0.320, 0.265, 0.690, 0.150, 0.060);
        var rec2020 = RgbToXyz(0.708, 0.292, 0.170, 0.797, 0.131, 0.046);

        SrgbToP3 = ToMatrix(Multiply(Inverse(p3), srgb));
        P3ToSrgb = ToMatrix(Multiply(Inverse(srgb), p3));
        SrgbToRec2020 = ToMatrix(Multiply(Inverse(rec2020), srgb));
        Rec2020ToSrgb = ToMatrix(Multiply(Inverse(srgb), rec2020));
    }

    /// <summary>Rebases a linear sRGB colour onto another gamut's primaries.</summary>
    /// <param name="linear">The colour, in linear sRGB primaries.</param>
    /// <param name="gamut">The gamut to express it in.</param>
    /// <returns>The same colour, in that gamut's linear primaries.</returns>
    /// <remarks>
    ///     The same light, described against different primaries — not a different colour. A value
    ///     that lands inside <c>[0, 1]</c> here is one the display can show.
    /// </remarks>
    public static Vector3 FromLinearSrgb(Vector3 linear, ColorGamut gamut) => gamut switch {
        ColorGamut.DisplayP3 => Transform(SrgbToP3, linear),
        ColorGamut.Rec2020 => Transform(SrgbToRec2020, linear),
        _ => linear
    };

    /// <summary>Rebases a colour from a gamut's primaries back onto linear sRGB.</summary>
    /// <param name="linear">The colour, in that gamut's linear primaries.</param>
    /// <param name="gamut">The gamut it is expressed in.</param>
    /// <returns>The same colour, in linear sRGB primaries.</returns>
    public static Vector3 ToLinearSrgb(Vector3 linear, ColorGamut gamut) => gamut switch {
        ColorGamut.DisplayP3 => Transform(P3ToSrgb, linear),
        ColorGamut.Rec2020 => Transform(Rec2020ToSrgb, linear),
        _ => linear
    };

    /// <summary>Whether a colour is showable on a display with this gamut.</summary>
    /// <param name="linear">The colour, in linear sRGB primaries.</param>
    /// <param name="gamut">The display's gamut.</param>
    /// <returns>Whether every channel lands in range once rebased.</returns>
    /// <remarks>
    ///     ⚠ <b>The tolerance is not decoration.</b> The binary search below converges <i>onto</i> the
    ///     gamut boundary, so it spends its last iterations asking about colours that differ from
    ///     exactly-representable by less than a float can hold. An exact test there flips on rounding
    ///     noise and the search fails to settle.
    /// </remarks>
    public static bool InGamut(Vector3 linear, ColorGamut gamut) {
        var mapped = FromLinearSrgb(linear, gamut);
        const float tolerance = 1e-5f;

        return mapped.X >= -tolerance && mapped.X <= 1f + tolerance
            && mapped.Y >= -tolerance && mapped.Y <= 1f + tolerance
            && mapped.Z >= -tolerance && mapped.Z <= 1f + tolerance;
    }

    /// <summary>Clamps a colour into a gamut channel by channel.</summary>
    /// <param name="linear">The colour, in linear sRGB primaries.</param>
    /// <param name="gamut">The gamut to clamp into.</param>
    /// <returns>The clamped colour, back in linear sRGB primaries.</returns>
    /// <remarks>
    ///     ⚠ <b>This is the operation the class exists to avoid, and it is still needed.</b> Used
    ///     alone it shifts hue. Used as CSS Color 4 uses it — on a colour whose chroma has already
    ///     been reduced to within one JND of the boundary — the shift is below what anyone can see,
    ///     and it recovers the chroma that a pure reduction would have given away near a concave
    ///     patch of the gamut surface. That is the "local MINDE" in the algorithm's name.
    /// </remarks>
    public static Vector3 Clip(Vector3 linear, ColorGamut gamut) {
        var mapped = FromLinearSrgb(linear, gamut);

        return ToLinearSrgb(
            new Vector3(
                Math.Clamp(mapped.X, 0f, 1f),
                Math.Clamp(mapped.Y, 0f, 1f),
                Math.Clamp(mapped.Z, 0f, 1f)
            ),
            gamut
        );
    }

    /// <summary>The perceptual distance between two colours.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>ΔE<sub>OK</sub>.</returns>
    /// <remarks>
    ///     Plain Euclidean distance, because Oklab is uniform enough not to need the correction terms
    ///     ΔE2000 spends its length on. Note that the distance is taken in Ok<i>lab</i> even though
    ///     the search that uses it walks Oklch: a difference of angle is not a distance.
    /// </remarks>
    public static float DeltaEOk(Oklab left, Oklab right) {
        var l = left.L - right.L;
        var a = left.A - right.A;
        var b = left.B - right.B;

        return MathF.Sqrt((l * l) + (a * a) + (b * b));
    }

    /// <summary>Brings a colour into a gamut, preserving lightness and hue.</summary>
    /// <param name="linear">The colour, in linear sRGB primaries, in or out of gamut.</param>
    /// <param name="gamut">The destination gamut.</param>
    /// <returns>A colour inside the gamut, still in linear sRGB primaries.</returns>
    /// <remarks>
    ///     A colour already inside the gamut is returned untouched — the algorithm implements a
    ///     relative colorimetric intent, so mapping is only ever a repair.
    /// </remarks>
    public static Vector3 Map(Vector3 linear, ColorGamut gamut) {
        var origin = Oklab.FromLinear(linear);

        // Out of range on the lightness axis: there is no chroma reduction that helps, because the
        // failure is not chroma. The specification names the answers rather than searching for them.
        if (origin.L >= 1f) {
            return new Vector3(1f, 1f, 1f);
        }

        if (origin.L <= 0f) {
            return default;
        }

        if (InGamut(linear, gamut)) {
            return linear;
        }

        var hue = MathF.Atan2(origin.B, origin.A);
        var current = linear;
        var clipped = Clip(current, gamut);

        // The first question is whether anyone could tell the difference without any search at all.
        // For colours barely outside the boundary — which most out-of-gamut colours in a real palette
        // are — this returns immediately and the loop never runs.
        if (DeltaEOk(Oklab.FromLinear(clipped), origin) < JustNoticeableDifference) {
            return clipped;
        }

        var min = 0f;
        var max = MathF.Sqrt((origin.A * origin.A) + (origin.B * origin.B));

        // ⚠ Tracks whether `min` is still known to be inside the gamut. Once the MINDE branch below
        // has moved `min` to a chroma that is *outside* it, the plain in-gamut test can no longer be
        // trusted to bracket the answer, and continuing to use it is how the reference pseudocode
        // used to loop forever (csswg-drafts#6999).
        var minInGamut = true;

        while (max - min > SearchEpsilon) {
            var chroma = (min + max) * 0.5f;
            current = new Oklab(origin.L, chroma * MathF.Cos(hue), chroma * MathF.Sin(hue)).ToLinear();

            if (minInGamut && InGamut(current, gamut)) {
                min = chroma;

                continue;
            }

            clipped = Clip(current, gamut);
            var difference = DeltaEOk(Oklab.FromLinear(clipped), Oklab.FromLinear(current));

            if (difference < JustNoticeableDifference) {
                if (JustNoticeableDifference - difference < SearchEpsilon) {
                    return clipped;
                }

                minInGamut = false;
                min = chroma;
            } else {
                max = chroma;
            }
        }

        return clipped;
    }

    /// <summary>Brings a colour into a gamut, preserving lightness, hue and alpha.</summary>
    /// <param name="colour">The colour, in linear sRGB primaries.</param>
    /// <param name="gamut">The destination gamut.</param>
    /// <returns>A colour inside the gamut.</returns>
    /// <remarks>
    ///     Alpha is carried through untouched: it is a coverage fraction, not a colour, and has no
    ///     gamut to be outside of.
    /// </remarks>
    public static Color4 Map(Color4 colour, ColorGamut gamut) {
        var mapped = Map(new Vector3(colour.R, colour.G, colour.B), gamut);

        return new Color4(mapped.X, mapped.Y, mapped.Z, colour.A);
    }

    static Vector3 Transform(in Matrix3x3 matrix, Vector3 value) => new(
        (matrix.M11 * value.X) + (matrix.M12 * value.Y) + (matrix.M13 * value.Z),
        (matrix.M21 * value.X) + (matrix.M22 * value.Y) + (matrix.M23 * value.Z),
        (matrix.M31 * value.X) + (matrix.M32 * value.Y) + (matrix.M33 * value.Z)
    );

    /// <summary>Builds the linear-RGB-to-XYZ matrix for a set of primaries on the D65 white point.</summary>
    /// <remarks>
    ///     The textbook construction: each primary contributes a column <c>(x/y, 1, (1-x-y)/y)</c>,
    ///     and those columns are then scaled so that <c>(1, 1, 1)</c> comes out as the white point
    ///     exactly. Without that scaling step the matrix has the right hues and the wrong balance,
    ///     which is the classic way a hand-built colour matrix goes subtly green.
    /// </remarks>
    static double[] RgbToXyz(double xr, double yr, double xg, double yg, double xb, double yb) {
        // D65, as CIE 15 and every one of these three standards give it.
        const double whiteX = 0.3127;
        const double whiteY = 0.3290;

        double[] primaries = [
            xr / yr, xg / yg, xb / yb,
            1d, 1d, 1d,
            (1d - xr - yr) / yr, (1d - xg - yg) / yg, (1d - xb - yb) / yb
        ];

        double[] white = [whiteX / whiteY, 1d, (1d - whiteX - whiteY) / whiteY];
        var inverse = Inverse(primaries);

        double[] scale = [
            (inverse[0] * white[0]) + (inverse[1] * white[1]) + (inverse[2] * white[2]),
            (inverse[3] * white[0]) + (inverse[4] * white[1]) + (inverse[5] * white[2]),
            (inverse[6] * white[0]) + (inverse[7] * white[1]) + (inverse[8] * white[2])
        ];

        return [
            primaries[0] * scale[0], primaries[1] * scale[1], primaries[2] * scale[2],
            primaries[3] * scale[0], primaries[4] * scale[1], primaries[5] * scale[2],
            primaries[6] * scale[0], primaries[7] * scale[1], primaries[8] * scale[2]
        ];
    }

    static double[] Multiply(double[] left, double[] right) {
        var result = new double[9];

        for (var row = 0; row < 3; row++) {
            for (var column = 0; column < 3; column++) {
                result[(row * 3) + column] =
                    (left[(row * 3) + 0] * right[column])
                    + (left[(row * 3) + 1] * right[3 + column])
                    + (left[(row * 3) + 2] * right[6 + column]);
            }
        }

        return result;
    }

    // Done in double rather than through Matrix3x3.Invert, because these matrices are built once at
    // startup and then used on every mapped colour: the cost of the extra precision is paid a
    // handful of times, and float inversion error would otherwise be baked into every result.
    static double[] Inverse(double[] matrix) {
        var determinant =
            (matrix[0] * ((matrix[4] * matrix[8]) - (matrix[5] * matrix[7])))
            - (matrix[1] * ((matrix[3] * matrix[8]) - (matrix[5] * matrix[6])))
            + (matrix[2] * ((matrix[3] * matrix[7]) - (matrix[4] * matrix[6])));

        var scale = 1d / determinant;

        return [
            ((matrix[4] * matrix[8]) - (matrix[5] * matrix[7])) * scale,
            ((matrix[2] * matrix[7]) - (matrix[1] * matrix[8])) * scale,
            ((matrix[1] * matrix[5]) - (matrix[2] * matrix[4])) * scale,
            ((matrix[5] * matrix[6]) - (matrix[3] * matrix[8])) * scale,
            ((matrix[0] * matrix[8]) - (matrix[2] * matrix[6])) * scale,
            ((matrix[2] * matrix[3]) - (matrix[0] * matrix[5])) * scale,
            ((matrix[3] * matrix[7]) - (matrix[4] * matrix[6])) * scale,
            ((matrix[1] * matrix[6]) - (matrix[0] * matrix[7])) * scale,
            ((matrix[0] * matrix[4]) - (matrix[1] * matrix[3])) * scale
        ];
    }

    static Matrix3x3 ToMatrix(double[] values) => new(
        (float) values[0], (float) values[1], (float) values[2],
        (float) values[3], (float) values[4], (float) values[5],
        (float) values[6], (float) values[7], (float) values[8]
    );
}
