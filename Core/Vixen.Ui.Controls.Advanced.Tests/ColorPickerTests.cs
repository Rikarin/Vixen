// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
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
}
