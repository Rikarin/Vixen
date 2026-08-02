// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Lighting;
using Xunit;

namespace Tests;

/// <summary>
///     Colour temperature, luminous units and exposure — the arithmetic a scene's brightness rests on.
/// </summary>
/// <remarks>
///     Everything here is checked against a number somebody can look up: 6500 K is white, a lumen
///     over a sphere is a candela, EV 15 is bright sun. A photometric pipeline whose constants are
///     only self-consistent is one where every scene is wrong by the same factor and nobody can say
///     which factor.
/// </remarks>
public class PhotometryTests {
    /// <summary>
    ///     ⚠ The contract the whole thing rests on: a temperature tints and does not brighten.
    /// </summary>
    /// <remarks>
    ///     Without it, warming a lamp from 6500 K to 2000 K would also change how much light it emits
    ///     — so an author could not separate "what colour is this lamp" from "how bright is it", and
    ///     no photometric quantity would mean anything.
    /// </remarks>
    [Theory]
    [InlineData(1850f)]
    [InlineData(2700f)]
    [InlineData(4000f)]
    [InlineData(6500f)]
    [InlineData(12000f)]
    public void Every_temperature_has_unit_luminance(float kelvin) {
        var colour = Photometry.FromTemperature(kelvin);
        var luminance = (0.2126f * colour.R) + (0.7152f * colour.G) + (0.0722f * colour.B);

        Assert.Equal(1f, luminance, 3);
    }

    /// <summary>D65 is what sRGB is white against, so 6500 K comes out very close to neutral.</summary>
    [Fact]
    public void Daylight_is_white() {
        var colour = Photometry.FromTemperature(6500f);

        Assert.Equal(1f, colour.R, 1);
        Assert.Equal(1f, colour.G, 1);
        Assert.Equal(1f, colour.B, 1);
    }

    /// <summary>A flame is red-heavy and blue-poor; a clear sky is the other way round.</summary>
    [Fact]
    public void A_low_temperature_is_orange_and_a_high_one_is_blue() {
        var candle = Photometry.FromTemperature(1850f);
        var overcast = Photometry.FromTemperature(9000f);

        Assert.True(candle.R > candle.B * 3f, $"1850 K came out {candle}");
        Assert.True(overcast.B > overcast.R, $"9000 K came out {overcast}");
    }

    /// <summary>A point light spreads its lumens over the whole sphere: 4π steradians.</summary>
    [Fact]
    public void A_point_lights_lumens_become_candela_over_a_sphere() {
        var candela = Photometry.Intensity(LightKind.Point, LightUnit.Lumen, 4f * MathF.PI * 100f);

        Assert.Equal(100f, candela, 2);
    }

    /// <summary>
    ///     ⚠ A spot's cone is part of its brightness, which is what a reflector does.
    /// </summary>
    /// <remarks>
    ///     Lumens are power. Narrowing the cone of a 600-lumen lamp concentrates the same power into
    ///     less solid angle, so it gets brighter — dividing by 4π instead would give a spot whose
    ///     brightness does not change when it is focused, which is not what any real fitting does.
    /// </remarks>
    [Fact]
    public void Narrowing_a_spot_makes_it_brighter_for_the_same_lumens() {
        var wide = Photometry.Intensity(LightKind.Spot, LightUnit.Lumen, 600f, MathUtil.DegreesToRadians(60f));
        var narrow = Photometry.Intensity(LightKind.Spot, LightUnit.Lumen, 600f, MathUtil.DegreesToRadians(15f));

        Assert.True(narrow > wide * 10f, $"60° gave {wide} cd and 15° gave {narrow} cd");
    }

    /// <summary>
    ///     ⚠ <see cref="LightUnit.Native" /> is zero, and every unit but lumens passes through.
    /// </summary>
    /// <remarks>
    ///     A zeroed <c>Light</c> — one a scene saved before there were units deserialises into — has
    ///     to keep meaning what it meant. If the default were <see cref="LightUnit.Lumen" /> every
    ///     such light would be divided by 4π on load.
    /// </remarks>
    [Fact]
    public void A_light_that_says_nothing_about_its_unit_is_unconverted() {
        Assert.Equal(default, LightUnit.Native);
        Assert.Equal(700f, Photometry.Intensity(LightKind.Point, LightUnit.Native, 700f));
        Assert.Equal(700f, Photometry.Intensity(LightKind.Point, LightUnit.Candela, 700f));
        Assert.Equal(700f, Photometry.Intensity(LightKind.Directional, LightUnit.Lux, 700f));
    }

    /// <summary>The standard saturation-based exposure, and its inverse.</summary>
    [Fact]
    public void Exposure_and_its_exposure_value_round_trip() {
        var exposure = Photometry.ExposureFromEv100(14f);

        Assert.Equal(1f / (1.2f * 16384f), exposure, 9);
        Assert.Equal(14f, Photometry.Ev100FromExposure(exposure), 3);
    }

