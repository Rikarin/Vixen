// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The colour picker's three keyboard-operable sub-parts, seen rather than merely reached.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The other half of #420, and a WCAG 2.4.7 failure on its own.</b> #420's argument was
///         that a role without a keyboard turns "this control is not available to me" into
///         "available and does nothing". A keyboard without a focus indicator is the mirror of it
///         for somebody not using a screen reader at all: the arrows work, and there is no way to
///         know which control they are reaching. <c>ControlTheme</c>'s ring is one selector list
///         naming twenty-one tags and none of these three is in it.
///     </para>
///     <para>
///         ⚠ <b>Two assertions per part, because either alone passes against the defect.</b> A
///         computed <c>border-color</c> that moves says the rule matched — a rule matching nothing
///         is indistinguishable from no rule, which is exactly what the tree looked like. A
///         <see cref="DrawCommandKind.Border" /> command of that colour, over the element's own box,
///         says something is painted — a ring declared on an element with no border width would
///         satisfy the first assertion and draw nothing whatever.
///     </para>
/// </remarks>
public class ColorPickerFocusRingTests {
    /// <summary>Builds a picker with a palette, in keyboard mode so that a focus is a visible one.</summary>
    /// <remarks>
    ///     ⚠ <c>:focus-visible</c> is not <c>:focus</c>: <c>ElementState.FocusVisible</c> is set only
    ///     when the focus arrived by keyboard, which is <c>UiDocument.KeyboardMode</c>. One Tab puts
    ///     the document in it; without that, every assertion here would be red against a correct
    ///     theme.
    /// </remarks>
    static ColorPicker Picker(AdvancedFixture fixture) {
        var picker = fixture.Add<ColorPicker>();

        picker.SetPalette(Color4.Red, Color4.Green, Color4.Blue);
        fixture.Type(InputKey.Tab);

        // ⚠ And straight back off again. The Tab is here for the mode, not for the destination —
        // it lands on the field, which is the first focusable thing in the picker, and a test that
        // read a "resting" border off an element the Tab had already ringed would compare a colour
        // with itself. `KeyboardMode` is cleared by the pointer moving and not by a blur, so it
        // survives this.
        fixture.Document.Focus(null);
        fixture.Update();

        return picker;
    }

    static Color4? BorderOf(AdvancedFixture fixture, UiElement element) =>
        fixture.Document.ColorOf(element.Style, fixture.Document.PropertyId("border-top-color"));

    /// <summary>Whether the frame paints a border of this colour over exactly this element's box.</summary>
    /// <remarks>
    ///     Over the element's own bounds, so that an accent border somewhere else in the picker —
    ///     the hex field, a hovered chip — cannot answer for it.
    /// </remarks>
    static bool Ringed(AdvancedFixture fixture, UiElement element, Color4 colour) {
        var bounds = element.Bounds;

        foreach (var command in fixture.Document.Drawing.Commands) {
            if (command.Kind == DrawCommandKind.Border
                && command.Color == colour
                && MathF.Abs(command.X - bounds.X) < 0.5f
                && MathF.Abs(command.Y - bounds.Y) < 0.5f
                && MathF.Abs(command.Width - bounds.Width) < 0.5f) {
                return true;
            }
        }

        return false;
    }

    static void AssertRings(AdvancedFixture fixture, UiElement element) {
        Assert.True(element.Focusable, $"<{element.Tag}> is not focusable, so this proves nothing about a ring");

        var resting = BorderOf(fixture, element);

        Assert.True(fixture.Document.Focus(element));
        fixture.Update();

        Assert.True(
            (element.State & ElementState.FocusVisible) != 0,
            $"<{element.Tag}> took the focus without it being a visible one, so `:focus-visible` cannot match"
        );

        var focused = BorderOf(fixture, element);

        Assert.NotNull(focused);

        Assert.False(
            resting == focused,
            $"<{element.Tag}> computes the same border-color focused as at rest, so no `:focus-visible` rule "
            + "matched it — which is what a missing rule and a rule that matches nothing look like alike"
        );

        Assert.True(
            Ringed(fixture, element, focused.Value),
            $"<{element.Tag}> computes a focus border the frame does not paint over its box, so the ring is a "
            + "declaration with no width behind it"
        );

        // And it goes away again, so the ring follows the focus rather than being stuck on.
        //
        // ⚠ True. `Focus(null)` used to answer false on success, and this line was written against
        // that; it now means what it says, which is that the focus went where it was asked to go.
        Assert.True(fixture.Document.Focus(null));
        fixture.Update();

        Assert.False(
            Ringed(fixture, element, focused.Value),
            $"<{element.Tag}> keeps its ring after the focus has left it"
        );
    }

    [Fact]
    public void The_two_dimensional_field_shows_where_the_focus_is() {
        using var fixture = new AdvancedFixture();
        AssertRings(fixture, Picker(fixture).Field);
    }

    [Fact]
    public void The_hue_band_shows_where_the_focus_is() {
        using var fixture = new AdvancedFixture();
        AssertRings(fixture, Picker(fixture).HueStrip);
    }

    [Fact]
    public void The_alpha_band_shows_where_the_focus_is() {
        using var fixture = new AdvancedFixture();
        AssertRings(fixture, Picker(fixture).AlphaStrip);
    }

    /// <summary>And the palette, which is the one that has nothing else to follow.</summary>
    /// <remarks>
    ///     ⚠ <b>The roving tab stop is what makes this the worst of the three.</b> Tab enters the
    ///     set once and the arrows walk it from chip to chip, so a palette with no ring gives a
    ///     keyboard user no indication at all of where in it they are — the focus moves and the
    ///     picture does not.
    /// </remarks>
    [Fact]
    public void A_palette_chip_shows_where_the_focus_is() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        var chips = picker.Palette.Children.OfType<ColorSwatch>().Where(chip => chip.Selectable).ToList();

        Assert.Equal(picker.Swatches.Count, chips.Count);

        // The second, not the first: the roving stop starts on chip zero, so a ring that only ever
        // appeared on the tab stop itself would pass against the first one.
        AssertRings(fixture, chips[1]);
    }

    /// <summary>A band's box does not move when the focus arrives, which is why the border is resting.</summary>
    /// <remarks>
    ///     ⚠ <b><c>box-sizing</c> is <c>border-box</c>, so a ring added on focus reflows the
    ///     element's own content by a pixel each way</b> — the marker drawn inside a band would
    ///     twitch every time the focus came and went. <c>gradient-rail</c> was given a transparent
    ///     resting border for exactly this and the two bands now are too; this is what would go red
    ///     if somebody folded the width into the <c>:focus-visible</c> rule to save a line.
    /// </remarks>
    [Fact]
    public void Taking_the_focus_moves_no_box() {
        using var fixture = new AdvancedFixture();
        var picker = Picker(fixture);

        var field = picker.Field.Bounds;
        var hue = picker.HueStrip.Bounds;

        Assert.True(fixture.Document.Focus(picker.Field));
        fixture.Update();

        Assert.Equal(field, picker.Field.Bounds);
        Assert.Equal(hue, picker.HueStrip.Bounds);
    }
}
