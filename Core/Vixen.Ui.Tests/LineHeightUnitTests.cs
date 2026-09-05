// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The <c>lh</c> unit, from the text of a declaration to the height of a box.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Three stages and each was missing, which is why none of them is asserted on its
///         own here.</b> <c>StyleValueParser</c> read no <c>lh</c>, <c>LengthContext</c> carried no
///         line height to resolve one against, and nothing handed it the element's. A test that
///         parsed the unit and stopped would have been green with the box still the wrong size —
///         that is doc 43's <c>max-block-lh</c> row exactly, whose <c>partial</c> came from the class
///         not resolving rather than from anything the row's own <c>value_gap</c> said.
///     </para>
///     <para>
///         <b>What this prints on the day nothing runs.</b> A <c>UiDocument</c> with no device, no
///         frame and no clock; the numbers are arithmetic on a declared line height and are the same
///         on every machine. The declared cases are what the assertions rest on, and
///         <see cref="Normal_line_height_answers_a_stand_in_for_the_fonts_metrics" /> is the one that
///         pins the approximation as an approximation rather than letting it pass unremarked.
///     </para>
/// </remarks>
public class LineHeightUnitTests {
    const float Tolerance = 0.001f;

    /// <summary>A declared line height is what <c>1lh</c> measures, and it is not the font size.</summary>
    /// <remarks>
    ///     ⚠ 30px against a 20px font, so a resolver that quietly answered <c>em</c> — the mistake
    ///     with the most plausible-looking output, since both are "something to do with the text" —
    ///     comes out at 20 and 40 rather than 30 and 60. Neither number is a multiple of the other's
    ///     wrong answer.
    /// </remarks>
    [Theory]
    [InlineData("1lh", 30f)]
    [InlineData("2lh", 60f)]
    [InlineData("0.5lh", 15f)]
    public void A_declared_line_height_is_what_lh_measures(string length, float expected) {
        var style = new BridgeFixture().Build(
            $"height: {length}",
            LengthContext.ForViewport(1000f, 500f).WithFontSize(20f).WithLineHeight(30f)
        );

        Assert.Equal(LayoutUnit.Point, style.Dimensions[(int)Dimension.Height].Unit);
        Assert.Equal(expected, style.Dimensions[(int)Dimension.Height].Value, Tolerance);
    }

    /// <summary><c>line-height: normal</c> is a stand-in and never a zero.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole point of the assertion is the <i>lower</i> bound.</b> CSS resolves
    ///     <c>normal</c> from the font's ascender, descender and line gap, which nothing that
    ///     resolves a length has — and the tempting answer for "I have no line height" is the scale
    ///     factor zero that <c>PixelsPer</c>'s own remark warns about, which would make
    ///     <c>max-height: 1lh</c> a box of no height. So <c>NaN</c> and an unset context both come
    ///     back as a multiple of the font size, and it is a documented approximation rather than a
    ///     measurement.
    /// </remarks>
    [Fact]
    public void Normal_line_height_answers_a_stand_in_for_the_fonts_metrics() {
        // `NaN` is how `UiElement.LineHeight` spells "the font decides".
        var normal = LengthContext.ForViewport(1000f, 500f).WithFontSize(20f).WithLineHeight(float.NaN);
        Assert.Equal(24f, normal.PixelsPer(StyleUnit.LineHeight), Tolerance);

        // And a context nobody told anything answers the same, rather than zero.
        Assert.Equal(24f, LengthContext.ForViewport(1000f, 500f).WithFontSize(20f).LineHeight, Tolerance);

        var style = new BridgeFixture().Build("height: 1lh", normal);
        Assert.Equal(24f, style.Dimensions[(int)Dimension.Height].Value, Tolerance);
    }

    /// <summary>And the box in a real document is one line tall, which is the claim that matters.</summary>
    /// <remarks>
    ///     ⚠ <b>End to end rather than through <c>BridgeFixture</c>, because the wiring is the half
    ///     that was missing and the bridge is handed its context by the test.</b>
    ///     <c>UiDocument.Apply</c> resolves the line height and then builds the layout style, and
    ///     nothing was carrying the first into the second — so a <c>1lh</c> would have measured
    ///     against the stand-in however the stylesheet was written, which reads as an off-by-a-fifth
    ///     box rather than as a broken one.
    /// </remarks>
    [Fact]
    public void A_document_measures_lh_against_the_elements_own_line_height() {
        using var document = new UiDocument(400f, 200f);
        document.Load(
            """
            root  { width: 400px; height: 200px; }
            #box  { font-size: 20px; line-height: 30px; height: 2lh; width: 10px; }
            """,
            StyleOrigin.Author
        );

        var box = document.Create("div", document.Root, "box");
        document.Update();

        Assert.Equal(60f, box.Height, Tolerance);
    }

    /// <summary>An inherited line height reaches it too, which the reference test could have hidden.</summary>
    /// <remarks>
    ///     ⚠ <b><c>line-height</c> is inherited outside the cascade</b> — a child of an element that
    ///     changed it has a byte-for-byte unchanged <c>ComputedStyle</c>, so
    ///     <c>UiDocument.Apply</c>'s reference comparison passes and the layout style is not rebuilt.
    ///     That is the arrangement in which a <c>1lh</c> is built once and keeps the box it happened
    ///     to get, and the second half of this test is what says the applied line height is part of
    ///     the test that decides to rebuild.
    /// </remarks>
    [Fact]
    public void A_changed_inherited_line_height_rebuilds_the_box_it_sized() {
        using var document = new UiDocument(400f, 200f);
        document.Load(
            """
            root       { width: 400px; height: 200px; }
            #outer     { font-size: 20px; line-height: 30px; }
            #outer.tall { line-height: 50px; }
            #box       { height: 1lh; width: 10px; }
            """,
            StyleOrigin.Author
        );

        var outer = document.Create("div", document.Root, "outer");
        var box = document.Create("div", outer, "box");

        document.Update();
        Assert.Equal(30f, box.Height, Tolerance);

        // The class is on the *parent*: the box's own declarations and its own computed style are
        // untouched, and only the line height it inherits has moved.
        outer.AddClass("tall");
        document.Update();

        Assert.Equal(50f, box.Height, Tolerance);
    }
}
