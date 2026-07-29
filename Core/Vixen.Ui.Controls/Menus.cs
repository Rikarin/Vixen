// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>One line of a menu.</summary>
/// <remarks>
///     A button, because that is what it is: it takes the focus, it answers to Enter and Space, and
///     it raises a click. What it has on top is a place for a shortcut on the right and, if it opens
///     one, a submenu.
/// </remarks>
public sealed partial class MenuItem : ButtonBase {
    Icon? icon;
    Menu? submenu;

    /// <inheritdoc />
    protected override string TagName => "menu-item";

    /// <summary>The submenu this opens, if it has one.</summary>
    /// <remarks>
    ///     ⚠ <b>Setting it puts the arrow on, which is why this is not an automatic property.</b> A
    ///     line that opens a menu and a line that runs a command are the same shape otherwise, and a
    ///     user has no way to tell which one they are about to commit to — so the one affordance that
    ///     distinguishes them cannot be something a caller has to remember to ask for.
    /// </remarks>
    public Menu? Submenu {
        get => submenu;

        internal set {
            submenu = value;

            if (value is not null) {
                _ = Arrow;
            }
        }
    }

    /// <summary>The arrow shown on the right when the item opens a submenu.</summary>
    /// <remarks>
    ///     Created on demand like <see cref="Mark" />, because most items are commands and an element
    ///     per line that is never drawn is an element per line all the same. It is pushed to the right
    ///     by the theme, the same way a shortcut is — the two never appear together, since a submenu
    ///     is not a command and has nothing to bind.
    /// </remarks>
    public Icon Arrow => arrow ??= Append();

    Icon? arrow;

    /// <summary>The shortcut shown on the right, if any.</summary>
    public KeyboardShortcut? Shortcut { get; private set; }

    /// <summary>The tick shown when the item is a checkable one that is on.</summary>
    public Icon Mark => icon ??= Prepend();

    /// <summary>Shows a key combination against the item.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">What is held with it.</param>
    /// <returns>The label, in case a caller wants to restyle it.</returns>
    /// <remarks>
    ///     ⚠ <b>It shows the shortcut and does not bind it.</b> A menu that registered hotkeys as a
    ///     side effect of being built would bind them again every time it was rebuilt, and would
    ///     unbind nothing when it was thrown away. Binding belongs to <c>Vixen.Input</c>, where an
    ///     action outlives the menu that displays it.
    /// </remarks>
    public KeyboardShortcut ShowShortcut(InputKey key, ModifierKeys modifiers = ModifierKeys.None) {
        Shortcut ??= Part<KeyboardShortcut>();
        Shortcut.Modifiers = modifiers;
        Shortcut.Key = key;

        return Shortcut;
    }

    Icon Prepend() {
        var mark = Part<Icon>();
        mark.Geometry = ControlIcons.Check;

        Document.Move(mark, 0);
        return mark;
    }

    Icon Append() {
        var mark = Part<Icon>(null, "submenu");
        mark.Geometry = ControlIcons.ChevronRight;

        return mark;
    }
}

/// <summary>A list of commands, floating over everything.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Arrows move the focus and do not activate</b> — the opposite of a tab strip and a
///         radio group, and for the opposite reason. Those are one tab stop whose members are
///         alternatives, so moving has to choose; a menu is a list of <i>commands</i>, and a menu
///         that ran each one as the highlight passed over it would be unusable. Enter runs the
///         highlighted one.
///     </para>
///     <para>
///         <b>Activating an item closes the whole chain.</b> A submenu three levels deep whose item
///         is chosen must not leave two menus standing, so the close walks up through
///         <see cref="ParentMenu" /> rather than closing only itself.
///     </para>
/// </remarks>
public partial class Menu : Overlay {
    readonly List<MenuItem> items = [];

    /// <inheritdoc />
    protected override string TagName => "menu";

    /// <summary>The items, in order, not counting separators.</summary>
    public IReadOnlyList<MenuItem> Items => items;

