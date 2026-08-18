// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Cameras;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
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

/// <summary>What a newly created light is — which is a photometric claim, not a convenience.</summary>
/// <remarks>
///     <para>
///         <c>Lights.Default</c> used to hand back <c>Intensity = 1</c> for all five kinds with the unit
///         left at <see cref="LightUnit.Native" />. For a point that is a dim candle and for the sun it
///         is one lux — four to five decimal orders below the sky the sun is standing under, which
///         renders as a frame with no sun in it at all. Every assertion below is the shape of that
///         failure: a magnitude against a real lamp, and the exposure the number has to survive.
///     </para>
///     <para>
///         ⚠ <b>Bands rather than equalities.</b> The point is not that the sun is 100 klx; it is that
///         the sun is daylight and not a rounding error. An equality here would make retuning a default
///         a test edit, which is how a band this wide stops meaning anything.
///     </para>
/// </remarks>
public class DefaultLightTests {
    /// <summary>What the editor's viewport grades at — <c>EditorWorldRenderer.Ev100</c>.</summary>
    /// <remarks>
    ///     Duplicated rather than referenced, because that constant is the editor's and this assembly
    ///     sits underneath it. It is here because the pane in which a seeded sun did nothing is graded
    ///     at exactly this, and an exposure the assertion invented would be an assertion about nothing.
    /// </remarks>
    const float ViewportEv100 = 13.5f;

    /// <summary>
    ///     ⚠ Every default states its unit, and stating it changes no arithmetic.
    /// </summary>
    /// <remarks>
    ///     The kind's own unit and <see cref="LightUnit.Native" /> are the same conversion — only
    ///     <see cref="LightUnit.Lumen" /> ever divides — so a default may label its number without
    ///     changing what any shader multiplies. That equality is the second half of this test, and it
    ///     is what makes the label free rather than a change of meaning.
    /// </remarks>
    [Theory]
    [InlineData(LightKind.Directional, LightUnit.Lux)]
    [InlineData(LightKind.Point, LightUnit.Candela)]
    [InlineData(LightKind.Spot, LightUnit.Candela)]
    [InlineData(LightKind.Rect, LightUnit.Nits)]
    [InlineData(LightKind.Tube, LightUnit.Nits)]
    public void Every_default_says_what_its_number_is_measured_in(LightKind kind, LightUnit unit) {
        var light = Lights.Default(kind);

        Assert.Equal(unit, light.Unit);

        Assert.Equal(Converted(light, LightUnit.Native), Converted(light, light.Unit));
    }

    static float Converted(Light light, LightUnit unit) =>
        Photometry.Intensity(light.Kind, unit, light.Intensity, light.OuterAngle, light.Radius, light.HalfLength);

    /// <summary>
    ///     ⚠ Every default is a lamp somebody could buy, in the unit that kind is measured in.
    /// </summary>
    /// <remarks>
    ///     Lux for the sun, candela for the two punctual kinds, nits for the two area ones — the four
    ///     that have a position are the same 1600-lumen bulb converted through the geometry each one
    ///     has, which is why the spot is an order brighter than the point for the same lamp.
    /// </remarks>
    [Theory]
    [InlineData(LightKind.Directional, 10_000f, 150_000f)]
    [InlineData(LightKind.Point, 60f, 400f)]
    [InlineData(LightKind.Spot, 600f, 6_000f)]
    [InlineData(LightKind.Rect, 100f, 1_500f)]
    [InlineData(LightKind.Tube, 500f, 5_000f)]
    public void Every_default_is_a_real_lamp_in_its_own_unit(LightKind kind, float least, float most) =>
        Assert.InRange(Lights.Default(kind).Intensity, least, most);

    /// <summary>A new sun is a clear midday one, which is the number the sky model also produces.</summary>
    /// <remarks>
    ///     Checked against <see cref="PhysicalSky.SunIlluminance" /> rather than against itself: the
    ///     two are the answers to the same question — how much light a surface facing the sun gets —
    ///     and a default that disagreed with the engine's own atmosphere by an order would make
    ///     "turn on the sky" a change of exposure.
    /// </remarks>
    [Fact]
    public void A_new_sun_is_the_daylight_the_sky_model_computes() {
        var overhead = PhysicalSky.SunIlluminance(
            new SkyParameters(new Vector3(0f, -1f, 0f), 3f)
        );

        Assert.InRange(Lights.Default(LightKind.Directional).Intensity, overhead * 0.5f, overhead * 2f);
    }

