// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>Which side of its anchor an overlay prefers.</summary>
public enum Placement : byte {
    /// <summary>Below, aligned to the anchor's left edge.</summary>
    Bottom,

    /// <summary>Above.</summary>
    Top,

    /// <summary>To the right, aligned to the anchor's top edge.</summary>
    Right,

    /// <summary>To the left.</summary>
    Left
}

/// <summary>Anything that appears over the rest of the interface.</summary>
/// <remarks>
///     <para>
///         <b>An overlay is a child of the root, not of whatever opened it</b>, and that is forced by
///         painting order: the draw list is document order, so an element can only be drawn over
///         something by coming after it. A popup that lived inside the button that opened it would
///         be painted inside that button's stacking position and clipped by every
///         <c>overflow: hidden</c> between the two. Being a root child costs a reference back to the
///         anchor and buys an overlay that is always on top and never clipped.
///     </para>
///     <para>
///         ⚠ <b>Placement runs a layout pass of its own.</b> Where a popup goes depends on how big
///         it is, and how big it is depends on a layout that has not happened when it opens. So
///         <see cref="Open" /> updates the document, reads the sizes, and then positions — one extra
///         pass, on a user action, rather than a popup that is in the wrong place for the first
///         frame anybody sees it.
///     </para>
///     <para>
///         <b>Position is <see cref="UiElement.OffsetX" />, not layout.</b> Moving a popup does not
///         disturb anything: no sibling reflows, no ancestor grows, and dragging one costs a walk
///         rather than a cascade.
///     </para>
/// </remarks>
public abstract partial class Overlay : Control {
    UiElement? anchor;

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Whether it is showing.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a <c>[UiProperty]</c>, unlike almost everything else on a control.</b> Opening
    ///     is not an assignment — it measures, places, moves the focus and may take a modal scope —
    ///     so a settable property would be an invitation to do half of it. <see cref="Open" /> and
    ///     <see cref="Close" /> are the whole interface, and this is what they report.
    /// </remarks>
    public bool IsOpen { get; private set; }

    /// <summary>Which side of the anchor it prefers.</summary>
    [UiProperty]
    public partial Placement Placement { get; set; }

    /// <summary>How far off the anchor it sits.</summary>
    [UiProperty(Default = 4f)]
    public partial float Gap { get; set; }

    /// <summary>Whether a click outside it closes it.</summary>
    [UiProperty(Default = true)]
    public partial bool LightDismiss { get; set; }

    /// <summary>Whether Escape closes it.</summary>
    [UiProperty(Default = true)]
    public partial bool CloseOnEscape { get; set; }

    /// <summary>What it is attached to, if anything.</summary>
    public UiElement? Anchor => anchor;

    /// <summary>Raised when it opens or closes.</summary>
    public event Action<Overlay, bool>? OpenChanged;

    /// <summary>
    ///     The two root handlers, kept so they can be taken off again.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Fields rather than inline lambdas, and that is the whole reason they are fields.</b>
    ///     <c>RemoveHandler</c> matches on the delegate, and a lambda written at the call site is a
    ///     fresh object every time it is evaluated — so registering with one and unregistering with a
    ///     syntactically identical one removes nothing and reads like it worked.
    /// </remarks>
    Action<UiElement, PointerEvent>? dismiss;
    Action<UiElement, KeyEvent>? escaped;

    /// <summary>What <see cref="Reposition" /> last measured, so a resize can be noticed.</summary>
    Vector2 placed;

    Action<UiDocument>? settle;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        AddClass("closed");

        dismiss = (element, args) => Dismiss(args);
        escaped = (element, args) => Escaped(args);

        // ⚠ On the root and on the capture leg, so a press anywhere in the document is seen before
        // whatever it landed on acts on it. Listening on the overlay itself would only hear presses
        // inside the overlay, which are the presses that must *not* close it.
        Document.Root.AddHandler(dismiss, RoutingStrategy.Capture, handledEventsToo: true);
        Document.Root.AddHandler(escaped, RoutingStrategy.Capture, handledEventsToo: true);

