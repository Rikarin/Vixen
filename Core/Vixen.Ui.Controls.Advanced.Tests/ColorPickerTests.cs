// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The conversions, which everything else in the picker rests on.</summary>
public class ColorModelTests {
    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0.2f, 0.6f, 0.9f)]
    [InlineData(0.5f, 0.5f, 0.5f)]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, 1f, 1f)]
    public void Hsv_round_trips(float r, float g, float b) {
        var colour = new Color4(r, g, b, 1f);
        var back = Hsv.FromRgb(colour).ToRgb();

        Assert.Equal(colour.R, back.R, 4);
        Assert.Equal(colour.G, back.G, 4);
        Assert.Equal(colour.B, back.B, 4);
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0.2f, 0.6f, 0.9f)]
    [InlineData(0.5f, 0.5f, 0.5f)]
    [InlineData(1f, 1f, 1f)]
    public void OkLch_round_trips_through_linear_light(float r, float g, float b) {
        var colour = new Color4(r, g, b, 1f);
        var back = OkLch.FromSrgb(colour).ToSrgb();

        // ⚠ The conversion has to go via linear RGB. Feeding Oklab sRGB-encoded values produces
        // something that is not Oklab, and the round trip is the cheapest way to catch it.
        Assert.Equal(colour.R, back.R, 3);
        Assert.Equal(colour.G, back.G, 3);
        Assert.Equal(colour.B, back.B, 3);
    }

    [Fact]
    public void Oklab_lightness_ranks_colours_the_way_an_eye_does() {
        // The reason the model is offered at all: HSV says these are equally bright and they are
        // nothing of the kind.
        var yellow = Hsv.FromRgb(new Color4(1f, 1f, 0f, 1f));
        var blue = Hsv.FromRgb(new Color4(0f, 0f, 1f, 1f));

        Assert.Equal(yellow.V, blue.V, 4);

        Assert.True(
            OkLch.FromSrgb(new Color4(1f, 1f, 0f, 1f)).L > OkLch.FromSrgb(new Color4(0f, 0f, 1f, 1f)).L + 0.3f
        );
    }

    [Fact]
    public void A_chroma_no_monitor_can_show_is_reported_as_out_of_gamut() {
        Assert.True(new OkLch(0.6f, 0.05f, 30f).IsInGamut);
        Assert.False(new OkLch(0.6f, 0.35f, 200f).IsInGamut);
    }

    /// <summary>The colour the old per-channel clamp would have produced, for comparison.</summary>
    /// <remarks>
    ///     ⚠ Written out here rather than described, because the test below is only worth running if
    ///     the two answers differ — and on any colour already inside the gamut they do not. This is
    ///     what turns "the mapping is right" into a claim that could have been false.
    /// </remarks>
    static OkLch Clamped(OkLch colour) {
        var radians = colour.H * MathF.PI / 180f;
        var linear = new Oklab(colour.L, colour.C * MathF.Cos(radians), colour.C * MathF.Sin(radians)).ToLinear();

        return OkLch.FromSrgb(
            new Color4(
                ColorSpace.LinearToSrgb(Math.Clamp(linear.X, 0f, 1f)),
                ColorSpace.LinearToSrgb(Math.Clamp(linear.Y, 0f, 1f)),
                ColorSpace.LinearToSrgb(Math.Clamp(linear.Z, 0f, 1f)),
                1f
            )
        );
    }

    [Fact]
    public void An_unshowable_colour_gives_up_its_chroma_and_keeps_its_hue() {
        var wanted = new OkLch(0.6f, 0.35f, 200f);

        // Vacuous on anything showable: clipping and mapping agree everywhere inside the gamut, so a
        // plausible-looking colour would pass against the clamp this replaced.
        Assert.False(wanted.IsInGamut);

        // ⚠ **And the instrument is checked before the claim.** The clamp does not merely give a
        // different number here, it gives a different *hue* — which is the defect, and is what makes
        // the assertions below able to fail.
        Assert.True(
            MathF.Abs(Clamped(wanted).H - wanted.H) > 2f,
            "the fixture has to be a colour the clamp visibly moves, or it proves nothing"
        );

        var shown = OkLch.FromSrgb(wanted.ToSrgb());

        Assert.True(shown.IsInGamut);

        // The hue and the lightness survive; the chroma is what was spent. The tolerance is not zero
        // because CSS Color 4 finishes with a clip inside one just-noticeable difference of the
        // boundary — the local MINDE — which recovers chroma at the price of a hue shift nobody can
        // see. Two degrees is far below that and far above what the clamp does.
        Assert.Equal(wanted.H, shown.H, 2f);
        Assert.Equal(wanted.L, shown.L, 0.02f);
        Assert.True(shown.C < wanted.C - 0.05f, "the chroma is what comes down");
    }

    [Theory]
    [InlineData("#f00", 1f, 0f, 0f, 1f)]
    [InlineData("f00", 1f, 0f, 0f, 1f)]
    [InlineData("#00ff00", 0f, 1f, 0f, 1f)]
    [InlineData("#0000ff80", 0f, 0f, 1f, 0.502f)]
    public void Hexadecimal_is_read_in_every_shape_people_write_it(string text, float r, float g, float b, float a) {
        Assert.True(Hex.TryParse(text, out var colour));

        Assert.Equal(r, colour.R, 2);
        Assert.Equal(g, colour.G, 2);
        Assert.Equal(b, colour.B, 2);
        Assert.Equal(a, colour.A, 2);
    }

    [Fact]
    public void A_short_form_digit_is_doubled_rather_than_shifted() {
        Assert.True(Hex.TryParse("#f80", out var colour));

        // ⚠ `#f80` is `#ff8800`, not `#f08000`. The shifted version is six percent too dark, which
        // nobody spots in review and everybody sees on screen.
        Assert.Equal("#ff8800", Hex.ToString(colour));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12zz34")]
    [InlineData("#12345")]
    [InlineData("rebeccapurple")]
    public void Nonsense_is_refused_rather_than_guessed_at(string text) => Assert.False(Hex.TryParse(text, out _));

    [Fact]
    public void An_opaque_colour_is_written_without_an_alpha_pair() {
        Assert.Equal("#3b6cf0", Hex.ToString(new Color4(0.2314f, 0.4235f, 0.9412f, 1f)));
        Assert.Equal("#3b6cf080", Hex.ToString(new Color4(0.2314f, 0.4235f, 0.9412f, 0.502f)));
    }
}

