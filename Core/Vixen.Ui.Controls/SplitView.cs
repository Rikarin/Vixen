// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>Two panes with a bar between them that resizes both.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This existed and could not be reached.</b> A draggable two-pane divider was written
///         once, welded inside <c>DockingHost</c> as <c>DockSplitterView</c>, so an application that
///         wanted a sidebar beside a document had to adopt the whole docking model — a layout tree,
///         panel identities, tab groups, a save format and drag-and-drop between groups — to get one
///         bar it could pull. <c>NavigationSplitView</c> and <c>NSSplitView</c> are the shape half of
///         desktop applications start from, and starting from a docking host is not the same offer.
///     </para>
///     <para>
///         <b>The ratio is <c>flex-grow</c> and a drag writes two declarations.</b> No rebuild, no
///         reparent, no measurement pass of its own — which is <c>DockSplitterView</c>'s arrangement
///         and its reasons, kept because they are the reasons a splitter feels attached to the
///         pointer.
///     </para>
///     <para>
///         ⚠ <b><c>flex-basis: 0px</c> on both panes is the half that looks redundant and is not.</b>
///         With the default <c>auto</c> basis flexbox shares out only what is left after the contents
///         have been measured, so two panes at 50/50 come out at whatever their contents wanted plus
///         half the remainder each — and a bar dragged to the middle does not land in the middle.
///     </para>
///     <para>
///         <b>Not a docking host and deliberately not on the way to being one.</b> There is no tab
///         group, no drag between panes and no saved layout: an application that wants those wants
///         <c>DockingHost</c>, and one that wants a sidebar should not have to explain why it does
///         not.
///     </para>
/// </remarks>
public sealed partial class SplitView : Control {
    /// <summary>How far one arrow press moves the bar, in pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Pixels, in the one control that argues for fractions everywhere else.</b>
    ///     <see cref="MinimumRatio" /> is a fraction because it has to survive a resize — it is
    ///     re-applied every time the split changes size and nothing tells this control when that
    ///     happens. A step is not re-applied: it is consumed at the instant of the press, against
    ///     the span the split has right then. And a fractional step is the thing that feels wrong at
    ///     both ends — a hundredth of a 2000-pixel window is a twenty-pixel jump, and a hundredth of
    ///     a 200-pixel one is two, so the same key does something different in every window.
    /// </remarks>
    const float KeyStep = 8f;

    /// <summary>What Page Up and Page Down move instead.</summary>
    const float KeyPage = KeyStep * 8f;

    /// <summary>What an arrow moves when the split has not been laid out yet.</summary>
    /// <remarks>
    ///     A split with no span has no pixels to convert, and refusing the press would make the
    ///     keyboard silently dead in exactly the case a test constructs. A fiftieth is the same
    ///     order as <see cref="KeyStep" /> against an ordinary pane.
    /// </remarks>
    const float UnlaidStep = 0.02f;

    bool dragging;

    /// <summary>The name markup writes to reach <see cref="Second" />.</summary>
    /// <remarks>
    ///     ⚠ <b>One slot name rather than two, because <see cref="UiElement.ContentHost" /> already
    ///     answers for the first.</b> A split is two things and a content host can only be one of
    ///     them, so the near pane is where unmarked children go — which is what a sidebar's own
    ///     markup reads like — and the far pane is named. Naming both would mean a
    ///     <c>&lt;SplitView&gt;</c> whose children silently went nowhere until the author found out
    ///     that they had to be labelled.
    /// </remarks>
    public const string SecondSlot = "second";

    /// <inheritdoc />
    protected override string TagName => "split-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>group</c>. ⚠ Unlike <see cref="Panel" />, which reports nothing on the grounds
    ///     that a box is not a landmark, this one is a box whose two halves are a fact about the
    ///     document: a screen reader reading straight through has no other way to learn that the
    ///     list it just left and the detail it is now in are two panes of one thing. The bar between
    ///     them carries <see cref="AccessibleRole.Separator" /> for the same reason.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Group;

    /// <summary>The near pane: the left one, or the top one.</summary>
    public UiElement First { get; private set; } = null!;

    /// <summary>The bar between them.</summary>
    public UiElement Bar { get; private set; } = null!;

    /// <summary>The far pane: the right one, or the bottom one.</summary>
    public UiElement Second { get; private set; } = null!;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ The null guard is <c>Card</c>'s and is load-bearing for its reason: <c>ContentHost</c>
    ///     can be read before <see cref="OnCreated" /> has run, and answering with an uninitialised
    ///     part is a null reference at the first nested tag.
    /// </remarks>
    protected override UiElement ContentHost => First ?? this;

