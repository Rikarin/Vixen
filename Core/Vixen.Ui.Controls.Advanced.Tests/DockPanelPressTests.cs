// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>What <see cref="DockPanel.WhenPressedIn" /> promises, which is the leg and the order.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The editor's own context tests do not pin either.</b> Five assemblies had copied
///         these eight lines and every one of them said in its comments that the capture leg and
///         <c>handledEventsToo</c> were the point — but rewriting the shared copy to bubble, without
///         handled events, left <c>CommandContextTests</c>' eleven cases green. A press in an empty
///         panel reaches the panel on either leg, so a suite that only presses empty panels cannot
///         tell the two apart. These two do, by putting something in the panel that consumes the
///         press first, which is the arrangement every real panel has.
///     </para>
///     <para>
///         What it costs to get this wrong is not a crash: it is that the first Delete after
///         clicking into a panel is still aimed at the panel before it.
///     </para>
/// </remarks>
public class DockPanelPressTests {
    [Fact]
    public void A_press_a_child_consumes_still_claims_the_panel() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("console", "Console");

        var row = panel.Add("row");
        row.SetStyle("height", "40px");

        // What a tree row, a slider and a viewport all do: handle the press and mark it handled.
        row.AddHandler<PointerEvent>((_, args) => args.Handled = true);

        var claims = 0;
        panel.WhenPressedIn(() => claims++);

        fixture.Update();
        fixture.Click(row);

        Assert.Equal(1, claims);
    }

    [Fact]
    public void The_claim_lands_before_the_child_acts_on_the_press() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("console", "Console");

        var row = panel.Add("row");
        row.SetStyle("height", "40px");

        var order = new List<string>();

        row.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action == PointerAction.Pressed) {
                    order.Add("row");
                }
            }
        );

        panel.WhenPressedIn(() => order.Add("panel"));

        fixture.Update();
        fixture.Click(row);

        // ⚠ Order, not merely presence. A handler that ran after the row's would mean the first
        // scoped command of a visit to a panel was resolved against the panel before it — the whole
        // reason this is the capture leg and not the bubble one.
        Assert.Equal(["panel", "row"], order);
    }

    [Fact]
    public void A_release_is_not_a_claim() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("console", "Console");

        var claims = 0;
        panel.WhenPressedIn(() => claims++);

        fixture.Update();

        var centre = AdvancedFixture.Centre(panel);
        fixture.Release(centre.X, centre.Y);

        Assert.Equal(0, claims);
    }
}