/// <summary>The control: the field, the strips, the palette, HDR and the dropper.</summary>
public class ColorPickerTests {
    static ColorPicker Picker(AdvancedFixture fixture) {
        var picker = fixture.Add<ColorPicker>();
        fixture.Update();

        return picker;
    }

    [Fact]
    public void Dragging_across_the_field_changes_the_saturation_and_the_value() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Value = new Color4(1f, 0f, 0f, 1f);
        fixture.Update();

        var bounds = picker.Field.Bounds;

        // Top-left is white — no saturation, full value — whatever the hue is. Pressed inside and
        // dragged to the corner, because the press has to hit-test and the corner is half-open.
        fixture.Press(bounds.X + 4f, bounds.Y + 4f);
        fixture.Move(bounds.X, bounds.Y);
        fixture.Release(bounds.X, bounds.Y);

        Assert.Equal(1f, picker.Value.R, 2);
        Assert.Equal(1f, picker.Value.G, 2);
        Assert.Equal(1f, picker.Value.B, 2);

        // Bottom is black at any saturation.
        fixture.Press(bounds.X + (bounds.Width * 0.5f), bounds.Bottom - 4f);
        fixture.Move(bounds.X + (bounds.Width * 0.5f), bounds.Bottom);
        fixture.Release(bounds.X + (bounds.Width * 0.5f), bounds.Bottom);