    /// <summary>
    ///     ⚠ The assertion this whole file was missing: a default sun survives the frame's exposure.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Photometric scale blindness.</b> A light four decimal orders below the frame it is in
    ///         is pixel-identical to a light that is not there — so "the sun was extracted", "the sun
    ///         reached the GPU" and "the shadow map was drawn" all pass while the picture is a picture
    ///         of the sky alone. Nothing above this line would have caught it.
    ///     </para>
    ///     <para>
    ///         What is computed is a 0.18 grey card facing the sun, graded through
    ///         <see cref="ViewportEv100" />: <c>E · ρ / π · exposure</c>. At the old default of one lux
    ///         that is 4·10⁻⁶ — black on any display — and the lower bound below is three orders above
    ///         it. The upper bound is the other failure: a default that clips the frame to white is
    ///         also a picture of the exposure rather than of the scene.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_default_sun_lights_a_grey_card_rather_than_vanishing_into_the_exposure() {
        var sun = LightExtractionSystem.Convert(Lights.Default(LightKind.Directional), Matrix4x4.Identity);
        var displayed = sun.Radiance.Y * 0.18f / MathF.PI * Photometry.ExposureFromEv100(ViewportEv100);

        Assert.InRange(displayed, 0.05f, 1f);
    }

    /// <summary>
    ///     ⚠ A default with the geometry read before it is set would divide by a zero solid angle.
    /// </summary>
    /// <remarks>
    ///     The spot's conversion needs its outer angle and the two area kinds need their extents, all
    ///     of which the factory sets in a switch. Computing the intensity in the initialiser instead
    ///     would clamp against <c>Photometry</c>'s 1e-4 floors and produce sixteen million candela,
    ///     which is a number that renders as pure white rather than as an error.
    /// </remarks>
    [Theory]
    [InlineData(LightKind.Spot)]
    [InlineData(LightKind.Rect)]
    [InlineData(LightKind.Tube)]
    public void A_shaped_default_converts_against_the_shape_it_was_given(LightKind kind) {
        var light = Lights.Default(kind);

        Assert.Equal(
            Photometry.Intensity(kind, LightUnit.Lumen, 1600f, light.OuterAngle, light.Radius, light.HalfLength),
            light.Intensity,
            2
        );
    }
}

/// <summary>The lens and the sensor, which decide the framing and the exposure together.</summary>
/// <remarks>
///     What makes this worth being one component rather than three sliders is that the numbers are
///     shared: the aperture is in the exposure and in the defocus, and the focal length is in the field
///     of view and in the defocus. These assert the shared ones agree.
/// </remarks>
public class PhysicalCameraTests {
    /// <summary>
    ///     ⚠ <c>Camera.Ev100</c> and <c>Photometry.Ev100FromCamera</c> are the same formula twice.
    /// </summary>
    /// <remarks>
    ///     They have to be: <c>Photometry</c> is <c>Vixen.Rendering</c>'s and <c>Camera</c> is
    ///     <c>Vixen.Engine</c>'s, which is the assembly underneath — so the component cannot call the
    ///     function and the duplicate is the only way the value reaches both. This is what stops it
    ///     drifting, and it is the reason the duplicate is allowed to exist at all.
    /// </remarks>
    [Theory]
    [InlineData(16f, 1f / 125f, 100f)]
    [InlineData(1.4f, 1f / 60f, 800f)]
    [InlineData(5.6f, 1f / 30f, 200f)]
    public void TheComponentsExposureIsPhotometrys(float aperture, float shutter, float iso) {
        var lens = Camera.Perspective with { Aperture = aperture, ShutterTime = shutter, Sensitivity = iso };

        Assert.Equal(Photometry.Ev100FromCamera(aperture, shutter, iso), lens.Ev100, 5);
    }

    /// <summary>
    ///     ⚠ A field of view written back reads back, which is what makes the property a property.
    /// </summary>
    /// <remarks>
    ///     <c>FieldOfView</c> is not stored — it solves for a focal length on the way in and derives
    ///     the angle again on the way out — so the round trip is the whole contract. Every line in the
    ///     engine that says <c>Camera.Perspective with { FieldOfView = x }</c> depends on it.
    /// </remarks>
    [Theory]
    [InlineData(30f)]
    [InlineData(60f)]
    [InlineData(90f)]
    [InlineData(120f)]
    public void AnAngleWrittenIsTheAngleReadBack(float degrees) {
        var camera = Camera.Perspective with { FieldOfView = MathUtil.DegreesToRadians(degrees) };

        Assert.Equal(degrees, MathUtil.RadiansToDegrees(camera.FieldOfView), 3);
    }

    /// <summary>
    ///     ⚠ Setting an angle on a camera with no sensor gives it one, rather than storing nothing.
    /// </summary>
    /// <remarks>
    ///     A focal length is an angle <em>against a sensor height</em>. With the height at zero the
    ///     solve gives zero, and a zero focal length reads back as the fallback angle — a property
    ///     that silently refuses to be written. So the setter supplies full frame first.
    /// </remarks>
    [Fact]
    public void AnAngleOnASensorlessCameraSuppliesASensor() {
        var camera = default(Camera);
        camera.FieldOfView = MathUtil.DegreesToRadians(45f);

        Assert.True(camera.HasLens);
        Assert.Equal(45f, MathUtil.RadiansToDegrees(camera.FieldOfView), 3);
    }