    /// <summary>The sunny-16 rule: f/16 at 1/125 s and ISO 100 is EV 15.</summary>
    /// <remarks>
    ///     The one line of this file that is checkable against something outside the engine, which is
    ///     why it is here. 1/125 s and not the 1/100 the rule is usually quoted with — "one over the
    ///     ISO" is a shutter speed cameras have, and 1/100 gives 14.64.
    /// </remarks>
    [Fact]
    public void A_camera_at_sunny_sixteen_is_exposure_value_fifteen() =>
        Assert.Equal(15f, Photometry.Ev100FromCamera(16f, 1f / 125f, 100f), 1);

    /// <summary>A light's radiance is its tint times its converted intensity, and nothing else.</summary>
    [Fact]
    public void A_lights_radiance_folds_in_its_unit_and_its_temperature() {
        var lamp = RenderLight.Point(Vector3.Zero, 10f, new Color3(1f, 1f, 1f), 4f * MathF.PI * 50f) with {
            Unit = LightUnit.Lumen,
            Temperature = 2200f
        };

        var tint = Photometry.FromTemperature(2200f);
        var radiance = lamp.Radiance;

        Assert.Equal(tint.R * 50f, radiance.X, 2);
        Assert.Equal(tint.G * 50f, radiance.Y, 2);
        Assert.Equal(tint.B * 50f, radiance.Z, 2);
    }
}

/// <summary>White balance, which is the inverse of the tint a light of the same temperature has.</summary>
public class WhiteBalanceTests {
    /// <summary>
    ///     ⚠ A lamp at 3200 K under a grade balanced to 3200 K comes out grey.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The property the whole arrangement exists for, and the reason white balance is computed
    ///         by <see cref="Photometry" /> rather than in the tonemapper: two fits of the Planckian
    ///         locus that nearly agree give a scene that is nearly neutral, which nobody can debug.
    ///         One curve read in two directions cancels the tint exactly.
    ///     </para>
    ///     <para>
    ///         ⚠ Grey, <em>not</em> white. The product's channels are equal and their common value is
    ///         below one, because neutralising a strongly tinted source means pulling its strong
    ///         channels down — a balance cannot add blue that the illuminant never emitted. What is
    ///         preserved is the luminance of a neutral, which is what
    ///         <see cref="EveryBalanceHasUnitLuminance" /> asserts.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(3200f)]
    [InlineData(5000f)]
    [InlineData(6500f)]
    public void BalancingToALightsTemperatureCancelsItsTint(float kelvin) {
        var lamp = Photometry.FromTemperature(kelvin);
        var balance = Photometry.WhiteBalance(kelvin, 0f);

        var red = lamp.R * balance.R;
        var green = lamp.G * balance.G;
        var blue = lamp.B * balance.B;

        Assert.True(red > 0f, $"{kelvin} K cancelled to nothing");
        Assert.Equal(red, green, 3);
        Assert.Equal(red, blue, 3);
    }

    /// <summary>
    ///     ⚠ A source that emits no blue cannot be balanced to neutral, and does not pretend to be.
    /// </summary>
    /// <remarks>
    ///     Below about 2000 K the Planckian locus leaves the sRGB gamut and
    ///     <see cref="Photometry.FromTemperature" /> clamps the blue primary to zero — so the emitter
    ///     really does produce none, and no per-channel gain recovers it. The two channels that exist
    ///     still cancel, which is the most a white balance can do and is what a real camera does with
    ///     a sodium lamp. Written down because the alternative reading — "the balance is broken at
    ///     1900 K" — is the one somebody will reach for.
    /// </remarks>
    [Fact]
    public void ASourceOutsideTheGamutCancelsOnTheChannelsItHas() {
        var lamp = Photometry.FromTemperature(1900f);
        var balance = Photometry.WhiteBalance(1900f, 0f);

        Assert.Equal(0f, lamp.B);
        Assert.Equal(lamp.R * balance.R, lamp.G * balance.G, 3);
    }

    /// <summary>Zero leaves the frame alone, which is what a document that says nothing gets.</summary>
    [Fact]
    public void NoTemperatureAndNoTintIsNeutral() {
        var balance = Photometry.WhiteBalance(0f, 0f);

        Assert.Equal(1f, balance.R, 4);
        Assert.Equal(1f, balance.G, 4);
        Assert.Equal(1f, balance.B, 4);
    }

    /// <summary>
    ///     Balancing to a warm illuminant cools the image, which is the direction a camera dial works.
    /// </summary>
    [Fact]
    public void BalancingToAWarmSourceCoolsTheImage() {
        var balance = Photometry.WhiteBalance(2700f, 0f);

        Assert.True(balance.B > balance.R, $"balancing to 2700 K gave {balance}, which is not cooler");
    }

