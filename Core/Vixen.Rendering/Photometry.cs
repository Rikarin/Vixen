// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>What an author's light intensity is measured in.</summary>
/// <remarks>
///     <para>
///         <b>A light has a physical brightness and a number an author types, and they are not the
///         same thing.</b> A shader wants luminous intensity in candela — that is what divides by the
///         square of the distance — and nobody buys a lamp in candela. A bulb's box says lumens, a
///         light meter reads lux, a screen is specified in nits, and each of those is the natural unit
///         for a different <em>kind</em> of light. So the unit is carried beside the number and the
///         conversion happens once per light per frame, on the way to the GPU.
///     </para>
///     <para>
///         ⚠ <b>Not every unit means something for every kind.</b> Lux is illuminance arriving at a
///         surface, which only a light with no falloff can promise — the sun. Nits is luminance
///         leaving a surface, which needs a light that <em>has</em> a surface. Asking for one that
///         does not apply is not an error: <see cref="Photometry.Intensity" /> falls back to the kind's
///         own natural unit, because a light that is off by a factor of 4π is worse than a light that
///         quietly ignored a menu choice.
///     </para>
/// </remarks>
public enum LightUnit {
    /// <summary>
    ///     Whatever the kind's own unit is, taken as written — candela for a point or a spot, lux for
    ///     a directional light, nits for an area one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Zero, and that is load-bearing.</b> A light that says nothing about its unit is a
    ///     light whose number goes to the shader unchanged, which is what every scene authored before
    ///     there were units means and what a zeroed struct has to keep meaning. Making
    ///     <see cref="Lumen" /> the default would divide every one of them by 4π.
    /// </remarks>
    Native = 0,

    /// <summary>Luminous power, in lumens — what a bulb's packaging says. Point, spot and area.</summary>
    Lumen = 1,

    /// <summary>Luminous intensity, in candela — what a punctual light's loop wants.</summary>
    Candela = 2,

    /// <summary>Illuminance, in lux — a light meter's reading. Directional only.</summary>
    Lux = 3,

    /// <summary>Luminance, in nits (cd/m²) — a screen's own brightness. Area lights.</summary>
    Nits = 4
}

/// <summary>
///     Colour temperature, luminous units, and the exposure that brings them back to a display.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a renderer needs any of this.</b> A scene lit in arbitrary multipliers is one where
///         every value has to be retuned when anything changes: move the sun down and the lamps are
///         suddenly wrong, add a lamp and the sun is. Physical units remove the coupling — a 2700 K
///         lantern emitting 600 lumens is that lantern under a noon sky and under a sunset, and what
///         changes between the two is the <em>exposure</em>, which is one number for the whole frame.
///     </para>
///     <para>
///         <b>Everything here is photometric, not radiometric.</b> The pipeline's "radiance" is
///         luminance in cd/m², the sun's intensity is illuminance in lux, and the tonemap divides by
///         a middle-grey exposure derived from an EV. That is the same choice Unity's HDRP and
///         Frostbite make, and the reason is that photometric units are the ones instruments and
///         datasheets are written in: a radiometric pipeline needs a spectral response curve before
///         anyone can say what a 60 W bulb is.
///     </para>
///     <para>
///         Free functions on values rather than methods on a light, because the sky needs the same
///         arithmetic and is not a light — see <see cref="Lighting.PhysicalSky" />.
///     </para>
/// </remarks>
public static class Photometry {
    /// <summary>
    ///     The reflectance the exposure equation is anchored to — the 12.5 of a reflected-light meter.
    /// </summary>
    /// <remarks>
    ///     ISO 2720's calibration constant for a reflected-light meter, over 100. It is why the
    ///     exposure below is <c>1 / (1.2 · 2^EV)</c> rather than <c>1 / 2^EV</c>: an EV names the
    ///     luminance a meter would call middle grey, and 1.2 is what maps that to 0.18 on the output.
    /// </remarks>
    public const float MiddleGrey = 1.2f;

    /// <summary>The lowest temperature the locus approximation is valid at, in kelvin.</summary>
    public const float MinimumTemperature = 1667f;

    /// <summary>The highest temperature the locus approximation is valid at, in kelvin.</summary>
    public const float MaximumTemperature = 25000f;