    /// <summary>The menu this one opened out of, if it is a submenu.</summary>
    public Menu? ParentMenu { get; internal set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // A menu is a focus scope, which is what stops Tab walking out of an open menu into the
        // window behind it.
        IsFocusScope = true;

        AddHandler<ClickEvent>(static (element, args) => ((Menu) element).Chosen(args));
        AddHandler<KeyEvent>(static (element, args) => ((Menu) element).Keyed(args));
    }

    /// <summary>Removes every item and separator.</summary>
    /// <remarks>
    ///     ⚠ <b>For a menu whose contents are decided when it opens.</b> Open Recent, Layouts ▸ and
    ///     Add Component ▸ are all lists that move between one opening and the next, and rebuilding
    ///     the <i>menu</i> instead would detach the overlay that anchors it and lose whatever the
    ///     caller had attached it to.
    ///     <para>
    ///         The separators go with the items, because they are the only thing that gives a menu of
    ///         nothing a height — a cleared menu that still drew three rules would open as a small
    ///         empty box with lines in it.
    ///     </para>
    /// </remarks>
    public void Clear() {
        while (Children.Count > 0) {
            Children[^1].Remove();
        }

        items.Clear();
    }

    /// <summary>Adds a command.</summary>
    /// <param name="label">What it says.</param>
    /// <returns>The item.</returns>
    public MenuItem AddItem(string? label = null) {
        var item = Add<MenuItem>();
        item.Label = label;

        items.Add(item);

        // ⚠ On the item and Direct, not on the menu, because a pointer entering an element is
        // raised once per element that it entered rather than routed — `EventRouter.Direct` — so a
        // handler up here would never hear about it. The same arrangement `MenuBar` uses.
        item.AddHandler<PointerEvent>(
            static (element, args) => {
                if (args.Action == PointerAction.Entered && element is MenuItem entered) {
                    ((Menu) entered.Parent!).Hovered(entered);
                }
            },
            RoutingStrategy.Direct
        );

        return item;
    }

    /// <summary>Adds a rule between two groups of commands.</summary>
    /// <returns>The separator.</returns>
    public Separator AddSeparator() => Add<Separator>();