        Assert.Equal(0f, picker.Value.R, 2);
    }

    [Fact]
    public void The_hue_survives_a_trip_through_grey() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Value = new Color4(0f, 0.6f, 1f, 1f);
        fixture.Update();

        var hue = picker.Hue;
        Assert.True(hue is > 180f and < 230f, $"hue is {hue}");

        // ⚠ The bug every picker has had: dragging the value to nothing must not lose which hue the
        // user was on, because the very next thing they do is drag it back up.
        var bounds = picker.Field.Bounds;

        fixture.Press(bounds.X + (bounds.Width * 0.5f), bounds.Bottom - 4f);
        fixture.Move(bounds.X + (bounds.Width * 0.5f), bounds.Bottom);
        fixture.Release(bounds.X + (bounds.Width * 0.5f), bounds.Bottom);

        Assert.Equal(new Color4(0f, 0f, 0f, 1f), picker.Value);
        Assert.Equal(hue, picker.Hue, 3);

        fixture.Press(bounds.X + (bounds.Width * 0.5f), bounds.Y + 4f);
        fixture.Move(bounds.X + (bounds.Width * 0.5f), bounds.Y);
        fixture.Release(bounds.X + (bounds.Width * 0.5f), bounds.Y);

        Assert.Equal(hue, Hsv.FromRgb(picker.Value).H, 1);
    }

    [Fact]
    public void Dragging_the_hue_strip_walks_all_the_way_round() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Value = new Color4(1f, 0f, 0f, 1f);
        fixture.Update();

        var bounds = picker.HueStrip.Bounds;

        fixture.Press(bounds.X + (bounds.Width / 3f), bounds.Y + 4f);
        fixture.Release(bounds.X + (bounds.Width / 3f), bounds.Y + 4f);

        Assert.Equal(120f, picker.Hue, 1);
        Assert.Equal(new Color4(0f, 1f, 0f, 1f), picker.Value);
    }

    [Fact]
    public void Dragging_the_alpha_strip_leaves_the_colour_alone() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Value = new Color4(0.2f, 0.4f, 0.9f, 1f);
        fixture.Update();

        var bounds = picker.AlphaStrip.Bounds;

        fixture.Press(bounds.X + (bounds.Width * 0.5f), bounds.Y + 4f);
        fixture.Release(bounds.X + (bounds.Width * 0.5f), bounds.Y + 4f);

        Assert.Equal(0.5f, picker.Value.A, 1);
        Assert.Equal(0.2f, picker.Value.R, 3);
        Assert.Equal(0.9f, picker.Value.B, 3);
    }

    [Fact]
    public void The_alpha_strip_can_be_taken_away() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.AllowAlpha = false;
        Assert.True(picker.AlphaStrip.HasClass("hidden"));

        picker.AllowAlpha = true;
        Assert.False(picker.AlphaStrip.HasClass("hidden"));
    }

    [Fact]
    public void The_hexadecimal_field_reads_and_writes() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Value = new Color4(0f, 1f, 0f, 1f);
        Assert.Equal("#00ff00", picker.HexField.Value);

        picker.HexField.Value = "#3b6cf0";
        fixture.Document.Focus(picker.HexField);
        fixture.Type(Vixen.Input.InputKey.Enter);

        Assert.Equal(0.231f, picker.Value.R, 2);
        Assert.Equal(0.941f, picker.Value.B, 2);
    }

    [Fact]
    public void Nonsense_in_the_hexadecimal_field_is_put_back_rather_than_left_there() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Value = new Color4(0f, 1f, 0f, 1f);

        picker.HexField.Value = "#12zz34";
        fixture.Document.Focus(picker.HexField);
        fixture.Type(Vixen.Input.InputKey.Enter);

        // ⚠ Otherwise the field looks accepted and the next thing to read the colour disagrees with
        // what is on the screen.
        Assert.Equal("#00ff00", picker.HexField.Value);
        Assert.Equal(new Color4(0f, 1f, 0f, 1f), picker.Value);
    }

    [Fact]
    public void Switching_model_keeps_the_colour_rather_than_the_axes() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Value = new Color4(0.2f, 0.6f, 0.9f, 1f);

        var before = picker.Value;
        picker.Model = ColorModel.OkLch;

        Assert.Equal(before, picker.Value);

        picker.Model = ColorModel.Hsv;
        Assert.Equal(before, picker.Value);
    }

    [Fact]
    public void The_perceptual_field_picks_perceptual_lightness() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Model = ColorModel.OkLch;
        picker.Value = new Color4(0.8f, 0.2f, 0.2f, 1f);

        fixture.Update();

        var bounds = picker.Field.Bounds;

        // A quarter down the field is a lightness of three quarters, by construction.
        fixture.Press(bounds.X + (bounds.Width * 0.4f), bounds.Y + (bounds.Height * 0.25f));
        fixture.Release(bounds.X + (bounds.Width * 0.4f), bounds.Y + (bounds.Height * 0.25f));

        Assert.Equal(0.75f, OkLch.FromSrgb(picker.Value).L, 1);
    }

    [Fact]
    public void A_palette_swatch_puts_its_colour_back() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.SetPalette(new Color4(1f, 0f, 0f, 1f), new Color4(0f, 0f, 1f, 1f));
        fixture.Update();

        var swatch = picker.Palette.Children.OfType<ColorSwatch>().ElementAt(1);
        fixture.Click(swatch);

        Assert.Equal(new Color4(0f, 0f, 1f, 1f), picker.Value);
    }

    /// <summary>
    ///     ⚠ <b>And exactly once, which is a swatch's business and not only the picker's.</b> A
    ///     swatch is the one control outside <c>ButtonBase</c> that raises its own
    ///     <c>ClickEvent</c> — it draws itself and has no label — and a markup <c>on:click</c>
    ///     listens for the tap as well, because most controls raise no activation at all. So a
    ///     swatch that did not declare <c>RaisesActivation</c> would report one press twice, which
    ///     for a palette is a colour applied and then applied again over whatever came between.
    /// </summary>
    [Fact]
    public void A_palette_swatch_reports_one_press_once() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.SetPalette(new Color4(1f, 0f, 0f, 1f), new Color4(0f, 0f, 1f, 1f));
        fixture.Update();

        var swatch = picker.Palette.Children.OfType<ColorSwatch>().ElementAt(1);
        var clicks = 0;

        // The call `<ColorSwatch on:click="@Pick" />` compiles to, made against a real context
        // rather than by adding a handler — what is on trial is the runtime's reading of the name.
        var host = new Nothing();
        BuildContext.BuildInto(host, fixture.Document, fixture.Document.Root)
            .On(swatch, "click", () => clicks++);

        fixture.Click(swatch);

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void The_same_colour_is_not_saved_twice() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Value = new Color4(1f, 0f, 0f, 1f);

        Assert.True(picker.AddToPalette());
        Assert.False(picker.AddToPalette());

        // ⚠ A palette is what an artist built for a scene, and one that filled up with copies of the
        // last colour picked would stop being that within an afternoon.
        Assert.Single(picker.Swatches);
    }

    [Fact]
    public void Intensity_multiplies_the_colour_without_moving_the_picker() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.AllowHdr = true;
        picker.Value = new Color4(0.5f, 0.25f, 0f, 1f);

        var marker = picker.Field.Marker;
        picker.Intensity = 8f;

        Assert.Equal(4f, picker.HdrValue.R, 3);
        Assert.Equal(2f, picker.HdrValue.G, 3);

        // The chromaticity survives, which is what keeping the two apart buys.
        Assert.Equal(marker, picker.Field.Marker);
        Assert.Equal(new Color4(0.5f, 0.25f, 0f, 1f), picker.Value);
    }

    [Fact]
    public void Turning_hdr_off_puts_the_intensity_back_to_one() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.AllowHdr = true;
        picker.Intensity = 6f;

        picker.AllowHdr = false;

        Assert.Equal(1f, picker.Intensity);
        Assert.Equal(picker.Value, picker.HdrValue);
    }

    [Fact]
    public void The_eyedropper_asks_the_application_and_waits_for_an_answer() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        var asked = 0;
        picker.EyedropperRequested += _ => asked++;

        fixture.Click(picker.Eyedropper);

        // ⚠ Sampling a pixel is a platform capability and this assembly has no platform. What it can
        // do is ask, and say that it is waiting.
        Assert.Equal(1, asked);
        Assert.True(picker.IsPicking);

        picker.Pick(new Color4(0.1f, 0.2f, 0.3f, 1f));

        Assert.False(picker.IsPicking);
        Assert.Equal(new Color4(0.1f, 0.2f, 0.3f, 1f), picker.Value);
    }

    [Fact]
    public void Every_change_is_announced_once() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        var changes = 0;
        picker.ValueChanged += (_, _) => changes++;

        picker.Value = new Color4(1f, 0f, 0f, 1f);
        Assert.Equal(1, changes);

        // Assigning the same colour is not a change, which is what stops a two-way binding from
        // looping.
        picker.Value = new Color4(1f, 0f, 0f, 1f);
        Assert.Equal(1, changes);
    }

    /// <summary>
    ///     ⚠ <b>The perceptual plane used to be recomputed from scratch on every draw.</b> Sixteen
    ///     columns times sixteen rows times a stop at each end is 512 <c>OkLch.ToSrgb</c> calls, and
    ///     at hue 0 exactly 169 of the plane's 272 distinct colours are outside sRGB — so most of
    ///     them were a <c>GamutMap.Map</c> binary search rather than three clamps, repeated for a
    ///     picture that had not changed.
    /// </summary>
    /// <remarks>
    ///     The count is the claim, not a duration: a wall-clock budget calibrated on an idle machine
    ///     is this repository's largest flake source, and 512-versus-0 is the same instrument
    ///     <c>UiGeometryBuilder.ColourSearches</c> already is.
    /// </remarks>
    [Fact]
    public void The_perceptual_plane_is_converted_once_per_hue_and_not_once_per_draw() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Model = ColorModel.OkLch;
        fixture.Update();

        // A row's bottom stop is the row below's top stop, so the plane is 16 columns of 17
        // lightnesses — the 512 stops the loop asked for are 272 colours.
        Assert.Equal(ColorField.Samples * (ColorField.Samples + 1), picker.Field.PlaneConversions);
        Assert.Equal(1, picker.Field.PlaneRebuilds);

        fixture.Update();
        fixture.Update();

        // ⚠ Nothing gates the draw itself: every `Document.Draw()` runs `OnDraw` on the field, and
        // before this the counter grew by 512 each time.
        Assert.Equal(ColorField.Samples * (ColorField.Samples + 1), picker.Field.PlaneConversions);
        Assert.Equal(1, picker.Field.PlaneRebuilds);
    }

    /// <summary>And the hue is what invalidates it, because it is the only other input.</summary>
    [Fact]
    public void Moving_the_hue_rebuilds_the_plane() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Model = ColorModel.OkLch;
        fixture.Update();

        picker.Value = new Color4(0f, 0.6f, 0.9f, 1f);
        fixture.Update();

        Assert.NotEqual(0f, picker.Hue);
        Assert.Equal(2, picker.Field.PlaneRebuilds);
        Assert.Equal(2 * ColorField.Samples * (ColorField.Samples + 1), picker.Field.PlaneConversions);
    }

    /// <summary>
    ///     The cache is only worth having if it holds the colours the uncached loop drew. Chroma is
    ///     the column and lightness is the row, so the whole grid is a closed form to compare with.
    /// </summary>
    [Fact]
    public void The_cached_plane_holds_the_colours_the_loop_drew() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        picker.Model = ColorModel.OkLch;
        picker.Value = new Color4(0.8f, 0.2f, 0.2f, 1f);
        fixture.Update();

        for (var i = 0; i < ColorField.Samples; i++) {
            var chroma = (i + 0.5f) / ColorField.Samples * ColorPicker.MaximumChroma;

            for (var j = 0; j < ColorField.Samples; j++) {
                var top = new OkLch(1f - (j / (float)ColorField.Samples), chroma, picker.Hue).ToSrgb();
                var bottom = new OkLch(1f - ((j + 1f) / ColorField.Samples), chroma, picker.Hue).ToSrgb();

                Assert.Equal(top, picker.Field.PlaneColour(i, j));
                Assert.Equal(bottom, picker.Field.PlaneColour(i, j + 1));
            }
        }
    }

    /// <summary>A component that draws nothing, so that a test can hold a real build context.</summary>
    sealed class Nothing : Component {
        protected override void Build(BuildContext ctx) { }
    }
}

