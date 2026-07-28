// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>One tab's header.</summary>
/// <remarks>
///     Made by <see cref="Tabs.AddTab" /> rather than constructed, because a tab without the panel
///     it shows is half a thing — and the pairing has to be made somewhere that can put the two in
///     different parts of the tree.
/// </remarks>
public sealed partial class TabItem : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "tab";

    /// <summary>The panel this tab shows.</summary>
    public UiElement Panel { get; internal set; } = null!;

    /// <summary>Whether this is the tab currently showing.</summary>
    public bool IsSelected => (State & ElementState.Checked) != 0;

    /// <inheritdoc />
    /// <remarks>
    ///     Selection is the <see cref="Tabs" />'s decision, so this does nothing but let the click
    ///     bubble to it. A tab that selected itself would have to reach across to its siblings to
    ///     deselect them, which is the arrangement that leaves two tabs selected the first time one
    ///     is removed.
    /// </remarks>
    protected override void Activate(ActivationDevice device, int count, ModifierKeys modifiers) =>
        base.Activate(device, count, modifiers);
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

        AddHandler<ClickEvent>(static (element, args) => ((Tabs) element).Chosen(args));
        AddHandler<KeyEvent>(static (element, args) => ((Tabs) element).Keyed(args));
    }

    /// <summary>Adds a tab and the panel behind it.</summary>
    /// <param name="label">What the tab says.</param>
    /// <returns>The tab, whose <see cref="TabItem.Panel" /> is where the content goes.</returns>
    /// <remarks>
    ///     The first tab added selects itself. A tab strip showing nothing is a state a caller can
    ///     ask for — <see cref="SelectedIndex" /> takes -1 — and never one they meant by adding a
    ///     tab.
    /// </remarks>
    public TabItem AddTab(string? label = null) {
        var tab = Strip.Add<TabItem>();
        tab.Label = label;
        tab.Panel = Panels.Add("tab-panel");

        tabs.Add(tab);

        if (SelectedIndex < 0) {
            SelectedIndex = tabs.Count - 1;
        } else {
            Restate();
        }

        return tab;
    }

    /// <summary>Takes a tab and its panel out.</summary>
    /// <param name="tab">The tab.</param>
    /// <returns>Whether it was one of this control's.</returns>
    /// <remarks>
    ///     ⚠ <b>The selection moves before the removal, not after.</b> Removing the selected tab
    ///     first would leave <see cref="SelectedIndex" /> pointing at whatever slid into its place —
    ///     or past the end — and the restyle that follows would run over a list that no longer
    ///     matches it.
    /// </remarks>
    public bool RemoveTab(TabItem tab) {
        ArgumentNullException.ThrowIfNull(tab);

        var index = tabs.IndexOf(tab);
        if (index < 0) {
            return false;
        }

        tabs.RemoveAt(index);

        // Whichever tab took its place, or the one before it if it was the last. Clamping rather
        // than clearing, because closing a document should leave the neighbouring one open rather
        // than leaving the editor showing nothing.
        var selected = SelectedIndex;
        if (selected > index || selected >= tabs.Count) {
            selected--;
        }

        tab.Panel.Remove();
        tab.Remove();

        SelectedIndex = Math.Clamp(selected, tabs.Count > 0 ? 0 : -1, tabs.Count - 1);
        Restate();

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
