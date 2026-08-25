// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>One dockable panel: a title, an id, and whatever the application puts in it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A panel is created once and moved thereafter, never rebuilt.</b> That is what
///         <c>UiDocument.Reparent</c> exists for and it is the whole reason docking is hard: a panel
///         torn out of one group and dropped into another has to keep its scroll position, its
///         selection, its focus and whatever the user has half-typed into it. A host that rebuilt the
///         panel would pass every structural test and lose the user's work.
///     </para>
///     <para>
///         ⚠ <b>It scrolls vertically by default, and it does so <i>itself</i> rather than by holding
///         a <c>ScrollView</c>.</b> This is the same judgement <see cref="DockGroupView" /> records
///         for its tab strip, one axis over: <c>overflow: hidden</c> in the theme is the draw list's
///         clip stack, <see cref="UiElement.OffsetY" /> is a post-layout translation, and between them
///         that is the whole of scrolling. What a <c>ScrollView</c> would add here is a box — and a
///         box is the thing this must not add. Every panel's content is laid out against the panel
///         today, so <c>height: 34%</c> on a profiler's grid, <c>height: 49%</c> on a quad viewport
///         and <c>width: 100%</c> on a GPU timeline's lanes all resolve against it. Interposing
///         <c>scroll-content</c> — which is <c>align-self: flex-start</c> with a shrink-to-fit height —
///         re-parents every one of those percentages onto a box whose size is what the content wanted,
///         which is circular. That is not a hypothetical: <c>StandardFrameView</c> carries a written
///         post-mortem of exactly that failure, measured on device three times.
///     </para>
///     <para>
///         ⚠ <b>The second reason is that a wrapper could not have been transparent anyway.</b> A
///         panel's whole compatibility surface is <c>Action&lt;DockPanel&gt;</c> and every builder
///         fills it with <c>panel.Add&lt;T&gt;()</c> — which is <see cref="UiElement.Add{T}" />, not
///         virtual, straight to <c>UiDocument.Create</c>. Worse, a couple of dozen asset editors are
///         handed the panel typed as a bare <see cref="UiElement" /> through
///         <c>IAssetEditorFactory.CreateView</c>, so even an override on this type would be bypassed
///         by every one of them. A redirect could not be made transparent; it could only be made
///         <i>inconsistently</i> transparent, which is worse than not doing it. Nothing here moves a
///         builder's children: <see cref="UiElement.Children" /> means exactly what it always meant.
///     </para>
///     <para>
///         <b>The price, said out loud.</b> Once a panel has overflowed it grows one extra child, the
///         <see cref="ScrollBar" />, appended after the content and absolutely positioned by the
///         theme. It is created on first overflow rather than at construction so that a panel whose
///         content fits — and a panel that has opted out — has children that are content and nothing
///         else. Code that walks a panel's children should skip anything it did not put there, which
///         is the rule <c>Control.Part</c> already states for every other control in the set.
///     </para>
///     <para>
///         <b>Vertical only.</b> Horizontal scrolling is a thing a panel asks for by putting
///         something horizontally scrollable in itself; a panel that grew a second bar because one
///         label was too long would spend a row of chrome on a problem wrapping already solves.
///     </para>
/// </remarks>
public sealed partial class DockPanel : Control {
    /// <summary>How much of the panel one page key moves, short of a full one.</summary>
    /// <remarks>
    ///     The same bargain <see cref="DockGroupView.PageFraction" /> makes and for the same reason: a
    ///     page that moved the whole viewport leaves nothing on screen that was there before.
    /// </remarks>
    const float PageMargin = 24f;

    /// <inheritdoc />
    protected override string TagName => "dock-panel";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>What identifies it in a saved layout.</summary>
    /// <remarks>
    ///     <para>
    ///         The name a serialised arrangement refers to the panel by, so changing it after
    ///         anything has been saved is renaming something two files already agree about. Set it
    ///         once, when the panel is made.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Settable, and it is what registers the panel with its host.</b>
    ///         <c>DockingHost.AddPanel</c> can assign it before anybody sees the panel; markup
    ///         cannot — a tag is created first and its attributes are assigned afterwards, so a
    ///         <c>&lt;DockPanel Id="hierarchy" /&gt;</c> arrives at its host nameless and acquires
    ///         its name a line later. So the assignment is the event: it is what files the panel
    ///         under its id and puts it in the arrangement, and re-assigning it moves the entry
    ///         rather than leaving a second one behind.
    ///     </para>
    /// </remarks>
    public string Id {
        get => id;
        set {
            ArgumentNullException.ThrowIfNull(value);

            if (string.Equals(id, value, StringComparison.Ordinal)) {
                return;
            }

            var previous = id;
            id = value;

            Host?.Rekey(this, previous);
        }
    }

    /// <summary>The host that owns it, once it has one.</summary>
    /// <remarks>
    ///     A back-pointer rather than a walk up the parents, because a panel spends its life being
    ///     reparented — <c>Detached</c>, a group view, another group view — and the one thing that
    ///     does not change is which host is doing the moving.
    /// </remarks>
    internal DockingHost? Host { get; set; }

    string id = string.Empty;

    /// <summary>What its tab says.</summary>
    [UiProperty(Changed = nameof(OnTitleChanged))]
    public partial string? Title { get; set; }

    /// <summary>Whether it may be closed.</summary>
    [UiProperty(Default = true)]
    public partial bool CanClose { get; set; }

    /// <summary>Whether its content scrolls vertically when there is more of it than fits.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>On by default, because the alternative default is content the user cannot reach.</b>
    ///         A panel is a box whose height is whatever the user last dragged a splitter to, and
    ///         almost every panel in an editor is a list, a form or a stack of sections — none of
    ///         which has any say in that height.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Off for anything that fills its box rather than stacking inside it.</b> A viewport,
    ///         a node canvas and a timeline all size a render target or a virtualised window from
    ///         their own laid-out box and hit-test in their own space — so a scroll offset they do not
    ///         know about is every pick landing somewhere else, and a scrollbar over them is permanent
    ///         chrome over content that was never too tall. Anything that already owns a scroll region
    ///         is the other half of the same rule: two scrollbars is a wheel that moves the wrong one.
    ///         <see cref="Fills" /> is how a view that has been handed the panel as a bare
    ///         <see cref="UiElement" /> says so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The class follows the property</b>, for the reason <see cref="ScrollBar" /> spells
    ///         out: the clipping and the containing block are theme rules keyed on <c>.scrolls</c>, and
    ///         a panel whose class said one thing while its offsets said the other would slide its
    ///         content out from under an unclipped box and draw it over its neighbours.
    ///     </para>
    /// </remarks>
    [UiProperty(Default = true, Changed = nameof(OnScrollsChanged))]
    public partial bool Scrolls { get; set; }

    /// <summary>How far down the content the panel is, in pixels.</summary>
    public float ScrollTop { get; private set; }

    /// <summary>How far down it can go.</summary>
    public float MaximumScroll => MathF.Max(0f, Extent - Height);

    /// <summary>Whether there is more content than the panel is showing.</summary>
    public bool Overflows => MaximumScroll > 0.5f;

    /// <summary>The bar down the right, once there has been something to scroll.</summary>
    /// <remarks>
    ///     <see langword="null" /> until the first time the content overflowed, and kept afterwards —
    ///     hidden by a class rather than removed, because a bar that was created and destroyed as the
    ///     content grew and shrank would restructure the tree on a layout pass and take the thumb out
    ///     from under whoever was dragging it.
    /// </remarks>
    public ScrollBar? Bar { get; private set; }

