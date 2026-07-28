// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Testing.Tests;

/// <summary>The waiting, which is the whole reason this library exists rather than a helper method.</summary>
/// <remarks>
///     Every one of these would pass trivially against a harness that never retried and simply
///     asserted twice, so each is written to fail that way: the thing being waited for does not exist
///     when the command is issued, and nothing but the command itself runs the frames that produce
///     it.
/// </remarks>
public class RetryTests {
    static UiTest Opened(int retryFrames = 60) =>
        UiTest.Create(400f, 300f, new UiTestOptions { RetryFrames = retryFrames });

    [Fact]
    public void An_element_that_appears_later_is_waited_for() {
        using var ui = Opened();
        ui.Load("root { width: 400px; height: 300px; } .toast { width: 100px; height: 20px; }");

        // A game that puts a toast up five frames after it is asked to, which is the ordinary shape
        // of the thing: almost nothing an interface does happens on the frame that caused it.
        var appearsAt = ui.FrameCount + 5;
        var created = false;

        ui.Ticked += () => {
            if (!created && ui.FrameCount >= appearsAt) {
                created = true;
                ui.Create("div", ui.Document.Root, null, "toast").Text = "Saved";
            }
        };

        Assert.Equal(0, ui.Get(".toast").Count);

        // Nothing here runs a frame except the assertion, so this can only pass by waiting.
        ui.Get(".toast").ShouldExist().ShouldHaveText("Saved");

        Assert.True(ui.FrameCount >= appearsAt);
    }

    [Fact]
    public void A_condition_already_true_costs_no_frames() {
        using var ui = Opened();
        ui.Load("root { width: 400px; height: 300px; } .ready { width: 10px; height: 10px; }");
        ui.Create("div", ui.Document.Root, null, "ready");
        ui.Frame();

        var before = ui.FrameCount;
        ui.Get(".ready").ShouldExist().ShouldBeVisible();

        // ⚠ Otherwise every assertion advances the clock, and a suite's gesture timings would depend
        // on how many things it happened to assert.
        Assert.Equal(before, ui.FrameCount);
    }

    [Fact]
    public void Running_out_of_budget_fails_after_exactly_that_many_frames() {
        using var ui = Opened(retryFrames: 7);
        ui.Load("root { width: 400px; height: 300px; }");

        var before = ui.FrameCount;
        Assert.Throws<UiTestException>(() => ui.Get(".never").ShouldExist());

        Assert.Equal(before + 7, ui.FrameCount);
    }

    [Fact]
    public void A_failure_carries_the_command_log_and_the_tree() {
        using var ui = Opened(retryFrames: 1);
        ui.Load("root { width: 400px; height: 300px; } .panel { width: 50px; height: 50px; }");
        ui.Create("div", ui.Document.Root, "shell", "panel");
        ui.Frame();

        ui.Get(".panel").ShouldExist();

        var failure = Assert.Throws<UiTestException>(() => ui.Get(".missing").ShouldHaveCount(2));

        // The claim, and what was actually there.
        Assert.Contains("should have 2 elements", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no elements", failure.Message, StringComparison.Ordinal);

        // The command before it, which is what tells somebody whether the test got where it thought.
        Assert.Contains("get \".panel\"", failure.Message, StringComparison.Ordinal);

        // And the interface, which is what answers "is my selector wrong".
        Assert.Contains("#shell", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subject_is_re_evaluated_rather_than_remembered() {
        using var ui = Opened();
        ui.Load("root { width: 400px; height: 300px; } .row { width: 40px; height: 10px; }");

        var rows = ui.Get(".row");
        Assert.Equal(0, rows.Count);

        ui.Create("div", ui.Document.Root, null, "row");
        ui.Frame();
        Assert.Equal(1, rows.Count);

        // ⚠ And it survives the elements it matched. A list rebuilt between two commands hands the
        // second the new elements rather than a fistful of removed ones — the "detached from the
        // DOM" failure this design cannot produce.
        ui.Get(".row").Element.Remove();
        ui.Create("div", ui.Document.Root, null, "row");
        ui.Create("div", ui.Document.Root, null, "row");
        ui.Frame();

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void The_log_records_how_long_a_command_waited() {
        using var ui = Opened(retryFrames: 3);
        ui.Load("root { width: 400px; height: 300px; }");

        Assert.Throws<UiTestException>(() => ui.Get(".never").ShouldExist());

        var line = ui.Log.Commands.Single(command => command.Text.Contains("should exist", StringComparison.Ordinal));
        Assert.Equal(3, line.Frames);
    }
}
