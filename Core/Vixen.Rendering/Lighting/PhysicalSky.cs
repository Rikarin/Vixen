// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Lighting;

/// <summary>The atmosphere, as the three numbers that decide what a daylight sky looks like.</summary>
/// <param name="SunDirection">
///     Which way the sun's light travels — <em>toward</em> the scene, the same sense
///     <see cref="RenderLight.Direction" /> has. Normalised on use.
/// </param>
/// <param name="Turbidity">
///     How much haze there is: the ratio of total to purely molecular optical thickness. 2 is an
///     exceptionally clear day, 3 a clear one, 6 hazy, 10 the sky over a city in summer.
/// </param>
/// <param name="GroundAlbedo">
///     What the ground below the horizon reflects, 0 to 1. Not part of the daylight model — it is what
///     the lower hemisphere of a baked cube is filled with, and it is the difference between a scene
///     with a bounce off the floor and one lit from above only.
/// </param>
/// <remarks>
///     Turbidity is the knob that moves the look most and the one whose name says least. Raising it
///     widens the sun's aureole, desaturates the blue and brings the horizon up — which is haze, and
///     which reads as distance.
/// </remarks>
public readonly record struct SkyParameters(Vector3 SunDirection, float Turbidity = 3f, float GroundAlbedo = 0.15f);

/// <summary>
///     A daylight sky, computed rather than painted: Preetham's analytic model, in cd/m².
/// </summary>
/// <remarks>
///     <para>
///         <b>What "physically based sky" buys over a gradient.</b> A gradient has to be re-authored
///         every time the sun moves, and the two are then two opinions about the same weather — a
///         sunset sky over a scene lit by a white noon sun. Here there is one input, the sun's
///         direction, and the sky, the sun's colour and the sun's brightness all fall out of it. Move
///         the sun down and the horizon reddens, the sky dims, the sun goes orange and its
///         illuminance drops by a factor of ten, because all four are the same air mass.
///     </para>
///     <para>
///         <b>Preetham et al. 1999, "A Practical Analytic Model for Daylight."</b> A Perez sky
///         luminance distribution fitted to spectral radiative-transfer simulations: five coefficients
///         per channel as linear functions of turbidity, a zenith value per channel as a polynomial in
///         the sun's zenith angle, and the ratio of the distribution at the view direction to the
///         distribution at the zenith. Output is <c>xyY</c>, converted here to linear sRGB.
///     </para>
///     <para>
///         ⚠ <b>It is a daylight model, and <see cref="DiffuseScale" /> is what carries it past the
///         end of daylight.</b> The published fit's zenith luminance stops falling well before the sun
///         reaches the horizon — it bottoms out near 1900 cd/m², about five times what a real sunset
///         sky has — so a scene authored by moving the sun down gets a disc that dims by a factor of a
///         thousand under a sky that does not dim at all. That scalar is the correction, and below the
///         horizon it is an extrapolation which says so. A model that covered twilight properly would
///         be Hosek-Wilkie with its own table, and this one is four dozen constants.
///     </para>
///     <para>
///         <b>The sun is the same atmosphere seen the other way.</b>
///         <see cref="SunIlluminance" /> and <see cref="SunTint" /> come from Rayleigh and Mie
///         transmittance along the same air mass the sky is computed for, so the disc's colour and the
///         horizon's colour are one calculation. That is what keeps "golden hour" a position rather
///         than a palette.
///     </para>
/// </remarks>
public static class PhysicalSky {
    /// <summary>Luminous solar constant at the top of the atmosphere, in lux.</summary>
    /// <remarks>
    ///     The 1361 W/m² solar constant through the photopic response, which is about 133 klx; 128 klx
    ///     is the value the daylight literature uses for the direct normal component before
    ///     extinction. Everything <see cref="SunIlluminance" /> does is take this away again.
    /// </remarks>
    public const float SolarConstant = 128000f;

