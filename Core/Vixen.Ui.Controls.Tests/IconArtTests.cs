// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>An icon with a colour per path, and the three places a colour can come from.</summary>
/// <remarks>
///     ⚠ <b>Assertions about pixels, because that is the only place the answer exists.</b> Whether a
///     path was painted in the theme's foreground or in a literal that happens to look like it is not
///     a question about the element tree — the two produce an identical <see cref="IconArt" /> and
///     differ only once the theme changes. So each of these draws twice under two stylesheets and
///     asserts which one moved.
/// </remarks>
public class IconArtTests {
    /// <summary>A filled square, in view-box units.</summary>
    static PathBuilder Square(float x, float y, float size) =>
        new PathBuilder().AddRectangle(new Rectangle(x, y, size, size));

    /// <summary>Opens a document with one icon filling it.</summary>
    static UiTest Opened(string? css = null) =>
        ControlHarness.Open(48f, 48f, "icon { width: 48px; height: 48px; }" + (css ?? string.Empty));

    /// <summary>The colour at a point of the picture, as bytes.</summary>
    static (byte R, byte G, byte B) At(UiTest ui, int x, int y) {
        var image = ui.Capture();
        var offset = image.Offset(x, y);

        return (image.Pixels[offset], image.Pixels[offset + 1], image.Pixels[offset + 2]);
    }

    [Fact]
    public void Two_paths_are_painted_in_two_colours() {
        using var ui = Opened();

        var icon = ui.Add<Icon>("art");

        // Two squares side by side on a 24 grid, so each covers a known quarter of the picture.
        icon.Art = new IconArt(
            new IconPath(Square(0f, 0f, 12f), IconPaint.Of(new Color4(1f, 0f, 0f, 1f))),
            new IconPath(Square(12f, 12f, 12f), IconPaint.Of(new Color4(0f, 0f, 1f, 1f)))
        );

        ui.Frame();

        var left = At(ui, 12, 12);
        var right = At(ui, 36, 36);

        Assert.True(left.R > 200 && left.B < 60, $"the first path should be red, and was {left}");
        Assert.True(right.B > 200 && right.R < 60, $"the second path should be blue, and was {right}");
    }

    [Fact]
    public void A_foreground_path_follows_the_theme_and_a_literal_one_does_not() {
        using var ui = Opened("icon { color: #ff0000; }");

        var icon = ui.Add<Icon>("art");

        icon.Art = new IconArt(
            new IconPath(Square(0f, 0f, 12f), IconPaint.Foreground),
            new IconPath(Square(12f, 12f, 12f), IconPaint.Of(new Color4(0f, 0f, 1f, 1f)))
        );

        ui.Frame();

        Assert.True(At(ui, 12, 12).R > 200, "the inherited path should have taken the element's colour");

        ui.Load("icon { color: #00ff00; }");
        ui.Frame();

        var inherited = At(ui, 12, 12);
        var literal = At(ui, 36, 36);

        Assert.True(inherited.G > 200 && inherited.R < 60, $"it should have followed the retheme, and was {inherited}");
        Assert.True(literal.B > 200, $"the literal path should not have moved, and was {literal}");
    }

    [Fact]
    public void A_token_path_is_whatever_the_cascade_says_it_is() {
        using var ui = Opened("root { --icon-warning: #ff0000; }");

        var icon = ui.Add<Icon>("art");
        icon.Art = new IconArt(new IconPath(Square(0f, 0f, 24f), IconPaint.Named("--icon-warning")));
        ui.Frame();

        Assert.True(At(ui, 24, 24).R > 200, "the token should have resolved to the property the root set");

        // ⚠ Set on the root and read on the icon, which is the whole point of it being a custom
        // property: a retheme is one rule at the top of the document and every icon under it follows.
        ui.Load("root { --icon-warning: #0000ff; }");
        ui.Frame();

        var recoloured = At(ui, 24, 24);
        Assert.True(recoloured.B > 200 && recoloured.R < 60, $"it should have followed the token, and was {recoloured}");
    }