    /// <summary>Raised when the title changes, so the tab showing it can follow.</summary>
    internal event Action<DockPanel>? TitleChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        if (Scrolls) {
            AddClass("scrolls");
        }

        AddHandler<WheelEvent>(static (element, args) => ((DockPanel) element).Wheeled(args));
        AddHandler<KeyEvent>(static (element, args) => ((DockPanel) element).Keyed(args));
        AddHandler<FocusEvent>(static (element, args) => ((DockPanel) element).Refocused(args));

        // ⚠ The pass rather than `Control.WhenResized`, and it is the case that method documents as
        // not being its own: whether a panel overflows depends on what is *in* it. A section expanded,
        // a list filtered or a row added changes the content's height without changing the panel's box
        // at all, so a refresh gated on this element's size would leave the bar describing the panel
        // as it was two edits ago.
        settle = _ => Refresh();
        Document.LayoutFinished += settle;
    }

    Action<UiDocument>? settle;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Unhooked, because a panel outlives a great deal and does not outlive this.</b> Closing
    ///     a tab removes the panel; a handler left on the document would go on measuring a subtree
    ///     nothing can see, once per pass, for the rest of the session.
    /// </remarks>
    protected override void OnRemoved() {
        if (settle is not null) {
            Document.LayoutFinished -= settle;
            settle = null;
        }

        base.OnRemoved();
    }

    /// <summary>Says that an element fills its panel, so the panel must not scroll.</summary>
    /// <param name="element">Anything inside the panel, or the panel itself.</param>
    /// <returns>Whether a panel was found to tell.</returns>
    /// <remarks>
    ///     ⚠ <b>A walk up rather than a property, because the caller usually does not have a
    ///     <see cref="DockPanel" />.</b> An asset editor's <c>CreateView</c> is handed "where the
    ///     controls go" as a bare <see cref="UiElement" />, which is right — an editor view has no
    ///     business knowing it is in a dock — and a factory that had to cast would be a factory that
    ///     silently stopped opting out the day somebody hosted it in a splitter instead. Written as a
    ///     question about an element rather than an instruction to a panel for the same reason: the
    ///     thing that knows it fills its box is the view, not the box.
    /// </remarks>
    public static bool Fills(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);

        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (walk is DockPanel panel) {
                panel.Scrolls = false;
                return true;
            }
        }

        return false;
    }

    /// <summary>Scrolls by a distance, clamped to what there is.</summary>
    /// <param name="delta">How far, positive towards the end of the content.</param>
    public void Scroll(float delta) => ScrollTo(ScrollTop + delta);

    /// <summary>Scrolls to a position, clamped to what there is.</summary>
    /// <param name="offset">How far from the top of the content.</param>
    /// <remarks>
    ///     ⚠ <b>The offset goes onto every content child, not onto one wrapper</b>, which is the whole
    ///     of what not having a wrapper costs. They all move by the same amount, so the arithmetic is
    ///     identical; the loop is over a handful of elements and runs only when the offset actually
    ///     changes.
    /// </remarks>
    public void ScrollTo(float offset) {
        var clamped = Math.Clamp(offset, 0f, MaximumScroll);

        if (ScrollTop.Equals(clamped)) {
            return;
        }

        ScrollTop = clamped;
        Slide();
    }

    /// <summary>Scrolls until an element inside the panel is visible, if it is not already.</summary>
    /// <param name="element">The element.</param>
    /// <remarks>
    ///     ⚠ <b>The minimum movement that works</b>, rather than centring — the reason
    ///     <c>ScrollView.ScrollIntoView</c> gives, which is that centring on every focus change makes a
    ///     form jump under somebody tabbing down it one field at a time.
    /// </remarks>
    public void Reveal(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);

        if (!Overflows) {
            return;
        }

        var target = element.Bounds;
        var viewport = Bounds;

        if (target.Height <= 0f && target.Width <= 0f) {
            return;
        }

        if (target.Top < viewport.Top) {
            ScrollTo(ScrollTop - (viewport.Top - target.Top));
        } else if (target.Bottom > viewport.Bottom) {
            ScrollTo(ScrollTop + (target.Bottom - viewport.Bottom));
        }
    }

    /// <summary>Brings the bar and the offset up to date with the content's size.</summary>
    /// <remarks>
    ///     Public and idempotent for the reason <c>ScrollView.Refresh</c> is: a caller that has just
    ///     filled a panel and wants to read <see cref="Overflows" /> before the next pass has a way to
    ///     say so.
    /// </remarks>
    public void Refresh() {
        if (!Scrolls) {
            // ⚠ Not merely "do nothing". A panel that scrolled and then opted out — which is what a
            // view calling `Fills` from its own creation looks like, one pass after the panel was made
            // — would otherwise keep whatever offset it had, and the content would stay pushed up with
            // no bar left to bring it back.
            Reset();
            return;
        }

        // ⚠ Clamped again here rather than only when scrolled, because what it clamps against is the
        // content's height and that changes without anybody assigning to the offset at all. Growing
        // the pane or collapsing a section shortens the range, and an offset past the end of it is a
        // panel scrolled into empty space with no way back.
        var clamped = Math.Clamp(ScrollTop, 0f, MaximumScroll);

        if (!ScrollTop.Equals(clamped)) {
            ScrollTop = clamped;
            Slide();
        }

        var overflows = Overflows;

        if (!overflows && Bar is null) {
            // The common case for a panel that fits, and it stays free: no element, no theme lookup,
            // nothing in `Children` that the application did not put there.
            return;
        }

        var bar = Bar ??= Add<ScrollBar>();

        bar.Orientation = Orientation.Vertical;
        bar.ViewportSize = Height;
        bar.ContentSize = Extent;
        bar.Value = ScrollTop;

        if (overflows) {
            bar.RemoveClass("hidden");
        } else {
            bar.AddClass("hidden");
        }

        if (subscribed) {
            return;
        }

        subscribed = true;
        bar.Scrolled += (_, value) => ScrollTo(value);
    }

    bool subscribed;

    /// <summary>How tall the content is, measured from where layout put it.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="UiElement.Top" /> rather than <see cref="UiElement.AbsoluteTop" />, because
    ///     the offset this is used to compute is already in the second one.</b> Measuring the scrolled
    ///     position to decide how far to scroll is the loop that makes a list run away from the
    ///     pointer. The bar is excluded because it is absolutely positioned and pinned to the panel's
    ///     own bottom, so counting it would make every panel overflow by exactly its own height.
    /// </remarks>
    float Extent {
        get {
            var bottom = 0f;

            foreach (var child in Children) {
                if (ReferenceEquals(child, Bar)) {
                    continue;
                }

                bottom = MathF.Max(bottom, child.Top + child.Height);
            }

            return bottom;
        }
    }

    void Slide() {
        foreach (var child in Children) {
            if (!ReferenceEquals(child, Bar)) {
                child.OffsetY = -ScrollTop;
            }
        }

        if (Bar is { } bar) {
            bar.Value = ScrollTop;
        }
    }

    /// <summary>Puts the content back where layout put it and takes the bar away.</summary>
    void Reset() {
        if (ScrollTop != 0f) {
            ScrollTop = 0f;
            Slide();
        }

        Bar?.AddClass("hidden");
    }

    void OnScrollsChanged(bool previous, bool current) {
        if (current) {
            AddClass("scrolls");
        } else {
            RemoveClass("scrolls");
            Reset();
        }
    }

    void OnTitleChanged(string? previous, string? current) => TitleChanged?.Invoke(this);

    void Wheeled(WheelEvent args) {
        if (!Scrolls) {
            return;
        }

        var before = ScrollTop;
        ScrollTo(ScrollTop + args.DeltaY);

        // ⚠ Handled only if it actually moved — the rule <c>ScrollView</c> states, and it matters more
        // here than there. A panel is the outermost scroller in an editor, so a panel that swallowed
        // every wheel would be one whose inner list could never hand a fully-scrolled wheel back, and
        // a panel that claimed a wheel it did nothing with would stop the group under it from ever
        // seeing one.
        if (!ScrollTop.Equals(before)) {
            args.Handled = true;
        }
    }

    void Keyed(KeyEvent args) {
        if (!Scrolls || args.Action != KeyAction.Pressed || !args.Has(ModifierKeys.None)) {
            return;
        }

        var page = MathF.Max(1f, Height - PageMargin);

        var moved = args.Key switch {
            InputKey.PageDown => ScrollTop + page,
            InputKey.PageUp => ScrollTop - page,
            InputKey.Home => 0f,
            InputKey.End => MaximumScroll,
            _ => float.NaN
        };

        if (float.IsNaN(moved)) {
            return;
        }

        var before = ScrollTop;
        ScrollTo(moved);

        if (!ScrollTop.Equals(before)) {
            args.Handled = true;
        }
    }

    void Refocused(FocusEvent args) {
        // Routed rather than a callback on the focused element, which is the only way a field five
        // levels down inside a panel gets scrolled to by something that knows nothing about it.
        if (Scrolls && args.Gained && args.Next is { } focused && !ReferenceEquals(focused, this)) {
            Reveal(focused);
        }
    }
}