    /// <summary>The colour of a black body at a temperature, as linear sRGB of unit luminance.</summary>
    /// <param name="kelvin">The temperature. Clamped to the range the approximation covers.</param>
    /// <returns>A linear colour whose luminance is 1, so it tints without brightening.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Unit luminance is the whole contract.</b> A temperature says what colour a light is
    ///         and an intensity says how bright — multiply them and you have the light. If this
    ///         returned an un-normalised colour then changing a lamp from 6500 K to 2000 K would also
    ///         change how much light it puts out, which is a coupling an author cannot work around
    ///         and is not what any photometric quantity means.
    ///     </para>
    ///     <para>
    ///         Kim et al.'s cubic fit to the Planckian locus, then <c>xyY → XYZ → linear sRGB</c>.
    ///         The fit is why the range is clamped: outside 1667–25000 K the polynomials diverge, and
    ///         a candle is 1850 K while an overcast sky is 7000 K, so the range covers everything
    ///         anybody lights a scene with.
    ///     </para>
    ///     <para>
    ///         ⚠ Negative channels are clamped rather than scaled. The locus leaves the sRGB gamut
    ///         below about 1900 K and above about 15000 K, and there is no colour in this space that
    ///         is what a 1700 K body actually looks like — clamping gives the nearest one, which is
    ///         the deepest orange the display has.
    ///     </para>
    /// </remarks>
    public static Color3 FromTemperature(float kelvin) {
        var t = Math.Clamp(kelvin, MinimumTemperature, MaximumTemperature);
        var inverse = 1f / t;
        var inverse2 = inverse * inverse;
        var inverse3 = inverse2 * inverse;

        var x = t <= 4000f
            ? (-0.2661239e9f * inverse3) - (0.2343589e6f * inverse2) + (0.8776956e3f * inverse) + 0.179910f
            : (-3.0258469e9f * inverse3) + (2.1070379e6f * inverse2) + (0.2226347e3f * inverse) + 0.240390f;

        var x2 = x * x;
        var x3 = x2 * x;

        var y = t switch {
            <= 2222f => (-1.1063814f * x3) - (1.34811020f * x2) + (2.18555832f * x) - 0.20219683f,
            <= 4000f => (-0.9549476f * x3) - (1.37418593f * x2) + (2.09137015f * x) - 0.16748867f,
            _ => (3.0817580f * x3) - (5.87338670f * x2) + (3.75112997f * x) - 0.37001483f
        };

        // xyY with Y = 1, which is what makes the normalisation below a division by a number near 1
        // rather than by whatever scale the locus happened to produce.
        var scale = 1f / MathF.Max(y, 1e-4f);
        var bigX = x * scale;
        var bigZ = (1f - x - y) * scale;

        var red = (3.2404542f * bigX) - 1.5371385f - (0.4985314f * bigZ);
        var green = (-0.9692660f * bigX) + 1.8760108f + (0.0415560f * bigZ);
        var blue = (0.0556434f * bigX) - 0.2040259f + (1.0572252f * bigZ);

        red = MathF.Max(red, 0f);
        green = MathF.Max(green, 0f);
        blue = MathF.Max(blue, 0f);

        var luminance = (0.2126f * red) + (0.7152f * green) + (0.0722f * blue);

        if (luminance <= 1e-4f) {
            return new Color3(1f, 1f, 1f);
        }

        return new Color3(red / luminance, green / luminance, blue / luminance);
    }

