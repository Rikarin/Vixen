// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The CSS system colours, and the forced-colours mode that substitutes them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>forced-colors</c> was a media feature with no mode for as long as it has been
///         evaluated.</b> A sheet could write <c>@media (forced-colors: active)</c> and have it
///         match, and nothing in the renderer ever read <see cref="MediaPreferences.ForcedColors" />
///         — so the half CSS actually specifies, the palette the user agent substitutes, did not
///         exist. Every assertion in this file was false before <see cref="SystemPalette" /> landed.
///     </para>
///     <para>
///         ⚠ <b>Asserted on the draw list rather than on a picture, and the colours are compared in
///         linear space.</b> A <see cref="DrawCommand" />'s colour is linear — the palette converts
///         its sRGB bytes on the way in — so an expectation written as <c>0xFFFFFF</c> has to make
///         the same trip. Comparing sRGB bytes against a linear command would fail on white by
///         nothing and on mid grey by a third.
///     </para>
/// </remarks>
public class ForcedColorsTests {
    static UiDocument Drawn(string css, bool forced = false, string classNames = "probe") {
        var document = new UiDocument(200f, 200f);

        if (forced) {
            document.Primary.Preferences = document.Primary.Preferences with { ForcedColors = true };
        }

        document.Load(".probe { width: 40px; height: 20px; } " + css);
        document.Root.Add("div", classNames: classNames);
        document.Update();
        document.Draw();

        return document;
    }

