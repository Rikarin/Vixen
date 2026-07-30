// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>The rule a saved window position is checked against before a window is put there.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 20's A2 names this as what was left of the docking gap.</b> <c>DockFloat</c>
///         records where a floating group was in desktop coordinates and says that whether it becomes
///         a real window is the host's answer at restore time — so whether that position is still a
///         <i>place</i> is the host's answer too. Restoring a torn-off panel onto a monitor that has
///         since been unplugged gives a window nobody can see, cannot move, and which — unlike the
///         main window — has no entry in the switcher to find it by.
///     </para>
///     <para>
///         The grip is a hundred and twenty points: enough of a title bar to grab. A window half off
///         the right edge is one the user put there and dragging it back is a gesture they know.
///     </para>
/// </remarks>
public class WindowReachabilityTests {
    static DisplayInfo Display(float x, float y, float width, float height) {
        var bounds = new Rectangle(x, y, width, height);

        return new DisplayInfo(
            0,
            "Test",
            bounds,
            bounds,
            1f,
            new DisplayMode(new Int2((int) width, (int) height), 60f, IsHdr: false),
            [],
            IsPrimary: true
        );
    }

    static IReadOnlyList<DisplayInfo> One => [Display(0f, 0f, 1920f, 1080f)];

    static IReadOnlyList<DisplayInfo> Two => [Display(0f, 0f, 1920f, 1080f), Display(1920f, 0f, 1920f, 1080f)];

    [Fact]
    public void A_window_on_a_display_is_reachable() {
        Assert.True(PlatformWindowHost.IsReachable(One, 100f, 100f, 320f, 240f));
    }

    [Fact]
    public void A_window_on_a_second_display_that_is_still_plugged_in_is_reachable() {
        Assert.True(PlatformWindowHost.IsReachable(Two, 2400f, 300f, 320f, 240f));
    }

    /// <summary>The case the rule exists for: the second monitor has gone.</summary>
    [Fact]
    public void The_same_window_is_not_reachable_once_that_display_has_gone() {
        Assert.False(PlatformWindowHost.IsReachable(One, 2400f, 300f, 320f, 240f));
    }

    /// <summary>
    ///     ⚠ Partly off an edge is <i>reachable</i>, and deliberately. The user dragged it there and
    ///     a rule that pulled it back would be an editor rearranging a window somebody arranged.
    /// </summary>
    [Fact]
    public void A_window_hanging_off_the_right_edge_is_still_reachable() {
        Assert.True(PlatformWindowHost.IsReachable(One, 1880f, 400f, 320f, 240f));
    }

    [Fact]
    public void A_window_whose_title_bar_is_above_the_top_is_not() {
        // Its body would be on screen and its title bar would not, which is the shape that cannot be
        // dragged back on every platform that puts the drag handle at the top.
        Assert.False(PlatformWindowHost.IsReachable(One, 400f, -300f, 320f, 240f));
    }

    /// <summary>
    ///     ⚠ A machine that reports no displays is a headless one, where every position is equally
    ///     invisible — so "there is nowhere to put a window" is not a reason to move one.
    /// </summary>
    [Fact]
    public void A_platform_with_no_displays_at_all_places_a_window_wherever_it_was_asked() {
        Assert.True(PlatformWindowHost.IsReachable([], -9000f, -9000f, 320f, 240f));
    }
}
