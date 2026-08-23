// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>One page of a settings window: a line in the rail, and what fills the pane.</summary>
/// <param name="Id">What a saved selection and a search result call it.</param>
/// <param name="Title">What the rail says.</param>
/// <param name="Build">Fills the pane. Called the first time the page is shown, and again after a revert.</param>
/// <remarks>
///     ⚠ <b>A factory rather than an element, for <see cref="PanelDescriptor" />'s reason.</b> A
///     settings window with nine pages builds one; building the other eight would stand up nine
///     inspectors over nine settings objects the user has not asked to look at. It is also what makes
///     Revert a rebuild rather than nine drawers each having to know how to un-edit themselves.
///     <para>
///         ⚠ <b>And it is what the panel ledger's exclusion on this window was about, wrongly.</b>
///         Markup cannot <i>be</i> an <c>Action&lt;UiElement&gt;</c> — but it never had to be. The
///         factory needs a host to be invoked <i>into</i>, and <c>&lt;settings-pane ref="@Pane" /&gt;</c>
///         is one.
///     </para>
/// </remarks>
public sealed record SettingsCategory(string Id, StringId Title, Action<UiElement> Build) {
    /// <summary>Puts this page's settings back to their defaults, or <see langword="null" /> if it cannot.</summary>
    /// <remarks>
    ///     Doc 20's A4 asks for "a Reset per category" rather than one for the window: a settings
    ///     window whose only reset throws away the other eight pages as well is one nobody presses.
    /// </remarks>
    public Action? Reset { get; init; }

    /// <summary>The words a search over every setting should match this page on.</summary>
    /// <remarks>
    ///     ⚠ <b>The page supplies them, because only it knows what is on it.</b> Doc 20 asks for "a
    ///     search box over every setting in every category", and a window that could only search the
    ///     nine category names would answer the easy half of that question. A page drawn from an
    ///     <c>[Inspector]</c> descriptor hands back its member names, which is the list somebody is
    ///     actually typing a fragment of.
    /// </remarks>
    public Func<IEnumerable<string>>? Keywords { get; init; }
}

/// <summary>The window behind both Preferences and Project Settings: a rail, a pane, and an Apply.</summary>
/// <remarks>
///     <para>
///         The panel is <c>SettingsView.vxml</c>; this file is the page record, the accessibility
///         modifier, and the one button subclass the rail needed.
///     </para>
///     <para>
///         <b>Doc 20's A4 is "two windows, one mechanism", and this is the mechanism.</b> The only
///         difference between Preferences and Project Settings is whose store the pages are over —
///         the user's or the project's — so a second implementation of the rail, the search, the
///         reset and the dirty tracking would be a second place for those four to be subtly wrong.
///     </para>
///     <para>
///         ⚠ <b>A panel rather than a modal, and doc 20's A2 is why.</b> Everything modal in this
///         editor is a drawn <see cref="Dialog" /> in the shell's own document, so that the golden
///         suite can photograph it and the automation harness can drive it — and a settings window is
///         the one thing people leave open beside what they are changing. Registered as a panel, it
///         docks, it tabs, and <c>view.float-panel</c> gives it a real operating-system window when
///         the user wants one, which is the same answer doc 20's "two windows" asks for by a
///         different route.
///     </para>
///     <para>
///         ⚠ <b>Nothing is written on a keystroke.</b> Doc 20 is explicit: the layout file's rule —
///         written on the way down — applies here for the same reason, with an explicit Apply for
///         anything that costs something to change. So an edit marks the window dirty,
///         <c>Apply</c> is what commits, and <c>Revert</c> rebuilds the page from what is still on
///         disk.
///     </para>
/// </remarks>
public sealed partial class SettingsView;

/// <summary>A rail tab, which is a button whose selection a binding is allowed to write.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Shape 5's escape, applied to a flag rather than to <c>Text</c>, and that
///         generalisation is the useful part.</b> <see cref="UiElement.State" /> is a flag set holding
///         Hover, Focused, Pressed and Checked, so a binding that assigned it whole would undo
///         whatever the pointer had just put there — which is why <c>KeyBindingsView</c> and
///         <c>FlameChartView</c> both keep <c>State |= Checked</c> imperative. A property that owns
///         <i>one bit</i> of it has no such problem, and is an ordinary target for an ordinary
///         binding.
///     </para>
///     <para>
///         ⚠ <b>Why the rail could not keep it imperative.</b> The loop's per-row handles come from
///         <c>refs</c>, which is filled by an effect and is therefore empty until the next flush;
///         <c>Select</c> is called synchronously, including once from <c>Add</c> before any frame has
///         run. A restate loop over <c>refs</c> would have silently done nothing the first time and
///         worked for ever after, which is the worst available failure.
///     </para>
///     <para>
///         ⚠ <b><see cref="ButtonBase" /> and not <see cref="Button" />, only because
///         <see cref="Button" /> is sealed.</b> The two are the same type — <see cref="Button" /> adds
///         a tag name and nothing else — so this answers to <c>button</c>, carries the same
///         <c>size-md variant-subtle settings-tab</c>, and
///         <c>settings-rail &gt; button.settings-tab:checked</c> reaches it unchanged. A whole-tree
///         dump of the rail before and after the port is identical, which is the test of that claim.
///     </para>
/// </remarks>
internal sealed class SettingsTab : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "button";

    /// <summary>Whether this is the page being shown.</summary>
    public bool Selected {
        get => (State & ElementState.Checked) != 0;

        set {
            if (value) {
                State |= ElementState.Checked;
            } else {
                State &= ~ElementState.Checked;
            }
        }
    }
}