    /// <summary>Which way the panes are stacked.</summary>
    /// <remarks>
    ///     Written through to a class — <c>vertical</c> — because which axis a split runs along is a
    ///     <c>flex-direction</c> and a cursor, both of which are the theme's.
    /// </remarks>
    [UiProperty(Changed = nameof(OnOrientationChanged))]
    public partial Orientation Orientation { get; set; }

    /// <summary>How much of the space <see cref="First" /> takes, from zero to one.</summary>
    [UiProperty(Default = 0.5f, Coerce = nameof(CoerceRatio), Changed = nameof(OnRatioChanged))]
    public partial float Ratio { get; set; }

    /// <summary>How small either pane may be made, as a fraction.</summary>
    /// <remarks>
    ///     ⚠ <b>A fraction and not a pixel width, which is the wrong answer for a resizable window
    ///     and the right one here.</b> A pixel minimum has to be re-clamped every time the split
    ///     itself changes size, and nothing in this control is told when that happens — the ratio is
    ///     applied by the cascade and the layout follows. A fraction survives a resize by meaning the
    ///     same thing at every size, which is also what makes it the number worth saving.
    /// </remarks>
    [UiProperty(Default = 0.1f, Changed = nameof(OnMinimumChanged))]
    public partial float MinimumRatio { get; set; }

    /// <summary>Raised while the bar is dragged, and once when it is let go.</summary>
    public event Action<SplitView, float>? RatioChanged;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><c>slot="second"</c>, and the bar is created between the two panes rather than
    ///     appended after them.</b> A part added later lands at the end, which for a splitter means
    ///     both panes on one side of it — visibly wrong, and wrong in a way no stylesheet can put
    ///     back without knowing the order it wanted.
    /// </remarks>
    protected override UiElement? NamedHost(string name) =>
        string.Equals(name, SecondSlot, StringComparison.Ordinal) ? Second : base.NamedHost(name);

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        First = Part("split-pane");
        Bar = Part("split-bar");
        Second = Part("split-pane");

        Bar.Role = AccessibleRole.Separator;

        // ⚠ The bar is a tab stop, and that is the whole of what makes the split reachable without a
        // pointer. ARIA's window splitter is focusable for a reason a sighted mouse user never
        // meets: a separator that cannot take the focus can be *announced* — a reader walking the
        // tree says "separator" — and cannot be *moved*, so the pane widths are a decision the
        // application made once on behalf of everybody who does not have a mouse.
        Bar.Focusable = true;
        Bar.AccessibleName = ControlStrings.SplitViewDivider.Text;

        Bar.AddHandler<PointerEvent>((_, args) => Pointed(args));
        Bar.AddHandler<KeyEvent>((_, args) => Keyed(args));

        Apply();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A disabled split has to take its bar out of the tab order, and no base class does it
    ///     for a part.</b> <c>Control</c> answers <c>Disabled</c> by clearing its own
    ///     <see cref="UiElement.Focusable" />, and the focus this control offers is not its own —
    ///     <see cref="UiDocument.TabOrder" /> skips a <c>display: none</c> subtree and knows nothing about
    ///     a disabled ancestor, so the bar would stay a stop that answers no key. The capture-leg
    ///     refusal already stops the keys; a tab stop that does nothing is the half it cannot reach.
    /// </remarks>
    protected override void OnPropertyChanged(UiPropertyKey key) {
        base.OnPropertyChanged(key);

        if (Bar is not null && string.Equals(key.Name, nameof(Disabled), StringComparison.Ordinal)) {
            Bar.Focusable = !Disabled;
        }
    }

    /// <summary>Writes the ratio onto the two panes.</summary>
    /// <remarks>
    ///     Public because the panes are, and because an application that replaced a pane's contents
    ///     with something that sets its own <c>flex</c> has to be able to say so again. It is what
    ///     every path in here ends with.
    /// </remarks>
    public void Apply() {
        Write(First, Ratio);
        Write(Second, 1f - Ratio);

        // ⚠ `aria-valuenow` on the separator, and it is what a keyboard resize is *for*. A bar that
        // moves and never says where it is tells a reader nothing but "separator" on every press,
        // which is indistinguishable from a key that did nothing. Invariant for `Slider`'s reason:
        // a bridge wants a number it can re-present, not one it has to parse back.
        Bar.AccessibleValue = Ratio.ToString("0.###", CultureInfo.InvariantCulture);
    }