        // ⚠ An overlay whose contents change size after it opened has to be placed again, and until
        // this existed nothing did it. `Open` measures once and places once, which is right for a
        // menu built before it is shown and wrong for everything whose content is decided while it
        // is up: a picker that grows as a query widens the list ends up hanging off the bottom of
        // the window, because the placement it got was for the height it had three keystrokes ago.
        // The same hook `ScrollView` uses, for the same reason — a size is only knowable after the
        // pass that computed it.
        settle = _ => Resized();
        Document.LayoutFinished += settle;
    }

    /// <summary>Places it again if the pass that just finished changed its size.</summary>
    /// <remarks>
    ///     ⚠ <b>Guarded on the size having actually changed, because <see cref="Reposition" /> writes
    ///     an offset and an offset is an input to the next layout.</b> Repositioning unconditionally
    ///     from a layout-finished callback is a loop that runs for as long as the overlay is open.
    /// </remarks>
    void Resized() {
        if (!IsOpen || anchor is null) {
            return;
        }

        var size = new Vector2(Bounds.Width, Bounds.Height);

        if (size == placed) {
            return;
        }

        Reposition();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>An overlay that is removed leaves two handlers on the root, and they are not
    ///     harmless.</b> Both close over <c>this</c>, so the root holds the removed overlay alive for
    ///     as long as the document lives, and every pointer event in the application walks two more
    ///     delegates per overlay ever created — a menu bar that rebuilds its menus leaks a pair each
    ///     time. Nothing could take them off before <see cref="UiElement.OnRemoved" /> existed.
    /// </remarks>
    protected override void OnRemoved() {
        if (dismiss is not null) {
            Document.Root.RemoveHandler(dismiss);
            dismiss = null;
        }

        if (escaped is not null) {
            Document.Root.RemoveHandler(escaped);
            escaped = null;
        }

        if (settle is not null) {
            Document.LayoutFinished -= settle;
            settle = null;
        }

        base.OnRemoved();
    }

    /// <summary>Shows it, beside an anchor.</summary>
    /// <param name="target">What to put it beside, or <c>null</c> to leave it where it is.</param>
    public void Open(UiElement? target = null) {
        anchor = target ?? anchor;

        if (IsOpen) {
            Reposition();
            return;
        }

        IsOpen = true;
        Restate();

        // The pass that gives it a size, so that the placement below has something to measure and
        // to flip against. See the type's remarks.
        Document.Update();
        Reposition();

        OnOpened();
    }

    /// <summary>Hides it.</summary>
    /// <param name="reason">Why.</param>
    public void Close(CloseReason reason = CloseReason.Code) {
        if (!IsOpen) {
            return;
        }

        IsOpen = false;
        Restate();
        OnClosed(reason);
    }

    /// <summary>Puts it where its placement asks, flipping if there is no room.</summary>
    /// <remarks>
    ///     ⚠ <b>Flip first, then clamp.</b> A popup with no room below goes above; one with no room
    ///     on either side is pushed back inside the viewport rather than being left hanging off it.
    ///     Clamping without flipping gives a menu that covers the button that opened it, which is
    ///     the one place it must not be.
    /// </remarks>
    public void Reposition() {
        if (anchor is null || anchor.IsRemoved) {
            return;
        }

        var target = anchor.Bounds;
        var size = Bounds;
        var viewport = Document.Viewport;

        // What this placement was computed against, so `Resized` can tell a pass that changed the
        // size from one that did not.
        placed = new Vector2(size.Width, size.Height);

        var placement = Flip(Placement, target, size, viewport.ViewportWidth, viewport.ViewportHeight);

        var (x, y) = placement switch {
            Placement.Top => (target.Left, target.Top - size.Height - Gap),
            Placement.Right => (target.Right + Gap, target.Top),
            Placement.Left => (target.Left - size.Width - Gap, target.Top),
            _ => (target.Left, target.Bottom + Gap)
        };

        MoveTo(
            Math.Clamp(x, 0f, MathF.Max(0f, viewport.ViewportWidth - size.Width)),
            Math.Clamp(y, 0f, MathF.Max(0f, viewport.ViewportHeight - size.Height))
        );
    }

    /// <summary>Puts its top-left corner at a point in document space.</summary>
    /// <remarks>
    ///     ⚠ <b>The offset is relative to where layout put it, so where layout put it is subtracted.</b>
    ///     An overlay is the root's child and the root has padding in most themes — assigning the
    ///     document coordinate straight into the offset would add that padding a second time, which
    ///     looks like a popup that is consistently a few pixels off in one direction.
    /// </remarks>
    public void MoveTo(float x, float y) {
        OffsetX += x - AbsoluteLeft;
        OffsetY += y - AbsoluteTop;
    }

    /// <summary>Called after it opens.</summary>
    protected virtual void OnOpened() {
    }

    /// <summary>Called after it closes.</summary>
    /// <param name="reason">Why it closed.</param>
    protected virtual void OnClosed(CloseReason reason) {
    }

    /// <summary>Whether a press at a point should close it.</summary>
    /// <remarks>
    ///     Overridden by a menu, whose press on its own anchor must close it rather than reopen it —
    ///     otherwise clicking the button that opened a menu closes and reopens it in one gesture,
    ///     and the menu never goes away.
    /// </remarks>
    protected virtual bool IsOutside(UiElement? hit) {
        for (var element = hit; element is not null; element = element.Parent) {
            // ⚠ The anchor counts as inside, and that is what stops a second click on the button
            // that opened a popup from reopening it. Light dismiss runs on the *press*, on the
            // root's capture leg, so it fires before the anchor's own handler sees anything — the
            // anchor then finds the popup already shut and opens it again, and the popup can never
            // be closed by clicking the thing that opened it. Leaving the anchor to decide is the
            // only arrangement in which "click it again to close" is expressible at all.
            if (ReferenceEquals(element, this) || ReferenceEquals(element, anchor)) {
                return false;
            }
        }

        return true;
    }

    static Placement Flip(Placement placement, Rectangle target, Rectangle size, float width, float height) =>
        placement switch {
            Placement.Bottom when target.Bottom + size.Height > height && target.Top - size.Height >= 0f =>
                Placement.Top,
            Placement.Top when target.Top - size.Height < 0f && target.Bottom + size.Height <= height =>
                Placement.Bottom,
            Placement.Right when target.Right + size.Width > width && target.Left - size.Width >= 0f =>
                Placement.Left,
            Placement.Left when target.Left - size.Width < 0f && target.Right + size.Width <= width =>
                Placement.Right,
            _ => placement
        };

    /// <summary>Puts the open state where the cascade and the listeners can see it.</summary>
    void Restate() {
        if (IsOpen) {
            RemoveClass("closed");
        } else {
            AddClass("closed");
        }

        // ⚠ Here rather than on each of the anchors, and that is what makes one line enough. The
        // element whose announced state changed is the *anchor* — a `MenuItem` gains `Expanded`, a
        // `Select` flips it, a `ComboBox` reads it off `Owner.List` — and not one of them is told
        // when this happens. The invalidation is a document-wide flag rather than a node, so the
        // place to raise it is the field every one of those overrides reads: `IsOpen`, which is
        // written in exactly two methods and restated in exactly this one.
        InvalidateAccessibility();

        Raise(new OpenChangedEvent { IsOpen = IsOpen });
        OpenChanged?.Invoke(this, IsOpen);
    }

    void Dismiss(PointerEvent args) {
        if (!IsOpen || !LightDismiss || args.Action != PointerAction.Pressed) {
            return;
        }

        if (IsOutside(Document.HitTest(args.X, args.Y))) {
            Close(CloseReason.LightDismissed);
        }
    }

    void Escaped(KeyEvent args) {
        if (!IsOpen || !CloseOnEscape || args is not { Action: KeyAction.Pressed, Key: InputKey.Escape }) {
            return;
        }

        Close(CloseReason.Cancelled);
        args.Handled = true;
    }
}