    [Fact]
    public void A_token_nothing_answers_falls_back_to_the_inherited_colour() {
        using var ui = Opened("icon { color: #00ff00; }");

        var icon = ui.Add<Icon>("art");
        icon.Art = new IconArt(new IconPath(Square(0f, 0f, 24f), IconPaint.Named("--nobody-sets-this")));
        ui.Frame();

        var fallback = At(ui, 24, 24);

        // A visible glyph in the wrong colour, never an invisible one — a plugin whose stylesheet has
        // not loaded yet must still have an icon.
        Assert.True(fallback.G > 200, $"an unanswered token should draw in `color`, and drew {fallback}");
    }

    [Fact]
    public void A_stroke_is_scaled_with_the_geometry() {
        using var ui = ControlHarness.Open(96f, 96f, "icon { width: 24px; height: 24px; }");

        var icon = ui.Add<Icon>("art");
        ui.Frame();

        // ⚠ Against the empty document rather than against zero. `Ink` sums the whole picture and the
        // background is most of it, so the absolute totals differ by a few percent and would pass any
        // threshold this test could set.
        var empty = ui.Ink();

        icon.Art = new IconArt(
            new IconPath(Square(4f, 4f, 16f), IconPaint.None, IconPaint.Of(new Color4(1f, 1f, 1f, 1f)), 2f)
        );

        ui.Frame();
        var drawn = ui.Ink() - empty;

        ui.Load("icon { width: 48px; height: 48px; }");
        ui.Frame();
        var doubled = ui.Ink() - empty;

        // Twice the size means twice the stroke width as well as twice the perimeter, so the lit area
        // grows by about four rather than by two. A width in device pixels would have doubled it.
        Assert.True(
            doubled > drawn * 3,
            $"a stroke authored in view-box units should scale with the art; {drawn} then {doubled}"
        );
    }

    /// <summary><c>none</c> is a paint, and it is the one a colour reading cannot see.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three states and not two, and the middle one is the whole defect.</b> SVG 2 §
    ///         13.2's <c>&lt;paint&gt;</c> is <c>none | &lt;color&gt; | …</c>, so a slot can be set to
    ///         a colour, set to nothing, or not set at all — and <c>UiDocument.ColorOf</c> answers
    ///         <c>null</c> to the last two alike. <c>Icon.Resolve</c> read that <c>null</c> as "the
    ///         author said nothing" and painted the inherited colour, so <c>fill: none</c> drew the
    ///         glyph it was written to hide.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Invisible to the consumption gate, which is why the ledger refused
    ///         <c>fill-none</c> and <c>stroke-none</c> for weeks rather than registering them.</b>
    ///         <c>fill</c> <i>is</i> read — the first state below proves it — so a family scored
    ///         green off the half that worked while the keyword painted. Only a per-value assertion
    ///         can tell the three apart, and only in pixels.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_fill_of_none_paints_nothing_where_an_unset_fill_paints_the_foreground() {
        using var ui = Opened("icon { color: #ff0000; }");

        var icon = ui.Add<Icon>("art");
        icon.Art = new IconArt(new IconPath(Square(0f, 0f, 24f), IconPaint.Foreground));
        ui.Frame();

        var unset = At(ui, 24, 24);
        Assert.True(unset.R > 200, $"an unset `fill` should draw in `color`, and drew {unset}");

        ui.Load("icon { color: #ff0000; fill: #0000ff; }");
        ui.Frame();

        var coloured = At(ui, 24, 24);
        Assert.True(coloured.B > 200 && coloured.R < 60, $"`fill` should override the foreground, and drew {coloured}");

        ui.Load("icon { color: #ff0000; fill: none; }");
        ui.Frame();

