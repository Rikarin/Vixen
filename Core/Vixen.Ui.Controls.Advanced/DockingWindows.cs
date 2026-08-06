// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls.Advanced;

public sealed partial class DockingHost {
    /// <summary>A floating group that got a real window instead of a rectangle.</summary>
    /// <remarks>
    ///     ⚠ <b>Keyed on the group object rather than on its index in <c>Layout.Floating</c>.</b> A
    ///     rebuild happens on every structural change and the index moves under it, so an index-keyed
    ///     window would be closed and reopened — a window blinking off and on again — the first time
    ///     the user docked something somewhere else.
    /// </remarks>
    sealed class TornWindow {
        public TornWindow(DockGroupNode group, IUiWindow window) {
            Group = group;
            Window = window;
        }

        /// <summary>Which floating group it shows.</summary>
        public DockGroupNode Group { get; }

        /// <summary>The window itself.</summary>
        public IUiWindow Window { get; }

        /// <summary>The rectangle a drop over this window is previewed with.</summary>
        /// <remarks>
        ///     One per window, because a preview is an element and an element is in exactly one
        ///     window. A single shared one would draw the drop target for the torn-off inspector in
        ///     the main window's top-left corner.
        /// </remarks>
        public UiElement? Preview { get; set; }

        /// <summary>The guide handles offered over the groups in this window.</summary>
        /// <inheritdoc cref="Preview" />
        public UiElement? Guides { get; set; }
    }

    readonly List<TornWindow> torn = [];

    /// <summary>The floating groups that have a window of their own.</summary>
    /// <remarks>
    ///     Empty when <see cref="UiDocument.CanOpenWindows" /> is false — a browser tab, an Android
    ///     activity, iOS — in which case every floating group is a rectangle inside this host
    ///     instead. Exposed because "did it get a real window" is the question a test asks and the
    ///     question a user's bug report is about.
    /// </remarks>
    public int TornWindowCount => torn.Count;

    /// <summary>Whether this host will give a floating group a window of its own.</summary>
    public bool CanTearOut => Document.CanOpenWindows;

    /// <summary>The panel a command about "this panel" means.</summary>
    /// <remarks>
    ///     The one the focus is in, and the front tab of the first group when the focus is nowhere.
    ///     A menu item that acted on the first panel in the arrangement regardless would be one that
    ///     did something surprising every time somebody used it from the keyboard.
    /// </remarks>
    public DockPanel? Active {
        get {
            for (var walk = Document.Focused; walk is not null; walk = walk.Parent) {
                if (walk is DockPanel focused && panels.ContainsKey(focused.Id)) {
                    return focused;
                }
            }

            // ⚠ Then the panel last pressed in, which is the answer for every panel whose contents
            // do not take focus — a console row, an inspector's label. Without it those panels could
            // be clicked all day and the editor would still say the outliner was the active one.
            if (pressed is { IsRemoved: false } clicked && panels.ContainsKey(clicked.Id)) {
                return clicked;
            }

            foreach (var group in Layout.Groups()) {
                var index = Math.Clamp(group.Selected, 0, Math.Max(0, group.Panels.Count - 1));

                if (group.Panels.Count > 0 && panels.TryGetValue(group.Panels[index], out var panel)) {
                    return panel;
                }
            }

            return null;
        }
    }

