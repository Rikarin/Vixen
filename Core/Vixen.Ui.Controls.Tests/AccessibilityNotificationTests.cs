// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>What a bridge that only re-reads when it is told ends up showing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Asserted through a consumer rather than on the tree, because the tree was never
///         wrong.</b> Doc 46 § A2 recorded that a state a control <i>computes</i> — ticked, selected,
///         open — raised no <c>AccessibilityInvalidated</c>, and a test that read
///         <c>AccessibleState</c> after ticking a box passed then and passes now: the computed view
///         is correct the instant the field changes. What was broken is the <i>notification</i>, and
///         the only thing that can see a missing notification is something that re-reads only when
///         it gets one. <see cref="Bridge" /> is that: a cached rendering, refreshed in the handler
///         and nowhere else, which is what an AT-SPI or UIAutomation bridge is.
///     </para>
///     <para>
///         ⚠ <b>The other half is that hovering must <i>not</i> raise.</b> A fix that invalidated on
///         any style-state change would pass the first test and be worse than the bug — the flag
///         would be set on every frame the pointer moved, and a bridge would diff its whole cached
///         tree for the mouse crossing a button. So the mask is asserted from both sides: a state
///         that is announced raises, and one that is not does not.
///     </para>
/// </remarks>
public class AccessibilityNotificationTests {
    /// <summary>A consumer that caches the tree and re-reads it only when it is told to.</summary>
    sealed class Bridge {
        readonly UiElement root;

        public Bridge(UiDocument document, UiElement root) {
            this.root = root;

            document.AccessibilityInvalidated += _ => {
                Republished++;
                Cached = AccessibilitySnapshot.Render(this.root);
            };
        }

        /// <summary>What it would be showing a screen reader right now.</summary>
        public string Cached { get; private set; } = string.Empty;

        /// <summary>How many times it was told to look again.</summary>
        public int Republished { get; private set; }
    }

    [Fact]
    public void A_bridge_that_waits_to_be_told_sees_a_checkbox_being_ticked() {
        using var fixture = new ControlFixture();

        var box = fixture.Add<CheckBox>();
        box.Label = "Shadows";

        var bridge = new Bridge(fixture.Document, box);

        // One frame to publish the tree the box was built into, so what follows is a state change
        // with no structural change beside it — which is the case the notification missed.
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        var published = bridge.Republished;

        Assert.Equal("checkbox \"Shadows\"", bridge.Cached);

        box.IsChecked = true;
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.True(bridge.Republished > published, "ticking the box told nobody");
        Assert.Equal("checkbox \"Shadows\" [checked]", bridge.Cached);

        // And back, because a notification that only fires one way leaves a screen reader announcing
        // a box as ticked for the rest of the session.
        box.IsChecked = false;
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("checkbox \"Shadows\"", bridge.Cached);
    }

    [Fact]
    public void Disabling_a_control_tells_the_bridge_and_hovering_it_does_not() {
        using var fixture = new ControlFixture();

        var button = fixture.Add<Button>();
        button.Label = "Save";

        var bridge = new Bridge(fixture.Document, button);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        button.Disabled = true;
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("button \"Save\" [disabled]", bridge.Cached);

        var quiet = bridge.Republished;

        // ⚠ Hover is a style state and is not an announced one. This is the assertion that stops the
        // fix being "invalidate whenever `State` is written", which would set the flag on every
        // frame a pointer moves across a toolbar.
        button.State |= ElementState.Hover;
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        button.State &= ~ElementState.Hover;
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal(quiet, bridge.Republished);
    }

    /// <summary>The residue #593 named, now that it is closed: a state read off a control's own field.</summary>
    /// <remarks>
    ///     ⚠ <b>This replaces a test that asserted the opposite.</b>
    ///     <c>A_state_computed_from_a_control_field_alone_still_reaches_nobody</c> stood here and
    ///     recorded the gap as a fact rather than a hope, written to go red the day it closed; this
    ///     is that day. <c>CheckBox.IsIndeterminate</c> swaps the announced <c>checked</c> for
    ///     <c>mixed</c> with no style-state write beside it, so the framework still cannot see it —
    ///     what changed is that the control says so, on the line that writes the field.
    /// </remarks>
    [Fact]
    public void A_half_ticked_box_tells_the_bridge_it_is_mixed_and_tells_it_again_when_it_is_not() {
        using var fixture = new ControlFixture();

        var box = fixture.Add<CheckBox>();
        box.Label = "Shadows";
        box.IsChecked = true;

        var bridge = new Bridge(fixture.Document, box);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("checkbox \"Shadows\" [checked]", bridge.Cached);

        box.IsIndeterminate = true;
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("checkbox \"Shadows\" [mixed]", bridge.Cached);

        // ⚠ And back, which is the half a one-way notification leaves wrong forever: `Mixed`
        // *replaces* `Checked` rather than joining it, so a bridge that was never told the flag
        // cleared announces a fully ticked box as half ticked for the rest of the session.
        box.IsIndeterminate = false;
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("checkbox \"Shadows\" [checked]", bridge.Cached);
    }

    /// <summary>A menu item's <c>expanded</c>, which is read off the submenu and not off the item.</summary>
    /// <remarks>
    ///     ⚠ <b>The element that changed and the element that is announced are not the same one</b>,
    ///     which is why no per-element mechanism would have caught this: <c>MenuItem</c> reads
    ///     <c>Submenu.IsOpen</c>, and the submenu is a sibling overlay that knows nothing about the
    ///     item. The invalidation is a document-wide flag, so <c>Overlay.Restate</c> — the one method
    ///     both <c>Open</c> and <c>Close</c> pass through — is where it belongs.
    /// </remarks>
    [Fact]
    public void Opening_a_submenu_tells_the_bridge_the_item_is_expanded() {
        using var fixture = new ControlFixture();

        var menu = fixture.Add<Menu>();
        var item = menu.AddItem("Open Recent");
        var submenu = fixture.Document.Root.Add<Menu>();

        item.Submenu = submenu;
        menu.Open();

        var bridge = new Bridge(fixture.Document, item);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("menuitem \"Open Recent\" [expandable]", bridge.Cached);

        submenu.Open(item);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("menuitem \"Open Recent\" [expandable expanded]", bridge.Cached);

        submenu.Close();
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("menuitem \"Open Recent\" [expandable]", bridge.Cached);
    }

    /// <summary>A slider's value, which changes on every arrow press and writes no style state at all.</summary>
    /// <remarks>
    ///     ⚠ <b>Driven by the keyboard rather than by the setter</b>, because a value a screen-reader
    ///     user changes is a value they changed with the keyboard — and the press is what proves the
    ///     notification survives the whole path rather than only the property.
    /// </remarks>
    [Fact]
    public void Arrowing_a_slider_tells_the_bridge_its_value_moved() {
        using var fixture = new ControlFixture();

        var slider = fixture.Add<Slider>();
        slider.Step = 0.25f;
        slider.AccessibleName = "Volume";

        fixture.Document.Focus(slider);

        var bridge = new Bridge(fixture.Document, slider);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("slider \"Volume\" = \"0\"", bridge.Cached);

        fixture.Type(InputKey.Right);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal("slider \"Volume\" = \"0.25\"", bridge.Cached);
    }
}