        // The empty document, so the assertion is "the background" rather than a colour written here.
        var absent = At(ui, 24, 24);
        Assert.True(
            absent.R < 60 && absent.B < 60,
            $"`fill: none` should paint nothing, and drew {absent} — the foreground fallback is the defect"
        );
    }

    [Fact]
    public void A_stroke_of_none_removes_the_outline_and_leaves_the_fill_alone() {
        using var ui = Opened("icon { color: #ff0000; }");

        var icon = ui.Add<Icon>("art");

        icon.Art = new IconArt(
            new IconPath(Square(4f, 4f, 16f), IconPaint.Of(new Color4(0f, 0f, 1f, 1f)), IconPaint.Foreground, 4f)
        );

        ui.Frame();

        // The stroke straddles the square's edge, so a point on the outline is the stroke's colour
        // and the middle is the fill's. 4 view-box units at 48/24 is 8 device pixels wide.
        Assert.True(At(ui, 8, 24).R > 200, "the outline should have taken the inherited colour");

        ui.Load("icon { color: #ff0000; stroke: none; }");
        ui.Frame();

        var outline = At(ui, 8, 24);
        var middle = At(ui, 24, 24);

        Assert.True(outline.R < 60, $"`stroke: none` should leave the outline unpainted, and drew {outline}");
        Assert.True(middle.B > 200, $"and must not touch the literal fill, which drew {middle}");
    }

    /// <summary><c>fill: none</c> reaches the single-path form as well as the art one.</summary>
    /// <remarks>
    ///     ⚠ <b>Two draw paths, and a fix to one of them is a family that works on some icons and not
    ///     others.</b> <c>Geometry</c> is the whole of the editor's chrome and never goes through
    ///     <c>Resolve</c> — the same split the <c>fill-*</c> colour family had to be careful of, one
    ///     keyword later.
    /// </remarks>
    [Fact]
    public void A_fill_of_none_reaches_the_single_path_form_too() {
        using var ui = Opened("icon { color: #ff0000; }");

        var icon = ui.Add<Icon>("art");
        icon.Geometry = Square(0f, 0f, 24f);
        ui.Frame();

        Assert.True(At(ui, 24, 24).R > 200, "a `Geometry` icon fills in the inherited colour");

        ui.Load("icon { color: #ff0000; fill: none; }");
        ui.Frame();

        var absent = At(ui, 24, 24);
        Assert.True(absent.R < 60, $"`fill: none` should reach this path too, and drew {absent}");
    }

    /// <summary>The boundary: <c>none</c> reaches what a colour reaches, and nothing else.</summary>
    /// <remarks>
    ///     A path that chose its own colour is not overridden by <c>fill</c>, so it is not erased by
    ///     <c>fill: none</c> either — the file-type glyphs in <c>StandardIcons</c> are brand-coloured
    ///     on purpose, and a document-wide <c>fill-none</c> blanking them would be the same
    ///     regression <c>fill-accent</c> repainting them would be.
    /// </remarks>
    [Fact]
    public void A_fill_of_none_leaves_a_literal_path_alone() {
        using var ui = Opened("icon { color: #ff0000; fill: none; }");

        var icon = ui.Add<Icon>("art");

        icon.Art = new IconArt(
            new IconPath(Square(0f, 0f, 12f), IconPaint.Foreground),
            new IconPath(Square(12f, 12f, 12f), IconPaint.Of(new Color4(0f, 0f, 1f, 1f)))
        );

        ui.Frame();

        var erased = At(ui, 12, 12);
        var literal = At(ui, 36, 36);

        Assert.True(erased.R < 60, $"the `currentColor` path should be gone, and drew {erased}");
        Assert.True(literal.B > 200, $"the literal path should still be there, and drew {literal}");
    }

    [Fact]
    public void A_single_path_icon_is_unchanged() {
        using var ui = Opened("icon { color: #ff0000; }");

        var icon = ui.Add<Icon>("art");
        icon.Geometry = Square(0f, 0f, 24f);
        ui.Frame();

        // The thirty-four glyphs the editor already ships are a `PathBuilder` and nothing else, and
        // this is the assertion that adding `Art` did not ask any of them to change.
        Assert.True(At(ui, 24, 24).R > 200, "a `Geometry` icon should still fill in the inherited colour");
    }
}
