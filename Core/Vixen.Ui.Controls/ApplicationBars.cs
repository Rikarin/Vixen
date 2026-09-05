// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>The strip of verbs across the top of a window.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="AccessibleRole.Toolbar" /> has existed in <c>Accessibility.cs</c> with
///         nothing to carry it.</b> The editor draws its own strip out of bare
///         <c>UiElement</c>s — <c>ToolbarPresenter.cs:51</c> — so no application could reach one and
///         the role reached no screen reader. This is the carrier.
///     </para>
///     <para>
///         ⚠ <b>One tab stop, and the arrows move inside it.</b> That is ARIA's toolbar pattern and
///         it is the whole reason a toolbar is a control rather than a <c>&lt;Panel&gt;</c> with a
///         class: a strip of fifteen buttons that are each a tab stop puts fifteen presses between
///         a keyboard user and the document. The roving index is <c>RadioGroup</c>'s, arranged
///         around <see cref="Active" /> instead of around a checked value.
///     </para>
///     <para>
///         <b>No overflow menu and no customisation.</b> <c>NSToolbar</c> has both and they are the
///         two features that make a toolbar a project rather than a control; an application that
///         needs them today puts a <see cref="Button" /> at the end and opens its own menu. Said
///         here rather than hinted at, because a half-built overflow is worse than none: a strip
///         that silently drops its last three buttons at a narrow width is a strip whose verbs
///         disappear with no way to find out where.
///     </para>
/// </remarks>
public sealed partial class Toolbar : Control {
    /// <inheritdoc />
    protected override string TagName => "toolbar";

    /// <inheritdoc />
    /// <remarks>The toolbar is never the stop; one of its items is.</remarks>
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.Toolbar;

    /// <summary>Which way it runs.</summary>
    /// <remarks>
    ///     Written through to a <c>vertical</c> class, on <c>SplitView.Orientation</c>'s terms: the
    ///     axis is a <c>flex-direction</c> and belongs to the theme. It also decides which pair of
    ///     arrow keys moves along the strip, which does not.
    /// </remarks>
    [UiProperty(Changed = nameof(OnOrientationChanged))]
    public partial Orientation Orientation { get; set; }

    /// <summary>The focusable things on it, in order.</summary>
    /// <remarks>
    ///     ⚠ <b>Read from the children each time rather than kept</b>, for <c>RadioGroup.Options</c>'
    ///     reason: a list the toolbar maintained would be a second place the truth lived, and a
    ///     <c>&lt;Button&gt;</c> written as a nested tag would be in the tree and not in the strip.
    ///     Descendants rather than children, because a <see cref="SegmentedControl" /> or a
    ///     <see cref="Separator" /> in the middle means the buttons are not all one level down.
    /// </remarks>
    public IReadOnlyList<UiElement> Items => [.. Focusables(this)];

