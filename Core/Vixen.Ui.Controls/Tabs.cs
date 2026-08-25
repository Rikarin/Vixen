// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>One tab's header.</summary>
/// <remarks>
///     <para>
///         A tab without the panel it shows is half a thing, and the pairing has to be made somewhere
///         that can put the two in different parts of the tree. That used to mean
///         <see cref="Tabs.AddTab" /> and only <see cref="Tabs.AddTab" />; it now means
///         <see cref="OnCreated" />, so that a tab written in markup is the same tab.
///     </para>
///     <para>
///         ⚠ <b>The pairing moved to a lifecycle hook rather than growing a second maker.</b>
///         <c>AddTab</c> could not be what markup calls — a <c>.vxml</c> writes tags, and the tag is
///         what has to work. Leaving the pairing in <c>AddTab</c> and adding an equivalent for the
///         declarative path would be two ways to half-build a tab, and the day they disagreed the
///         symptom would be a panel with nothing in it. So <c>AddTab</c> now does what markup does:
///         it adds a <c>TabItem</c> to the strip and lets the tab join.
///     </para>
/// </remarks>
public sealed partial class TabItem : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "tab";

    /// <summary>The panel this tab shows.</summary>
    public UiElement Panel { get; internal set; } = null!;

    /// <summary>Whether this is the tab currently showing.</summary>
    public bool IsSelected => (State & ElementState.Checked) != 0;

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.Tab;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><see cref="AccessibleStates.Selected" />, and this is where the base class's
    ///     <see cref="ElementState.Checked" /> is given its second meaning.</b> A tab and a checkbox
    ///     set the same style flag — that is what <c>:checked</c> draws both with — and they mean
    ///     entirely different things to somebody who is listening rather than looking. Overriding
    ///     rather than adding, because a tab is not also ticked.
    /// </remarks>
    protected override AccessibleStates NativeAccessibleState =>
        IsSelected ? AccessibleStates.Selected : AccessibleStates.None;

    /// <summary>A tab's content is its panel's content.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes <c>&lt;TabItem&gt;…&lt;/TabItem&gt;</c> put its children where they show
    ///     rather than inside the header.</b> The two are in different parts of the tree, so without
    ///     this a tab's markup content would land in the strip, beside the label, and the panel it
    ///     was written for would be empty. The fallback matters for one instant only: an element's
    ///     <c>ContentHost</c> can be read before <see cref="OnCreated" /> has run, and answering
    ///     <see langword="null" /> there would be a crash where answering the header is merely the
    ///     old behaviour.
    /// </remarks>
    protected override UiElement ContentHost => Panel ?? this;

    /// <inheritdoc />
    /// <remarks>
    ///     Selection is the <see cref="Tabs" />'s decision, so this does nothing but let the click
    ///     bubble to it. A tab that selected itself would have to reach across to its siblings to
    ///     deselect them, which is the arrangement that leaves two tabs selected the first time one
    ///     is removed.
    /// </remarks>
    protected override void Activate(ActivationDevice device, int count, ModifierKeys modifiers) =>
        base.Activate(device, count, modifiers);

    /// <inheritdoc />
    /// <remarks>
    ///     A tab in a strip belongs to the <see cref="Tabs" /> that strip is part of, and it is the
    ///     only thing that can give it a panel. A <c>TabItem</c> added anywhere else is an ordinary
    ///     button with no panel, which is what it looks like and is not worth a diagnostic.
    /// </remarks>
    protected override void OnCreated() {
        base.OnCreated();
        Owner?.Adopt(this);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Here rather than only in <see cref="Tabs.RemoveTab" />, because markup removes
    ///     elements without asking.</b> An <c>@if</c> whose arm leaves takes its <c>TabItem</c> with
    ///     it, and a <c>Tabs</c> that went on holding it would keep a dead tab in
    ///     <see cref="Tabs.Items" /> and an orphaned panel in the tree — with <c>SelectedIndex</c>
    ///     possibly pointing at it.
    /// </remarks>
    protected override void OnRemoved() {
        Owner?.Orphan(this);
        base.OnRemoved();
    }

    /// <summary>The <see cref="Tabs" /> whose strip this tab is in, if it is in one.</summary>
    /// <remarks>
    ///     ⚠ <b>Exactly two levels and not an ancestor walk.</b> A tab's panel can hold another
    ///     <c>Tabs</c>, so "the nearest <c>Tabs</c> above me" is a question with the wrong answer for
    ///     any nested tab; <see cref="Tabs.Adopt" /> checks the strip is <i>its</i> strip for the same
    ///     reason.
    /// </remarks>
    Tabs? Owner => Parent?.Parent as Tabs;
}