    static void Write(UiElement pane, float share) {
        pane.SetStyle("flex-grow", share.ToString("0.#####", CultureInfo.InvariantCulture));
        pane.SetStyle("flex-basis", "0px");
    }

    float CoerceRatio(float value) => Math.Clamp(value, MinimumRatio, 1f - MinimumRatio);

    void OnRatioChanged(float previous, float current) {
        Apply();
        RatioChanged?.Invoke(this, current);
    }

    /// <remarks>
    ///     ⚠ <b>Written back through the property, so that widening the minimum past where the bar
    ///     already is moves the bar.</b> A minimum that only applied to the next assignment would
    ///     leave a split sitting outside its own minimum with no path that ever fixes it — and
    ///     <c>Ratio = Ratio</c> would not do it either, because the setter compares before it
    ///     assigns.
    /// </remarks>
    void OnMinimumChanged(float previous, float current) => Ratio = Math.Clamp(Ratio, current, 1f - current);

    void OnOrientationChanged(Orientation previous, Orientation current) {
        if (current == Orientation.Vertical) {
            AddClass("vertical");
        } else {
            RemoveClass("vertical");
        }
    }

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                dragging = true;

                // The focus follows the drag, which is `Slider`'s arrangement and the reason is the
                // same: a bar somebody has just pulled is the one the arrow keys should move next.
                // `:focus-visible` is what keeps a click from lighting the ring.
                Document.Focus(Bar);
                Document.CapturePointer(Bar);

                args.Handled = true;
                break;

            case PointerAction.Moved when dragging:
                Drag(args);
                args.Handled = true;
                break;

            case PointerAction.Released when dragging:
                dragging = false;
                Document.ReleasePointer();

                args.Handled = true;
                break;

            default:
                break;
        }
    }

    void Drag(PointerEvent args) {
        var span = Span();

        if (span <= 0f) {
            return;
        }

        var along = Orientation == Orientation.Vertical ? args.Y - Bounds.Y : args.X - Bounds.X;

        Ratio = along / span;
    }

    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only the arrow pair along the split's own axis</b>, which is <c>Toolbar</c>'s
    ///         rule and is here for a sharper reason: a split view has a whole application in its
    ///         two panes. Answering all four would take Up and Down away from a list in the pane
    ///         beside the bar the moment the focus was on the bar — and the focus is on the bar
    ///         after every drag.
    ///     </para>
    ///     <para>
    ///         <b>Home and End go to the minimum and the maximum, not to zero and one.</b>
    ///         <see cref="CoerceRatio" /> clamps to <see cref="MinimumRatio" /> either way, so
    ///         asking for zero would land on the minimum and report a key that overshot. ⚠ Which
    ///         is also why there is no collapse-and-restore on Enter, the fourth thing ARIA's
    ///         window splitter names: a collapsed pane is ratio zero and this control cannot
    ///         represent one — <c>MinimumRatio</c> is a floor with no exception in it, and adding
    ///         the exception is a different decision from adding a keystroke.
    ///     </para>
    /// </remarks>
    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed) {
            return;
        }

        var vertical = Orientation == Orientation.Vertical;
        var span = Span();

        var step = span > 0f ? KeyStep / span : UnlaidStep;
        var page = span > 0f ? KeyPage / span : UnlaidStep * 8f;

        var moved = args.Key switch {
            InputKey.Left when !vertical => Ratio - step,
            InputKey.Right when !vertical => Ratio + step,
            InputKey.Up when vertical => Ratio - step,
            InputKey.Down when vertical => Ratio + step,
            InputKey.PageUp => Ratio - page,
            InputKey.PageDown => Ratio + page,
            InputKey.Home => MinimumRatio,
            InputKey.End => 1f - MinimumRatio,
            _ => float.NaN
        };

        if (float.IsNaN(moved)) {
            return;
        }

        Ratio = moved;
        args.Handled = true;
    }

    /// <remarks>
    ///     ⚠ <b>The bar's own thickness is subtracted, because it is not either pane's.</b> Measuring
    ///     the ratio against the whole split makes the bar drift away from the pointer by half its
    ///     width at one end and towards it at the other — which reads as a splitter that does not
    ///     quite follow the mouse and is the same arithmetic <c>DockSplitterView.Drag</c> writes.
    /// </remarks>
    float Span() =>
        Orientation == Orientation.Vertical ? Bounds.Height - Bar.Height : Bounds.Width - Bar.Width;
}