    /// <summary>The luminance of one direction of sky, in cd/m².</summary>
    /// <param name="direction">The view ray, pointing away from the viewer. Normalised on use.</param>
    /// <param name="sky">The atmosphere.</param>
    /// <returns>Linear sRGB luminance. Values in the thousands are ordinary.</returns>
    /// <remarks>
    ///     Below the horizon this is the ground: the horizon's own luminance times
    ///     <see cref="SkyParameters.GroundAlbedo" />, which is a diffuse bounce and not a second sky.
    ///     A cube baked without it has a black lower hemisphere, and every surface in the scene then
    ///     has an ambient term that stops dead at its equator.
    /// </remarks>
    public static Vector3 Radiance(Vector3 direction, in SkyParameters sky) {
        var view = Normalise(direction);
        var sun = -Normalise(sky.SunDirection);
        var turbidity = Math.Clamp(sky.Turbidity, 1.8f, 10f);

        // The sun's zenith angle, clamped an inch above the horizon. The fit's zenith-luminance
        // polynomial goes negative past about 89°, and a sky whose brightest point is a negative
        // number is not dark — it is inside out.
        var sunTheta = MathF.Acos(Math.Clamp(sun.Y, 0.001f, 1f));
        var scale = DiffuseScale(sky);

        if (view.Y < 0f) {
            var horizon = Sky(new Vector3(view.X, 0.001f, view.Z), sun, turbidity, sunTheta);
            return horizon * scale * Math.Clamp(sky.GroundAlbedo, 0f, 1f);
        }

        return Sky(view, sun, turbidity, sunTheta) * scale;
    }

    /// <summary>How much of the fit's daylight luminance a sky this low actually has.</summary>
    /// <param name="sky">The atmosphere.</param>
    /// <returns>A scalar, 1 with the sun overhead and falling towards zero at the horizon.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Preetham's zenith luminance has a floor, and the floor is where sunset lives.</b>
    ///         Strip the fit down as the sun approaches the horizon and <c>tan χ</c> goes to zero,
    ///         leaving <c>−0.2155 T + 2.4192</c> — about 1900 cd/m² whatever else is true. A real clear
    ///         zenith is around 4000 cd/m² with the sun 30° up, 1400 at 6°, and 400 at sunset. So the
    ///         fit is roughly right in the middle of its range and roughly <em>five times</em> too
    ///         bright at the bottom of it, which is not a subtlety: it is the difference between a
    ///         moody sunset and an overcast noon with an orange light in it. Every attempt to author
    ///         one by moving the sun down finds it, because the disc dims by a factor of ten over the
    ///         same few degrees and the sky does not move at all.
    ///     </para>
    ///     <para>
    ///         <b>The correction is the beam's own extinction, at a third the path.</b> The sky is lit
    ///         by the same sunlight the disc is, so when the beam is attenuated a thousandfold the
    ///         diffuse sky cannot be unchanged — but it is not attenuated equally either, because it is
    ///         scattered high in the atmosphere and comes down to the observer close to vertical rather
    ///         than along the whole slant path. A cube root of the beam transmittance is that geometry
    ///         in one exponent, normalised so the sun overhead leaves the fit alone.
    ///     </para>
    ///     <para>
    ///         It is worth what it costs: against measured clear-sky zenith luminances this gives about
    ///         4100 cd/m² at 30°, 1200 at 6° and 100 at the horizon, where the fit alone gives 4500,
    ///         2350 and 1860.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Below the horizon it is an extrapolation and not a model.</b> There is no direct
    ///         beam left to take a root of, so the scale continues from its horizon value with an
    ///         exponential in elevation — about one and a quarter stops per degree, which is roughly
    ///         what civil twilight measures. It is dark, it is smooth, and it is not <see cref="Radiance" />
    ///         claiming to know what the sky looks like an hour after sunset. Nothing here models
    ///         earthshine, airglow or a moon, so a sky far below the horizon is black rather than the
    ///         deep blue a night sky really is.
    ///     </para>
    /// </remarks>
    public static float DiffuseScale(in SkyParameters sky) {
        var sun = -Normalise(sky.SunDirection);
        var elevation = MathF.Asin(Math.Clamp(sun.Y, -1f, 1f));
        var turbidity = Math.Clamp(sky.Turbidity, 1.8f, 10f);

        // Divided through by the value the sun straight up would give, so this is a correction and
        // not a dimmer: overhead it is exactly one and the fit is used as published. Recomputed from
        // the turbidity rather than written down, because haze attenuates the beam as well — a
        // constant here would make raising the turbidity darken the noon sky, which is a second
        // effect the model already has and would then have twice.
        var overhead = MathF.Cbrt(Luminance(Extinction(1f, turbidity)));

        if (elevation > 0f) {
            return MathF.Cbrt(Luminance(Extinction(AirMass(elevation), turbidity))) / overhead;
        }

        // The horizon's own value, continued downward. Taken from the same expression rather than
        // written down for the reason above, and it is what makes the two arguments meet: a step at
        // zero elevation would be the sky jumping a stop as the sun crossed it.
        const float DecayPerRadian = 48f;

        var horizon = MathF.Cbrt(Luminance(Extinction(AirMass(0f), turbidity))) / overhead;

        return horizon * MathF.Exp(DecayPerRadian * elevation);
    }