/// <summary>The field: a swatch in a row, and the picker it drops.</summary>
/// <remarks>
///     ⚠ <b>What a colour looks like in a property row.</b> A <c>ColorPicker</c> is a 150-pixel
///     field, two bands, a hex box, an intensity slider and a palette — the right thing to open and
///     the wrong thing to leave sitting in an inspector, where a material with four tints is four of
///     them stacked past the bottom of the panel.
/// </remarks>
public class ColorInputTests {
    static ColorInput Input(AdvancedFixture fixture) {
        var input = fixture.Add<ColorInput>();
        fixture.Update();

        return input;
    }

    [Fact]
    public void It_shows_a_swatch_and_opens_the_picker_when_it_is_clicked() {
        using var fixture = new AdvancedFixture();
        var input = Input(fixture);

        input.Value = new Color4(0.2f, 0.4f, 0.9f, 1f);
        fixture.Update();

        Assert.Equal(input.Value, input.Swatch.Color);
        Assert.False(input.IsOpen);

        // The picker takes no room until it is asked for, which is the whole point: the row is the
        // swatch's height and nothing else.
        Assert.True(input.Height < 40f, $"the field is {input.Height} tall, which is a picker rather than a row");

        var bounds = input.Bounds;

        fixture.Press(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));
        fixture.Release(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));
        fixture.Update();

        Assert.True(input.IsOpen, "clicking the swatch did not open the picker");

        // ⚠ On the document root, not under the field. A panel that dropped out of the row would be
        // clipped by every scrolling ancestor between the two — `SelectBase`'s argument, and this is
        // the control that would meet it first.
        Assert.Same(fixture.Document.Root, input.Popup.Parent);
    }

    [Fact]
    public void The_swatch_follows_what_the_picker_chooses() {
        using var fixture = new AdvancedFixture();
        var input = Input(fixture);

        Color4? reported = null;

        input.ValueChanged += (_, colour) => reported = colour;

        input.Open();
        fixture.Update();

        input.Picker.Value = new Color4(1f, 0f, 0f, 1f);
        fixture.Update();

        Assert.Equal(new Color4(1f, 0f, 0f, 1f), input.Value);
        Assert.Equal(new Color4(1f, 0f, 0f, 1f), input.Swatch.Color);
        Assert.Equal(new Color4(1f, 0f, 0f, 1f), reported);
    }

    /// <summary>
    ///     ⚠ <b>The popover is a root child, so the subtree removal does not reach it.</b> A field
    ///     taken off a rebuilt inspector row would otherwise leave an invisible overlay on the root,
    ///     still listening for pointer events — once per rebuild, for ever.
    /// </summary>
    [Fact]
    public void Removing_the_field_takes_its_popover_with_it() {
        using var fixture = new AdvancedFixture();
        var input = Input(fixture);

        var popup = input.Popup;

        Assert.Contains(popup, fixture.Document.Root.Children);

        input.Remove();
        fixture.Update();

        Assert.DoesNotContain(popup, fixture.Document.Root.Children);
    }
}
