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
        Bar.AddHandler<PointerEvent>((_, args) => Pointed(args));

        Apply();
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

    /// <remarks>
    ///     ⚠ <b>The bar's own thickness is subtracted, because it is not either pane's.</b> Measuring
    ///     the ratio against the whole split makes the bar drift away from the pointer by half its
    ///     width at one end and towards it at the other — which reads as a splitter that does not
    ///     quite follow the mouse and is the same arithmetic <c>DockSplitterView.Drag</c> writes.
    /// </remarks>
    void Drag(PointerEvent args) {
        var vertical = Orientation == Orientation.Vertical;
        var span = (vertical ? Bounds.Height : Bounds.Width) - (vertical ? Bar.Height : Bar.Width);

        if (span <= 0f) {
            return;
        }

        var along = vertical ? args.Y - Bounds.Y : args.X - Bounds.X;

        Ratio = along / span;
    }
}
