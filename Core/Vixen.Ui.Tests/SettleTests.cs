// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The callback that says the boxes are final, and the loop that lets one change them.</summary>
public class SettleTests {
    static UiDocument Documented() {
        var document = new UiDocument(400f, 300f);
        document.Load("""
            root { width: 400px; height: 300px; flex-direction: column; }
            .row { height: 20px; }
        """);

        return document;
    }

    [Fact]
    public void A_handler_sees_the_boxes_the_pass_produced() {
        using var document = Documented();
        document.Root.Add("div", classNames: "row");
        var second = document.Root.Add("div", classNames: "row");

        var height = -1f;
        var top = -1f;

        document.LayoutFinished += _ => {
            height = second.Height;
            top = second.AbsoluteTop;
        };

        document.Update();

        // The whole point. Before this existed a control had to read its own size from the previous
        // frame, because there was no moment it could be told the current one was ready.
        Assert.Equal(20f, height, 0.001f);

        // ⚠ **And the absolute position, not only the size** — which is a second assertion rather
        // than a thorough one. Sizes come from the flexbox pass and document-space positions come
        // from the accumulate walk after it, so a callback raised between the two answers correctly
        // about `Height` and with the previous frame's `AbsoluteTop`. A test that read only the size
        // could not see the difference, and a scroll bar asking where its content is would.
        Assert.Equal(20f, top, 0.001f);
    }

    [Fact]
    public void A_handler_that_changes_the_document_gets_another_pass() {
        using var document = Documented();

        var added = false;
        document.LayoutFinished += d => {
            if (added) {
                return;
            }

            added = true;
            d.Root.Add("div", classNames: "row");
        };

        document.Update();

        // A virtualiser that has just learned its viewport is taller realises more rows — a
        // structural change during a pass that has already run — and the frame must not be drawn
        // with those rows unlaid.
        Assert.Single(document.Root.Children);
        Assert.Equal(20f, document.Root.Children[0].Height, 0.001f);
        Assert.True(document.Settled);
        Assert.Equal(1, document.SettlingPasses);
    }

    [Fact]
    public void A_handler_that_never_stops_is_stopped() {
        using var document = Documented();

        var passes = 0;
        document.LayoutFinished += d => {
            passes++;
            d.Root.Add("div", classNames: "row");
        };

        document.Update();

        // ⚠ "The interface hangs" is a worse failure than any interface the loop could have
        // produced, so the budget is a hard stop rather than a warning. What the frame shows is one
        // pass behind what the handler asked for, and `Settled` is how a control's author finds out
        // they wrote this.
        Assert.False(document.Settled);
        Assert.Equal(UiDocument.SettlePasses, document.SettlingPasses);
        Assert.Equal(UiDocument.SettlePasses + 1, passes);
    }

    [Fact]
    public void A_quiet_handler_costs_one_call_and_no_extra_pass() {
        using var document = Documented();
        document.Root.Add("div", classNames: "row");

        var calls = 0;
        document.LayoutFinished += _ => calls++;

        document.Update();

        Assert.Equal(1, calls);
        Assert.Equal(0, document.SettlingPasses);
        Assert.True(document.Settled);

        // And an unchanged document does not run a pass at all, so it does not call this either —
        // the callback says "the layout finished", and no layout happened.
        Assert.False(document.Update());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void The_tick_carries_the_hosts_clock_rather_than_reading_one() {
        using var document = Documented();

        var seen = TimeSpan.Zero;
        document.Ticked += (_, now) => seen = now;

        document.Tick(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), seen);
        Assert.Equal(TimeSpan.FromSeconds(3), document.Now);
    }
}
