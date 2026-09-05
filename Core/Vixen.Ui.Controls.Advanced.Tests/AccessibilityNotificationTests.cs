// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The two advanced controls whose announced view is read off a field — issue #593.</summary>
/// <remarks>
///     <para>
///         <c>Vixen.Ui.Controls.Tests.AccessibilityNotificationTests</c>'s counterpart, with the same
///         consumer and for the same reason: the computed view was never wrong, so nothing that reads
///         the tree can see the defect. Only something that re-reads <i>when it is told</i> can, which
///         is what an AT-SPI or UIAutomation bridge is.
///     </para>
///     <para>
///         ⚠ <b>A tree row was half broken, and the sabotage is what showed which half.</b> With the
///         invalidation removed, <i>expanding</i> still reached the bridge — the realise inserts a
///         row for the new child, and a structural edit already invalidates — while
///         <i>collapsing</i> did not, because a virtualised row is <b>parked and rebound rather than
///         detached</b>, so there is no structural edit at all. Half a notification is the worse
///         half: a screen reader is told about every node the user opened and about none of the ones
///         they closed. Both legs are asserted here for that reason, and a test that only expanded
///         would have been green against the bug.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class AccessibilityNotificationTests {
    /// <summary>A consumer that caches the tree and re-reads it only when it is told to.</summary>
    sealed class Bridge {
        readonly UiElement root;

        public Bridge(UiDocument document, UiElement root) {
            this.root = root;

            document.AccessibilityInvalidated += _ => Cached = AccessibilitySnapshot.Render(this.root);
        }

        /// <summary>What it would be showing a screen reader right now.</summary>
        public string Cached { get; private set; } = string.Empty;
    }

    [Fact]
    public void Expanding_a_node_tells_the_bridge_the_row_is_expanded() {
        using var fixture = new AdvancedFixture();

        var tree = fixture.Add<TreeView>();
        var node = tree.Root.Add("Materials");
        node.Add("Crate");

        tree.Refresh();
        fixture.Update();

        var row = tree.Rows[0];
        var bridge = new Bridge(fixture.Document, row);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("treeitem \"Materials\" [expandable]", bridge.Cached);

        tree.Expand(node);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("treeitem \"Materials\" [expandable expanded]", bridge.Cached);

        tree.Expand(node, false);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("treeitem \"Materials\" [expandable]", bridge.Cached);
    }

    /// <summary>The hue band, which #420 gave a keyboard and a <c>slider</c> role and no notification.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the picker rather than the strip</b>, on <c>ColorPickerKeyboardTests</c>'
    ///     terms: a band does not own its own fraction — it raises <c>Moved</c> and the picker writes
    ///     it back in <c>Sync</c>, which is therefore also the only place that knows the announced
    ///     value has moved.
    /// </remarks>
    [Fact]
    public void Arrowing_the_hue_band_tells_the_bridge_its_value_moved() {
        using var fixture = new AdvancedFixture();

        var picker = fixture.Add<ColorPicker>();
        fixture.Update();

        Assert.True(fixture.Document.Focus(picker.HueStrip));

        var bridge = new Bridge(fixture.Document, picker.HueStrip);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        var before = bridge.Cached;

        fixture.Type(InputKey.Right);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.NotEqual(before, bridge.Cached);
        Assert.Contains("= \"0.01\"", bridge.Cached, StringComparison.Ordinal);
    }
}