    /// <summary>How much light the sun delivers to a surface facing it, in lux.</summary>
    /// <param name="sky">The atmosphere.</param>
    /// <remarks>
    ///     <para>
    ///         The whole point of this being here rather than typed into a scene: a directional light
    ///         set to 100000 lux under a sunset sky is a scene lit by two different times of day. On a
    ///         clear day this is about 95 klx overhead, 20 klx at ten degrees of elevation, and a few
    ///         hundred at the horizon.
    ///     </para>
    ///     <para>
    ///         Extinction only — no circumsolar component and no ground bounce, both of which the sky
    ///         cube already carries.
    ///     </para>
    /// </remarks>
    public static float SunIlluminance(in SkyParameters sky) => SolarConstant * Luminance(Transmittance(sky));

    /// <summary>Rec. 709 luminance of a linear triple, which is what "how much light" means here.</summary>
    static float Luminance(Vector3 linear) =>
        (0.2126f * linear.X) + (0.7152f * linear.Y) + (0.0722f * linear.Z);

    /// <summary>What colour the sun is at this elevation, as a tint of unit luminance.</summary>
    /// <param name="sky">The atmosphere.</param>
    /// <remarks>
    ///     Unit luminance for <see cref="Photometry.FromTemperature" />'s reason: this says what
    ///     colour, <see cref="SunIlluminance" /> says how much, and multiplying a tint that also
    ///     carried brightness would count the extinction twice.
    /// </remarks>
    public static Color3 SunTint(in SkyParameters sky) {
        var transmittance = Transmittance(sky);
        var luminance = Luminance(transmittance);

        return luminance <= 1e-6f
            ? new Color3(1f, 1f, 1f)
            : new Color3(transmittance.X / luminance, transmittance.Y / luminance, transmittance.Z / luminance);
    }