    /// <summary>Takes a panel out into a window of its own, where it already is on screen.</summary>
    /// <param name="id">The panel.</param>
    /// <returns>Whether there is a panel by that id.</returns>
    /// <remarks>
    ///     ⚠ <b>Over exactly where the panel was, which is the whole reason this overload exists.</b>
    ///     A window that appeared at a fixed offset, or at the pointer, is a panel that visibly jumps
    ///     somewhere else at the moment it is undocked — and the user then has to find it again to
    ///     work out whether the command did what they meant. Placed over its own rectangle, the only
    ///     thing that changes is that it now has a frame.
    /// </remarks>
    public bool Float(string id) {
        ArgumentNullException.ThrowIfNull(id);

        if (!panels.TryGetValue(id, out var panel)) {
            return false;
        }

        if (Desktop(panel, out var bounds) && bounds.Width > 0f && bounds.Height > 0f) {
            Float(id, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        } else {
            // A panel that has never been laid out, or a host that cannot say where its window is.
            // Somewhere is better than nowhere, and the user can move it.
            Float(id, 0f, 0f, TearWidth, TearHeight);
        }

        return true;
    }

    /// <summary>Finds or opens the window a floating group belongs in.</summary>
    /// <returns>The entry, or <c>null</c> when this document has no way to open a window.</returns>
    TornWindow? Torn(DockFloat floated) {
        foreach (var entry in torn) {
            if (ReferenceEquals(entry.Group, floated.Group)) {
                return entry;
            }
        }

        if (Document.Windows is not { CanOpen: true } host) {
            return null;
        }

        // ⚠ The saved bounds are used *here* and never written back to the window afterwards. Once
        // it is open the window is the authority on where it is — the same rule `IWindow` states —
        // and a rebuild that pushed the layout's numbers back would fight every drag the user makes:
        // the move writes the layout, the layout is rebuilt, the rebuild moves the window.
        var request = new UiWindowRequest(TitleOf(floated.Group), floated.X, floated.Y, floated.Width, floated.Height) {
            // ⚠ Not decoration. A routed event climbs the element tree, so a window whose surface
            // hung off the document root would put every tab in it out of this host's reach — the
            // click that selects a tab, the one that closes a panel and the drag that moves it all
            // arrive here by bubbling, and all three would stop one element short.
            Owner = this
        };

        if (host.Open(Document, request) is not { } opened) {
            return null;
        }

        var torned = new TornWindow(floated.Group, opened);

        opened.CloseRequested += _ => Undock(torned);
        opened.Moved += _ => Relocate(torned);

        torn.Add(torned);
        return torned;
    }

    /// <summary>Builds a floating group into the window it was given.</summary>
    void BuildTorn(TornWindow entry) {
        var root = entry.Window.Surface.Root;

        while (root.Children.Count > 0) {
            root.Children[^1].Remove();
        }

        var group = BuildGroup(entry.Group, root);

        // ⚠ The basis travels with the grow factor wherever one is written — see `DockingHost.Rebuild`.
        // A torn-off window is the case where forgetting it is least visible and worst: the window has
        // a size the operating system gave it, and a group that sized itself to its content instead
        // would put the panel's bottom somewhere past the window's.
        group.SetStyle("flex-grow", "1");
        group.SetStyle("flex-basis", "0px");

        entry.Preview = root.Add("dock-preview");
        entry.Preview.AddClass("hidden");

        entry.Guides = BuildGuides(root);
    }

    /// <summary>Closes the windows whose group has left the arrangement.</summary>
    /// <remarks>
    ///     ⚠ <b>After the panels have been parked and before anything is built.</b> Disposing a
    ///     window takes its surface, and a surface goes with everything still inside it — so a panel
    ///     that had not been moved into the holder first would be destroyed by the window closing
    ///     rather than docked back into the main one.
    /// </remarks>
    void Retire() {
        for (var i = torn.Count - 1; i >= 0; i--) {
            if (Layout.IndexOfFloating(torn[i].Group) >= 0) {
                continue;
            }

            var entry = torn[i];
            torn.RemoveAt(i);

            entry.Preview = null;
            entry.Guides = null;
            entry.Window.Dispose();
        }
    }

    /// <summary>Puts a closing window's panels back into the docked tree.</summary>
    /// <remarks>
    ///     ⚠ <b>Closing a torn-off window is not closing the panels in it.</b> Every docking system
    ///     that made it so grew a bug report titled "I lost my inspector" — the window's close button
    ///     is a foot away from the panel's, and one of them destroys work while the other rearranges
    ///     it. So the panels come home and the user closes them individually if that is what they
    ///     meant.
    /// </remarks>
    void Undock(TornWindow entry) {
        var index = Layout.IndexOfFloating(entry.Group);

        if (index < 0) {
            return;
        }

        var ids = entry.Group.Panels.ToArray();
        Layout.RemoveFloating(index);

        foreach (var id in ids) {
            if (Layout.DockedGroups() is [var first, ..]) {
                first.Add(id);
            } else {
                Layout.Root = new DockGroupNode(id);
            }
        }

        // The group is no longer floating, so the rebuild's `Retire` is what disposes the window —
        // one place that closes windows rather than two that have to agree.
        Rebuild();
    }

    /// <summary>Writes a window's new position and size back into the arrangement.</summary>
    void Relocate(TornWindow entry) {
        var index = Layout.IndexOfFloating(entry.Group);

        if (index < 0) {
            return;
        }

        var (x, y, width, height) = entry.Window.Bounds;
        var current = Layout.Floating[index];

        if (Same(current.X, x) && Same(current.Y, y) && Same(current.Width, width) && Same(current.Height, height)) {
            return;
        }

        Layout.SetFloating(index, current with { X = x, Y = y, Width = width, Height = height });

        // Not a rebuild. Nothing about the arrangement's *structure* changed, and rebuilding on
        // every pixel of a window drag would reparent every panel in it sixty times a second.
        LayoutChanged?.Invoke(this);
    }

    /// <summary>Closes every window this host opened.</summary>
    /// <remarks>
    ///     ⚠ <b>A host taken out of the document takes its windows with it.</b> Their surfaces are
    ///     inside it — they are children of this element — so removal would destroy the contents
    ///     either way; what it would not do is close the operating-system windows, and an editor that
    ///     swapped its shell would be left with two empty frames on the desktop that nothing owns.
    /// </remarks>
    protected override void OnRemoved() {
        for (var i = torn.Count - 1; i >= 0; i--) {
            torn[i].Preview = null;
            torn[i].Guides = null;
            torn[i].Window.Dispose();
        }

        torn.Clear();
        base.OnRemoved();
    }

    static bool Same(float a, float b) => MathF.Abs(a - b) < 0.5f;

    /// <summary>What a torn-off window's title bar says.</summary>
    /// <remarks>
    ///     The selected panel's title, because that is what the window is showing. A group of three
    ///     named after all three would be a title bar nobody can read, and one named "Vixen" would
    ///     be four identical entries in the window switcher.
    /// </remarks>
    string TitleOf(DockGroupNode group) {
        var index = Math.Clamp(group.Selected, 0, Math.Max(0, group.Panels.Count - 1));

        return group.Panels.Count > 0 && panels.TryGetValue(group.Panels[index], out var panel)
            ? panel.Title ?? group.Panels[index]
            : "Panel";
    }

    /// <summary>Where a surface's top-left corner is on the desktop.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero for a document with one surface, and that is what keeps the single-window case
    ///     exactly what it was.</b> With one window "desktop space" and "document space" differ by a
    ///     constant that nothing measures, so not asking the host is both cheaper and the only
    ///     answer available when there is no host at all.
    /// </remarks>
    bool Locate(UiSurface surface, out Vector2 origin) {
        origin = default;

        if (Document.Windows is { } host && host.TryLocate(surface, out var x, out var y)) {
            origin = new Vector2(x, y);
            return true;
        }

        // With one surface there is no second space for anything to be in, so "the desktop" and
        // "the document" differ by a constant that nothing here measures — and not asking is both
        // cheaper and the only answer available when there is no host at all. With more than one it
        // is not knowable, and saying so is what makes docking degrade to working inside each window
        // rather than dropping panels at coordinates it invented.
        return Document.Surfaces.Count <= 1;
    }

    /// <summary>The rectangle an element occupies in desktop space.</summary>
    bool Desktop(UiElement element, out Rectangle bounds) {
        bounds = default;

        if (Document.SurfaceOf(element) is not { } surface || !Locate(surface, out var origin)) {
            return false;
        }

        var box = element.Bounds;
        bounds = new Rectangle(box.X + origin.X, box.Y + origin.Y, box.Width, box.Height);

        return true;
    }
}