    /// <summary>Adds a command that opens a menu of its own.</summary>
    /// <param name="label">What it says.</param>
    /// <returns>The submenu, whose items are added the same way as this one's.</returns>
    /// <remarks>
    ///     The submenu is a root child like every other overlay — it is not inside this menu, which
    ///     is what lets it hang off the side without being clipped by it.
    /// </remarks>
    public Menu AddSubmenu(string? label = null) {
        var item = AddItem(label);

        var submenu = Document.Root.Add<Menu>();
        submenu.Placement = Placement.Right;
        submenu.ParentMenu = this;

        item.Submenu = submenu;
        return submenu;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A submenu is a sibling, not a child, so removing a menu left every submenu it opened
    ///     in the document.</b> That is the cost of the root-child arrangement and there was no way to
    ///     pay it until <see cref="UiElement.OnRemoved" /> existed. The recursion is the point — a
    ///     submenu has submenus — and it terminates because <c>Remove</c> announces before it detaches,
    ///     so each one is announced exactly once on the way down.
    /// </remarks>
    protected override void OnRemoved() {
        foreach (var item in items) {
            if (item.Submenu is { IsRemoved: false } submenu) {
                item.Submenu = null;
                Document.Remove(submenu);
            }
        }

        base.OnRemoved();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The first item takes the focus, so that a menu opened from the keyboard can be used from
    ///     the keyboard. A menu opened by a click gets it too, which is harmless: the ring only
    ///     shows in keyboard mode.
    /// </remarks>
    protected override void OnOpened() {
        base.OnOpened();

        if (items.Count > 0) {
            Document.Focus(items[0]);
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(CloseReason reason) {
        base.OnClosed(reason);

        // Every submenu goes with it. One left open after its parent has gone is an orphan floating
        // over the interface with nothing to dismiss it.
        foreach (var item in items) {
            item.Submenu?.Close(reason);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A press inside a submenu is not outside <i>this</i> menu, or opening a submenu would
    ///     immediately light-dismiss the parent it came from.
    /// </remarks>
    protected override bool IsOutside(UiElement? hit) {
        if (!base.IsOutside(hit)) {
            return false;
        }

        foreach (var item in items) {
            if (item.Submenu is { IsOpen: true } submenu && !submenu.IsOutside(hit)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Closes this menu and every one it opened out of.</summary>
    public void CloseChain(CloseReason reason) {
        for (Menu? menu = this; menu is not null; menu = menu.ParentMenu) {
            menu.Close(reason);
        }
    }

    /// <summary>Follows the pointer across the items: opens what it rests on, closes what it left.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What every desktop menu does, and it is not a nicety.</b> A submenu that needed a
    ///         click is one where reaching a nested command costs a click per level, and — worse —
    ///         where sliding off "3D Object" onto "Delete" leaves the shapes hanging over the item the
    ///         pointer is now on. Hovering a sibling closing the open one is the same rule as opening,
    ///         seen from the other side.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Closed on entering a sibling rather than on leaving the item, and the difference
    ///         is the whole gesture.</b> A submenu is placed beside its parent item, so the pointer
    ///         going to it <i>leaves</i> that item — a close on exit would shut the menu the user is
    ///         reaching for, every time, and there would be no way to reach a nested command with the
    ///         mouse at all.
    ///     </para>
    ///     <para>
    ///         Nothing happens while the menu itself is shut. A closed menu still has its items and
    ///         they still answer to the pointer crossing where they used to be.
    ///     </para>
    /// </remarks>
    void Hovered(MenuItem item) {
        if (!IsOpen || item.Disabled) {
            return;
        }

        foreach (var other in items) {
            if (!ReferenceEquals(other, item) && other.Submenu is { IsOpen: true } open) {
                open.Close(CloseReason.Code);
            }
        }

        if (item.Submenu is { IsOpen: false } submenu) {
            submenu.Open(item);
        }
    }

    void Chosen(ClickEvent args) {
        if (args.Source is not MenuItem item || !items.Contains(item)) {
            return;
        }

        if (item.Submenu is { } submenu) {
            // Already open where the pointer got there first, which is the common case now that
            // hovering opens it. Re-opening would re-place a menu the user is already inside.
            if (!submenu.IsOpen) {
                submenu.Open(item);
            }

            return;
        }

        CloseChain(CloseReason.Committed);
    }

    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || items.Count == 0) {
            return;
        }

        var current = items.FindIndex(item => item.IsFocused);

        switch (args.Key) {
            case InputKey.Down:
                Highlight(current < 0 ? 0 : (current + 1) % items.Count);
                break;

            case InputKey.Up:
                Highlight(current < 0 ? items.Count - 1 : (current - 1 + items.Count) % items.Count);
                break;

            case InputKey.Home:
                Highlight(0);
                break;

            case InputKey.End:
                Highlight(items.Count - 1);
                break;

            case InputKey.Right when current >= 0 && items[current].Submenu is { } submenu:
                submenu.Open(items[current]);
                break;

            case InputKey.Left when ParentMenu is not null:
                Close(CloseReason.Cancelled);
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Highlight(int index) => Document.Focus(items[index]);
}

/// <summary>A menu opened at a point rather than beside an element.</summary>
/// <remarks>
///     ⚠ <b>Not a <see cref="Menu" /> with a different opening method — a distinct type, because the
///     placement rule differs.</b> A menu beside an anchor flips to the other side of it when there
///     is no room; a context menu has no side to be on, so it is pinned to the cursor and pushed
///     back inside the viewport. Sharing one type would mean a menu that sometimes appears above the
///     cursor by a rule that makes no sense for a cursor.
/// </remarks>
public sealed partial class ContextMenu : Menu {
    /// <inheritdoc />
    protected override string TagName => "context-menu";

    /// <summary>Opens it with its corner at a point in document space.</summary>
    /// <param name="x">Where.</param>
    /// <param name="y">Ditto.</param>
    public void OpenAt(float x, float y) {
        Open();

        var size = Bounds;
        var viewport = Document.Viewport;

        MoveTo(
            Math.Clamp(x, 0f, MathF.Max(0f, viewport.ViewportWidth - size.Width)),
            Math.Clamp(y, 0f, MathF.Max(0f, viewport.ViewportHeight - size.Height))
        );
    }

    /// <summary>Makes a secondary click anywhere in a subtree open it there.</summary>
    /// <param name="target">The subtree's root.</param>
    public void Attach(UiElement target) {
        ArgumentNullException.ThrowIfNull(target);

        target.AddHandler<PointerEvent>(
            (element, args) => {
                if (args is { Action: PointerAction.Pressed, Button: PointerButton.Secondary }) {
                    OpenAt(args.X, args.Y);
                    args.Handled = true;
                }
            }
        );
    }
}

/// <summary>One name on a menu bar.</summary>
public sealed partial class MenuBarItem : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "menu-bar-item";

    /// <summary>The menu it drops.</summary>
    public Menu Menu { get; internal set; } = null!;
}

/// <summary>The strip of menus along the top of a window.</summary>
/// <remarks>
///     ⚠ <b>Once one menu is open, hovering another opens it.</b> That is not decoration — it is what
///     every menu bar does, and without it a user who opened the wrong menu has to click twice to
///     see the right one. It is also why the bar tracks whether anything is open at all: the same
///     hover with nothing open must do nothing.
/// </remarks>
public sealed partial class MenuBar : Control {
    readonly List<MenuBarItem> items = [];

    /// <inheritdoc />
    protected override string TagName => "menu-bar";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The names on the bar, in order.</summary>
    public IReadOnlyList<MenuBarItem> Items => items;

    /// <summary>The menu currently dropped, if any.</summary>
    public Menu? Current { get; private set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        AddHandler<ClickEvent>(static (element, args) => ((MenuBar) element).Chosen(args));
    }

    /// <summary>Adds a name and the menu it drops.</summary>
    /// <param name="label">The name.</param>
    /// <returns>The menu, whose items are added with <see cref="Menu.AddItem" />.</returns>
    public Menu AddMenu(string? label = null) {
        var item = Add<MenuBarItem>();
        item.Label = label;

        var menu = Document.Root.Add<Menu>();
        menu.Placement = Placement.Bottom;
        menu.OpenChanged += (opened, isOpen) => Current = isOpen ? (Menu) opened : Current == opened ? null : Current;

        item.Menu = menu;
        items.Add(item);

        item.AddHandler<PointerEvent>(
            (element, args) => {
                // Only while something is already open. A hover with the bar at rest is just a
                // pointer crossing a word.
                if (args.Action == PointerAction.Entered && Current is not null && !ReferenceEquals(Current, menu)) {
                    Current.Close(CloseReason.Code);
                    menu.Open(item);
                }
            },
            RoutingStrategy.Direct
        );

        return menu;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Every menu the bar dropped is a root child. Removing the bar took its <i>items</i> with it,
    ///     because those really are children, and left the menus themselves — so a shell that rebuilt
    ///     its menu bar accumulated a full set of orphaned menus each time, each still holding the two
    ///     capture handlers <see cref="Overlay" /> puts on the root.
    /// </remarks>
    protected override void OnRemoved() {
        foreach (var item in items) {
            if (item.Menu is { IsRemoved: false } menu) {
                item.Menu = null!;
                Document.Remove(menu);
            }
        }

        base.OnRemoved();
    }

    void Chosen(ClickEvent args) {
        if (args.Source is not MenuBarItem item || !items.Contains(item)) {
            return;
        }

        // A second click on the open menu's own name closes it, which is what a menu bar does and
        // what the overlay's light dismiss would otherwise fight: the press closes it, and then the
        // click would reopen it.
        if (item.Menu.IsOpen) {
            item.Menu.Close(CloseReason.Code);
        } else {
            item.Menu.Open(item);
        }

        args.Handled = true;
    }
}
