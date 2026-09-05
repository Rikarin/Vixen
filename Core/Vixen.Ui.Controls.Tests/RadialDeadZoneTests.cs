// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>That <see cref="RadialMenu.DeadZone" /> is the number the aim is measured against.</summary>
/// <remarks>
///     ⚠ <b>The dead zone had a test and the property did not.</b> The editor's
///     <c>SceneMenuTests.Aiming_is_by_direction_and_the_middle_is_a_dead_zone</c> asserts against
///     <c>menu.DeadZone * 0.5f</c> — it <i>reads</i> the property and never assigns it, so a
///     <c>WedgeAt</c> that had the 26-pixel default written into it as a literal would satisfy that
///     test exactly. Nothing in the repository ever set the property, which is the same fact stated
///     the other way round.
/// </remarks>
public class RadialDeadZoneTests {
    /// <summary>Four wedges, so that north, east, south and west are one each.</summary>
    const int Wedges = 4;

    /// <summary>Outside the 26-pixel default and inside a widened one, which is the whole test.</summary>
    static readonly Vector2 East = new(40f, 0f);

    [Fact]
    public void A_flick_past_the_default_dead_zone_aims_at_the_wedge_it_points_at() {
        using var fixture = new ControlFixture();
        var menu = Menu(fixture);

        Assert.Equal(26f, menu.DeadZone);
        Assert.Equal(1, menu.WedgeAt(East));
    }

    [Fact]
    public void Widening_the_dead_zone_swallows_a_flick_the_default_would_have_taken() {
        using var fixture = new ControlFixture();
        var menu = Menu(fixture);

        menu.DeadZone = 80f;

        Assert.Equal(-1, menu.WedgeAt(East));

        // And only swallows it: past the widened zone the same direction still names the same wedge,
        // so this is a threshold moving rather than the aim breaking.
        Assert.Equal(1, menu.WedgeAt(East * 4f));
    }

    /// <summary>
    ///     ⚠ Zero is a legitimate setting rather than "use the default" — a menu on a touch screen
    ///     that opens under the finger has no travel to spend — so it must not be treated as a
    ///     sentinel.
    /// </summary>
    [Fact]
    public void A_dead_zone_of_zero_takes_a_flick_of_one_pixel() {
        using var fixture = new ControlFixture();
        var menu = Menu(fixture);

        menu.DeadZone = 0f;

        Assert.Equal(0, menu.WedgeAt(new Vector2(0f, -1f)));
        Assert.Equal(1, menu.WedgeAt(new Vector2(1f, 0f)));
    }

    static RadialMenu Menu(ControlFixture fixture) {
        var menu = fixture.Add<RadialMenu>();

        for (var index = 0; index < Wedges; index++) {
            menu.AddItem($"Item {index}");
        }

        fixture.Update();

        return menu;
    }
}