/// <summary>A strip of tabs and the panels behind them.</summary>
/// <remarks>
///     <para>
///         <b>Two parts, and the tabs are not the parents of their panels.</b> A tab is in the strip
///         and its panel is below it, so the two cannot be one element — which is why
///         <see cref="AddTab" /> exists and why <see cref="TabItem.Panel" /> is a reference rather
///         than a child.
///     </para>
///     <para>
///         ⚠ <b>The unselected panels stay in the tree with <c>display: none</c>.</b> They keep
///         their scroll position, their focus history and their state, and re-selecting a tab costs
///         a restyle rather than a rebuild — which is what makes a tabbed inspector usable. The cost
///         is that ten tabs is ten panels' worth of elements whether or not anybody looks at them,
///         and a panel that is genuinely expensive should be built on first selection by the
///         application. Said plainly rather than solved with a lazy mode nobody would find.
///     </para>
///     <para>
///         <b>Arrows select rather than only moving the focus</b>, for the reason a radio group's
///         do: the strip is one tab stop, so a keyboard that could move without choosing could not
///         choose at all.
///     </para>
/// </remarks>
public sealed partial class Tabs : Control {
    readonly List<TabItem> tabs = [];

    /// <inheritdoc />
    protected override string TagName => "tabs";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip the headers are in.</summary>
    public UiElement Strip { get; private set; } = null!;

    /// <summary>The area the panels are in.</summary>
    public UiElement Panels { get; private set; } = null!;

    /// <summary>The tabs, in order.</summary>
    public IReadOnlyList<TabItem> Items => tabs;

    /// <summary>Which tab is showing, or -1 if none is.</summary>
    [UiProperty(Default = -1, Changed = nameof(OnSelectedChanged))]
    public partial int SelectedIndex { get; set; }

    /// <summary>The tab that is showing, if one is.</summary>
    public TabItem? Selected =>
        SelectedIndex >= 0 && SelectedIndex < tabs.Count ? tabs[SelectedIndex] : null;

    /// <summary>Raised when a different tab is shown.</summary>
    public event Action<Tabs, int>? SelectionChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Strip = Part("tab-strip");
        Panels = Part("tab-panels");

        // ⚠ The strip is the `tablist`, and this control is not in the accessibility tree at all —
        // its `NativeRole` is `None` and it is left that way. A `Tabs` is a layout: a strip above an
        // area, and the two are siblings rather than one thing. Giving the outer element a role
        // would put a node between the tab list and the panels that stands for neither, and a
        // screen reader would announce a group nobody can explain. `Panels` is likewise nothing —
        // the panels inside it are the `tabpanel`s, and the box holding them is a box.
        Strip.Role = AccessibleRole.TabList;