/// <summary>One tab in a group's strip.</summary>
public sealed partial class DockTab : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "dock-tab";

    /// <summary>Which panel it stands for.</summary>
    public string PanelId { get; internal set; } = string.Empty;

    /// <summary>The button that closes the panel, if it may be closed.</summary>
    public IconButton? CloseButton { get; private set; }

    /// <summary>Gives it a close button.</summary>
    internal void AllowClosing() {
        CloseButton ??= Part<IconButton>();
        CloseButton.LeadingIcon.Geometry = ControlIcons.Close;
        CloseButton.Variant = ControlVariant.Subtle;
        CloseButton.Label = ControlStrings.DockClose.Text;
        CloseButton.TabIndex = -1;
    }
}

/// <summary>A group's tab strip and the panel it is showing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The strip scrolls, and it has to.</b> A group is how many panels somebody stacked
///         into one place, and that is unbounded — six tabs in a pane a quarter of the window wide is
///         an ordinary arrangement. Without somewhere for them to go, flexbox either shrinks every
///         tab until none of the titles can be read or pushes the last of them out of the box, and in
///         both cases the panels on the end are ones the user cannot get back to.
///     </para>
///     <para>
///         <b>Four elements, and the middle one is the only one that moves.</b> The strip is a row
///         holding a previous button, a clipping viewport, a next button; the viewport holds a list
///         that keeps its natural width and is slid sideways by <see cref="UiElement.OffsetX" />. The
///         clipping is <c>overflow: hidden</c> in the theme, which is the draw list's clip stack —
///         the same mechanism <c>ScrollView</c> uses, and deliberately not <c>ScrollView</c> itself,
///         because a tab strip with a scrollbar under it is two rows of chrome to save one.
///     </para>
/// </remarks>
public sealed partial class DockGroupView : Control {
    /// <summary>How much of the visible width one press of an arrow moves.</summary>
    /// <remarks>
    ///     Not all of it: a page that moved the full width would leave nothing on screen that was
    ///     there before, and the tab you were looking for is the one you have just scrolled past.
    /// </remarks>
    public const float PageFraction = 0.75f;

    /// <inheritdoc />
    protected override string TagName => "dock-group";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip along the top: the arrows and the tabs between them.</summary>
    public UiElement Strip { get; private set; } = null!;

    /// <summary>Where the tabs live. Inside <see cref="Strip" />, and what scrolls.</summary>
    public UiElement Tabs { get; private set; } = null!;

    /// <summary>The arrow that scrolls towards the first tab, shown only when there is one off-screen.</summary>
    public IconButton Previous { get; private set; } = null!;

    /// <summary>The arrow that scrolls towards the last.</summary>
    public IconButton Next { get; private set; } = null!;

    /// <summary>Where the panels live.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <summary>The arrangement node this is showing.</summary>
    public DockGroupNode? Node { get; internal set; }

    /// <summary>How far the tabs are scrolled, in pixels from the first one.</summary>
    public float ScrollLeft { get; private set; }

    /// <summary>How far they can be scrolled.</summary>
    public float MaximumScroll => MathF.Max(0f, Tabs.Width - Viewport.Width);

    /// <summary>Whether there are tabs the strip is not showing.</summary>
    public bool Overflows => MaximumScroll > 0.5f;

    /// <summary>The clipping box the tabs are slid inside.</summary>
    UiElement Viewport { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Strip = Part("dock-tabstrip");

        Previous = Strip.Add<IconButton>();
        Previous.LeadingIcon.Geometry = ControlIcons.ChevronLeft;
        Previous.Variant = ControlVariant.Subtle;
        Previous.Label = ControlStrings.DockPreviousTab.Text;
        Previous.TabIndex = -1;

        Viewport = Strip.Add("dock-tabs-viewport");
        Tabs = Viewport.Add("dock-tabs");

        Next = Strip.Add<IconButton>();
        Next.LeadingIcon.Geometry = ControlIcons.ChevronRight;
        Next.Variant = ControlVariant.Subtle;
        Next.Label = ControlStrings.DockNextTab.Text;
        Next.TabIndex = -1;

        Body = Part("dock-body");

        Previous.Clicked += _ => Scroll(-Viewport.Width * PageFraction);
        Next.Clicked += _ => Scroll(Viewport.Width * PageFraction);

        // ⚠ Subscribed to the pass directly rather than through `Control.WhenResized`, and it is the
        // case `WhenResized` documents as not being its own: whether the tabs fit depends on the
        // *tabs*, not on this. A panel added, closed or renamed changes the strip's content without
        // changing the group's box at all — so a refresh gated on this element's size would leave the
        // arrows saying what was true two panels ago.
        settle = _ => Refresh();
        Document.LayoutFinished += settle;
    }

    Action<UiDocument>? settle;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>And it must, because these are built and thrown away constantly.</b> Every structural
    ///     change rebuilds the views from the arrangement, so a group view that left a handler on the
    ///     document would leak one per dock, per drag, per rename — and every stale handler would go
    ///     on measuring an element that is no longer in the tree.
    /// </remarks>
    protected override void OnRemoved() {
        if (settle is not null) {
            Document.LayoutFinished -= settle;
            settle = null;
        }

        base.OnRemoved();
    }

    /// <summary>Scrolls the tabs by a distance, clamped to what there is.</summary>
    /// <param name="delta">How far, positive towards the last tab.</param>
    public void Scroll(float delta) => ScrollTo(ScrollLeft + delta);

