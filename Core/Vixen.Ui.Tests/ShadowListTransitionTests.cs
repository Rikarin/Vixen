// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A comma-separated <c>box-shadow</c> list in a transition, read in the middle of its run (#1472).</summary>
/// <remarks>
///     CSS Backgrounds 3 § 7 interpolates two shadow lists shadow by shadow, pads the shorter with
///     transparent shadows whose lengths are zero and whose <c>inset</c> matches the longer list's, and
///     is discrete when an <c>inset</c> differs at any position. Before #1472 a list was not read as
///     shadows at all — the comma rode on the colour before it and made it a keyword — so every row
///     below but the equal-colour one applied the end at once.
/// </remarks>
public class ShadowListTransitionTests {
    const float Tolerance = 0.01f;

    static UiElement Settled(UiDocument document, string rest, string on) {
        document.Load(
            $$"""
            root { width: 200px; height: 100px; }
            #box { width: 40px; height: 20px; background-color: #ffffff;
                   transition-property: box-shadow; transition-duration: 1s;
                   transition-timing-function: linear; {{rest}} }
            #box.on { {{on}} }
            """
        );

        var box = document.Create("div", document.Root, "box");

        Frame(document, 0.0);

        box.AddClass("on");

        Frame(document, 0.0);
        Frame(document, 0.5);

        return box;
    }

    static void Frame(UiDocument document, double seconds) {
        document.Tick(TimeSpan.FromSeconds(seconds));
        document.Update();
    }

    /// <summary>
    ///     The value the cascade holds, read shadow by shadow — split on its top-level commas the way
    ///     the draw side splits it, so the test reads exactly what a frame paints.
    /// </summary>
    static StyleValue[] Shadows(UiDocument document, UiElement box) {
        Assert.True(box.Style.TryGet(document.Styles.Properties.Lookup("box-shadow"), out var value), "box-shadow never arrived");

        var text = document.Styles.Values.NameOf(value);
        var parser = new StyleValueParser(document.Styles.Values, document.Styles.Names);
        var shadows = new List<StyleValue>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++) {
            if (i == text.Length || (text[i] == ',' && depth == 0)) {
                shadows.Add(parser.Parse(text.AsSpan(start, i - start)));
                start = i + 1;
            } else if (text[i] == '(') {
                depth++;
            } else if (text[i] == ')') {
                depth--;
            }
        }

