// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     What a two-line <c>&lt;span&gt;</c>'s background and hit area are today, and what CSS says
///     they should be.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THESE TESTS ASSERT WHAT VIXEN DOES, AND A BROWSER DOES SOMETHING ELSE. They should
///         FAIL and be inverted when a fragment-walking draw list lands</b> — the same shape as
///         <c>A_span_inside_a_span_is_still_atomic</c> in <c>InlineFragmentationTests</c>, and the
///         reason they are here rather than in a report is that a gap nobody can see is a gap nobody
///         closes.
///     </para>
///     <para>
///         ⚠ <b>The layout half shipped and nothing reads it.</b> <c>LayoutTree.GetFragment</c> and
///         <c>GetFragmentCount</c> give a span crossing a line break one rectangle per line, with
///         <c>LayoutFragmentEnds</c> saying which of its two real ends each carries — and their only
///         caller anywhere in the tree is <c>InlineFragmentationTests</c>. No painter, no hit test and
///         no consumer outside the layout assembly reads a fragment at all, so
///         <c>DrawListBuilder.EmitBody</c> paints one rectangle taken from <c>UiElement.Width</c> and
///         <c>Height</c>, which for a fragmented node is the UNION of its fragments.
///     </para>
///     <para>
///         ⚠ <b>The union is the right answer to a different question, which is why this is a hole
///         and not an oversight.</b> CSS 2.1 §10.1 makes the union the containing block of an
///         absolutely positioned descendant of an inline box, and it is what a scroll extent and a
///         coarse hit test want. It is not what a background covers. On a ragged second line the
///         difference is a visible rectangle of colour where there is no text.
///     </para>
///     <para>
///         ⚠ <b>And the painter and the hit test have to move together.</b>
///         <c>DrawListBuilder</c>'s own remark makes paint order and hit-test order agreeing the
///         guarantee that a click lands on what is drawn; walking fragments in one and not the other
///         breaks it on exactly the wrapped inline the feature is for. Both halves are pinned below
///         so that a fix which moves only one goes red.
///     </para>
/// </remarks>
public class InlineFragmentPaintingTests {
    const float Tolerance = 0.001f;

    /// <summary>
    ///     A 100-point line holding a span of three boxes: 30 and 60 on the first line, 60 on the
    ///     second.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The ragged second line is the whole fixture.</b> Two full lines would make the union
    ///     and the two fragments the same rectangle, and every assertion below would pass against an
    ///     implementation that never heard of a fragment. 30 + 60 fills the first line to 90 and the
    ///     third box wraps to 60, so the union is 90 wide and thirty points of its second row belong
    ///     to no fragment at all.
    /// </remarks>
    static (UiDocument Document, UiElement Run) Build() {
        var document = new UiDocument(400f, 300f);

        document.Load(
            """
            root { width: 400px; height: 300px; }
            #box { display: block; width: 100px; height: 40px; }
            #run { display: inline; background-color: #ff0000; }
            #a   { display: inline-block; width: 30px; height: 20px; }
            #b   { display: inline-block; width: 60px; height: 20px; }
            #c   { display: inline-block; width: 60px; height: 20px; }
            """
        );

        var box = document.Root.Add("div", id: "box");
        var run = box.Add("div", id: "run");
        run.Add("div", id: "a");
        run.Add("div", id: "b");
        run.Add("div", id: "c");

        document.Update();
        document.Draw();

        return (document, run);
    }

    /// <summary>The span's own rectangle is the union of its fragments, which is what the walk gives.</summary>
    /// <remarks>
    ///     Asserted first because everything below is a consequence of it: the layout is right and it
    ///     is the only thing either consumer looks at.
    /// </remarks>
    [Fact]
    public void A_fragmented_span_reports_the_union_of_its_fragments() {
        var (document, run) = Build();
        using var scope = document;

        Assert.Equal(90f, run.Width, Tolerance);
        Assert.Equal(40f, run.Height, Tolerance);
    }

    /// <summary>Its background is one rectangle covering both lines, where CSS paints two.</summary>
    /// <remarks>
    ///     ⚠ <b>Chrome paints (0, 0, 90, 20) and (0, 20, 60, 20).</b> Vixen paints
    ///     (0, 0, 90, 40) — one rectangle, and thirty points of colour on the second row that no
    ///     browser puts there. Invert this when the draw list walks fragments.
    /// </remarks>
    [Fact]
    public void A_fragmented_spans_background_is_painted_as_one_rectangle() {
        var (document, _) = Build();
        using var scope = document;

        var command = Assert.Single(document.Drawing.Commands);

        Assert.Equal(DrawCommandKind.Rectangle, command.Kind);
        Assert.Equal(0f, command.X, Tolerance);
        Assert.Equal(0f, command.Y, Tolerance);
        Assert.Equal(90f, command.Width, Tolerance);
        Assert.Equal(40f, command.Height, Tolerance);
    }

    /// <summary>And it is clickable there too, which is the half that keeps the two consistent.</summary>
    /// <remarks>
    ///     ⚠ <b>This one is not a bug on its own and would become one alone.</b> The point below is
    ///     inside the union and inside no fragment, so a browser hits nothing there. Vixen hits the
    ///     span — wrong, and wrong in the same place it paints, which is the invariant
    ///     <c>DrawListBuilder</c> states. A fragment-walking painter that left the hit test alone
    ///     would make this test the only thing standing between the engine and a click landing on
    ///     something that is not drawn.
    /// </remarks>
    [Fact]
    public void A_fragmented_span_is_hit_in_the_part_of_its_union_no_fragment_covers() {
        var (document, run) = Build();
        using var scope = document;

        // (75, 30) is on the second row, thirty points past the end of the box that is actually
        // there. Both fragments end before it; the union does not.
        Assert.Same(run, document.HitTest(75f, 30f));
    }
}