    /// <summary>Scrolls the tabs to a position, clamped to what there is.</summary>
    /// <param name="offset">How far from the first tab.</param>
    public void ScrollTo(float offset) {
        ScrollLeft = Math.Clamp(offset, 0f, MaximumScroll);

        Tabs.OffsetX = -ScrollLeft;
        Update();
    }

    /// <summary>Scrolls until a tab is wholly visible, if it is not already.</summary>
    /// <param name="tab">The tab.</param>
    /// <remarks>
    ///     ⚠ <b>What makes a scrolling strip usable rather than merely possible.</b> Selecting a panel
    ///     from a menu, closing the tab in front of the one you wanted, or restoring a layout all put
    ///     the current tab wherever it happens to fall — and a strip that showed the selected panel's
    ///     body while its tab sat off the end reads as the selection having been lost.
    /// </remarks>
    public void Reveal(UiElement tab) {
        ArgumentNullException.ThrowIfNull(tab);

        if (!Overflows) {
            return;
        }

        var left = tab.AbsoluteLeft - Tabs.AbsoluteLeft;
        var right = left + tab.Width;

        if (left < ScrollLeft) {
            ScrollTo(left);
        } else if (right > ScrollLeft + Viewport.Width) {
            ScrollTo(right - Viewport.Width);
        }
    }

    /// <summary>Asks for a tab to be revealed once there are boxes to measure.</summary>
    /// <param name="tab">The tab, or <see langword="null" /> to forget a pending request.</param>
    /// <remarks>
    ///     What a rebuild uses: the tabs it has just created are all zero-sized until the pass that
    ///     follows it, so "scroll until the selected one is visible" cannot be answered yet. One
    ///     pending request rather than a queue — the only thing that ever asks is the rebuild, and the
    ///     only tab worth revealing is the one that ends up selected.
    /// </remarks>
    public void RevealAfterLayout(UiElement? tab) => pending = tab;

    UiElement? pending;

    /// <summary>Brings the arrows and the scroll offset up to date with the tabs.</summary>
    /// <remarks>
    ///     Public and idempotent for the same reason <c>ScrollView.Settle</c> is: a caller that has
    ///     just filled a strip and wants to read <see cref="Overflows" /> before the next pass has a
    ///     way to say so.
    /// </remarks>
    public void Refresh() {
        // ⚠ Clamped again here, not only when scrolled. Widening the pane or closing a tab shortens
        // the range, and an offset left past the end of it is a strip scrolled into empty space with
        // the arrows greyed out and no way back.
        ScrollTo(ScrollLeft);

        if (pending is not { } tab) {
            return;
        }

        // ⚠ Cleared before the reveal, not after. `Reveal` scrolls, a scroll is a change, and a
        // change runs the settle loop round again — so a request left in place would be honoured on
        // every pass and would fight anybody scrolling the strip by hand.
        pending = null;
        Reveal(tab);
    }

    void Update() {
        var overflows = Overflows;

        Toggle(Previous, overflows);
        Toggle(Next, overflows);

        // ⚠ Disabled rather than hidden at the ends. An arrow that vanished when it ran out would
        // move the other one and the whole strip sideways on every scroll, so the button under the
        // pointer would be a different button by the time it was pressed again.
        Previous.Disabled = ScrollLeft <= 0.5f;
        Next.Disabled = ScrollLeft >= MaximumScroll - 0.5f;
    }

    static void Toggle(UiElement element, bool shown) {
        if (shown) {
            element.RemoveClass("hidden");
        } else {
            element.AddClass("hidden");
        }
    }
}

/// <summary>The bar between two halves of a split, and the drag that moves it.</summary>
/// <remarks>
///     ⚠ <b>A drag writes two inline declarations and nothing else.</b> No rebuild, no reparent, no
///     new elements — the two halves are flex items and the ratio is their <c>flex-grow</c>, so
///     moving a splitter is a restyle of two elements and a relayout of what is inside them. A
///     docking host that rebuilt its tree on every mouse-move would be one nobody could drag.
/// </remarks>
public sealed partial class DockSplitterView : Control {
    bool dragging;

    /// <inheritdoc />
    protected override string TagName => "dock-splitter";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The split it divides.</summary>
    internal DockSplitNode? Node { get; set; }

    /// <summary>The two halves, which its drag resizes.</summary>
    internal UiElement? First { get; set; }

    /// <summary>Ditto.</summary>
    internal UiElement? Second { get; set; }

    /// <summary>Raised when a drag changes the ratio.</summary>
    internal event Action<DockSplitterView>? Moved;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        AddHandler<PointerEvent>(static (element, args) => ((DockSplitterView) element).Pointed(args));
    }

    /// <summary>Writes a ratio onto the two halves.</summary>
    /// <param name="first">The upper or left half.</param>
    /// <param name="second">The other one.</param>
    /// <param name="ratio">How much of the space the first takes.</param>
    /// <remarks>
    ///     ⚠ <b><c>flex-basis: 0px</c> on both, which is what makes the grow factors mean the ratio
    ///     they say.</b> With the default <c>auto</c> basis, flexbox distributes only the space left
    ///     over after the content has been measured — so two halves at 50/50 come out at whatever
    ///     their contents happened to want, plus half the remainder each, and a splitter dragged to
    ///     the middle does not land in the middle.
    /// </remarks>
    internal static void Apply(UiElement first, UiElement second, float ratio) {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        first.SetStyle("flex-grow", Fraction(ratio));
        first.SetStyle("flex-basis", "0px");

        second.SetStyle("flex-grow", Fraction(1f - ratio));
        second.SetStyle("flex-basis", "0px");
    }

    static string Fraction(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

    void Pointed(PointerEvent args) {
        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                dragging = true;
                Document.CapturePointer(this);

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
        if (Node is not { } node || First is not { } first || Second is not { } second || Parent is not { } split) {
            return;
        }

        var bounds = split.Bounds;
        var vertical = node.Orientation == Orientation.Vertical;

        // The splitter's own thickness is not available to either half, so the ratio is measured
        // against what the two of them share rather than against the whole split.
        var span = (vertical ? bounds.Height : bounds.Width) - (vertical ? Height : Width);
        if (span <= 0f) {
            return;
        }

        var along = vertical ? args.Y - bounds.Y : args.X - bounds.X;

        node.Ratio = Math.Clamp(along / span, DockSplitNode.MinimumRatio, 1f - DockSplitNode.MinimumRatio);
        Apply(first, second, node.Ratio);

        Moved?.Invoke(this);
    }
}

/// <summary>Panels arranged into splits and tab groups, dragged between them, and saved.</summary>
/// <remarks>
///     <para>
///         <b>Two things, kept apart on purpose.</b> <see cref="DockLayout" /> is the arrangement —
///         a tree of splits and groups that is saved, restored and compared; this is the elements
///         that show it. Every structural change edits the model and then rebuilds the views from
///         it, so "what is on screen" and "what would be saved" cannot drift apart.
///     </para>
///     <para>
///         ⚠ <b>Rebuilding the views does not rebuild the panels.</b> Before a rebuild every panel is
///         reparented into a hidden holder; afterwards each is reparented into its group. The
///         elements survive, which is the whole point — a panel that was rebuilt would lose its
///         scroll offset, its selection and its focus every time somebody dragged a splitter's
///         neighbour into another group.
///     </para>
///     <para>
///         <b>A splitter drag is the exception and does not rebuild anything</b>: it writes
///         <c>flex-grow</c> on two elements. Rebuilding at sixty hertz would work and would feel
///         like treacle.
///     </para>
/// </remarks>
public sealed partial class DockingHost : Control {
    readonly Dictionary<string, DockPanel> panels = new(StringComparer.Ordinal);
    readonly List<DockGroupView> groups = [];
    readonly List<UiElement> windows = [];