    /// <summary>Bakes the whole sky into a cube, one texel at a time.</summary>
    /// <param name="size">One face's side in texels.</param>
    /// <param name="sky">The atmosphere.</param>
    /// <returns>A cube of luminance, ready for <c>EnvironmentTexture.Bake</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive.</exception>
    /// <remarks>
    ///     Small is fine and small is the point: a daylight sky has no detail below the sun's aureole,
    ///     so 32 or 64 a side captures everything the prefilter would keep — and the prefilter is the
    ///     expensive half.
    /// </remarks>
    public static CubeImage Bake(int size, in SkyParameters sky) {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        var image = new CubeImage(size);

        for (var face = 0; face < 6; face++) {
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    image.At((CubeFace)face, x, y) = Radiance(image.DirectionOf((CubeFace)face, x, y), sky);
                }
            }
        }

        return image;
    }

    /// <summary>The model proper: Perez at the view direction over Perez at the zenith.</summary>
    static Vector3 Sky(Vector3 view, Vector3 sun, float turbidity, float sunTheta) {
        var direction = Normalise(view);

        // Clamped for the same reason the sun's angle is: `1/cos θ` in the Perez distribution runs
        // away at the horizon, and every daylight implementation holds it just short of it.
        var cosTheta = MathF.Max(direction.Y, 0.001f);
        var gamma = MathF.Acos(Math.Clamp(Vector3.Dot(direction, sun), -1f, 1f));

        var t2 = turbidity * turbidity;
        var s2 = sunTheta * sunTheta;
        var s3 = s2 * sunTheta;

        // Zenith luminance, in kcd/m² — hence the thousand below.
        var chi = ((4f / 9f) - (turbidity / 120f)) * (MathF.PI - (2f * sunTheta));
        var zenithY = MathF.Max((((4.0453f * turbidity) - 4.9710f) * MathF.Tan(chi)) - (0.2155f * turbidity) + 2.4192f, 0f);

        var zenithX = (t2 * ((0.00166f * s3) - (0.00375f * s2) + (0.00209f * sunTheta)))
            + (turbidity * ((-0.02903f * s3) + (0.06377f * s2) - (0.03202f * sunTheta) + 0.00394f))
            + ((0.11693f * s3) - (0.21196f * s2) + (0.06052f * sunTheta) + 0.25886f);

        var zenithYc = (t2 * ((0.00275f * s3) - (0.00610f * s2) + (0.00317f * sunTheta)))
            + (turbidity * ((-0.04214f * s3) + (0.08970f * s2) - (0.04153f * sunTheta) + 0.00516f))
            + ((0.15346f * s3) - (0.26756f * s2) + (0.06670f * sunTheta) + 0.26688f);

        var luminance = zenithY
            * 1000f
            * Ratio(cosTheta, gamma, sunTheta, (0.1787f * turbidity) - 1.4630f, (-0.3554f * turbidity) + 0.4275f,
                (-0.0227f * turbidity) + 5.3251f, (0.1206f * turbidity) - 2.5771f, (-0.0670f * turbidity) + 0.3703f);

        var chromaX = zenithX
            * Ratio(cosTheta, gamma, sunTheta, (-0.0193f * turbidity) - 0.2592f, (-0.0665f * turbidity) + 0.0008f,
                (-0.0004f * turbidity) + 0.2125f, (-0.0641f * turbidity) - 0.8989f, (-0.0033f * turbidity) + 0.0452f);

        var chromaY = zenithYc
            * Ratio(cosTheta, gamma, sunTheta, (-0.0167f * turbidity) - 0.2608f, (-0.0950f * turbidity) + 0.0092f,
                (-0.0079f * turbidity) + 0.2102f, (-0.0441f * turbidity) - 1.6537f, (-0.0109f * turbidity) + 0.0529f);

        return XyzToLinear(XyYToXyz(chromaX, chromaY, MathF.Max(luminance, 0f)));
    }

    /// <summary>Perez's F at the view direction over F at the zenith — the model's whole shape.</summary>
    static float Ratio(float cosTheta, float gamma, float sunTheta, float a, float b, float c, float d, float e) {
        var at = Perez(cosTheta, gamma, a, b, c, d, e);
        var atZenith = Perez(1f, sunTheta, a, b, c, d, e);

        return MathF.Abs(atZenith) < 1e-6f ? 0f : at / atZenith;
    }

    /// <summary>The Perez luminance distribution: a horizon gradient times a solar aureole.</summary>
    static float Perez(float cosTheta, float gamma, float a, float b, float c, float d, float e) =>
        (1f + (a * MathF.Exp(b / MathF.Max(cosTheta, 0.001f))))
        * (1f + (c * MathF.Exp(d * gamma)) + (e * MathF.Cos(gamma) * MathF.Cos(gamma)));

    /// <summary>Rayleigh and Mie transmittance along the sun's path, per channel.</summary>
    /// <remarks>
    ///     <para>
    ///         Optical depths at 610, 550 and 465 nm — the sRGB primaries' rough centres — with
    ///         Rayleigh going as <c>λ^-4.08</c> and aerosol as <c>λ^-1.3</c> against a turbidity-driven
    ///         Ångström β. That difference in exponent <em>is</em> the sunset: at ten air masses the
    ///         blue is attenuated forty times harder than the red.
    ///     </para>
    ///     <para>
    ///         Kasten and Young's air mass rather than <c>1/cos θ</c>, because the two differ by a
    ///         factor of two below five degrees of elevation, and below five degrees is the whole
    ///         subject.
    ///     </para>
    /// </remarks>
    static Vector3 Transmittance(in SkyParameters sky) {
        var sun = -Normalise(sky.SunDirection);
        var elevation = MathF.Asin(Math.Clamp(sun.Y, -1f, 1f));

        return elevation <= 0f
            ? Vector3.Zero
            : Extinction(AirMass(elevation), Math.Clamp(sky.Turbidity, 1.8f, 10f));
    }

    /// <summary>Kasten and Young's relative optical air mass, at one for the sun overhead.</summary>
    /// <remarks>
    ///     Its own function because <see cref="DiffuseScale" /> asks for the path at two elevations
    ///     that are not the sun's — one and zero — and because <c>1/cos θ</c> is what it is <em>not</em>:
    ///     the two differ by a factor of two below five degrees, and below five degrees is the subject.
    /// </remarks>
    static float AirMass(float elevation) =>
        1f / (MathF.Sin(elevation) + (0.50572f * MathF.Pow(6.07995f + (elevation * 180f / MathF.PI), -1.6364f)));

    /// <summary>What survives a given path, per channel.</summary>
    static Vector3 Extinction(float airMass, float turbidity) {
        var beta = MathF.Max((0.04608f * turbidity) - 0.04586f, 0f);

        var rayleigh = new Vector3(0.0656f, 0.1001f, 0.1985f);
        var mie = new Vector3(1.901f, 2.175f, 2.706f) * beta;

        return new Vector3(
            MathF.Exp(-airMass * (rayleigh.X + mie.X)),
            MathF.Exp(-airMass * (rayleigh.Y + mie.Y)),
            MathF.Exp(-airMass * (rayleigh.Z + mie.Z))
        );
    }

    static Vector3 XyYToXyz(float x, float y, float luminance) {
        if (y <= 1e-4f) {
            return Vector3.Zero;
        }

        var scale = luminance / y;
        return new Vector3(x * scale, luminance, (1f - x - y) * scale);
    }

    /// <summary>CIE XYZ to linear sRGB, D65. Negatives clamped — the sky leaves the gamut near the sun.</summary>
    static Vector3 XyzToLinear(Vector3 xyz) =>
        new(
            MathF.Max((3.2404542f * xyz.X) - (1.5371385f * xyz.Y) - (0.4985314f * xyz.Z), 0f),
            MathF.Max((-0.9692660f * xyz.X) + (1.8760108f * xyz.Y) + (0.0415560f * xyz.Z), 0f),
            MathF.Max((0.0556434f * xyz.X) - (0.2040259f * xyz.Y) + (1.0572252f * xyz.Z), 0f)
        );

    static Vector3 Normalise(Vector3 value) =>
        value.LengthSquared() < 1e-12f ? new Vector3(0f, 1f, 0f) : Vector3.Normalize(value);
}
