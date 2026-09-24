// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A box that comes out zero wide or zero tall, and the children that overflow it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The paint walk and the hit test disagreed about these children (#1375).</b>
///         <c>DrawListBuilder.Emit</c> returned at the top for any zero-sized element and took the
///         subtree with it. <c>UiDocument.HitTest</c> prunes on no size, so it still reached the
///         children. They were laid out, they took the pointer, and nothing drew them. CSS paints a
///         child that overflows a zero box, so the hit test was right and the paint was wrong.
///     </para>
///     <para>
///         <b>Each test asks both questions about the same point</b>: is something painted there, and
///         does a click there land on it. The two answers must agree. The first three fixtures are
///         the ways to reach a zero box that the issue named. The last three are the zero boxes that
///         still paint nothing, because in CSS they paint nothing.
///     </para>
///     <para>
///         The device picture of the first fixture is <c>UiApplicationCaptureTests</c> in
///         <c>Vixen.Ui.Desktop.Tests</c>, through the whole application on a real GPU.
///     </para>
/// </remarks>
public class ZeroSizedBoxPaintTests {
    /// <summary>The child's fill, which nothing else in these fixtures uses.</summary>
    static readonly Color4 Red = new(1f, 0f, 0f, 1f);

    /// <summary>An element that draws a 10×10 square whatever its own size is.</summary>
    sealed class Marker : UiElement {
        protected internal override void OnDraw(DrawContext context) =>
            context.FillRectangle(new Rectangle(300f, 200f, 10f, 10f), new Color4(0f, 1f, 0f, 1f));
    }

    static UiDocument Laid(string css) {
        var document = new UiDocument(400f, 300f);
        document.Load(
            "root { width: 400px; height: 300px; flex-direction: row; align-items: flex-start; }\n"
            + ".child { display: block; width: 120px; height: 30px; background-color: #ff0000; }\n"
            + css
        );

        document.Root.Add("div", classNames: "box").Add("div", classNames: "child");
        document.Update();
        document.Draw();

        return document;
    }

    static UiElement Box(UiDocument document) => document.Root.Children[0];

    static UiElement Child(UiDocument document) => Box(document).Children[0];

    /// <summary>The red rectangles in the draw list: the child's background and nothing else.</summary>
    static int ChildRectangles(UiDocument document) =>
        document.Drawing.Commands.Count(command => command.Kind == DrawCommandKind.Rectangle && command.Color == Red);

    /// <summary>A flex item with <c>container-type: inline-size</c> and no width is 0 wide, and its child is still drawn.</summary>
    /// <remarks>
    ///     The fixture the issue measured. Before the fix the draw list held no rectangle for the child
    ///     and <c>HitTest(50, 15)</c> returned it.
    /// </remarks>
    [Fact]
    public void A_child_overflowing_a_zero_wide_query_container_is_painted_where_it_is_hit() {
        using var document = Laid(".box { display: block; container-type: inline-size; }");

        Assert.Equal(0f, Box(document).Width);
        Assert.Equal(120f, Child(document).Width);

        Assert.Equal(1, ChildRectangles(document));
        Assert.Same(Child(document), document.HitTest(50f, 15f));
    }

    /// <summary>A <c>height: 0</c> wrapper, the anchor an absolutely positioned child hangs from.</summary>
    [Fact]
    public void A_child_overflowing_a_zero_tall_wrapper_is_painted() {
        using var document = Laid(".box { display: block; width: 200px; height: 0px; }");

        Assert.Equal(0f, Box(document).Height);

        Assert.Equal(1, ChildRectangles(document));
        Assert.Same(Child(document), document.HitTest(50f, 15f));
    }

    /// <summary><c>contain: size</c> with no stated size is 0×0, and its contents are still painted.</summary>
    [Fact]
    public void A_child_of_a_size_contained_box_with_no_size_is_painted() {
        using var document = Laid(".box { display: block; contain: size; }");

        Assert.Equal(0f, Box(document).Width);
        Assert.Equal(0f, Box(document).Height);

        Assert.Equal(1, ChildRectangles(document));
        Assert.Same(Child(document), document.HitTest(50f, 15f));
    }

    /// <summary><c>display: none</c> is a zero box too, and nothing in its subtree is emitted.</summary>
    /// <remarks>
    ///     ⚠ The case the old early return existed for. The layout zeroes the whole subtree, so a walk
    ///     into it would emit children at 0×0. The draw list is compared with a document that has no
    ///     box at all, so an invisible command left behind fails as surely as a visible one.
    /// </remarks>
    [Fact]
    public void Display_none_emits_nothing_for_its_subtree() {
        using var hidden = Laid(".box { display: none; }");

        // A control that draws whatever its size is, which is what a walk into the zeroed subtree
        // would reach. The child's background cannot show that: a zeroed child paints no box.
        hidden.Root.Children[0].Add<Marker>("marker");
        hidden.Update();
        hidden.Draw();

        using var absent = new UiDocument(400f, 300f);
        absent.Load("root { width: 400px; height: 300px; }");
        absent.Update();
        absent.Draw();

        Assert.Equal(0, ChildRectangles(hidden));
        Assert.Equal(absent.Drawing.Commands.Count, hidden.Drawing.Commands.Count);
        Assert.Same(hidden.Root, hidden.HitTest(50f, 15f));
    }

    /// <summary>A zero axis the box's own <c>overflow</c> clips cuts the child on both sides of the question.</summary>
    [Fact]
    public void A_zero_tall_box_that_clips_paints_nothing_and_takes_no_click() {
        using var document = Laid(".box { display: block; width: 200px; height: 0px; overflow: hidden; }");

        Assert.Equal(0, ChildRectangles(document));
        Assert.Same(document.Root, document.HitTest(50f, 15f));
    }

    /// <summary>A zero-wide box that clips only vertically still lets the child overflow sideways.</summary>
    /// <remarks>
    ///     The box is 0 wide and 30 tall, and its clip cuts only the top and bottom edges. So the child,
    ///     which is 30 tall, is inside the clip and outside the box's width, and CSS paints it.
    /// </remarks>
    [Fact]
    public void A_zero_wide_box_that_clips_only_the_other_axis_still_paints_its_child() {
        using var document = Laid(".box { display: block; container-type: inline-size; overflow-y: hidden; }");

        Assert.Equal(0f, Box(document).Width);
        Assert.Equal(30f, Box(document).Height);

        Assert.Equal(1, ChildRectangles(document));
        Assert.Same(Child(document), document.HitTest(50f, 15f));
    }

    /// <summary>The zero box itself draws no background: it has no area to draw it in.</summary>
    [Fact]
    public void The_zero_box_paints_no_background_of_its_own() {
        using var document = Laid(".box { display: block; width: 200px; height: 0px; background-color: #0000ff; }");

        var blue = new Color4(0f, 0f, 1f, 1f);

        Assert.DoesNotContain(document.Drawing.Commands, command => command.Color == blue);
        Assert.Equal(1, ChildRectangles(document));
    }
}