    DockTab? dragged;
    DockGroupNode? hovered;
    DockZone zone;

    /// <inheritdoc />
    protected override string TagName => "docking-host";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The arrangement being shown.</summary>
    public DockLayout Layout { get; private set; } = new();

    /// <summary>Where the docked tree is built.</summary>
    public UiElement Surface { get; private set; } = null!;

    /// <summary>Where panels wait while they are not placed.</summary>
    /// <remarks>
    ///     Hidden by the theme rather than detached from the document, because there is no such
    ///     thing as an element outside a document — one taken out is removed, and removal is final.
    ///     A parked panel is therefore a panel in a box nobody can see.
    /// </remarks>
    public UiElement Detached { get; private set; } = null!;

    /// <summary>The rectangle shown while a tab is being dragged.</summary>
    public UiElement Preview { get; private set; } = null!;

    /// <summary>The panels, by id.</summary>
    public IReadOnlyDictionary<string, DockPanel> Panels => panels;

    /// <summary>Raised after anything changes the arrangement.</summary>
    /// <remarks>The moment to save it. An application that persists layouts hangs off this.</remarks>
    public event Action<DockingHost>? LayoutChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Detached = Part("dock-detached");
        Surface = Part("dock-surface");
        Preview = Part("dock-preview");

        Preview.AddClass("hidden");

        // After the preview, because the tree order is the paint order and a guide drawn under the
        // rectangle it is offering would be a handle the user cannot see at the moment they need it.
        Guides = BuildGuides(this);

        AddHandler<ClickEvent>(static (element, args) => ((DockingHost) element).Chosen(args));
        AddHandler<DragEvent>(static (element, args) => ((DockingHost) element).Dragged(args));

        // ⚠ On the capture leg, so it runs before whatever was pressed does anything with the event
        // — including marking it handled, which a tree row and a button both do. Which panel the
        // user is working in is not something any of them should be able to swallow.
        AddHandler<PointerEvent>(
            static (element, args) => ((DockingHost) element).Pressed(args),
            RoutingStrategy.Capture,
            handledEventsToo: true
        );

