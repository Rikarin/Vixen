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

    /// <summary>The states that still reach nobody, named so that the residue is a fact rather than a hope.</summary>
    /// <remarks>
    ///     ⚠ <b>A test that asserts a known gap, and it is deliberately written to fail on the day
    ///     the gap closes.</b> <c>CheckBox.IsIndeterminate</c> changes the announced state from
    ///     <c>checked</c> to <c>mixed</c> with no style-state write beside it, so the framework
    ///     cannot see it and the coalesced flag is never set. Recording it here is what stops the
    ///     first test above being read as "computed states now notify"; when the residue is fixed
    ///     this goes red and is deleted, which is the point of writing it down in code rather than
    ///     in a comment. The residue is #593.
    /// </remarks>
    [Fact]
    public void A_state_computed_from_a_control_field_alone_still_reaches_nobody() {
        using var fixture = new ControlFixture();

        var box = fixture.Add<CheckBox>();
        box.Label = "Shadows";

        var bridge = new Bridge(fixture.Document, box);
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        var quiet = bridge.Republished;

        box.IsIndeterminate = true;
        fixture.Advance(TimeSpan.FromMilliseconds(16));

        // The tree is right the instant the field changes; nothing told the bridge to look.
        Assert.Equal(AccessibleStates.Mixed, box.AccessibleState & AccessibleStates.Mixed);
        Assert.Equal(quiet, bridge.Republished);
        Assert.Equal("checkbox \"Shadows\"", bridge.Cached);
    }
}
