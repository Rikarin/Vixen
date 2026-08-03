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