/// <summary>A floating panel attached to something.</summary>
/// <remarks>
///     The plain overlay: a box, positioned beside an anchor, that closes when something else is
///     clicked. A menu, a dropdown and a date picker are all this with content in them, which is why
///     it is a control of its own rather than a base class nobody instantiates.
/// </remarks>
public sealed partial class Popover : Overlay {
    /// <inheritdoc />
    protected override string TagName => "popover";

    /// <summary>Where the content goes.</summary>
    public UiElement Content { get; private set; } = null!;

    /// <inheritdoc />
    /// <remarks>So that markup written inside a <c>&lt;Popover&gt;</c> lands in its panel.</remarks>
    protected override UiElement ContentHost => Content;

    /// <summary>Raised when an element is added to <see cref="Content" />.</summary>
    /// <remarks>
    ///     ⚠ <b>For a control whose contents live in a popover it does not contain, which is every
    ///     control with a dropdown.</b> <see cref="UiElement.OnChildAdded" /> fires on the element a
    ///     child was added to — <see cref="Content" /> — and that is a part of this overlay rather
    ///     than of the field that opened it. So a <c>SelectBase</c> routing its markup options here
    ///     through <see cref="UiElement.ContentHost" /> would place them correctly and never hear
    ///     that they had arrived: an <c>&lt;Option&gt;</c> would draw, and the closed field would go
    ///     on showing its placeholder.
    ///     <para>
    ///         An event rather than a second element between the popover and its options, because
    ///         that element would be a flex item the theme has to size — <c>popover.select-list</c>
    ///         lays its children out in a column — and a DOM level added to satisfy a notification
    ///         is a DOM level every stylesheet has to know about.
    ///     </para>
    /// </remarks>
    public event Action<Popover, UiElement>? ContentAdded;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // ⚠ Typed, but with the same tag as the plain part it replaces, so nothing a stylesheet says
        // changes. Its whole job is to have somewhere to override `OnChildAdded`.
        var content = Part<PopoverContent>();
        content.Owner = this;

        Content = content;
    }

    void Added(UiElement child) => ContentAdded?.Invoke(this, child);

    /// <summary>The panel inside a popover, which exists to forward what lands in it.</summary>
    sealed partial class PopoverContent : UiElement {
        /// <summary>The popover this is the content of.</summary>
        internal Popover? Owner { get; set; }

        /// <inheritdoc />
        protected override string TagName => "popover-content";

        /// <inheritdoc />
        protected override void OnChildAdded(UiElement child) {
            base.OnChildAdded(child);
            Owner?.Added(child);
        }
    }
}

