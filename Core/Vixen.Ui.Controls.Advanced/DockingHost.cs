// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>One dockable panel: a title, an id, and whatever the application puts in it.</summary>
/// <remarks>
///     ⚠ <b>A panel is created once and moved thereafter, never rebuilt.</b> That is what
///     <c>UiDocument.Reparent</c> exists for and it is the whole reason docking is hard: a panel torn
///     out of one group and dropped into another has to keep its scroll position, its selection, its
///     focus and whatever the user has half-typed into it. A host that rebuilt the panel would pass
///     every structural test and lose the user's work.
/// </remarks>
public sealed partial class DockPanel : Control {
    /// <inheritdoc />
    protected override string TagName => "dock-panel";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>What identifies it in a saved layout.</summary>
    /// <remarks>
    ///     Assigned by <see cref="DockingHost.AddPanel" /> and not afterwards: it is the name a
    ///     serialised arrangement refers to the panel by, so changing it is renaming something two
    ///     files already agree about.
    /// </remarks>
    public string Id { get; internal set; } = string.Empty;

    /// <summary>What its tab says.</summary>
    [UiProperty(Changed = nameof(OnTitleChanged))]
    public partial string? Title { get; set; }

    /// <summary>Whether it may be closed.</summary>
    [UiProperty(Default = true)]
    public partial bool CanClose { get; set; }

    /// <summary>Raised when the title changes, so the tab showing it can follow.</summary>
    internal event Action<DockPanel>? TitleChanged;

    void OnTitleChanged(string? previous, string? current) => TitleChanged?.Invoke(this);
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
        CloseButton.Label = "Close";
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
        Previous.Label = "Previous tab";
        Previous.TabIndex = -1;

        Viewport = Strip.Add("dock-tabs-viewport");
        Tabs = Viewport.Add("dock-tabs");

        Next = Strip.Add<IconButton>();
        Next.LeadingIcon.Geometry = ControlIcons.ChevronRight;
        Next.Variant = ControlVariant.Subtle;
        Next.Label = "Next tab";
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

        var panel = Detached.Add<DockPanel>();
        panel.Id = id;
        panel.Title = title ?? id;
        panel.TitleChanged += _ => Rebuild();

        panels[id] = panel;

        if (Layout.Find(id) is null) {
            if (Layout.Groups() is [var first, ..]) {
                first.Add(id);
            } else {
                Layout.Root = new DockGroupNode(id);
            }
        }

        Rebuild();
        return panel;
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

    /// <summary>Moves a panel next to, or into, a group.</summary>
    /// <param name="id">The panel.</param>
    /// <param name="target">The group.</param>
    /// <param name="side">Which side of it, or its middle.</param>
    public void Dock(string id, DockGroupNode target, DockZone side) {
        Layout.Dock(id, target, side);
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
            view.SetStyle("flex-grow", "1");
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

        BuildGroup(window.Group, view).SetStyle("flex-grow", "1");
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

            Show(view, zone);
            return;
        }

        Hide();
    }

    /// <summary>Where the drag is now, in desktop space.</summary>
    Vector2 pointer;

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
        var where = pointer;

        Cancel();

        if (target is not null) {
            Dock(tab.PanelId, target, side);
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

        Hide();
    }
}
