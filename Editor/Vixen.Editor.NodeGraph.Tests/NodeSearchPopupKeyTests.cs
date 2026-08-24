// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Testing;
using Xunit;

namespace Tests;

/// <summary>The search-to-create popup, driven by real keys after its handler moved into markup.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This panel had no test of any kind before now.</b> <c>NodeSearch.Rank</c> is covered
///         in <c>LayoutAndSearchTests</c>; the popup over it was not, so its capture-leg key handler
///         could have been deleted outright with every suite still green. The port moved that
///         handler from <c>OnComposed</c> into <c>&lt;self on:keydown.capture&gt;</c>, which is a
///         change to <i>how it is registered</i> and to nothing else — the only test that can speak
///         to it is one that presses a key.
///     </para>
///     <para>
///         ⚠ <b>And the arrangement is the one that made the port risky.</b> <c>Show</c> ends with
///         <c>Document.Focus(Field)</c>, so the key arrives at the search box and the popup only
///         sees it on the way down. A handler on the first markup root — the <c>&lt;SearchBox&gt;</c>
///         itself — would be a different element with different route coverage, which is the
///         behaviour change five pickers stayed hand-written to avoid. Down and Enter arriving here
///         is the evidence that <c>&lt;self /&gt;</c> named the host and not a root beside it.
///     </para>
///     <para>
///         ⚠ <b>No <c>.handled</c>, and the last test is why that is deliberate.</b> This popup
///         <i>acts</i> on the key — Enter creates a node — so running on an event another handler
///         had already claimed would create a node on a keystroke that was not meant for it. Only
///         the two chord-recording panels take the modifier.
///     </para>
/// </remarks>
public sealed class NodeSearchPopupKeyTests : IDisposable {
    readonly ViewFixture fixture = new();
    readonly NodeTypeRegistry registry = new();

    public NodeSearchPopupKeyTests() =>
        Vixen.Editor.NodeGraph.Tests.NodeTypes.Register(registry);

    public void Dispose() => fixture.Dispose();

    /// <summary>Down and Up move the highlight, ahead of the box that has the focus.</summary>
    [Fact]
    public void The_arrows_move_the_highlight_before_the_box_sees_them() {
        var (popup, ui) = Open();

        Assert.True(popup.Results.Count > 1, "the fixture library should offer more than one node");
        Assert.Equal(0, popup.Highlighted);

        ui.PressKey(InputKey.Down);
        ui.Frame();

        Assert.Equal(1, popup.Highlighted);

        ui.PressKey(InputKey.Up);
        ui.Frame();

        Assert.Equal(0, popup.Highlighted);
    }

    /// <summary>Enter creates the highlighted node and closes the popup.</summary>
    [Fact]
    public void Enter_creates_the_highlighted_node() {
        var (popup, ui) = Open();

        NodeSearchResult? accepted = null;

        popup.Accepted += (_, result) => accepted = result;

        var wanted = popup.Results[0];

        ui.PressKey(InputKey.Enter);
        ui.Frame();

        Assert.NotNull(accepted);
        Assert.Equal(wanted.Type, accepted!.Value.Type);
        Assert.False(popup.IsOpen);
    }

    /// <summary>
    ///     ⚠ <b>A key something else has claimed moves nothing, which is the assertion that this
    ///     popup was <i>not</i> given <c>.handled</c>.</b> Three of the five capture-leg panels must
    ///     not have the modifier and two must; a port that spread it across all five would pass every
    ///     other test in this file. Enter is used rather than Down because the harm is concrete here:
    ///     with <c>handled</c> the popup would create a node on a keystroke another handler had
    ///     already dealt with.
    /// </summary>
    [Fact]
    public void A_key_something_else_has_claimed_creates_nothing() {
        var (popup, ui) = Open();

        var accepted = 0;
        var claimed = 0;

        popup.Accepted += (_, _) => accepted++;

        ui.Document.Root.AddHandler<KeyEvent>(
            (_, args) => {
                claimed++;
                args.Handled = true;
            },
            RoutingStrategy.Capture
        );

        ui.PressKey(InputKey.Enter);
        ui.Frame();

        Assert.True(claimed > 0, "the root handler should have seen the key first");
        Assert.Equal(0, accepted);
        Assert.True(popup.IsOpen);
    }

    /// <summary>
    ///     ⚠ <b>The assertion that <c>&lt;self /&gt;</c> is on the host and not on the first root,
    ///     and the only one in this file that can tell the two apart.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The three tests above do <i>not</i> distinguish them, and it is worth saying why
    ///     rather than deleting them.</b> <c>Show</c> focuses the <c>SearchBox</c>, so the route runs
    ///     root → popup → box and a capture handler written on the box is on that route as its
    ///     target — a handler in the wrong place still hears every key those tests press, and
    ///     moving the attribute onto <c>&lt;SearchBox&gt;</c> leaves all three green. That was
    ///     checked rather than assumed.
    ///
    ///     What separates them is a key that arrives somewhere <i>else</i> in the panel, which is
    ///     the README's own example: "a key arriving while the focus is on the result list would
    ///     never reach it". The list is not an ancestor of the box, so a handler on the box is not
    ///     on this route at all, while the host is. Raising at the list is how that is said without
    ///     needing a focusable row.
    /// </remarks>
    [Fact]
    public void A_key_arriving_over_the_list_reaches_the_host_and_not_a_root_beside_it() {
        var (popup, ui) = Open();

        Assert.True(popup.Results.Count > 1, "the fixture library should offer more than one node");
        Assert.Equal(0, popup.Highlighted);

        var args = new KeyEvent { Key = InputKey.Down, Action = KeyAction.Pressed };

        popup.List.Raise(args);
        ui.Frame();

        Assert.Equal(1, popup.Highlighted);

        // And it was taken, which is the other half of being on the capture leg.
        Assert.True(args.Handled);
    }

    /// <summary>
    ///     <c>&lt;self /&gt;</c> creates no element, so the popup's roots are the three the C# built.
    /// </summary>
    [Fact]
    public void The_self_tag_adds_no_element_to_the_popup() {
        var (popup, _) = Open();

        Assert.Equal(
            ["search-box", "node-search-list", "node-search-empty"],
            popup.Children.Select(child => child.Tag)
        );
    }

    /// <summary>An open popup over the fixture's library, and a harness to press keys into.</summary>
    (NodeSearchPopup Popup, UiTest Ui) Open() {
        var popup = fixture.Ui.Root.Add<NodeSearchPopup>();
        var ui = UiTest.Adopt(fixture.Ui);

        // `Show` focuses the field, which is the arrangement under test rather than a convenience.
        popup.Show(registry, null, 100f, 100f);
        ui.Frames(2);

        Assert.True(popup.IsOpen);
        Assert.Same(popup.Field, fixture.Ui.Focused);

        return (popup, ui);
    }
}
