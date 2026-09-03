// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>Whether an interface is still, as a number a test can assert on.</summary>
/// <remarks>
///     <para>
///         <b>The instrument for "redraw only when something changed", and it is deliberately only the
///         instrument.</b> <c>UiDocument.Update</c> and <c>UiDocument.Draw</c> have always returned
///         "did anything change" and <i>every</i> frame loop in the tree discarded both returns —
///         <c>EditorHost.Run</c>, <c>UiApplication.Run</c> and this harness alike — so nothing could
///         state stillness as a property, and a redraw gate had no number to be judged on.
///         <see cref="UiTest.Updates" /> and <see cref="UiTest.Redraws" /> are that number.
///     </para>
///     <para>
///         ⚠ <b>Every test here asserts both halves, because a counter that cannot rise is worse than
///         no counter.</b> The failure this file exists to prevent is the one that keeps recurring in
///         this tree: an instrument that reports success on the day it does not run. A stillness
///         counter wired to a constant <c>false</c> would make every "the interface is still" test in
///         the codebase pass for ever, so each case pairs "it stayed flat" with "and it moved when
///         something happened".
///     </para>
///     <para>
///         ⚠ <b>Counted in frames of work, never in milliseconds.</b> The harness's clock moves only
///         when <see cref="UiTest.Frame" /> says so, so these are deterministic on any machine at any
///         load — which a "the still frame was faster" assertion would not be.
///     </para>
/// </remarks>
public class StillnessTests {
    const int Side = 64;

    /// <summary>A document with one box, and a class that makes it wider.</summary>
    /// <remarks>
    ///     ⚠ <b>Both rules are classes, and <c>.wide</c> comes second.</b> An id selector outranks a
    ///     class whatever the order, so a box declared as <c>#box</c> would keep its 10px and the
    ///     "and it moved" half of every test below would assert nothing — which is the failure mode
    ///     this file is written against, reached by writing the fixture rather than the counter wrong.
    /// </remarks>
    static UiTest Opened() {
        var ui = UiTest.Create(Side, Side, new UiTestOptions { RetryFrames = 2 });

        ui.Load(
            $"root {{ width: {Side}px; height: {Side}px; }} "
            + ".box { width: 10px; height: 10px; background-color: red; } "
            + ".wide { width: 20px; }"
        );

        return ui;
    }

    /// <summary>A document nobody touched neither updates nor redraws, however long it runs.</summary>
    /// <remarks>
    ///     The settle happens first — <see cref="UiTest.Load" /> and the first frames do real work, and
    ///     a test that counted from zero would be asserting that the interface never appeared.
    /// </remarks>
    [Fact]
    public void A_still_document_neither_updates_nor_redraws() {
        using var ui = Opened();

        ui.Create("div", classNames: "box");
        ui.Frames(4);

        var updates = ui.Updates;
        var redraws = ui.Redraws;

        ui.Frames(30);

        Assert.Equal(updates, ui.Updates);
        Assert.Equal(redraws, ui.Redraws);

        // And the counters are not simply stuck: the same document, changed, moves both. Without this
        // half the assertion above is satisfied by a counter that never counts anything.
        ui.Get(".box").Element.AddClass("wide");
        ui.Frames(1);

        Assert.True(ui.Updates > updates, "Restyling an element has to count as work.");
        Assert.True(ui.Redraws > redraws, "A box that changed width has to count as a redraw.");
    }

    /// <summary>⚠ Writing what is already there is not a redraw, and that is the basis of a gate.</summary>
    /// <remarks>
    ///     Adding a class an element already carries changes nothing, so the picture is identical and
    ///     the frame did not need drawing. <c>DrawList</c> answers by comparing the rebuilt commands
    ///     against the previous frame's rather than by trusting a dirty flag — a gate built on the flag
    ///     would redraw here for nothing.
    /// </remarks>
    [Fact]
    public void Writing_the_class_that_is_already_there_is_not_a_redraw() {
        using var ui = Opened();

        var box = ui.Create("div", classNames: "box");

        box.AddClass("wide");
        ui.Frames(4);

        var redraws = ui.Redraws;

        box.AddClass("wide");
        ui.Frames(2);

        Assert.Equal(redraws, ui.Redraws);

        // The contrast, on the same element and the same class, so the only difference is whether the
        // write changed anything.
        box.RemoveClass("wide");
        ui.Frames(1);

        Assert.True(ui.Redraws > redraws, "A width that actually changed has to count as a redraw.");
    }

    /// <summary>An element added and an element removed both count, so a gate cannot miss either.</summary>
    /// <remarks>
    ///     ⚠ <b>The removal is the half that gets missed.</b> A sweep that drops nothing is invisible
    ///     to every test whose subject stays alive, and this codebase has shipped that shape more than
    ///     once — so the take-away is asserted beside the put-in rather than assumed to be symmetric.
    /// </remarks>
    [Fact]
    public void Adding_and_removing_an_element_both_count_as_a_redraw() {
        using var ui = Opened();

        ui.Frames(4);

        var redraws = ui.Redraws;
        var box = ui.Create("div", classNames: "box");

        ui.Frames(1);

        Assert.True(ui.Redraws > redraws, "An element that appeared has to count as a redraw.");

        redraws = ui.Redraws;
        box.Remove();
        ui.Frames(1);

        Assert.True(ui.Redraws > redraws, "An element that went has to count as a redraw.");
    }

    /// <summary>The counters describe frames, so they never move on their own.</summary>
    /// <remarks>
    ///     ⚠ Neither counter can exceed <see cref="UiTest.FrameCount" />: one increment per frame at
    ///     most, by construction. A counter that could run ahead of the frames would be counting
    ///     passes rather than frames, and "still for thirty frames" would stop meaning anything.
    /// </remarks>
    [Fact]
    public void Neither_counter_can_run_ahead_of_the_frames() {
        using var ui = Opened();

        ui.Create("div", classNames: "box");
        ui.Frames(10);

        Assert.True(ui.Updates <= ui.FrameCount);
        Assert.True(ui.Redraws <= ui.FrameCount);
        Assert.True(ui.Redraws > 0, "The interface appearing is at least one redraw.");
    }
}