    static Color4 Srgb(uint packed) =>
        new Color((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed).ToLinear();

    static IReadOnlyList<DrawCommand> Fills(UiDocument document) =>
        [.. document.Drawing.Commands.Where(command => command.Kind == DrawCommandKind.Rectangle)];

    static IReadOnlyList<DrawCommand> Rings(UiDocument document) =>
        [.. document.Drawing.Commands.Where(command => command.Kind == DrawCommandKind.Border)];

    [Fact]
    public void A_system_colour_keyword_parses_as_a_colour_and_not_as_a_keyword() {
        // ⚠ The claim #836 and #838 both rest on: a grep for `CanvasText` across the tree was empty,
        // so `background-color: Canvas` resolved, computed, reached the draw list as a keyword and
        // painted nothing at all.
        using var document = Drawn(".probe { background-color: Canvas; }");

        Assert.Equal(Srgb(0xFFFFFF), Assert.Single(Fills(document)).Color);
    }

    [Fact]
    public void A_system_colour_keyword_is_case_insensitive_like_every_other_css_keyword() {
        using var document = Drawn(".probe { background-color: cAnVaStExT; }");

        Assert.Equal(Srgb(0x000000), Assert.Single(Fills(document)).Color);
    }

    [Fact]
    public void An_authored_background_is_drawn_as_canvas_when_colours_are_forced() {
        using var plain = Drawn(".probe { background-color: #ff0000; }");
        using var high = Drawn(".probe { background-color: #ff0000; }", forced: true);

        // The instrument first: without the mode the authored red survives, so a green result below
        // cannot come from the substitution being applied to everything unconditionally.
        Assert.Equal(Srgb(0xFF0000), Assert.Single(Fills(plain)).Color);
        Assert.Equal(Srgb(0xFFFFFF), Assert.Single(Fills(high)).Color);
    }

    [Fact]
    public void A_forced_surface_takes_the_palette_it_is_given_and_not_a_fixed_table() {
        var document = new UiDocument(200f, 200f);
        document.Primary.Preferences = document.Primary.Preferences with { ForcedColors = true };
        document.SystemColors.Reset(SystemPalette.HighContrast);

        document.Load(".probe { width: 40px; height: 20px; background-color: #ff0000; }");
        document.Root.Add("div", classNames: "probe");
        document.Update();
        document.Draw();

        // Windows' High Contrast Black paints its canvas black, where the light default is white —
        // which is the difference that tells "the palette was read" from "a constant was returned".
        Assert.Equal(Srgb(0x000000), Assert.Single(Fills(document)).Color);
        document.Dispose();
    }

    [Fact]
    public void A_transparent_background_stays_transparent_under_forced_colours() {
        // ⚠ The exception that keeps the mode usable. `background-color: transparent` is the initial
        // value, so almost every element in a real tree has one; forcing those to `Canvas` would
        // paint an opaque rectangle behind every element in the document and the window would come
        // out blank rather than high-contrast.
        using var document = Drawn(".probe { background-color: transparent; }", forced: true);

        Assert.All(Fills(document), fill => Assert.Equal(0f, fill.Color.A));
    }

    [Fact]
    public void An_element_that_opts_out_keeps_the_colour_its_sheet_chose() {
        using var document = Drawn(
            ".probe { background-color: #ff0000; forced-color-adjust: none; }",
            forced: true
        );

        Assert.Equal(Srgb(0xFF0000), Assert.Single(Fills(document)).Color);
    }

    [Fact]
    public void The_opt_out_reaches_a_child_because_it_inherits() {
        var document = new UiDocument(200f, 200f);
        document.Primary.Preferences = document.Primary.Preferences with { ForcedColors = true };

        document.Load(
            ".swatch { forced-color-adjust: none; } .probe { width: 40px; height: 20px; background-color: #ff0000; }"
        );

        var swatch = document.Root.Add("div", classNames: "swatch");
        swatch.Add("div", classNames: "probe");
        document.Update();
        document.Draw();

        // ⚠ The case the property exists for, and the one a non-inherited reading would fail: the
        // class goes on the container and the colours are on its children.
        Assert.Equal(Srgb(0xFF0000), Assert.Single(Fills(document)).Color);
        document.Dispose();
    }

    [Fact]
    public void Outline_hidden_keeps_a_ring_under_forced_colours_and_outline_none_does_not() {
        using var none = Drawn(".probe { outline-style: none; outline-width: 2px; }", forced: true);
        using var hidden = Drawn(".probe { outline-style: hidden; outline-width: 2px; }", forced: true);

        // ⚠ CSS UI 4 makes the two words synonyms on an outline and this engine agrees everywhere
        // else — the difference exists only here, and only because Tailwind's `outline-hidden` needs
        // a focus indicator that survives high contrast while `outline-none` means "gone".
        Assert.Empty(Rings(none));

        var ring = Assert.Single(Rings(hidden));

        // ⚠ Black, because this document's *mode* is forced and its *palette* is still the light
        // default — the two are separate, and only a host swaps the second (`PlatformInput.Repalette`).
        // `CanvasText` on white paper is black, and a ring drawn in it is exactly the visible
        // indicator the class is for.
        Assert.Equal(Srgb(0x000000), ring.Color);
        Assert.Equal(2f, ring.Thickness, 0.001f);
    }

    [Fact]
    public void Outline_hidden_draws_nothing_when_colours_are_not_forced() {
        using var document = Drawn(".probe { outline-style: hidden; outline-width: 2px; }");

        Assert.Empty(Rings(document));
    }

    [Fact]
    public void A_restored_ring_ignores_the_transparent_colour_the_class_wrote() {
        using var document = Drawn(
            ".probe { outline-style: hidden; outline-width: 2px; outline-color: transparent; }",
            forced: true
        );

        // The whole point: v4 writes `outline: 2px solid transparent`, and a ring that honoured that
        // colour would be drawn and invisible, which is the state this branch exists to leave.
        Assert.Equal(1f, Assert.Single(Rings(document)).Color.A);
    }

    [Fact]
    public void A_restored_ring_keeps_a_width_the_author_gave_it() {
        using var document = Drawn(".probe { outline-style: hidden; outline-width: 6px; }", forced: true);

        Assert.Equal(6f, Assert.Single(Rings(document)).Thickness, 0.001f);
    }

    [Fact]
    public void Changing_the_palette_changes_what_a_system_colour_draws_without_a_reload() {
        var document = new UiDocument(200f, 200f);
        document.Load(".probe { width: 40px; height: 20px; background-color: Canvas; }");
        document.Root.Add("div", classNames: "probe");
        document.Update();
        document.Draw();

        Assert.Equal(Srgb(0xFFFFFF), Assert.Single(Fills(document)).Color);

        // ⚠ No `Load`, no restyle: `StyleEngine.SetMedia` deliberately does not reload sheets when
        // the appearance changes, so a semantic colour that needed one would be the wrong shape. What
        // has to notice is the parse cache, which is keyed by interned value id — `Canvas` is the
        // same id in both appearances — and it notices through `SystemPalette.Revision`.
        document.SystemColors.Reset(SystemPalette.Dark);
        document.Draw();

        Assert.Equal(Srgb(0x121212), Assert.Single(Fills(document)).Color);
        document.Dispose();
    }

    [Fact]
    public void A_palette_write_that_changes_nothing_does_not_bump_the_revision() {
        var palette = new SystemPalette();
        var before = palette.Revision;

        Assert.False(palette.Reset(SystemPalette.Light));
        Assert.Equal(before, palette.Revision);

        Assert.True(palette.Reset(SystemPalette.Dark));
        Assert.NotEqual(before, palette.Revision);
    }

    [Fact]
    public void A_whole_table_costs_one_revision_and_not_fifteen() {
        var palette = new SystemPalette();
        var before = palette.Revision;

        palette.Reset(SystemPalette.HighContrast);

        // The property that makes an appearance switch one cache clear rather than fifteen mid-frame.
        Assert.Equal(before + 1, palette.Revision);
    }
}
