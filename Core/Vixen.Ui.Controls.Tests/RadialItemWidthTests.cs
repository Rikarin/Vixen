// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>
///     A radial item near the menu's right edge is as wide as the same item at the top (#1447).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="RadialMenu" /> places each item by <c>left</c> alone, computed from the item's own
///         measured width. Since #1447 an absolute box with <c>width: auto</c> shrinks to its containing
///         block less its inset, as CSS 2 §10.3.7 says — and the east wedge of a 260 px menu with a
///         92 px radius has less room to its right than a long label needs. Without
///         <c>white-space: nowrap</c> on <c>radial-item</c> it wrapped, and because <c>Place</c> reads
///         the width it just changed, the wedge drifted on every pass.
///     </para>
///     <para>
///         The oracle is the north item: the same label, centred, with room on both sides. Every
///         item must be its width and its height, and stay so across passes.
///     </para>
/// </remarks>
public class RadialItemWidthTests {
    const string Label = "Add a component here";

    [Fact]
    public void The_east_wedge_is_as_wide_and_as_tall_as_the_north_one() {
        using var fixture = new ControlFixture();
        var menu = fixture.Add<RadialMenu>();

        for (var index = 0; index < 4; index++) {
            menu.AddItem(Label);
        }

        menu.OpenAt(400f, 300f);

        // Several passes: Place reads each item's width to centre it, so a width that depended on
        // where it was put would move again on the next one.
        for (var pass = 0; pass < 4; pass++) {
            fixture.Update();
        }

        var north = menu.Items[0].Bounds;
        Assert.True(north.Width > 120f, $"the premise: a label wide enough to need the room, {north.Width}");

        foreach (var item in menu.Items) {
            Assert.Equal(north.Width, item.Bounds.Width, 0.5f);
            Assert.Equal(north.Height, item.Bounds.Height, 0.5f);
        }
    }
}