    /// <summary>Which item is the strip's one tab stop.</summary>
    /// <remarks>
    ///     ⚠ <b>Where the focus <i>would land</i>, which is not where it is.</b> A toolbar the user
    ///     has tabbed away from keeps this, so tabbing back returns to the button they were on —
    ///     the behaviour every toolbar has and the reason a roving index is a stored index rather
    ///     than a reading of the focus.
    /// </remarks>
    public UiElement? Active { get; private set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AddHandler<KeyEvent>(static (element, args) => ((Toolbar) element).Keyed(args));

        // On the toolbar rather than on each item: an item does not know what is next to it, and a
        // strip that subscribed per child would have to unsubscribe per removal.
        AddHandler<FocusEvent>(static (element, args) => ((Toolbar) element).Focused(args));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A child arriving is what puts the tab index back</b>, and a snapshot cannot do it:
    ///     which item is the single stop depends on how many there are, so a toolbar built from a
    ///     plugin's late registration would otherwise have every button a stop but the first.
    /// </remarks>
    protected override void OnChildAdded(UiElement child) {
        base.OnChildAdded(child);
        Rove();
    }

    /// <summary>Brings the roving tab index into line with <see cref="Active" />.</summary>
    /// <remarks>
    ///     Public because <see cref="Items" /> is: a strip whose contents were removed, or whose
    ///     buttons became focusable after they were added, has to be able to say so. Nothing else
    ///     can notice either.
    /// </remarks>
    public void Rove() {
        var items = Items;

        if (items.Count == 0) {
            Active = null;
            return;
        }

        if (Active is null || !items.Contains(Active)) {
            Active = items[0];
        }

        foreach (var item in items) {
            item.TabIndex = ReferenceEquals(item, Active) ? 0 : -1;
        }
    }

    /// <summary>Every focusable descendant, in document order.</summary>
    /// <remarks>
    ///     ⚠ <b>It does not descend into a nested <see cref="Toolbar" />.</b> Two strips that shared
    ///     one roving index would fight over which of them owns the tab stop, and the inner one
    ///     would lose silently — its own <see cref="Rove" /> would run and be overwritten by the
    ///     outer's on the next child.
    /// </remarks>
    static IEnumerable<UiElement> Focusables(UiElement parent) {
        foreach (var child in parent.Children) {
            if (child.Focusable) {
                yield return child;
                continue;
            }

            if (child is Toolbar) {
                continue;
            }

            foreach (var deeper in Focusables(child)) {
                yield return deeper;
            }
        }
    }

    void OnOrientationChanged(Orientation previous, Orientation current) {
        if (current == Orientation.Vertical) {
            AddClass("vertical");
        } else {
            RemoveClass("vertical");
        }
    }

    /// <remarks>
    ///     ⚠ <b>The focus arriving by any route moves the stop.</b> A user who clicked the fourth
    ///     button and then tabbed away must come back to the fourth button, and a roving index
    ///     maintained only by the arrow keys sends them back to the first.
    /// </remarks>
    void Focused(FocusEvent args) {
        if (!args.Gained || args.Source is not { } landed || ReferenceEquals(landed, this)) {
            return;
        }

        if (Items.Contains(landed)) {
            Active = landed;
            Rove();
        }
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || !args.Has(ModifierKeys.None)) {
            return;
        }

        var vertical = Orientation == Orientation.Vertical;

        var step = args.Key switch {
            InputKey.Right when !vertical => 1,
            InputKey.Left when !vertical => -1,
            InputKey.Down when vertical => 1,
            InputKey.Up when vertical => -1,
            _ => 0
        };

        var items = Items;

        if (step == 0 || items.Count == 0) {
            return;
        }

        // Wraps, for `RadioGroup.Keyed`'s reason: a strip is a cycle rather than a layout, and Right
        // on the last button doing nothing reads as the keyboard being broken.
        var current = Active is null ? -1 : IndexOf(items, Active);
        var next = current < 0
            ? step > 0 ? 0 : items.Count - 1
            : (current + step + items.Count) % items.Count;

        Active = items[next];
        Rove();
        Document.Focus(items[next]);

        args.Handled = true;
    }

    static int IndexOf(IReadOnlyList<UiElement> items, UiElement item) {
        for (var i = 0; i < items.Count; i++) {
            if (ReferenceEquals(items[i], item)) {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>The strip along the bottom that says what is going on.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="AccessibleRole.Status" /> is the whole reason this is a control.</b> A
///         status bar built out of a bare <c>UiElement</c> and a stylesheet — which is what
///         <c>EditorShell.cs:138-155</c> does — is visually right and silent: ARIA's <c>status</c>
///         is a live region, so a screen reader announces a change to it <i>without</i> moving the
///         focus, and that is the entire behaviour a status bar exists to have.
///     </para>
///     <para>
///         <b>A container and not a label.</b> Every real status bar has several cells — a message,
///         a line and column, an encoding, a progress spinner — so the message is
///         <see cref="Message" /> and the rest are children. <see cref="UiElement.ContentHost" /> is
///         the trailing area, so <c>&lt;StatusBar&gt;</c>'s nested tags land after the message
///         rather than on top of it.
///     </para>
/// </remarks>
public sealed partial class StatusBar : Control {
    /// <inheritdoc />
    protected override string TagName => "status-bar";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.Status;

    /// <summary>Where the message is drawn.</summary>
    public UiElement Label { get; private set; } = null!;

    /// <summary>Where everything else goes.</summary>
    public UiElement Trailing { get; private set; } = null!;

    /// <summary>What it currently says, or <see langword="null" /> for nothing.</summary>
    /// <remarks>
    ///     A projection of the label's text rather than a value this control holds, on
    ///     <c>ButtonBase.Label</c>'s terms — so it is not a <c>[UiProperty]</c> and the cascade has
    ///     no business matching on it.
    /// </remarks>
    public string? Message {
        get => Label.Text;
        set => Label.Text = value;
    }

    /// <inheritdoc />
    protected override UiElement ContentHost => Trailing ?? this;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Label = Part("status-message");
        Trailing = Part("status-trailing");
    }
}