/// <summary>A short label that appears beside something the pointer is resting on.</summary>
/// <remarks>
///     <para>
///         <b>The delay needs a clock, and the document now has one.</b> A tooltip waits half a
///         second before appearing, and nothing here is told what time it is except through input
///         events — which stop arriving precisely when the pointer is resting. So it subscribes to
///         <see cref="UiDocument.Ticked" />, which a host with a frame loop drives through
///         <see cref="UiDocument.Tick" />, the same clock <c>GestureRecognizer</c> uses for a long
///         press.
///     </para>
///     <para>
///         ⚠ <b>A host that never ticks gets a tooltip that never appears.</b> <see cref="Tick" /> is
///         still public and a caller may drive it directly; what is gone is the arrangement where
///         every application had to remember to. Which of "never" and "instantly" is the better
///         failure is arguable — what is not is that the difference used to be a method call in
///         somebody else's loop.
///     </para>
///     <para>
///         It is never focusable and never takes the pointer: <c>pointer-events: none</c> in the
///         theme, so a tooltip that lands under the cursor does not immediately hide itself by
///         taking the hover away from the thing it describes.
///     </para>
/// </remarks>
public sealed partial class Tooltip : Overlay {
    TimeSpan entered;
    bool waiting;

    /// <inheritdoc />
    protected override string TagName => "tooltip";

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>tooltip</c>. Its name is its own <c>Text</c>, from the base — <see cref="Label" />
    ///     is that text under another name.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Tooltip;

    /// <summary>How long the pointer must rest before it appears.</summary>
    [UiProperty]
    public partial TimeSpan Delay { get; set; }

    /// <summary>What it says.</summary>
    public string? Label {
        get => Text;
        set => Text = value;
    }

    Action<UiDocument, TimeSpan>? ticked;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Placement = Placement.Bottom;
        LightDismiss = false;

        if (Delay == TimeSpan.Zero) {
            Delay = TimeSpan.FromMilliseconds(500);
        }

        // ⚠ Held in a field, because an event cannot be unsubscribed from a lambda it was never
        // given a name for. The same reason `Overlay` keeps its two capture handlers.
        ticked = (_, now) => Tick(now);
        Document.Ticked += ticked;
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (ticked is not null) {
            Document.Ticked -= ticked;
            ticked = null;
        }

        base.OnRemoved();
    }

    /// <summary>Attaches it to an element, so that hovering that element shows it.</summary>
    /// <param name="target">The element.</param>
    /// <remarks>
    ///     ⚠ <b>Attaching also says that this tooltip <i>describes</i> the target, and that is the
    ///     textbook use of <see cref="AccessibleRelation.DescribedBy" />.</b> A tooltip is shown by
    ///     hovering, which is a gesture a screen-reader user does not make — so a tooltip that was
    ///     only ever a hover behaviour is a sentence written for one kind of user and withheld from
    ///     another. The relation puts it in <c>AccessibleDescription</c>, read on demand, whether or
    ///     not the tooltip is open: an announcement of what the button does is wanted at the moment
    ///     the button is reached, not half a second after the pointer stops on it.
    /// </remarks>
    public void Attach(UiElement target) {
        ArgumentNullException.ThrowIfNull(target);

        target.AddHandler<PointerEvent>(
            (element, args) => Crossed(element, args),
            RoutingStrategy.Direct
        );

        target.AddAccessibleRelation(AccessibleRelation.DescribedBy, this);
    }

    /// <summary>Tells it what time it is, so that a pointer that has rested long enough shows it.</summary>
    /// <param name="now">The current time, on the same clock as the input events.</param>
    public void Tick(TimeSpan now) {
        if (!waiting || now - entered < Delay) {
            return;
        }

        waiting = false;
        Open(pending);
    }

    UiElement? pending;

    void Crossed(UiElement target, PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Entered:
                entered = args.Timestamp;
                pending = target;

                // ⚠ With no delay it opens now, and with one it waits for a tick that may never
                // come. That is the honest arrangement: a host with a frame loop calls Tick and
                // gets the half-second every tooltip is supposed to have, and one that does not
                // gets a tooltip that never appears rather than one that appears instantly. Which
                // of those is the better failure is arguable; what is not is that it should be
                // written down, since the difference is a method call in somebody else's loop.
                if (Delay <= TimeSpan.Zero) {
                    Open(target);
                } else {
                    waiting = true;
                }

                break;

            case PointerAction.Exited:
                waiting = false;
                Close();

                break;

            default:
                break;
        }
    }
}