    /// <summary>
    ///     ⚠ A lens named by its round number frames the angle a photographer expects.
    /// </summary>
    /// <remarks>
    ///     <c>WithLens</c> is the other convention from <c>Perspective</c>'s — a round focal length
    ///     with the angle falling out, rather than a round angle with the focal length falling out.
    ///     Both are standard in different industries, and having both named is what stops either one
    ///     looking like an unexplained constant. These are the numbers on the barrel of a real lens.
    /// </remarks>
    [Theory]
    [InlineData(18f, 67.4f)]
    [InlineData(24f, 53.1f)]
    [InlineData(35f, 37.8f)]
    [InlineData(50f, 27.0f)]
    [InlineData(85f, 16.1f)]
    public void A_lens_named_by_its_length_frames_the_angle_it_should(float millimetres, float degrees) {
        var camera = Camera.WithLens(millimetres);

        Assert.Equal(degrees, MathUtil.RadiansToDegrees(camera.FieldOfView), 1);
        Assert.Equal(millimetres, camera.FocalLength);
    }

    /// <summary>
    ///     ⚠ A lens changes the framing and nothing else — not the exposure, not the sensor.
    /// </summary>
    /// <remarks>
    ///     The aperture, the shutter and the ISO are what decide the exposure, and a focal length is
    ///     in none of them. A <c>WithLens</c> that quietly reset them would make "put a 50 on it"
    ///     change the brightness of the picture, which is exactly the coupling one camera component
    ///     exists to remove.
    /// </remarks>
    [Fact]
    public void Naming_a_lens_leaves_the_exposure_alone() {
        var camera = Camera.WithLens(50f);

        Assert.Equal(Camera.Perspective.Ev100, camera.Ev100, 5);
        Assert.Equal(Camera.Perspective.SensorWidth, camera.SensorWidth);
        Assert.Equal(Camera.Perspective.FarPlane, camera.FarPlane);
    }

    /// <summary>
    ///     ⚠ The same lens on a smaller sensor is a longer lens, which is why both are given.
    /// </summary>
    /// <remarks>
    ///     35 mm is a documentary wide on full frame and a normal lens on Super 35. A focal length
    ///     quoted without a sensor says nothing, so anything reproducing a real camera has to give
    ///     both — and this holds that the second overload actually uses the one it was given.
    /// </remarks>
    [Fact]
    public void A_smaller_sensor_makes_the_same_lens_narrower() {
        var full = Camera.WithLens(35f);
        var super35 = Camera.WithLens(35f, 24.89f, 18.67f);

        Assert.True(
            super35.FieldOfView < full.FieldOfView,
            $"full frame gave {MathUtil.RadiansToDegrees(full.FieldOfView):F1}° and "
            + $"Super 35 gave {MathUtil.RadiansToDegrees(super35.FieldOfView):F1}°"
        );

        Assert.Equal(29.9f, MathUtil.RadiansToDegrees(super35.FieldOfView), 1);
    }

    /// <summary>
    ///     ⚠ Shallow focus costs field of view, which is the trade one component makes visible.
    /// </summary>
    /// <remarks>
    ///     The question the default camera raises the first time somebody turns on <c>!DepthOfField</c>
    ///     and sees nothing. It is not a fault in the pass: depth of field falls with the square of
    ///     the focal length, and <c>Perspective</c> is a 20.8 mm ultra-wide because a game's field of
    ///     view is about twice a film camera's. A longer lens is what buys the blur, and it costs the
    ///     framing — which is the trade a cinematographer actually makes.
    /// </remarks>
    [Fact]
    public void A_longer_lens_defocuses_far_more_and_sees_far_less() {
        var wide = Camera.Perspective with { FocusDistance = 5f };
        var portrait = Camera.WithLens(85f) with { FocusDistance = 5f };

        Assert.True(
            portrait.CircleOfConfusion(12f) > wide.CircleOfConfusion(12f) * 10f,
            $"85 mm blurred {portrait.CircleOfConfusion(12f):F6} m against the wide's "
            + $"{wide.CircleOfConfusion(12f):F6} m"
        );

        Assert.True(portrait.FieldOfView < wide.FieldOfView * 0.3f, "the long lens did not cost any framing");
    }

    /// <summary>The default camera frames exactly what it framed before it had a lens.</summary>
    /// <remarks>
    ///     ⚠ The reason <c>Perspective</c> derives its focal length from 60° rather than naming a
    ///     round 35 mm: every sample and every test was composed through that angle, and a lens chosen
    ///     for being a nice number would have reframed all of them.
    /// </remarks>
    [Fact]
    public void TheDefaultCameraIsStillSixtyDegrees() =>
        Assert.Equal(60f, MathUtil.RadiansToDegrees(Camera.Perspective.FieldOfView), 3);

