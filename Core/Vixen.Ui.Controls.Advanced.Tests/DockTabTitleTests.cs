// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>A dock tab's title is one line, however narrow its group (#1477).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Found as a picture</b>: in the editor's asset-picker reference the inspector's tab
///         strip sat ten pixels below the hierarchy's beside it. The inspector's group also holds
///         "Build Settings", and in a 184 px group that title was laid out 55 wide and two lines tall
///         — so its tab was 47 tall, and every tab in the row stretched to it.
///     </para>
///     <para>
///         The oracle is a one-word tab in the same group: every title must be exactly as tall as
///         it, and every tab as tall as its tab. The premise is asserted too — the group really is
///         narrower than its titles, or the test could not tell wrapping from not.
///     </para>
/// </remarks>
public class DockTabTitleTests {
    [Fact]
    public void A_two_word_title_in_a_narrow_group_stays_on_one_line() {
        // The inspector column's width in the picture the defect was found in.
        using var fixture = new AdvancedFixture(width: 184f, height: 300f);

        var host = fixture.Add<DockingHost>();
        host.AddPanel("inspector", "Inspector");
        host.AddPanel("build", "Build Settings");
        host.AddPanel("another", "Another Long Title");
        fixture.Update();
        fixture.Update();

        var group = Assert.Single(host.Groups);
        var tabs = group.Tabs.Children.OfType<DockTab>().ToList();
        var one = tabs[0];

        Assert.True(group.Tabs.Width > group.Strip.Width, $"the premise: the titles overflow the group, {group.Tabs.Width} in {group.Strip.Width}");

        var oneLine = Title(one).Height;
        Assert.True(oneLine > 0f, "the one-word title is measured");

        foreach (var tab in tabs) {
            Assert.Equal(oneLine, Title(tab).Height, 0.5f);
            Assert.Equal(one.Height, tab.Height, 0.5f);
        }
    }

    static UiElement Title(DockTab tab) => tab.Children.First(child => !string.IsNullOrEmpty(child.Text));
}
