// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A <c>box-shadow</c> transition with a <c>none</c> end, read in the middle of its run (#1464).</summary>
/// <remarks>
///     CSS Backgrounds 3 § 7 interpolates a missing shadow as a transparent one with every length
///     zero and the <c>inset</c> of the shadow it pairs with, so <c>none</c> → a shadow fades and
///     grows in. Before #1464 a <c>none</c> end was a keyword, which has no midpoint, and under
///     <c>transition-behavior: normal</c> the end was applied at once.
/// </remarks>
public class ShadowTransitionTests {
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

    static StyleValue Shadow(UiDocument document, UiElement box) {
        Assert.True(box.Style.TryGet(document.Styles.Properties.Lookup("box-shadow"), out var value), "box-shadow never arrived");

        return new StyleValueParser(document.Styles.Values, document.Styles.Names).Parse(value);
    }

    static DrawCommand DrawnShadow(UiDocument document) {
        document.Draw();

        return Assert.Single(document.Drawing.Commands, command => command.Kind == DrawCommandKind.Shadow);
    }

    /// <summary>⚠ <b>The issue's case: half way from <c>none</c> the blur is half and the colour half faded.</b></summary>
    [Fact]
    public void A_none_start_grows_from_a_transparent_zero_shadow() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: none;", "box-shadow: 4px 2px 8px #ff0000;");

        var value = Shadow(document, box);

        Assert.Equal(StyleValueKind.List, value.Kind);
        Assert.Equal(2f, value.Items[0].Number, Tolerance);
        Assert.Equal(1f, value.Items[1].Number, Tolerance);
        Assert.Equal(4f, value.Items[2].Number, Tolerance);
        Assert.Equal(0.5f, value.Items[3].Color.A, Tolerance);

        // The colour keeps its hue on the way in rather than travelling through black.
        Assert.Equal(1f, value.Items[3].Color.R, Tolerance);

        var shadow = DrawnShadow(document);
        Assert.Equal(0.5f, shadow.Color.A, Tolerance);
        Assert.Equal(2f, shadow.X, Tolerance);
    }

    /// <summary>An undeclared start is the initial <c>none</c>, and the same identity.</summary>
    [Fact]
    public void An_undeclared_start_grows_from_a_transparent_zero_shadow() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "", "box-shadow: 0px 0px 8px #ff0000;");

        var value = Shadow(document, box);

        Assert.Equal(4f, value.Items[2].Number, Tolerance);
        Assert.Equal(0.5f, value.Items[3].Color.A, Tolerance);
    }

    /// <summary>A <c>none</c> end fades the shadow out the same way.</summary>
    [Fact]
    public void A_none_end_fades_to_a_transparent_zero_shadow() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: 0px 6px 8px #ff0000;", "box-shadow: none;");

        var value = Shadow(document, box);

        Assert.Equal(3f, value.Items[1].Number, Tolerance);
        Assert.Equal(0.5f, value.Items[3].Color.A, Tolerance);
    }

    /// <summary>
    ///     The padding shadow takes the <c>inset</c> of the shadow it pairs with, so an inner shadow
    ///     grows in as an inner shadow.
    /// </summary>
    [Fact]
    public void A_none_start_against_an_inset_shadow_grows_an_inset_one() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: none;", "box-shadow: inset 0px 0px 8px #ff0000;");

        var value = Shadow(document, box);

        Assert.Equal("inset", document.Styles.Names.NameOf(value.Items[0].Keyword));
        Assert.Equal(4f, value.Items[3].Number, Tolerance);
        Assert.Equal(0.5f, value.Items[4].Color.A, Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>An <c>inset</c> on one end and not the other is discrete</b>, so the end is applied at
    ///     once — the half a fix that lerped any two lists of the same length would break.
    /// </summary>
    [Fact]
    public void An_inset_mismatch_does_not_interpolate() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "box-shadow: 0px 0px 2px #ff0000;", "box-shadow: inset 0px 0px 8px;");

        var value = Shadow(document, box);

        Assert.Equal("inset", document.Styles.Names.NameOf(value.Items[0].Keyword));
        Assert.Equal(8f, value.Items[3].Number, Tolerance);
    }
}
