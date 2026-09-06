// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>
///     A two-line <c>&lt;span&gt;</c>'s background, border and hit area, which are one box per line.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These asserted what Vixen did and a browser did something else, until the painter
///         walked fragments.</b> They are inverted now, and the four numbers each one moved between
///         are recorded in the remark on it — a test whose old expectation is unrecoverable is a test
///         that cannot say what it caught.
///     </para>
///     <para>
///         ⚠ <b>The layout half shipped years ahead of its only two consumers.</b>
///         <c>LayoutTree.GetFragment</c> and <c>GetFragmentCount</c> give a span crossing a line break
///         one rectangle per line, with <c>LayoutFragmentEnds</c> saying which of its two real ends
///         each carries — and until this landed their only caller anywhere in the tree was
///         <c>InlineFragmentationTests</c>. <c>DrawListBuilder.EmitBody</c> painted one rectangle
///         taken from <c>UiElement.Width</c> and <c>Height</c>, which for a fragmented node is the
///         UNION of its fragments.
///     </para>
///     <para>
///         ⚠ <b>The union is the right answer to a different question, which is why this was a hole
///         and not an oversight.</b> CSS 2.1 §10.1 makes the union the containing block of an
///         absolutely positioned descendant of an inline box, and it is what a scroll extent and a
///         coarse hit test want. It is not what a background covers. On a ragged second line the
///         difference was a visible rectangle of colour where there is no text.
///     </para>
///     <para>
///         ⚠ <b>And the painter and the hit test move together.</b> <c>DrawListBuilder</c>'s own
///         remark makes paint order and hit-test order agreeing the guarantee that a click lands on
///         what is drawn; walking fragments in one and not the other breaks it on exactly the wrapped
///         inline the feature is for. Both halves are pinned below so that a change which moves only
///         one goes red.
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
    static (UiDocument Document, UiElement Run) Build(string extra = "") {
        var document = new UiDocument(400f, 300f);

        document.Load(
            $$"""
              root { width: 400px; height: 300px; }
              #box { display: block; width: 100px; height: 40px; }
              #run { display: inline; background-color: #ff0000; {{extra}} }
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
    ///     Asserted first because everything below is a departure from it: the layout is right, and
    ///     the union is what both consumers used to be the whole of.
    /// </remarks>
    [Fact]
    public void A_fragmented_span_reports_the_union_of_its_fragments() {
        var (document, run) = Build();
        using var scope = document;

        Assert.Equal(90f, run.Width, Tolerance);
        Assert.Equal(40f, run.Height, Tolerance);
    }

    /// <summary>Its background is one rectangle per line, which is what Chrome paints.</summary>
    /// <remarks>
    ///     ⚠ <b>This asserted a single (0, 0, 90, 40) until the painter walked fragments</b> — one
    ///     rectangle, and thirty points of colour on the second row that no browser puts there. The
    ///     two rectangles below are Chrome's.
    /// </remarks>
    [Fact]
    public void A_fragmented_spans_background_is_one_rectangle_per_line() {
        var (document, _) = Build();
        using var scope = document;

        Assert.Equal(2, document.Drawing.Commands.Count);

        var first = document.Drawing.Commands[0];
        var second = document.Drawing.Commands[1];

        Assert.Equal(DrawCommandKind.Rectangle, first.Kind);
        Assert.Equal(0f, first.X, Tolerance);
        Assert.Equal(0f, first.Y, Tolerance);
        Assert.Equal(90f, first.Width, Tolerance);
        Assert.Equal(20f, first.Height, Tolerance);

        Assert.Equal(DrawCommandKind.Rectangle, second.Kind);
        Assert.Equal(0f, second.X, Tolerance);
        Assert.Equal(20f, second.Y, Tolerance);
        Assert.Equal(60f, second.Width, Tolerance);
        Assert.Equal(20f, second.Height, Tolerance);
    }

    /// <summary>And nothing is clickable in the part of the union no fragment covers.</summary>
    /// <remarks>
    ///     ⚠ <b>This asserted <c>Assert.Same(run, …)</c> until the hit test walked fragments.</b> The
    ///     point is inside the union and inside no fragment, so a browser hits nothing there. Vixen
    ///     hit the span — wrong, and wrong in the same place it painted, which is the invariant
    ///     <c>DrawListBuilder</c> states. It answers the block behind it now, which is what is really
    ///     drawn at (75, 30).
    /// </remarks>
    [Fact]
    public void A_fragmented_span_is_not_hit_where_no_fragment_covers() {
        var (document, run) = Build();
        using var scope = document;

        // (75, 30) is on the second row, thirty points past the end of the box that is actually
        // there. Both fragments end before it; the union does not.
        Assert.NotSame(run, document.HitTest(75f, 30f));

        // ⚠ And the two halves of the invariant are the same walk: a point inside the second
        // fragment is still clickable, so this is not passing by refusing everything. It answers the
        // inline-block sitting in that fragment rather than the span itself, which is the ordinary
        // deepest-hit rule and not a fragment question — the span is what contains it.
        Assert.Same(run, document.HitTest(30f, 30f)?.Parent);
    }

    /// <summary>
    ///     A break is not an edge of the box, so only the first fragment is stroked on the left and
    ///     only the last on the right.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>CSS Display §2.2, and it is what takes a fragment off the uniform-ring path.</b> A
    ///     box whose four border widths agree is one <c>Border</c> command; dropping the inline-start
    ///     width on the second fragment makes its widths disagree, so it comes out as bands — a top,
    ///     a bottom and a right, and no left. What proves the rule rather than the machinery is the
    ///     absence of a band at <c>x == 0</c> on the second line.
    /// </remarks>
    [Fact]
    public void A_break_is_stroked_on_neither_side_of_it() {
        var (document, _) = Build("border: 2px solid #0000ff;");
        using var scope = document;

        var bands = document.Drawing.Commands
            .Where(command => command.Color == new Core.Mathematics.Color4(0f, 0f, 1f, 1f))
            .ToList();

        // The first fragment keeps its left edge and loses its right one; the second is the mirror.
        // Each is therefore three bands, not four, and the two missing ones are at the break.
        Assert.Equal(6, bands.Count);

        // A 2px border widens each fragment by its real ends: 90 + 2 on the first line, 60 + 2 on the
        // second. `Band` emits top, bottom, left, right in that order and returns on a zero width, so
        // a fragment missing an end is three bands and the one it is missing is the one not here.
        var first = bands.Take(3).ToList();
        var second = bands.Skip(3).ToList();

        Assert.Contains(first, band => band is { X: 0f, Width: 2f, Height: 16f });
        Assert.DoesNotContain(first, band => band is { X: 90f, Width: 2f });

        Assert.DoesNotContain(second, band => band is { X: 0f, Width: 2f, Height: 16f });
        Assert.Contains(second, band => band is { X: 60f, Width: 2f, Height: 16f });
    }

    /// <summary>And a break gets no rounded corner either, for the same reason.</summary>
    /// <remarks>
    ///     ⚠ <b>A radius is a decoration of an end, so rounding a break would draw a pill in the
    ///     middle of a paragraph.</b> <c>DrawCommand.Radius</c> is the single <c>float</c> a box takes
    ///     when all four corners are the same circle — which no fragment of a rounded fragmented box
    ///     is, since two of its corners are squared. So both fragments carry a zero scalar and their
    ///     real radii in the side buffer, and this asserts the scalar rather than the shape record
    ///     because the scalar is what a consumer reading only <c>Radius</c> would round all four
    ///     corners by.
    /// </remarks>
    [Fact]
    public void A_rounded_fragmented_span_curves_only_its_real_ends() {
        var (document, _) = Build("border-radius: 8px;");
        using var scope = document;

        Assert.Equal(2, document.Drawing.Commands.Count);
        Assert.All(document.Drawing.Commands, command => Assert.Equal(0f, command.Radius, Tolerance));
        Assert.All(document.Drawing.Commands, command => Assert.True(command.HasStyle));

        // The unfragmented case is the control: one box, one uniform radius, no side-buffer entry.
        var (plain, _) = Build();
        using var other = plain;

        Assert.All(plain.Drawing.Commands, command => Assert.False(command.HasStyle));
    }
}