    /// <summary>
    ///     What one light's authored intensity is in the unit the GPU record wants.
    /// </summary>
    /// <param name="kind">Which kind of light it is.</param>
    /// <param name="unit">What <paramref name="intensity" /> is measured in.</param>
    /// <param name="intensity">The authored number.</param>
    /// <param name="outerAngle">A spot's outer cone half-angle, in radians.</param>
    /// <param name="radius">A sphere or tube radius, or a rectangle's half-height, in metres.</param>
    /// <param name="halfLength">Half a tube's length or a rectangle's half-width, in metres.</param>
    /// <returns>
    ///     Illuminance in lux for a directional light, luminous intensity in candela for a punctual
    ///     one, luminance in nits for an area one — whichever the shader's loop multiplies.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         <b>The unit a kind converts <em>to</em> is fixed by the shader, not by the author.</b>
    ///         A directional light has no falloff, so what reaches a surface is what it emits and the
    ///         answer is lux. A punctual light's contribution is divided by the square of the
    ///         distance, so the answer has to be candela. An area light is integrated over its own
    ///         solid angle, so the answer is nits. Everything below is one of three conversions into
    ///         those.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A spot's cone is part of its brightness.</b> Lumens are power, and a spot puts its
    ///         power through the cone rather than over the whole sphere — so narrowing the cone of a
    ///         600-lumen lamp makes it brighter, exactly as a real reflector does. That is
    ///         <c>Φ / (2π(1 − cos θ))</c> and not <c>Φ / 4π</c>, and getting it wrong is a spot whose
    ///         brightness does not change when it is focused.
    ///     </para>
    /// </remarks>
    public static float Intensity(
        LightKind kind,
        LightUnit unit,
        float intensity,
        float outerAngle = 0f,
        float radius = 0f,
        float halfLength = 0f
    ) =>
        kind switch {
            // Lux is what a directional light already is; nothing else means anything for a light
            // with no position, so every other unit is read as lux rather than scaled by a solid
            // angle it does not have.
            LightKind.Directional => intensity,

            LightKind.Spot => unit == LightUnit.Lumen
                ? intensity / MathF.Max(ConeSolidAngle(outerAngle), 1e-4f)
                : intensity,

            LightKind.Point => unit == LightUnit.Lumen ? intensity / (4f * MathF.PI) : intensity,

            // Area lights emit from a surface, so lumens become nits through that surface's area and
            // a hemisphere of π steradians — the same L = Φ / (A·π) both shapes use, differing only
            // in what A is.
            LightKind.Rect => unit == LightUnit.Lumen
                ? intensity / MathF.Max(4f * halfLength * radius * MathF.PI, 1e-4f)
                : intensity,

            LightKind.Tube => unit == LightUnit.Lumen
                ? intensity / MathF.Max(CapsuleArea(radius, halfLength) * MathF.PI, 1e-4f)
                : intensity,

            _ => intensity
        };

    /// <summary>The solid angle of a cone of a given half-angle, in steradians.</summary>
    /// <param name="halfAngle">The half-angle, in radians.</param>
    public static float ConeSolidAngle(float halfAngle) =>
        2f * MathF.PI * (1f - MathF.Cos(Math.Clamp(halfAngle, 0f, MathF.PI)));

    /// <summary>The surface area of a capsule: a cylinder's side plus a sphere's worth of caps.</summary>
    /// <param name="radius">Its radius.</param>
    /// <param name="halfLength">Half the length of the cylindrical part.</param>
    public static float CapsuleArea(float radius, float halfLength) =>
        (4f * MathF.PI * radius * halfLength) + (4f * MathF.PI * radius * radius);

    /// <summary>The linear multiplier that takes a scene's luminance to display-referred values.</summary>
    /// <param name="ev100">The exposure value at ISO 100.</param>
    /// <remarks>
    ///     <para>
    ///         <c>1 / (1.2 · 2^EV)</c> — the standard saturation-based exposure. An EV of 15 is bright
    ///         sun, 12 is overcast, 5 is a lit interior, 0 is moonlight; each step of one is a halving
    ///         of the light the frame is exposed for, which is what makes it the knob an author
    ///         reaches for after moving the sun.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing above this line knows about it.</b> Exposure is applied once, in the
    ///         tonemap, to the whole image — a light that carried its own exposure would be a light
    ///         whose brightness changed when the camera did, which is the coupling physical units are
    ///         for removing.
    ///     </para>
    /// </remarks>
    public static float ExposureFromEv100(float ev100) => 1f / (MiddleGrey * MathF.Pow(2f, ev100));

    /// <summary>The exposure value a linear multiplier corresponds to.</summary>
    /// <param name="exposure">The multiplier.</param>
    public static float Ev100FromExposure(float exposure) =>
        MathF.Log2(1f / (MiddleGrey * MathF.Max(exposure, 1e-9f)));

    /// <summary>The exposure value a photographic camera's three settings give.</summary>
    /// <param name="aperture">The f-number — f/1.4, f/16.</param>
    /// <param name="shutterTime">How long the shutter is open, in seconds.</param>
    /// <param name="sensitivity">The ISO.</param>
    /// <remarks>
    ///     For an author who would rather think in stops than in a bare number, and for a game with a
    ///     camera whose aperture is part of its depth of field. <c>EV = log2(N² / t · 100 / S)</c>.
    /// </remarks>
    public static float Ev100FromCamera(float aperture, float shutterTime, float sensitivity) =>
        MathF.Log2(aperture * aperture / MathF.Max(shutterTime, 1e-6f) * 100f / MathF.Max(sensitivity, 1e-3f));
}
