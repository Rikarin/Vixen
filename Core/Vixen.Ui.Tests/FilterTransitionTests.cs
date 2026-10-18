// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A <c>filter</c> transition, read in the middle of its run.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>No filter transition interpolated at all before #1439, not even
///         <c>blur(2px)</c> → <c>blur(8px)</c>.</b> A filter function parses as a list headed by its
///         name, <c>[blur, 2px]</c>, and <see cref="StyleValue.CanInterpolate" /> refused a keyword pair
///         even when the two were the same word — so every filter list was discrete, and under
///         <c>transition-behavior: normal</c> a discrete value is applied at once.
///     </para>
///     <para>
///         Filter Effects 1 § 13 is the rule: two lists of the same functions in the same order
///         interpolate pairwise; a <c>none</c> end is the other list's functions at their identity;
///         anything else is discrete. Every assertion is a midpoint, because the endpoints are what a
///         transition and its absence agree on.
///     </para>
/// </remarks>
public class FilterTransitionTests {
    const float Tolerance = 0.01f;

    static UiElement Settled(UiDocument document, string rest, string on) {
        document.Load(
            $$"""
            root { width: 200px; height: 100px; }
            #box { width: 40px; height: 20px; background-color: #ffffff;
                   transition-property: filter; transition-duration: 1s;
                   transition-timing-function: linear; {{rest}} }
            #box.on { {{on}} }
            """
        );

        var box = document.Create("div", document.Root, "box");

        Frame(document, 0.0);

        box.AddClass("on");

        // The pass that sees the class starts the transition at time zero.
        Frame(document, 0.0);
        Frame(document, 0.5);

        return box;
    }

    static void Frame(UiDocument document, double seconds) {
        document.Tick(TimeSpan.FromSeconds(seconds));
        document.Update();
    }

    /// <summary>The filter the cascade holds now, parsed.</summary>
    static StyleValue Filter(UiDocument document, UiElement box) {
        Assert.True(box.Style.TryGet(document.Styles.Properties.Lookup("filter"), out var value), "filter never arrived");

        return new StyleValueParser(document.Styles.Values, document.Styles.Names).Parse(value);
    }

    static string Text(UiDocument document, UiElement box) {
        Assert.True(box.Style.TryGet(document.Styles.Properties.Lookup("filter"), out var value), "filter never arrived");

        return document.Styles.Values.NameOf(value);
    }

    /// <summary>The blur radius the draw list asks for, which is the number a picture is drawn from.</summary>
    static float DrawnBlur(UiDocument document) {
        document.Draw();

        var layer = Assert.Single(
            document.Drawing.Commands,
            command => command.Kind == DrawCommandKind.LayerPush && command.Blur > 0f
        );

        return layer.Blur;
    }

    /// <summary>⚠ <b>The issue's own case: half way from <c>blur(2px)</c> to <c>blur(8px)</c> is <c>blur(5px)</c>.</b></summary>
    [Fact]
    public void A_blur_is_half_way_at_the_midpoint() {
        using var document = new UiDocument(200f, 100f);
        Settled(document, "filter: blur(2px);", "filter: blur(8px);");

        Assert.Equal(5f, DrawnBlur(document), Tolerance);
    }

    /// <summary>Two lists of the same functions interpolate pairwise, and write back as a list of functions.</summary>
    /// <remarks>
    ///     ⚠ <b>The text is asserted, and that is the half that was a second defect.</b> A list of two
    ///     functions is a list of lists, and <see cref="StyleValue.ToCss" /> joined every list with
    ///     spaces — <c>blur 5px grayscale 0.5</c>, which reads back as one four-item function and is
    ///     refused whole by the filter reader. A lone function survives that, because
    ///     <c>blur 5px</c> parses to the same two items <c>blur(5px)</c> does.
    /// </remarks>
    [Fact]
    public void A_list_of_the_same_functions_interpolates_pairwise() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "filter: blur(2px) grayscale(0);", "filter: blur(8px) grayscale(1);");

        Assert.Equal("blur(5px) grayscale(0.5)", Text(document, box));
        Assert.Equal(5f, DrawnBlur(document), Tolerance);
    }

    /// <summary>A <c>none</c> start is the other list at its identity: <c>blur(0)</c>, so half way is <c>blur(4px)</c>.</summary>
    [Fact]
    public void A_none_start_travels_from_the_identity() {
        using var document = new UiDocument(200f, 100f);
        Settled(document, "filter: none;", "filter: blur(8px);");

        Assert.Equal(4f, DrawnBlur(document), Tolerance);
    }

    /// <summary>An undeclared start is the initial <c>none</c>, and the same identity.</summary>
    /// <remarks>Each amount function's identity is its own: one for <c>brightness</c>, zero for <c>sepia</c>.</remarks>
    [Fact]
    public void An_undeclared_start_travels_from_each_functions_own_identity() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "", "filter: brightness(3) sepia(1) hue-rotate(90deg);");

        Assert.Equal("brightness(2) sepia(0.5) hue-rotate(45deg)", Text(document, box));
    }

    /// <summary>And a <c>none</c> end is the identity too, so leaving a blur fades it out.</summary>
    [Fact]
    public void A_none_end_travels_to_the_identity() {
        using var document = new UiDocument(200f, 100f);
        Settled(document, "filter: blur(8px);", "filter: none;");

        Assert.Equal(4f, DrawnBlur(document), Tolerance);
    }

    /// <summary>
    ///     ⚠ <b>Different functions do not interpolate</b>, which is the half a fix that let every
    ///     keyword pair through would break.
    /// </summary>
    /// <remarks>
    ///     <c>grayscale(0)</c> to <c>sepia(1)</c> has no midpoint, so under
    ///     <c>transition-behavior: normal</c> the new value is applied at once — half way through it is
    ///     already the end. The two arguments are both numbers on purpose: a pair whose arguments could
    ///     not interpolate anyway would stay discrete even if the function names were let through.
    /// </remarks>
    [Fact]
    public void A_list_of_different_functions_does_not_interpolate() {
        using var document = new UiDocument(200f, 100f);
        var box = Settled(document, "filter: grayscale(0);", "filter: sepia(1);");

        var value = Filter(document, box);

        Assert.Equal(StyleValueKind.List, value.Kind);
        Assert.Equal("sepia", document.Styles.Names.NameOf(value.Items[0].Keyword));
        Assert.Equal(1f, value.Items[1].Number, Tolerance);
    }
}