    /// <summary>A 50 mm lens on full frame is the ~40° diagonal everybody calls normal.</summary>
    /// <remarks>
    ///     Checkable outside the engine, which is why it is here: 24 mm of sensor height over a 50 mm
    ///     focal length is 27° vertically, and that is what "a nifty fifty" looks like.
    /// </remarks>
    [Fact]
    public void AFiftyMillimetreLensOnFullFrameIsTwentySevenDegrees() {
        var lens = Camera.Perspective with { FocalLength = 50f };

        Assert.Equal(27f, MathUtil.RadiansToDegrees(lens.FieldOfView), 0);
        Assert.Equal(39.6f, MathUtil.RadiansToDegrees(lens.HorizontalFieldOfView), 0);
    }

    /// <summary>A shorter lens sees more, which is the whole of what a focal length is.</summary>
    [Fact]
    public void AShorterLensSeesMore() {
        var wide = Camera.Perspective with { FocalLength = 18f };
        var tight = Camera.Perspective with { FocalLength = 200f };

        Assert.True(wide.FieldOfView > tight.FieldOfView * 5f);
    }

    /// <summary>
    ///     ⚠ The exposure is the same function a light meter implements.
    /// </summary>
    /// <remarks>
    ///     Sunny 16 — f/16 at one over the ISO — is EV 15, and a camera set to it here has to say so
    ///     or the whole point of the component is lost.
    /// </remarks>
    [Fact]
    public void SunnySixteenIsExposureValueFifteen() {
        var lens = Camera.Perspective with { Aperture = 16f, ShutterTime = 1f / 125f, Sensitivity = 100f };

        Assert.Equal(15f, lens.Ev100, 1);
    }

    /// <summary>Opening a stop halves the exposure value, which is what a stop is.</summary>
    [Fact]
    public void OpeningOneStopMovesTheExposureValueByOne() {
        var closed = Camera.Perspective with { Aperture = 4f };
        var open = Camera.Perspective with { Aperture = 2.8f };

        Assert.Equal(1f, closed.Ev100 - open.Ev100, 1);
    }

    /// <summary>
    ///     ⚠ A zeroed component is not a camera, and says so rather than producing infinities.
    /// </summary>
    /// <remarks>
    ///     Zero is what an entity gets by default and what a scene saved before this existed
    ///     deserialises into. A sensor of zero width has an infinite field of view; an aperture of zero
    ///     has an exposure value of minus infinity. <c>HasLens</c> is what extraction asks first.
    /// </remarks>
    [Fact]
    public void AZeroedLensIsNotValidAndFallsBackRatherThanDividing() {
        var zeroed = default(Camera);

        Assert.False(zeroed.HasLens);
        Assert.True(float.IsFinite(zeroed.FieldOfView));
        Assert.Equal(0f, zeroed.CircleOfConfusion(10f));
        Assert.True(Camera.Perspective.HasLens);
    }

    /// <summary>Nothing is defocused at the focus distance, and more is further from it.</summary>
    [Fact]
    public void DefocusIsZeroAtTheFocalPlaneAndGrowsEitherSide() {
        var lens = Camera.Perspective with { FocusDistance = 5f, Aperture = 1.4f };

        Assert.Equal(0f, lens.CircleOfConfusion(5f), 6);
        Assert.True(lens.CircleOfConfusion(20f) > lens.CircleOfConfusion(8f));
        Assert.True(lens.CircleOfConfusion(1f) > lens.CircleOfConfusion(4f));
    }

    /// <summary>
    ///     ⚠ A wider aperture defocuses more — the same number that brightened the exposure.
    /// </summary>
    /// <remarks>
    ///     This is the property the whole component exists for. Two unrelated sliders can be set so
    ///     that a bright image has deep focus, which no lens does.
    /// </remarks>
    [Fact]
    public void AWiderApertureBothBrightensAndDefocuses() {
        var open = Camera.Perspective with { FocusDistance = 5f, Aperture = 1.4f };
        var closed = open with { Aperture = 11f };

        Assert.True(open.Ev100 < closed.Ev100, "the wider aperture did not need less light");
        Assert.True(open.CircleOfConfusion(20f) > closed.CircleOfConfusion(20f), "it did not blur more either");
    }

    /// <summary>Focused at infinity nothing is defocused, which is what a frame with no focus wants.</summary>
    [Fact]
    public void FocusingAtInfinityLeavesEverythingSharp() {
        var lens = Camera.Perspective with { FocusDistance = 0f };

        Assert.Equal(0f, lens.CircleOfConfusion(1f));
        Assert.Equal(0f, lens.CircleOfConfusion(1000f));
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