        // Tab moves the focus without any pointer being involved, and the active panel has to follow
        // it or the border says one thing while the keyboard does another.
        AddHandler<FocusEvent>(static (element, args) => ((DockingHost) element).Focused(args));
    }

    /// <summary>Adds a panel, docking it if the arrangement does not already place it.</summary>
    /// <param name="id">What a saved layout calls it.</param>
    /// <param name="title">What its tab says.</param>
    /// <returns>The panel, whose children are the application's to fill.</returns>
    /// <remarks>
    ///     ⚠ <b>A panel the arrangement already knows about is left where the arrangement put it.</b>
    ///     That is what makes "load the layout, then register the panels" work, which is the order
    ///     every application does it in: the layout comes off disk before the code that builds the
    ///     panels has run.
    /// </remarks>
    public DockPanel AddPanel(string id, string? title = null) {
        ArgumentNullException.ThrowIfNull(id);

        if (panels.TryGetValue(id, out var existing)) {
            return existing;
        }

        // ⚠ Added to the *host* and not to `Detached`, which is where it used to go, so that this
        // method and a nested `<DockPanel Id="…" />` come through the same `OnChildAdded` — which is
        // what parks it, adopts it and hangs the title handler on it. Everything below is then two
        // property assignments, and the second of them is what registers the panel: see
        // `DockPanel.Id`.
        var panel = Add<DockPanel>();
        panel.Id = id;
        panel.Title = title ?? id;

        return panel;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A nested <c>&lt;DockPanel&gt;</c> is parked rather than left where it was written.</b>
    ///     A panel's place in the tree is the arrangement's to decide — <see cref="Rebuild" /> moves
    ///     every one of them into the group view it belongs to on every change — so a panel that
    ///     stayed a direct child of the host would be drawn beside the docked surface and outside
    ///     every group. <see cref="Detached" /> is where a panel waits, and waiting is what a panel
    ///     that has just been made is doing.
    ///     <para>
    ///         ⚠ <b>It does not register the panel, because it cannot: the id has not arrived yet.</b>
    ///         A tag is created and then assigned to, so this runs one line before
    ///         <c>Id="hierarchy"</c> does. Registration is the id's own business — see
    ///         <see cref="DockPanel.Id" /> — and a panel that never gets one stays parked and
    ///         invisible, which is the honest outcome for a panel with no name.
    ///     </para>
    /// </remarks>
    protected override void OnChildAdded(UiElement child) {
        base.OnChildAdded(child);

        if (child is not DockPanel panel) {
            return;
        }

        panel.Host = this;

        // ⚠ **A title change writes the tab's label; it does not rebuild the tree.** This used to
        // call `Rebuild`, which tears down every group view and every floating window and builds
        // them again — for a string. An editor's panel title carries its document's dirty marker, so
        // that was a full teardown on the keystroke that first made a scene dirty and another on the
        // save that made it clean.
        //
        // ⚠ It also has to be this way now rather than merely better. The handler is subscribed here
        // rather than after the title is first assigned, which is where `AddPanel` used to put it —
        // so a rebuild would report a layout change for the initial `Title = …` as well as for the
        // `Id = …` beside it, and an application that saves on `LayoutChanged` would write the
        // arrangement twice for every panel it adds. A test caught it.
        //
        // A panel with no tab yet — parked, or not named — finds nothing and does nothing, which is
        // exactly right: `Build` reads `Title` when it makes the tab.
        panel.TitleChanged += changed => {
            if (changed is DockPanel named) {
                Retitle(named);
            }
        };

        Document.Reparent(panel, Detached);

        // ⚠ **Only a panel that already has a name, and the guard is load-bearing rather than
        // defensive.** `Rekey` ends in `Rebuild`, which raises `LayoutChanged` — so registering a
        // nameless panel here and then again from the id's setter one line later reports two changes
        // for one `AddPanel`, and an application that saves on every change writes the arrangement
        // twice per panel. A test caught it. What is left for this call is the one order the id's
        // setter cannot cover: an element built by hand, given an id, and adopted afterwards.
        if (panel.Id.Length > 0) {
            Rekey(panel, string.Empty);
        }
    }

    /// <summary>Writes a panel's title onto the tab that shows it, if it has one.</summary>
    void Retitle(DockPanel panel) {
        if (panel.Id.Length == 0) {
            return;
        }

        foreach (var group in groups) {
            foreach (var child in group.Tabs.Children) {
                if (child is DockTab tab && string.Equals(tab.PanelId, panel.Id, StringComparison.Ordinal)) {
                    tab.Label = panel.Title;
                    return;
                }
            }
        }
    }

    /// <summary>Files a panel under its current id, taking it out from under its previous one.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="previous">What it was called before, or empty if it had no name.</param>
    internal void Rekey(DockPanel panel, string previous) {
        if (previous.Length > 0) {
            panels.Remove(previous);
            Layout.RemovePanel(previous);
        }

        if (panel.Id.Length == 0) {
            // Named and then un-named, which only `Id = ""` can do. The entry above is gone; there
            // is nothing to file it under, so the panel goes back to waiting in `Detached`.
            Rebuild();
            return;
        }

        panels[panel.Id] = panel;

        // ⚠ A panel the arrangement already places is left where the arrangement put it, which is
        // what makes "load the layout, then register the panels" work — and that is the order every
        // application does it in, because the layout comes off disk before the code that builds the
        // panels has run.
        if (Layout.Find(panel.Id) is null) {
            if (Layout.Groups() is [var first, ..]) {
                first.Add(panel.Id);
            } else {
                Layout.Root = new DockGroupNode(panel.Id);
            }
        }

        Rebuild();
    }

    /// <summary>Takes a panel out of the arrangement and out of the document.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemovePanel(string id) {
        ArgumentNullException.ThrowIfNull(id);

        if (!panels.Remove(id, out var panel)) {
            return false;
        }

        Layout.RemovePanel(id);

        // Parked before it is removed, so that removal takes one element rather than a group view
        // that a rebuild is about to replace anyway.
        if (!ReferenceEquals(panel.Parent, Detached)) {
            Document.Reparent(panel, Detached);
        }

        panel.Remove();
        Rebuild();

        return true;
    }

    /// <summary>Which panel the user last worked in, whether or not anything in it takes focus.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Focus alone is not enough, and this is the gap it leaves.</b> A tree row and a
    ///         text field take focus, so the outliner and the scene lit up when clicked; a console
    ///         row and an inspector's label do not, so those two panels never showed as focused
    ///         however many times they were clicked. A dozen identical panes where the border is
    ///         right for two of them is worse than no border at all — it is a signal that reads as
    ///         broken rather than as absent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A press outside the focused element's panel takes the focus with it.</b>
    ///         Otherwise clicking the console would leave the outliner still holding the keyboard,
    ///         and the next Delete would act on a panel the user had visibly left.
    ///     </para>
    /// </remarks>
    DockPanel? pressed;

    void Pressed(PointerEvent args) {
        if (args.Action != PointerAction.Pressed || PanelUnder(args.Source) is not { } panel) {
            return;
        }

        pressed = panel;

        if (Document.Focused is { } focus && !ReferenceEquals(PanelUnder(focus), panel)) {
            // Cleared rather than moved: what inside the new panel deserves the keyboard is that
            // panel's business, and whatever is pressed takes it on the bubble leg anyway.
            Document.Focus(null);
        }

        MarkActive();
    }

    void Focused(FocusEvent args) {
        if (args.Gained && PanelUnder(args.Next) is { } panel) {
            pressed = panel;
        }

        MarkActive();
    }

    /// <summary>The panel an element is inside, if it is inside one this host owns.</summary>
    DockPanel? PanelUnder(UiElement? element) {
        for (var walk = element; walk is not null; walk = walk.Parent) {
            if (walk is DockPanel panel && panels.ContainsKey(panel.Id)) {
                return panel;
            }
        }

        return null;
    }

    /// <summary>Puts the <c>active</c> class on the group holding <see cref="Active" /> and no other.</summary>
    /// <remarks>
    ///     A class rather than a state, because <c>:focus-within</c> already carries the meaning for
    ///     the panels that do take focus — a theme styles the two together and gets one rule.
    /// </remarks>
    internal void MarkActive() {
        // ⚠ The panel the user is actually in, not `Active`. That property falls back to the front
        // tab of the first group so that a command about "this panel" always has one — which is
        // right for a command and wrong for a border: it would light a panel up before anybody had
        // touched anything, saying the keyboard is somewhere it is not.
        var active = PanelUnder(Document.Focused) ?? (pressed is { IsRemoved: false } ? pressed : null);

        foreach (var view in groups) {
            var holds = active is not null && view.Node?.IndexOf(active.Id) >= 0;

            if (holds) {
                view.AddClass("active");
            } else {
                view.RemoveClass("active");
            }
        }
    }

    /// <summary>Moves a panel next to, or into, a group.</summary>
    /// <param name="id">The panel.</param>
    /// <param name="target">The group.</param>
    /// <param name="side">Which side of it, or its middle.</param>
    /// <param name="index">Where in the target's tab order, for a centre drop, or -1 for the end.</param>
    public void Dock(string id, DockGroupNode target, DockZone side, int index = -1) {
        Layout.Dock(id, target, side, index);
        Rebuild();
    }

    /// <summary>Takes a panel out of the docked tree into a window of its own.</summary>
    /// <param name="id">The panel.</param>
    /// <param name="x">Where the window goes — see <see cref="DockFloat" /> for which space.</param>
    /// <param name="y">Ditto.</param>
    /// <param name="width">How big it is.</param>
    /// <param name="height">Ditto.</param>
    /// <remarks>
    ///     A real operating-system window where <see cref="UiDocument.CanOpenWindows" /> says there
    ///     can be one, and a rectangle floating inside this host where there cannot. Callers do not
    ///     choose: a control that opened a window on a platform without them would be a control
    ///     nobody could ship in a browser.
    /// </remarks>
    public void Float(string id, float x, float y, float width = 320f, float height = 240f) {
        Layout.Float(id, x, y, width, height);
        Rebuild();
    }

    /// <summary>Shows a different arrangement, keeping the panels.</summary>
    /// <param name="layout">The arrangement.</param>
    /// <remarks>
    ///     What a named layout preset is, and what "reset to default" is. Panels the arrangement
    ///     does not mention end up in the first group rather than nowhere — an unplaced panel is a
    ///     panel the user cannot get back.
    /// </remarks>
    public void SetLayout(DockLayout layout) {
        ArgumentNullException.ThrowIfNull(layout);
        Layout = layout;

        foreach (var id in panels.Keys) {
            if (Layout.Find(id) is not null) {
                continue;
            }

            if (Layout.Groups() is [var first, ..]) {
                first.Add(id);
            } else {
                Layout.Root = new DockGroupNode(id);
            }
        }

        Rebuild();
    }

    /// <summary>The arrangement as YAML.</summary>
    /// <returns>The text.</returns>
    public string Save() => Layout.Save();

    /// <summary>Reads an arrangement back and shows it.</summary>
    /// <param name="yaml">The text.</param>
    public void Load(string yaml) => SetLayout(DockLayout.Load(yaml));

    /// <summary>The group views currently on screen, docked ones first.</summary>
    public IReadOnlyList<DockGroupView> Groups => groups;

    /// <summary>Builds the elements the arrangement describes.</summary>
    /// <remarks>
    ///     ⚠ <b>Park, tear down, build, replace.</b> Tearing down first would take the panels with
    ///     it — <c>Remove</c> is recursive and a panel is inside a group view — so every panel is
    ///     moved into the hidden holder before anything is removed, and moved back afterwards.
    /// </remarks>
    public void Rebuild() {
        foreach (var panel in panels.Values) {
            if (!ReferenceEquals(panel.Parent, Detached)) {
                Document.Reparent(panel, Detached);
            }
        }

        while (Surface.Children.Count > 0) {
            Surface.Children[^1].Remove();
        }

        foreach (var window in windows) {
            window.Remove();
        }

        windows.Clear();
        groups.Clear();

        // ⚠ After the panels are parked and before anything is built. A window closing takes its
        // surface and everything left in it; a panel that was still inside one would be destroyed
        // rather than docked back.
        Retire();

        if (Layout.Root is { } root) {
            var view = Build(root, Surface);

            // ⚠ Both, and for the reason `DockSplitterView.Apply` gives for writing the same pair:
            // with the default `auto` basis a growing item starts at its content's height and is
            // never asked to shrink, so the whole arrangement came out as tall as its tallest panel's
            // content inside a surface that was correctly the size of the window. Nothing clipped,
            // and no panel could tell that it had more content than room.
            view.SetStyle("flex-grow", "1");
            view.SetStyle("flex-basis", "0px");
        }

        for (var i = 0; i < Layout.Floating.Count; i++) {
            var floated = Layout.Floating[i];

            // A real window where the platform has them, a rectangle inside this host where it does
            // not. Same arrangement, same file, same panels — the difference is one the browser and
            // the phone impose and the code above this line never sees.
            if (Torn(floated) is { } window) {
                BuildTorn(window);
            } else {
                BuildFloating(floated, i);
            }
        }

        // ⚠ The views are new, so the class is not on any of them. A rebuild is what every dock,
        // float and layout change ends with — an active border that survived the arrangement it was
        // drawn against would be a border on whichever group happened to be built in that slot.
        MarkActive();

        LayoutChanged?.Invoke(this);
    }

    UiElement Build(DockNode node, UiElement parent) =>
        node switch {
            DockSplitNode split => BuildSplit(split, parent),
            DockGroupNode group => BuildGroup(group, parent),
            _ => parent.Add("dock-empty")
        };

    UiElement BuildSplit(DockSplitNode node, UiElement parent) {
        var split = parent.Add("dock-split", null, node.Orientation == Orientation.Vertical ? "vertical" : "horizontal");

        var first = Build(node.First, split);
        var splitter = split.Add<DockSplitterView>();
        var second = Build(node.Second, split);

        splitter.Node = node;
        splitter.First = first;
        splitter.Second = second;
        splitter.Moved += _ => LayoutChanged?.Invoke(this);

        DockSplitterView.Apply(first, second, node.Ratio);
        return split;
    }

    DockGroupView BuildGroup(DockGroupNode node, UiElement parent) {
        var view = parent.Add<DockGroupView>();
        view.Node = node;

        groups.Add(view);

        for (var i = 0; i < node.Panels.Count; i++) {
            var id = node.Panels[i];

            if (!panels.TryGetValue(id, out var panel)) {
                // The arrangement names a panel nothing has registered. It is not an error — a saved
                // layout outlives the code that made the panels, and the tab appears the moment
                // `AddPanel` is called with that id.
                continue;
            }

            var tab = view.Tabs.Add<DockTab>();
            tab.PanelId = id;
            tab.Label = panel.Title;

            if (panel.CanClose) {
                tab.AllowClosing();
            }

            Document.Reparent(panel, view.Body);

            if (i == node.Selected) {
                tab.State |= ElementState.Checked;
                panel.AddClass("selected");

                // ⚠ Asked for rather than done, because a tab that has just been created has no box
                // to measure against yet — `Reveal` reads widths and offsets, and every one of them
                // is zero until the pass that follows this rebuild.
                view.RevealAfterLayout(tab);
            } else {
                panel.RemoveClass("selected");
            }
        }

        return view;
    }

    void BuildFloating(DockFloat window, int index) {
        var view = Add<UiElement>("dock-float");

        view.SetStyle("width", Pixels(window.Width));
        view.SetStyle("height", Pixels(window.Height));
        view.OffsetX = window.X;
        view.OffsetY = window.Y;

        var group = BuildGroup(window.Group, view);

        group.SetStyle("flex-grow", "1");
        group.SetStyle("flex-basis", "0px");

        windows.Add(view);

        // The index is what a drag would write back through `SetFloating`, and it is carried on the
        // element rather than in a parallel list so that removing a window cannot leave it stale.
        view.SetStyle("--dock-window", index.ToString(CultureInfo.InvariantCulture));
    }

    static string Pixels(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    /// <summary>Whether a point is inside a rectangle, half-open the way hit testing is.</summary>
    static bool Inside(Rectangle bounds, float x, float y) =>
        x >= bounds.X && y >= bounds.Y && x < bounds.X + bounds.Width && y < bounds.Y + bounds.Height;

    void Chosen(ClickEvent args) {
        switch (args.Source) {
            case IconButton close when close.Parent is DockTab tab:
                RemovePanel(tab.PanelId);
                args.Handled = true;

                break;

            case DockTab tab when Layout.Find(tab.PanelId) is var (group, index):
                group.Selected = index;

                Rebuild();
                args.Handled = true;

                break;

            default:
                break;
        }
    }

    /// <summary>Drags a tab, showing where it would land and putting it there.</summary>
    /// <remarks>
    ///     ⚠ <b>The preview is what makes docking usable, and it is not decoration.</b> Five zones
    ///     over every group in the window is a target the user cannot see, and a drop that lands
    ///     somewhere unexpected is worse than no docking at all — so the rectangle shows the zone
    ///     the release would actually use, computed by the same code that will act on it.
    /// </remarks>
    void Dragged(DragEvent args) {
        switch (args.Stage) {
            case DragStage.Started when TabOf(args.Source) is { } tab:
                dragged = tab;
                break;

            case DragStage.Moved when dragged is not null:
                Track(args.X, args.Y);
                break;

            case DragStage.Completed when dragged is { } tab:
                Drop(tab);
                break;

            case DragStage.Cancelled when dragged is not null:
                Cancel();
                break;

            default:
                break;
        }
    }

    /// <summary>Which tab a drag that started on an element is a drag of.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The element a press lands on is the deepest one under the pointer, and on a tab
    ///         that is almost never the tab.</b> A tab's title is a child element — it has to be, or
    ///         a tab could not also have an icon — so a drag begun anywhere on the words reported the
    ///         label, a test for the tab itself failed, and the only part of a tab that could be
    ///         picked up was the few pixels of padding around the text. Users find that and report it
    ///         as docking being broken, which it effectively was.
    ///     </para>
    ///     <para>
    ///         <b>The close button is the exception and stops the walk.</b> It is inside the tab, so
    ///         an ancestor search that did not stop would make the button a drag handle — and a press
    ///         on it that wandered a few pixels before letting go would dock the panel somewhere
    ///         instead of doing the one thing its icon promises.
    ///     </para>
    /// </remarks>
    static DockTab? TabOf(UiElement? source) {
        for (var walk = source; walk is not null; walk = walk.Parent) {
            if (walk is DockTab tab) {
                return tab;
            }

            if (walk is IconButton) {
                return null;
            }
        }

        return null;
    }

    /// <summary>Follows a tab drag, in desktop space, across every window this host has.</summary>
    /// <remarks>
    ///     ⚠ <b>Desktop space rather than the document's, because two windows have two coordinate
    ///     spaces and a drag between them has to be in one.</b> The pointer's position arrives in
    ///     whichever surface last received it — which during a capture is the window the drag started
    ///     in, wherever the cursor has since wandered — and every group's rectangle is in its own.
    ///     Both are lifted through <see cref="Locate" /> to the one space they share. With a single
    ///     window that lift is the identity, so this is exactly what it was.
    /// </remarks>
    void Track(float x, float y) {
        hovered = null;

        var source = Document.PointerSurface ?? Document.Primary;

        if (!Locate(source, out var origin)) {
            Hide();
            return;
        }

        pointer = new Vector2(origin.X + x, origin.Y + y);

        foreach (var view in groups) {
            if (view.Node is not { } node || !Desktop(view, out var bounds) || !Inside(bounds, pointer.X, pointer.Y)) {
                continue;
            }

            hovered = node;
            zone = ZoneAt(bounds, pointer.X, pointer.Y);
            index = zone == DockZone.Center ? InsertionAt(view, x, y) : -1;

            Show(view, zone);
            return;
        }

        Hide();
    }

    /// <summary>Where the drag is now, in desktop space.</summary>
    Vector2 pointer;

    /// <summary>Which place in the target's tab order a centre drop would take, or -1 for the end.</summary>
    int index = -1;

    /// <summary>Where in a group's strip a drop at a point belongs.</summary>
    /// <param name="view">The group under the pointer.</param>
    /// <param name="x">Where, in the *document's* space — the strip's rectangles are in it too.</param>
    /// <param name="y">Ditto.</param>
    /// <returns>The index to insert at, or -1 for the end.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What makes a stack re-orderable, and it has to come from the pointer.</b> Dropping
    ///         onto a group's centre appended, so the only reordering anybody could perform was
    ///         "send this to the end" — and dragging a tab two places to the left did nothing at all,
    ///         which reads as the tabs not being draggable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Before or after the tab under the pointer, decided by its midpoint.</b> Nearest-
    ///         edge rather than "the tab you are over": dropping on the right half of the second tab
    ///         plainly means third, and a rule that said second would make every leftward drag
    ///         overshoot and every rightward one fall short.
    ///     </para>
    ///     <para>
    ///         A pointer that is over the body rather than the strip gets -1, which is the end. That
    ///         is the honest answer for a gesture that said nothing about order.
    ///     </para>
    /// </remarks>
    static int InsertionAt(DockGroupView view, float x, float y) {
        var strip = view.Tabs;

        if (!Inside(strip.Bounds, x, y)) {
            return -1;
        }

        var place = 0;

        foreach (var child in strip.Children) {
            if (child is not DockTab tab) {
                continue;
            }

            if (x < tab.Bounds.X + (tab.Bounds.Width * 0.5f)) {
                return place;
            }

            place++;
        }

        return -1;
    }

    /// <summary>Takes the drop preview and the guides off every window.</summary>
    void Hide() {
        Preview.AddClass("hidden");
        Guides.AddClass("hidden");

        foreach (var entry in torn) {
            entry.Preview?.AddClass("hidden");
            entry.Guides?.AddClass("hidden");
        }
    }

    /// <summary>Which zone of a rectangle a point is in.</summary>
    /// <remarks>
    ///     ⚠ <b>The nearest edge wins, rather than the first test that passes.</b> A point in a
    ///     corner is within the margin of two edges, and a chain of <c>if</c>s would always give it
    ///     to whichever was written first — so dragging into the top-left corner of a panel would
    ///     dock left every time however clearly the pointer was aiming up.
    /// </remarks>
    internal static DockZone ZoneOf(Rectangle bounds, float x, float y, float margin = 0.25f) {
        if (bounds.Width <= 0f || bounds.Height <= 0f) {
            return DockZone.Center;
        }

        var horizontal = (x - bounds.X) / bounds.Width;
        var vertical = (y - bounds.Y) / bounds.Height;

        var distances = new[] { horizontal, 1f - horizontal, vertical, 1f - vertical };
        var zones = new[] { DockZone.Left, DockZone.Right, DockZone.Top, DockZone.Bottom };

        var best = DockZone.Center;
        var nearest = margin;

        for (var i = 0; i < distances.Length; i++) {
            if (distances[i] < nearest) {
                nearest = distances[i];
                best = zones[i];
            }
        }

        return best;
    }

    /// <summary>Draws the drop rectangle and the guides in the window the group being hovered is in.</summary>
    /// <remarks>
    ///     ⚠ Both overlays belong to a window and the geometry arrives in desktop space, so it is
    ///     brought back down into the surface the group lives in. A preview positioned from desktop
    ///     coordinates would draw the drop target for a torn-off inspector several hundred pixels
    ///     outside its own window.
    /// </remarks>
    void Show(DockGroupView view, DockZone side) {
        var bounds = view.Bounds;

        var half = side is DockZone.Center
            ? bounds
            : side switch {
                DockZone.Left => new Rectangle(bounds.X, bounds.Y, bounds.Width * 0.5f, bounds.Height),
                DockZone.Right => new Rectangle(bounds.X + (bounds.Width * 0.5f), bounds.Y, bounds.Width * 0.5f, bounds.Height),
                DockZone.Top => new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height * 0.5f),
                _ => new Rectangle(bounds.X, bounds.Y + (bounds.Height * 0.5f), bounds.Width, bounds.Height * 0.5f)
            };

        Hide();

        var (preview, guides) = OverlaysFor(view);

        if (preview is not null) {
            preview.RemoveClass("hidden");
            Place(preview, half);
        }

        if (guides is not null) {
            Guide(guides, bounds, side);
        }
    }

    /// <summary>Puts an overlay over a rectangle given in its own surface's coordinates.</summary>
    /// <remarks>
    ///     The overlay is a child of the host, or of a torn-off window's root, so the offset is
    ///     measured from where layout put it — which is wherever a zero-sized absolutely positioned
    ///     element lands.
    /// </remarks>
    static void Place(UiElement element, Rectangle bounds) {
        element.SetStyle("width", Pixels(bounds.Width));
        element.SetStyle("height", Pixels(bounds.Height));

        element.OffsetX += bounds.X - element.AbsoluteLeft;
        element.OffsetY += bounds.Y - element.AbsoluteTop;
    }

    /// <summary>The preview and the guides belonging to the window a group view is in.</summary>
    (UiElement? Preview, UiElement? Guides) OverlaysFor(DockGroupView view) {
        var surface = Document.SurfaceOf(view);

        foreach (var entry in torn) {
            if (ReferenceEquals(entry.Window.Surface, surface)) {
                return (entry.Preview, entry.Guides);
            }
        }

        return (Preview, Guides);
    }

    /// <summary>How far above and to the left of the cursor a torn-off window's corner lands.</summary>
    /// <remarks>
    ///     So that the tab stays roughly under the pointer that is holding it rather than the window
    ///     appearing with its corner there — which reads as the panel jumping away at the moment of
    ///     release.
    /// </remarks>
    const float TearGrip = 48f;

    /// <summary>How big a panel torn out onto the desktop starts.</summary>
    const float TearWidth = 420f;

    /// <inheritdoc cref="TearWidth" />
    const float TearHeight = 320f;

    void Drop(DockTab tab) {
        var target = hovered;
        var side = zone;
        var place = index;
        var where = pointer;

        Cancel();

        if (target is not null) {
            Dock(tab.PanelId, target, side, place);
            return;
        }

        // ⚠ Outside every group *and* outside this host is what a tear-out is, and requiring both is
        // deliberate. The gaps inside the arrangement are the splitters, six pixels wide, and a drop
        // on one of those is a miss rather than a request for a new window — floating the panel
        // there would make a fumbled drag cost the user their layout.
        if (!Desktop(this, out var host) || Inside(host, where.X, where.Y)) {
            return;
        }

        Float(tab.PanelId, where.X - TearGrip, where.Y - (TearGrip * 0.25f), TearWidth, TearHeight);
    }

    void Cancel() {
        dragged = null;
        hovered = null;
        index = -1;

        Hide();
    }
}