        return [.. shadows];
    }

    /// <summary>The blur of a shadow written <c>[inset] x y blur color</c>.</summary>
    static float Blur(StyleValue shadow) => shadow.Items[^2].Number;

    static Vixen.Core.Mathematics.Color4 Colour(StyleValue shadow) => shadow.Items[^1].Color;

    /// <summary>
    ///     ⚠ <b>The row that worked, and only by accident</b>: the colours on both ends are equal, so
    ///     the keyword the first one had become matched itself. Kept so the fix cannot lose it.
    /// </summary>
    [Fact]
    public void Two_lists_with_equal_colours_interpolate_every_blur() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: 0 0 2px #ff0000, 0 0 2px #0000ff;", "box-shadow: 0 0 8px #ff0000, 0 0 8px #0000ff;");

        var shadows = Shadows(document, box);

        Assert.Equal(2, shadows.Length);
        Assert.Equal(5f, Blur(shadows[0]), Tolerance);
        Assert.Equal(5f, Blur(shadows[1]), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>The issue's second row</b>: a changed non-last colour. Both blurs are half way and the
    ///     first colour is between red and green — not the end applied at once.
    /// </summary>
    [Fact]
    public void A_changed_colour_in_a_list_interpolates() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: 0 0 2px #ff0000, 0 0 2px #0000ff;", "box-shadow: 0 0 8px #00ff00, 0 0 8px #0000ff;");

        var shadows = Shadows(document, box);

        Assert.Equal(2, shadows.Length);
        Assert.Equal(5f, Blur(shadows[0]), Tolerance);
        Assert.Equal(5f, Blur(shadows[1]), Tolerance);
        Assert.InRange(Colour(shadows[0]).R, 0.05f, 0.95f);
        Assert.InRange(Colour(shadows[0]).G, 0.05f, 0.95f);
        Assert.Equal(1f, Colour(shadows[1]).B, Tolerance);
    }

    /// <summary>⚠ <b><c>none</c> against a list is a list of transparent zero shadows</b>, one per shadow.</summary>
    [Fact]
    public void A_none_start_grows_every_shadow_of_a_list() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: none;", "box-shadow: 0 0 8px #ff0000, 0 0 8px #0000ff;");

        var shadows = Shadows(document, box);

        Assert.Equal(2, shadows.Length);
        Assert.Equal(4f, Blur(shadows[0]), Tolerance);
        Assert.Equal(4f, Blur(shadows[1]), Tolerance);
        Assert.Equal(0.5f, Colour(shadows[0]).A, Tolerance);
        Assert.Equal(0.5f, Colour(shadows[1]).A, Tolerance);

        // Each fades in at its own hue, not through black.
        Assert.Equal(1f, Colour(shadows[0]).R, Tolerance);
        Assert.Equal(1f, Colour(shadows[1]).B, Tolerance);
    }

    /// <summary>A <c>none</c> end fades the whole list out the same way.</summary>
    [Fact]
    public void A_none_end_fades_every_shadow_of_a_list() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: 0 0 8px #ff0000, 0 4px 8px #0000ff;", "box-shadow: none;");

        var shadows = Shadows(document, box);

        Assert.Equal(2, shadows.Length);
        Assert.Equal(2f, shadows[1].Items[1].Number, Tolerance);
        Assert.Equal(0.5f, Colour(shadows[1]).A, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>A list of one against a list of two</b>: the shorter is padded at its end with a
    ///     transparent zero shadow shaped after the longer's second, so the first shadow travels and the
    ///     second fades in.
    /// </summary>
    [Fact]
    public void A_shorter_list_is_padded_with_transparent_zero_shadows() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: 0 0 2px #ff0000;", "box-shadow: 0 0 8px #ff0000, 0 0 8px #0000ff;");

        var shadows = Shadows(document, box);

        Assert.Equal(2, shadows.Length);
        Assert.Equal(5f, Blur(shadows[0]), Tolerance);
        Assert.Equal(1f, Colour(shadows[0]).A, Tolerance);
        Assert.Equal(4f, Blur(shadows[1]), Tolerance);
        Assert.Equal(0.5f, Colour(shadows[1]).A, Tolerance);
        Assert.Equal(1f, Colour(shadows[1]).B, Tolerance);
    }

    /// <summary>The longer list on the start side: its extra shadow fades out.</summary>
    [Fact]
    public void A_longer_start_fades_its_extra_shadow_out() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: 0 0 8px #ff0000, 0 0 8px #0000ff;", "box-shadow: 0 0 2px #ff0000;");

        var shadows = Shadows(document, box);

        Assert.Equal(2, shadows.Length);
        Assert.Equal(5f, Blur(shadows[0]), Tolerance);
        Assert.Equal(4f, Blur(shadows[1]), Tolerance);
        Assert.Equal(0.5f, Colour(shadows[1]).A, Tolerance);
    }

    /// <summary>The padding takes the <c>inset</c> of the shadow it pairs with.</summary>
    [Fact]
    public void Padding_against_an_inset_shadow_is_inset() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: inset 0 0 2px #ff0000;", "box-shadow: inset 0 0 8px #ff0000, inset 0 0 8px #0000ff;");

        var shadows = Shadows(document, box);

        Assert.Equal(2, shadows.Length);
        Assert.Equal("inset", document.Styles.Names.NameOf(shadows[1].Items[0].Keyword));
        Assert.Equal(4f, Blur(shadows[1]), Tolerance);
        Assert.Equal(0.5f, Colour(shadows[1]).A, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>An <c>inset</c> mismatch at ANY position is discrete for the whole list</b>, so the end
    ///     is applied at once — here at the second shadow, which a check of only the first would miss.
    /// </summary>
    [Fact]
    public void An_inset_mismatch_at_the_second_position_does_not_interpolate() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: 0 0 2px #ff0000, 0 0 2px #0000ff;", "box-shadow: 0 0 8px #ff0000, inset 0 0 8px #0000ff;");

        var shadows = Shadows(document, box);

        Assert.Equal(2, shadows.Length);
        Assert.Equal(8f, Blur(shadows[0]), Tolerance);
        Assert.Equal("inset", document.Styles.Names.NameOf(shadows[1].Items[0].Keyword));
    }

    /// <summary>What a frame paints at the midpoint of <c>none</c> → two shadows: two half-faded shadows.</summary>
    [Fact]
    public void The_midpoint_of_a_list_draws_every_shadow() {
        using var document = new UiDocument(200f, 100f);
        Settled(document, "box-shadow: none;", "box-shadow: 0 0 8px #ff0000, 0 0 8px #0000ff;");

        document.Draw();
        var drawn = document.Drawing.Commands.ToArray().Where(command => command.Kind == DrawCommandKind.Shadow).ToArray();

        Assert.Equal(2, drawn.Length);
        Assert.All(drawn, shadow => Assert.Equal(0.5f, shadow.Color.A, Tolerance));
    }
}
