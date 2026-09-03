// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>
///     <c>ShouldHaveSize</c> and <c>ShouldHavePosition</c>: the two questions the cascade cannot
///     answer.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not one number in these fixtures' stylesheets is a number these assertions expect</b>,
///         and that is deliberate. An assertion on the layout box that could be satisfied by reading
///         the declaration back would be <c>ShouldHaveLength</c> under a second name; every size
///         asserted here is produced by <c>flex-grow</c>, a percentage or a margin, so a
///         re-implementation that consulted the cascade would answer "no such property" rather than
///         the right number by luck.
///     </para>
///     <para>
///         ⚠ <b>And the waiting half is asserted separately</b>, because it is the other half of what
///         these buy over reading <c>UiElement.Width</c>: layout runs inside <c>Frame</c>, so an
///         element created by the line above is zero by zero until one does.
///     </para>
/// </remarks>
public class LayoutBoxTests {
    /// <summary>A row whose two children are sized by nothing they declare.</summary>
    /// <remarks>
    ///     ⚠ The 360 is the row's <i>content</i> box and the 20 either side is outside it — this
    ///     framework's <c>box-sizing</c> is CSS's initial <c>content-box</c>, so the row draws 400
    ///     wide. The first child takes a third of the 360 and the second the rest, by ratio: 120 and
    ///     240 appear nowhere in the sheet, and neither does either child's position.
    /// </remarks>
    static UiTest Fixture() {
        var ui = UiTest.Create(400f, 300f);

        ui.Load("""
            root  { width: 400px; height: 300px; }
            .row  { width: 360px; height: 80px; padding: 10px 20px; margin-left: 30px; }
            .a    { flex-grow: 1; }
            .b    { flex-grow: 2; }
        """);

        var row = ui.Create("div", ui.Document.Root, "row", "row");
        ui.Create("div", row, "a", "a");
        ui.Create("div", row, "b", "b");
        ui.Frame();

        return ui;
    }

    [Fact]
    public void A_flexed_width_is_asserted_although_no_rule_declares_it() {
        using var ui = Fixture();

        // 360 of content box split 1:2. The height is the row's content height, by `align-items:
        // stretch` — also declared nowhere.
        ui.Get("#a").ShouldHaveSize(120f, 80f);
        ui.Get("#b").ShouldHaveSize(240f, 80f);
    }

    [Fact]
    public void The_cascade_cannot_answer_the_same_question() {
        using var ui = Fixture();

        // ⚠ The argument for the assertion existing at all, made as a test rather than in a comment.
        // `width` is not a declared property of `.a`, so the length assertion is not merely wrong
        // here — it has nothing to read.
        var failure = Assert.Throws<UiTestException>(() => ui.Get("#a").ShouldHaveLength("width", 120f));

        Assert.Contains("not an absolute length", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_position_is_the_document_space_one() {
        using var ui = Fixture();

        // ⚠ 50 and not 20. The row's own 30 of margin is in these numbers and is in none of the
        // relative ones — `UiElement.Left` of the first child is 20, the padding, whatever the row
        // does. An assertion written against that pair passes on a panel that slid sideways and took
        // the element with it, which is the failure this one is shaped to catch.
        ui.Get("#a").ShouldHavePosition(50f, 10f);
        ui.Get("#b").ShouldHavePosition(170f, 10f);
    }

    [Fact]
    public void A_size_that_is_wrong_fails_saying_what_it_settled_at() {
        using var ui = Fixture();

        var failure = Assert.Throws<UiTestException>(() => ui.Get("#a").ShouldHaveSize(200f, 80f));

        Assert.Contains("is 120×80", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_position_that_is_wrong_fails_saying_where_it_is() {
        using var ui = Fixture();

        var failure = Assert.Throws<UiTestException>(() => ui.Get("#b").ShouldHavePosition(50f, 10f));

        Assert.Contains("is at (170, 10)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_box_laid_out_later_is_waited_for() {
        using var ui = UiTest.Create(400f, 300f);
        ui.Load("root { width: 400px; height: 300px; } .panel { width: 50%; height: 40px; }");

        var panel = ui.Create("div", ui.Document.Root, "panel", "panel");

        // ⚠ Zero by zero, on the frame it was created: layout is a pass, not a property setter. A
        // test reading `UiElement.Width` here reads this, which is the whole reason the assertion
        // waits rather than compares.
        Assert.Equal(0f, panel.Width);

        var before = ui.FrameCount;
        ui.Get("#panel").ShouldHaveSize(200f, 40f);

        // Nothing above runs a frame, so this can only have passed by running one.
        Assert.True(ui.FrameCount > before);
    }

    [Fact]
    public void A_box_that_moves_later_is_waited_for() {
        using var ui = UiTest.Create(400f, 300f);

        ui.Load("""
            root      { width: 400px; height: 300px; }
            .spacer   { width: 0; height: 10px; }
            .spacer.w { width: 60px; }
            .box      { width: 40px; height: 10px; }
        """);

        var spacer = ui.Create("div", ui.Document.Root, "spacer", "spacer");
        ui.Create("div", ui.Document.Root, "box", "box");
        ui.Frame();

        ui.Get("#box").ShouldHavePosition(0f, 0f);

        // A game that widens the spacer four frames in, pushing the box along.
        var widensAt = ui.FrameCount + 4;
        ui.Ticked += () => {
            if (ui.FrameCount >= widensAt) {
                spacer.AddClass("w");
            }
        };

        ui.Get("#box").ShouldHavePosition(60f, 0f);

        Assert.True(ui.FrameCount >= widensAt);
    }
}
