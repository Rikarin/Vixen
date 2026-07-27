// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>A colour in Oklab: perceptual lightness, and two opponent axes.</summary>
/// <param name="L">Lightness, 0 at black and 1 at white.</param>
/// <param name="A">Green to red.</param>
/// <param name="B">Blue to yellow.</param>
/// <remarks>
///     <para>
///         The space to interpolate colours in, and the reason is one specific failure. Fading from
///         blue to yellow in linear RGB passes through a muddy grey, and fading from blue to white
///         in sRGB visibly detours through purple — because neither space is arranged so that equal
///         numeric steps are equal perceptual steps. Oklab is, near enough, and it is the reason a
///         gradient or a colour transition in this engine looks like the one the designer drew.
///     </para>
///     <para>
///         Björn Ottosson's 2020 derivation. It is not a rediscovery of CIELAB: it fits the same
///         cone-response-then-cube-root shape to better data, and its hue lines stay straight, which
///         is what stops a blue→white fade bending towards purple. Cheap enough to do per frame —
///         two 3×3 matrices and three cube roots each way.
///     </para>
///     <para>
///         <b>The input is linear.</b> Feeding sRGB-encoded values in produces something that is not
///         Oklab and does not have any of the properties it is being used for. See
///         <see cref="ColorSpace" /> for why that is the easiest mistake in colour handling to make
///         and the hardest to see.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Oklab(float L, float A, float B) {
    /// <summary>Converts a linear RGB colour to Oklab.</summary>
    /// <param name="linear">The linear colour.</param>
    /// <returns>The same colour in Oklab.</returns>
    public static Oklab FromLinear(Vector3 linear) {
        var l = (0.4122214708f * linear.X) + (0.5363325363f * linear.Y) + (0.0514459929f * linear.Z);
        var m = (0.2119034982f * linear.X) + (0.6806995451f * linear.Y) + (0.1073969566f * linear.Z);
        var s = (0.0883024619f * linear.X) + (0.2817188376f * linear.Y) + (0.6299787005f * linear.Z);

        // Signed cube root, because a linear colour may legitimately be negative: an HDR value
        // outside the sRGB gamut, or a colour that has been through a matrix. `MathF.Cbrt` handles
        // the sign itself, unlike `MathF.Pow(x, 1f / 3f)`, which returns NaN.
        var lc = MathF.Cbrt(l);
        var mc = MathF.Cbrt(m);
        var sc = MathF.Cbrt(s);

        return new Oklab(
            (0.2104542553f * lc) + (0.7936177850f * mc) - (0.0040720468f * sc),
            (1.9779984951f * lc) - (2.4285922050f * mc) + (0.4505937099f * sc),
            (0.0259040371f * lc) + (0.7827717662f * mc) - (0.8086757660f * sc)
        );
    }

    /// <summary>Converts back to linear RGB.</summary>
    /// <returns>The same colour in linear RGB.</returns>
    /// <remarks>
    ///     The result can fall outside <c>[0, 1]</c>, and that is information rather than an error:
    ///     it means the colour is outside the sRGB gamut, which the midpoint of an interpolation
    ///     between two in-gamut colours genuinely can be. Clamping belongs at the point of display,
    ///     where what to do about it is a decision someone can make.
    /// </remarks>
    public Vector3 ToLinear() {
        var lc = L + (0.3963377774f * A) + (0.2158037573f * B);
        var mc = L - (0.1055613458f * A) - (0.0638541728f * B);
        var sc = L - (0.0894841775f * A) - (1.2914855480f * B);

        var l = lc * lc * lc;
        var m = mc * mc * mc;
        var s = sc * sc * sc;

        return new Vector3(
            (4.0767416621f * l) - (3.3077115913f * m) + (0.2309699292f * s),
            (-1.2684380046f * l) + (2.6097574011f * m) - (0.3413193965f * s),
            (-0.0041960863f * l) - (0.7034186147f * m) + (1.7076147010f * s)
        );
    }

    /// <summary>Interpolates between two Oklab colours.</summary>
    /// <param name="from">The colour at 0.</param>
    /// <param name="to">The colour at 1.</param>
    /// <param name="amount">Where between them.</param>
    /// <returns>The interpolated colour.</returns>
    public static Oklab Lerp(Oklab from, Oklab to, float amount) => new(
        MathUtil.Lerp(from.L, to.L, amount),
        MathUtil.Lerp(from.A, to.A, amount),
        MathUtil.Lerp(from.B, to.B, amount)
    );

    /// <summary>Interpolates two linear colours perceptually, alpha included.</summary>
    /// <param name="from">The colour at 0.</param>
    /// <param name="to">The colour at 1.</param>
    /// <param name="amount">Where between them.</param>
    /// <returns>The interpolated colour, in linear RGB.</returns>
    /// <remarks>
    ///     <para>
    ///         Alpha is interpolated linearly and separately, because it is a coverage fraction
    ///         rather than a colour and has no perceptual axis to travel along.
    ///     </para>
    ///     <para>
    ///         What this does <i>not</i> do is treat a fully transparent colour as having no hue.
    ///         CSS says a transition to <c>transparent</c> should behave as a transition to the same
    ///         colour with zero alpha, precisely so that fading out does not pass through black. That
    ///         belongs where the value is known to be a CSS colour, not here.
    ///     </para>
    /// </remarks>
    public static Color4 Lerp(Color4 from, Color4 to, float amount) {
        var mixed = Lerp(
            FromLinear(new Vector3(from.R, from.G, from.B)),
            FromLinear(new Vector3(to.R, to.G, to.B)),
            amount
        ).ToLinear();

        return new Color4(mixed.X, mixed.Y, mixed.Z, MathUtil.Lerp(from.A, to.A, amount));
    }
}