        AddHandler<ClickEvent>(static (element, args) => ((Tabs) element).Chosen(args));
        AddHandler<KeyEvent>(static (element, args) => ((Tabs) element).Keyed(args));
    }

    /// <summary>Markup content is tabs, so it goes in the strip.</summary>
    /// <remarks>
    ///     ⚠ <b>The strip and not <see cref="Panels" />, which is the half of the answer that is not
    ///     obvious.</b> The children a caller writes are <c>&lt;TabItem&gt;</c>s — headers — and each
    ///     one brings its panel with it via <see cref="TabItem.ContentHost" />. Pointing this at the
    ///     panels instead would put every header where the content shows and leave the strip empty.
    ///     One <c>ContentHost</c> is one slot, and this is the slot the author writes into.
    /// </remarks>
    protected override UiElement ContentHost => Strip;

    /// <summary>Adds a tab and the panel behind it.</summary>
    /// <param name="label">What the tab says.</param>
    /// <returns>The tab, whose <see cref="TabItem.Panel" /> is where the content goes.</returns>
    /// <remarks>
    ///     The first tab added selects itself. A tab strip showing nothing is a state a caller can
    ///     ask for — <see cref="SelectedIndex" /> takes -1 — and never one they meant by adding a
    ///     tab.
    ///     <para>
    ///         ⚠ <b>Three lines, because the pairing is <see cref="TabItem.OnCreated" />'s now.</b>
    ///         Adding the element <i>is</i> adding the tab: <c>Strip.Add&lt;TabItem&gt;()</c> runs the
    ///         hook, which takes a panel and joins the list, so this method and
    ///         <c>&lt;TabItem /&gt;</c> in a <c>.vxml</c> reach the same state by the same code.
    ///     </para>
    /// </remarks>
    public TabItem AddTab(string? label = null) {
        var tab = Strip.Add<TabItem>();
        tab.Label = label;

        return tab;
    }

    /// <summary>Gives a tab that has just appeared in the strip its panel and its place.</summary>
    /// <param name="tab">The tab.</param>
    internal void Adopt(TabItem tab) {
        // Its parent is `Strip` — `TabItem.Owner` established that much — but not necessarily *this*
        // control's, once tabs are nested inside tabs.
        if (!ReferenceEquals(tab.Parent, Strip) || tabs.Contains(tab)) {
            return;
        }

        tab.Panel ??= Panels.Add("tab-panel");
        tabs.Add(tab);

        // ⚠ **The pairing said twice, because the second reader cannot see the first.** The class
        // remark above is explicit that a tab is not the parent of its panel — the two are in
        // different parts of the tree, which is why `TabItem.Panel` is a reference — so no walk over
        // `Parent` recovers "this header shows that area". That is exactly what a relation is for,
        // and it is set here rather than in `AddTab` for `Adopt`'s own reason: a `<TabItem />`
        // written in markup never goes through `AddTab`, and a relation only the code path
        // established would be a tab strip that reads correctly in one of the two ways it can be
        // built.
        tab.AddAccessibleRelation(AccessibleRelation.Controls, tab.Panel);

        // And the other way round, which is not the same statement. `aria-controls` says what
        // pressing the tab does; the panel needs a *name*, and the only words it has are on the
        // header. Without this the content area is announced as an unnamed region.
        tab.Panel.Role = AccessibleRole.TabPanel;
        tab.Panel.AddAccessibleRelation(AccessibleRelation.LabelledBy, tab);

        if (SelectedIndex < 0) {
            SelectedIndex = tabs.Count - 1;
        } else {
            Restate();
        }
    }

    /// <summary>Takes a tab that is leaving out of the list, and its panel out of the tree.</summary>
    /// <param name="tab">The tab.</param>
    /// <remarks>
    ///     ⚠ <b>The selection moves before the removal, not after.</b> Removing the selected tab
    ///     first would leave <see cref="SelectedIndex" /> pointing at whatever slid into its place —
    ///     or past the end — and the restyle that follows would run over a list that no longer
    ///     matches it.
    /// </remarks>
    internal void Orphan(TabItem tab) {
        var index = tabs.IndexOf(tab);
        if (index < 0) {
            return;
        }

        tabs.RemoveAt(index);

        // Whichever tab took its place, or the one before it if it was the last. Clamping rather
        // than clearing, because closing a document should leave the neighbouring one open rather
        // than leaving the editor showing nothing.
        var selected = SelectedIndex;
        if (selected > index || selected >= tabs.Count) {
            selected--;
        }

        tab.Panel?.Remove();

        SelectedIndex = Math.Clamp(selected, tabs.Count > 0 ? 0 : -1, tabs.Count - 1);
        Restate();
    }

    /// <summary>Takes a tab and its panel out.</summary>
    /// <param name="tab">The tab.</param>
    /// <returns>Whether it was one of this control's.</returns>
    /// <remarks>
    ///     Removing the element is what removes the tab — <see cref="Orphan" /> is
    ///     <see cref="TabItem.OnRemoved" />'s, so this and an <c>@if</c> arm that leaves take the
    ///     same path and cannot drift apart.
    /// </remarks>
    public bool RemoveTab(TabItem tab) {
        ArgumentNullException.ThrowIfNull(tab);

        if (!tabs.Contains(tab)) {
            return false;
        }

        tab.Remove();

        return true;
    }

    void Chosen(ClickEvent args) {
        if (args.Source is not TabItem tab) {
            return;
        }

        var index = tabs.IndexOf(tab);
        if (index >= 0) {
            SelectedIndex = index;
        }
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || !args.Has(ModifierKeys.None) || tabs.Count == 0) {
            return;
        }

        var target = args.Key switch {
            InputKey.Left => WrapFrom(-1),
            InputKey.Right => WrapFrom(1),
            InputKey.Home => 0,
            InputKey.End => tabs.Count - 1,
            _ => -1
        };

        if (target < 0) {
            return;
        }

        SelectedIndex = target;
        Document.Focus(tabs[target]);

        args.Handled = true;
    }

    int WrapFrom(int step) {
        var current = SelectedIndex < 0 ? (step > 0 ? -1 : 0) : SelectedIndex;
        return (current + step + tabs.Count) % tabs.Count;
    }

    void OnSelectedChanged(int previous, int current) {
        Restate();

        Raise(new ValueChangedEvent<int> { Previous = previous, Value = current });
        SelectionChanged?.Invoke(this, current);
    }

    /// <summary>Puts <c>:checked</c> on the selected tab, <c>.selected</c> on its panel, and the tab stop on it.</summary>
    /// <remarks>
    ///     ⚠ <b>A class on the panel rather than a state.</b> <see cref="ElementState" /> is the set
    ///     of things a selector can ask about and there is no <c>:selected</c> in it — panels are not
    ///     interactive, so the pseudo-classes do not describe them. A class is what the cascade has
    ///     for "this element is in a mode", and it is what the theme's <c>display</c> rule matches.
    /// </remarks>
    void Restate() {
        for (var i = 0; i < tabs.Count; i++) {
            var selected = i == SelectedIndex;
            var tab = tabs[i];

            if (selected) {
                tab.State |= ElementState.Checked;
                tab.Panel.AddClass("selected");
            } else {
                tab.State &= ~ElementState.Checked;
                tab.Panel.RemoveClass("selected");
            }

            tab.TabIndex = selected || (SelectedIndex < 0 && i == 0) ? 0 : -1;
        }
    }
}