    /// <summary>The tint axis moves green against the other two and changes nothing else.</summary>
    [Fact]
    public void TintMovesGreenAgainstMagenta() {
        var green = Photometry.WhiteBalance(0f, -0.5f);
        var magenta = Photometry.WhiteBalance(0f, 0.5f);

        Assert.True(green.G > magenta.G, $"{green} against {magenta}");
        Assert.True(magenta.R > green.R, $"{green} against {magenta}");
    }

    /// <summary>
    ///     ⚠ Unit luminance, so a grade cannot change the exposure.
    /// </summary>
    /// <remarks>
    ///     White balance says what colour, not how much. One that carried brightness would make
    ///     correcting a warm scene darken it, and `ev100` would then be a number that depended on the
    ///     grade.
    /// </remarks>
    [Theory]
    [InlineData(2000f, 0f)]
    [InlineData(6500f, 0.7f)]
    [InlineData(0f, -1f)]
    public void EveryBalanceHasUnitLuminance(float kelvin, float tint) {
        var balance = Photometry.WhiteBalance(kelvin, tint);
        var luminance = (0.2126f * balance.R) + (0.7152f * balance.G) + (0.0722f * balance.B);

        Assert.Equal(1f, luminance, 3);
    }
}

/// <summary>
///     The analytic daylight sky, checked at the elevations a scene is actually lit at.
/// </summary>
/// <remarks>
///     Preetham's model is four dozen fitted constants and a typo in any of them produces a sky that
///     still looks like a sky. So these assert the <em>relations</em> that make it worth having: the
///     sun's illuminance falls with elevation, its colour reddens, and the horizon is brighter than
///     the zenith when the sun is low.
/// </remarks>
public class PhysicalSkyTests {
    static SkyParameters At(float elevationDegrees, float turbidity = 3f) {
        var elevation = MathUtil.DegreesToRadians(elevationDegrees);

        // The direction light *travels*, which is down and away from the sun.
        return new(
            new Vector3(-MathF.Cos(elevation), -MathF.Sin(elevation), 0f),
            turbidity
        );
    }

    /// <summary>Clear midday sun is about a hundred thousand lux, which is a number anyone can check.</summary>
    [Fact]
    public void The_sun_overhead_is_about_a_hundred_thousand_lux() {
        var lux = PhysicalSky.SunIlluminance(At(90f));

        Assert.InRange(lux, 80000f, 115000f);
    }

    /// <summary>
    ///     ⚠ The relation the whole model exists for: elevation decides everything at once.
    /// </summary>
    /// <remarks>
    ///     Ten degrees of elevation is roughly six air masses, so the sun is an order of magnitude
    ///     dimmer than overhead and much redder. A scene that typed both numbers in by hand would
    ///     eventually have a sunset sky over a noon sun.
    /// </remarks>
    [Fact]
    public void A_low_sun_is_dimmer_and_redder_than_a_high_one() {
        var noon = PhysicalSky.SunIlluminance(At(90f));
        var evening = PhysicalSky.SunIlluminance(At(8f));

        Assert.True(evening < noon * 0.35f, $"overhead {noon} lx against {evening} lx at 8°");

        var high = PhysicalSky.SunTint(At(90f));
        var low = PhysicalSky.SunTint(At(8f));

        Assert.True(low.R / low.B > high.R / high.B * 3f, $"overhead {high} against {low} at 8°");
    }

    /// <summary>Below the horizon there is no sun at all, and nothing goes negative.</summary>
    [Fact]
    public void A_sun_below_the_horizon_delivers_nothing() =>
        Assert.Equal(0f, PhysicalSky.SunIlluminance(At(-5f)));

    /// <summary>The sun overhead leaves the published fit exactly as published.</summary>
    /// <remarks>
    ///     <see cref="PhysicalSky.DiffuseScale" /> is a correction and not a dimmer, so the one place
    ///     the fit is unquestionably inside its own range is the place it must be untouched.
    /// </remarks>
    [Fact]
    public void The_sky_is_unscaled_with_the_sun_overhead() =>
        Assert.Equal(1f, PhysicalSky.DiffuseScale(At(90f)), 2);

