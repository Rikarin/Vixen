// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The colour picker's three sub-parts, operated without a pointer.</summary>
/// <remarks>
///     <para>
///         <b>Doc 46 § A2's refusal, discharged in the order it insisted on.</b> The bands, the
///         square and the palette chips were pointer-only and had no accessible role, and the issue
///         that owed them said the ordering was the point: a role added before a keyboard converts
///         "this control is not available to me" into "this control is available and does nothing",
///         which is the one failure a screen-reader user cannot diagnose. So every test here asserts
///         a keystroke <i>moved something</i> as well as asserting the role — a role assertion on
///         its own is exactly the half that would have been a regression.
///     </para>
///     <para>
///         ⚠ <b>Driven through the picker rather than through a bare sub-part</b>, because a strip
///         does not own its own value: it raises <c>Moved</c> and the picker writes the fraction
///         back on the next sync. A test that constructed a lone <c>ColorStrip</c> and pressed Right
///         would see nothing move and would be measuring the absence of an owner.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class ColorPickerKeyboardTests {
    [Fact]
    public void The_hue_band_is_a_named_slider_the_arrows_move() {
        using var fixture = new AdvancedFixture();

        var picker = fixture.Add<ColorPicker>();
        fixture.Update();

        Assert.Equal(AccessibleRole.Slider, picker.HueStrip.Role);
        Assert.Equal(ControlStrings.ColorPickerHue.Text, picker.HueStrip.AccessibleName);
        Assert.Equal(ControlStrings.ColorPickerAlpha.Text, picker.AlphaStrip.AccessibleName);

        Assert.True(fixture.Document.Focus(picker.HueStrip));

        // ⚠ The hue and not the colour. White has no hue of its own, so `Compose` returns white
        // again and `Value` never moves — asserting on the colour here would fail against a
        // perfectly working keyboard, which is the trap this control's own remarks warn about.
        fixture.Type(InputKey.Right);
        Assert.Equal(360f * ColorStrip.KeyStep, picker.Hue, 3);

        fixture.Type(InputKey.PageUp);
        Assert.Equal(360f * ColorStrip.KeyStep * 11f, picker.Hue, 3);

        fixture.Type(InputKey.End);
        Assert.Equal(360f, picker.Hue, 3);

        fixture.Type(InputKey.Home);
        Assert.Equal(0f, picker.Hue, 3);

        // The value a bridge reads, and it tracks the same number rather than being written twice.
        fixture.Type(InputKey.Right);
        Assert.Equal("0.01", picker.HueStrip.AccessibleValue);
    }

    [Fact]
    public void The_alpha_band_moves_the_opacity_and_nothing_else() {
        using var fixture = new AdvancedFixture();

        var picker = fixture.Add<ColorPicker>();
        picker.Value = new Color4(0.2f, 0.4f, 0.6f, 1f);

        fixture.Update();
        Assert.True(fixture.Document.Focus(picker.AlphaStrip));

        fixture.Type(InputKey.Left);

        Assert.Equal(1f - ColorStrip.KeyStep, picker.Value.A, 3);
        Assert.Equal(0.2f, picker.Value.R, 3);
        Assert.Equal("0.99", picker.AlphaStrip.AccessibleValue);
    }

    [Fact]
    public void The_field_is_an_application_whose_up_arrow_moves_the_marker_up() {
        using var fixture = new AdvancedFixture();

        var picker = fixture.Add<ColorPicker>();
        fixture.Update();

        // Not `slider`: the field is two numbers and `aria-valuenow` is one. Both are in the value.
        Assert.Equal(AccessibleRole.Application, picker.Field.Role);
        Assert.Equal(ControlStrings.ColorPickerField.Text, picker.Field.AccessibleName);

        Assert.True(fixture.Document.Focus(picker.Field));

        fixture.Type(InputKey.Down);
        fixture.Type(InputKey.Down);
        fixture.Type(InputKey.Right);

        Assert.Equal(2f * ColorStrip.KeyStep, picker.Field.Marker.Y, 3);
        Assert.Equal(ColorStrip.KeyStep, picker.Field.Marker.X, 3);

        // ⚠ Up *decreases* Y. The marker is in the field's own coordinates and those run down, so
        // an Up arrow that added would darken the colour — the one error in the switch that the
        // arithmetic alone would not reveal.
        fixture.Type(InputKey.Up);
        Assert.Equal(ColorStrip.KeyStep, picker.Field.Marker.Y, 3);

        Assert.Equal("0.01, 0.01", picker.Field.AccessibleValue);

        // Home and End are the horizontal axis; there is no "start" of a square.
        fixture.Type(InputKey.End);

        Assert.Equal(1f, picker.Field.Marker.X, 3);
        Assert.Equal(ColorStrip.KeyStep, picker.Field.Marker.Y, 3);
    }

    [Fact]
    public void A_palette_is_one_tab_stop_and_the_arrows_move_it() {
        using var fixture = new AdvancedFixture();

        var picker = fixture.Add<ColorPicker>();

        picker.SetPalette(
            new Color4(1f, 0f, 0f, 1f),
            new Color4(0f, 1f, 0f, 1f),
            new Color4(0f, 0f, 1f, 1f)
        );

        fixture.Update();

        var chips = Chips(picker);
        Assert.Equal(3, chips.Count);

        // The whole reason a roving stop exists: a palette of sixteen is one Tab, not sixteen.
        Assert.Same(chips[0], Assert.Single(UiDocument.TabOrder(picker.Palette)));

        Assert.True(fixture.Document.Focus(chips[0]));

        fixture.Type(InputKey.Right);
        Assert.True(chips[1].IsFocused);
        Assert.Same(chips[1], Assert.Single(UiDocument.TabOrder(picker.Palette)));

        // ⚠ Wraps, because Tab is what leaves the set and the arrows therefore have no end to stop
        // at — a user who reaches the last chip is looking for the first one.
        fixture.Type(InputKey.Right);
        fixture.Type(InputKey.Right);
        Assert.True(chips[0].IsFocused);

        fixture.Type(InputKey.Left);
        Assert.True(chips[2].IsFocused);
    }

    [Fact]
    public void Enter_on_a_chip_chooses_it_and_the_chosen_one_says_so() {
        using var fixture = new AdvancedFixture();

        var picker = fixture.Add<ColorPicker>();
        var green = new Color4(0f, 1f, 0f, 1f);

        picker.SetPalette(new Color4(1f, 0f, 0f, 1f), green);
        fixture.Update();

        var chips = Chips(picker);

        Assert.Equal(AccessibleRole.Option, chips[0].Role);
        Assert.Equal(AccessibleRole.ListBox, picker.Palette.Role);
        Assert.Equal(ControlStrings.ColorPickerPalette.Text, picker.Palette.AccessibleName);

        // The name is the colour, which is the same six characters in every language.
        Assert.Equal("#ff0000", chips[0].AccessibleName);

        Assert.True(fixture.Document.Focus(chips[1]));
        fixture.Type(InputKey.Enter);

        Assert.Equal(green, picker.Value);
        Assert.Equal(AccessibleStates.Selected, chips[1].AccessibleState & AccessibleStates.Selected);
        Assert.Equal(AccessibleStates.None, chips[0].AccessibleState & AccessibleStates.Selected);
    }

    /// <summary>The two swatches that must not be stops: the preview, and the pooled spares.</summary>
    /// <remarks>
    ///     ⚠ <b>A parked chip is hidden and would still be in the tab order</b>, because
    ///     <c>UiDocument.TabOrder</c> collects on <c>Focusable</c> and knows nothing about a class
    ///     that hides an element. A palette that shrank from three colours to one would otherwise
    ///     leave two invisible stops behind it, which is the shape of accessibility bug nobody
    ///     reports because nothing is on screen to point at.
    /// </remarks>
    [Fact]
    public void A_parked_chip_and_the_preview_are_pictures_rather_than_stops() {
        using var fixture = new AdvancedFixture();

        var picker = fixture.Add<ColorPicker>();

        picker.SetPalette(
            new Color4(1f, 0f, 0f, 1f),
            new Color4(0f, 1f, 0f, 1f),
            new Color4(0f, 0f, 1f, 1f)
        );

        fixture.Update();

        picker.SetPalette(new Color4(1f, 0f, 0f, 1f));
        fixture.Update();

        Assert.Equal(3, Chips(picker).Count);
        Assert.Single(UiDocument.TabOrder(picker.Palette));

        Assert.False(picker.Preview.Focusable);
        Assert.Equal(AccessibleRole.Img, picker.Preview.Role);
        Assert.Empty(UiDocument.TabOrder(picker.Preview.Parent!).OfType<ColorSwatch>());
    }

    [Fact]
    public void Everything_the_keyboard_now_reaches_is_named() {
        using var fixture = new AdvancedFixture();

        var picker = fixture.Add<ColorPicker>();

        picker.AllowHdr = true;
        picker.SetPalette(new Color4(1f, 0f, 0f, 1f));

        fixture.Update();

        // The rule that made the roles pay for themselves: a focusable element with no role, and a
        // widget role with no name, are both offenders. Six new tab stops, none of them silent.
        Assert.Empty(AccessibilitySnapshot.Unnamed(picker));
    }

    static List<ColorSwatch> Chips(ColorPicker picker) => [.. picker.Palette.Children.OfType<ColorSwatch>()];
}