    /// <summary>
    ///     ⚠ The sky dims with the sun, which the fit on its own does not do.
    /// </summary>
    /// <remarks>
    ///     Preetham's zenith luminance bottoms out near 1900 cd/m² whatever the sun does, so a scene
    ///     authored by moving the sun down gets a disc a thousand times dimmer under a sky that has not
    ///     moved. Every attempt at a sunset finds it: the level goes flat and orange rather than dark
    ///     and orange, and no amount of exposure separates the two again. The zenith numbers below are
    ///     what a clear sky measures.
    /// </remarks>
    [Theory]
    [InlineData(30f, 3000f, 6000f)]
    [InlineData(6f, 700f, 2000f)]
    [InlineData(0.2f, 40f, 500f)]
    public void The_zenith_falls_with_the_sun_rather_than_stopping_at_the_fit(
        float elevation,
        float least,
        float most
    ) {
        var zenith = PhysicalSky.Radiance(Vector3.UnitY, At(elevation, turbidity: 2.6f)).Y;

        Assert.InRange(zenith, least, most);
    }

    /// <summary>Twilight is continuous through the horizon and keeps falling below it.</summary>
    /// <remarks>
    ///     The two sides are different arguments — extinction above, an extrapolation below — and a
    ///     step between them would be a sky that jumps a stop as the sun crosses zero.
    /// </remarks>
    [Fact]
    public void The_sky_crosses_the_horizon_without_a_step_and_keeps_falling() {
        var above = PhysicalSky.DiffuseScale(At(0.01f));
        var below = PhysicalSky.DiffuseScale(At(-0.01f));
        var deeper = PhysicalSky.DiffuseScale(At(-4f));

        // Relative, because the two sides are steep here and both are a few hundredths: the question
        // is whether they meet, and a fixed number of decimal places would be asking how small they
        // are instead.
        Assert.True(MathF.Abs(above - below) < above * 0.05f, $"{above} above the horizon, {below} below");
        Assert.True(deeper < below * 0.2f, $"{below} just under the horizon against {deeper} at −4°");
        Assert.True(deeper > 0f, "twilight went to nothing rather than towards it");
    }

    /// <summary>The sky is luminance, in the thousands, and finite everywhere.</summary>
    [Theory]
    [InlineData(60f)]
    [InlineData(20f)]
    [InlineData(3f)]
    public void The_sky_is_positive_and_finite_all_the_way_round(float elevation) {
        var sky = At(elevation);

        foreach (var direction in Directions()) {
            var radiance = PhysicalSky.Radiance(direction, sky);

            Assert.True(float.IsFinite(radiance.X) && radiance.X >= 0f, $"{direction} gave {radiance}");
            Assert.True(float.IsFinite(radiance.Y) && radiance.Y >= 0f, $"{direction} gave {radiance}");
            Assert.True(float.IsFinite(radiance.Z) && radiance.Z >= 0f, $"{direction} gave {radiance}");
        }
    }

    /// <summary>Looking at the sun is far brighter than looking away from it.</summary>
    [Fact]
    public void The_solar_aureole_is_the_brightest_part_of_the_sky() {
        var sky = At(25f);
        var toward = PhysicalSky.Radiance(-sky.SunDirection, sky);
        var away = PhysicalSky.Radiance(Vector3.Normalize(new(1f, 0.4f, 0f)), sky);

        Assert.True(toward.Y > away.Y, $"toward the sun {toward.Y}, away {away.Y}");
    }

    /// <summary>Below the horizon is the ground bounce, which is dimmer than the sky above it.</summary>
    /// <remarks>
    ///     Not black, which is what a cube baked without it has — and a black lower hemisphere is an
    ///     ambient term that stops dead at every surface's equator.
    /// </remarks>
    [Fact]
    public void The_ground_is_a_dimmed_horizon_rather_than_black() {
        var sky = At(30f) with { GroundAlbedo = 0.2f };
        var down = PhysicalSky.Radiance(new Vector3(0.3f, -0.9f, 0f), sky);
        var up = PhysicalSky.Radiance(new Vector3(0.3f, 0.9f, 0f), sky);

        Assert.True(down.Y > 0f, "the ground came out black");
        Assert.True(down.Y < up.Y, $"the ground {down.Y} is not dimmer than the sky {up.Y}");
    }

    /// <summary>A bake fills every texel of every face.</summary>
    [Fact]
    public void A_baked_cube_has_light_on_all_six_faces() {
        var image = PhysicalSky.Bake(8, At(35f));

        for (var face = 0; face < 6; face++) {
            var total = 0f;

            for (var y = 0; y < 8; y++) {
                for (var x = 0; x < 8; x++) {
                    total += image.At((CubeFace)face, x, y).Y;
                }
            }

            Assert.True(total > 0f, $"face {face} is entirely black");
        }
    }

    static IEnumerable<Vector3> Directions() {
        for (var pitch = -80; pitch <= 80; pitch += 20) {
            for (var yaw = 0; yaw < 360; yaw += 30) {
                var p = MathUtil.DegreesToRadians(pitch);
                var y = MathUtil.DegreesToRadians(yaw);

                yield return new(MathF.Cos(p) * MathF.Cos(y), MathF.Sin(p), MathF.Cos(p) * MathF.Sin(y));
            }
        }
    }
}
