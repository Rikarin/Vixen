// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>One line of a menu, in the model rather than on screen.</summary>
/// <remarks>
///     ⚠ <b>A menu is described by command ids and rebuilt from them.</b> It holds no labels, no
///     shortcuts and no enablement — all three come from the registry and the keymap when the menu
///     is built, so a command renamed, rebound or disabled is right everywhere at once and a menu
///     cannot disagree with a toolbar about what something is called.
/// </remarks>
public abstract record MenuEntry;

/// <summary>A line that runs a command.</summary>
/// <param name="CommandId">Which one.</param>
/// <remarks>
///     An id nothing has registered is skipped when the menu is built, rather than shown dead or
///     thrown over: a menu model outlives the plugin whose commands it names.
/// </remarks>
public sealed record MenuCommand(string CommandId) : MenuEntry;

/// <summary>A rule between two groups of lines.</summary>
/// <remarks>
///     Collapsed when it would be first, last, or next to another — a separator either side of a
///     command that turned out not to exist is what a menu built from a model that has lost entries
///     otherwise looks like.
/// </remarks>
public sealed record MenuSeparator : MenuEntry;

/// <summary>A line that opens a menu of its own.</summary>
/// <param name="Group">What is in it.</param>
public sealed record MenuSubmenu(MenuGroup Group) : MenuEntry;

/// <summary>Lines decided when the menu is built rather than when it is described.</summary>
/// <param name="CommandIds">What to show, asked each time the menu is rebuilt.</param>
/// <remarks>
///     What the panel list, the layout list and "open recent" are: a set that changes while the
///     editor runs, and one that a static model would have to be rebuilt to follow.
/// </remarks>
public sealed record MenuDynamic(Func<IEnumerable<string>> CommandIds) : MenuEntry;

/// <summary>A named list of lines: one menu, or one submenu.</summary>
public sealed class MenuGroup {
    readonly List<MenuEntry> entries = [];

    /// <summary>Creates a group.</summary>
    /// <param name="title">What its name says.</param>
    public MenuGroup(StringId title) => Title = title;

    /// <summary>What its name says.</summary>
    public StringId Title { get; }

    /// <summary>What is in it, in order.</summary>
    public IReadOnlyList<MenuEntry> Entries => entries;

    /// <summary>Adds a command.</summary>
    /// <param name="commandId">Its id.</param>
    /// <returns>This group, so a menu reads as a list.</returns>
    public MenuGroup Add(string commandId) {
        ArgumentException.ThrowIfNullOrEmpty(commandId);

        entries.Add(new MenuCommand(commandId));
        return this;
    }

    /// <summary>Adds several commands.</summary>
    /// <param name="commandIds">Their ids.</param>
    /// <returns>This group.</returns>
    public MenuGroup Add(params ReadOnlySpan<string> commandIds) {
        foreach (var id in commandIds) {
            Add(id);
        }

        return this;
    }

    /// <summary>Adds a rule.</summary>
    /// <returns>This group.</returns>
    public MenuGroup AddSeparator() {
        entries.Add(new MenuSeparator());
        return this;
    }

    /// <summary>Adds a submenu.</summary>
    /// <param name="title">What its line says.</param>
    /// <returns>The submenu, whose entries are added the same way.</returns>
    public MenuGroup AddSubmenu(StringId title) {
        var group = new MenuGroup(title);
        entries.Add(new MenuSubmenu(group));

        return group;
    }

    /// <summary>Adds a submenu at a place in the list.</summary>
    /// <param name="index">Where, clamped to the ends.</param>
    /// <param name="title">What its line says.</param>
    /// <returns>The submenu.</returns>
    /// <remarks>
    ///     For a feature putting its verbs into a menu somebody else built — the blockout tools
    ///     belong beside Work Plane and Measure rather than after the camera bookmarks, and a module
    ///     that could only append would reorder the menu the moment it stopped being compiled in.
    ///     Clamped rather than checked, for <see cref="MenuModel.InsertMenu" />'s reason: a position
    ///     is a preference about where something reads best, not an index into something the caller
    ///     owns.
    /// </remarks>
    public MenuGroup InsertSubmenu(int index, StringId title) {
        var group = new MenuGroup(title);
        entries.Insert(Math.Clamp(index, 0, entries.Count), new MenuSubmenu(group));

        return group;
    }

    /// <summary>Where a submenu with a title's id sits among the entries, or -1.</summary>
    /// <param name="titleId">The <see cref="StringId" />'s id.</param>
    /// <returns>The index, or -1 when there is no such submenu.</returns>
    /// <remarks>
    ///     ⚠ <b>How a module says "after Measure" without holding a number.</b> An index a feature
    ///     hard-coded would silently move the day somebody adds a line above it; an id is the same
    ///     answer next release. By id and not by displayed name so that a localised editor agrees.
    /// </remarks>
    public int IndexOfSubmenu(string titleId) {
        ArgumentException.ThrowIfNullOrEmpty(titleId);

        for (var index = 0; index < entries.Count; index++) {
            if (entries[index] is MenuSubmenu submenu
                && string.Equals(submenu.Group.Title.Id, titleId, StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Adds a set of commands decided when the menu is built.</summary>
    /// <param name="commandIds">What to show.</param>
    /// <returns>This group.</returns>
    public MenuGroup AddDynamic(Func<IEnumerable<string>> commandIds) {
        ArgumentNullException.ThrowIfNull(commandIds);

        entries.Add(new MenuDynamic(commandIds));
        return this;
    }

    /// <summary>Takes a line out.</summary>
    /// <param name="entry">Which one, as <see cref="Entries" /> reported it.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>
    ///     ⚠ <b>By identity, which is why the caller has to have kept the entry.</b>
    ///     <see cref="MenuCommand" /> is a record, so removing "the one naming <c>file.save</c>"
    ///     would remove whichever of them compared equal first — and a plugin that put its command
    ///     in two menus would take the wrong one out. What unloading a plugin does.
    /// </remarks>
    public bool Remove(MenuEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);

        var index = entries.FindIndex(candidate => ReferenceEquals(candidate, entry));

        if (index < 0) {
            return false;
        }

        entries.RemoveAt(index);
        return true;
    }
}

/// <summary>The whole menu bar, described.</summary>
public sealed class MenuModel {
    readonly List<MenuGroup> menus = [];

    /// <summary>The menus along the bar, in order.</summary>
    public IReadOnlyList<MenuGroup> Menus => menus;

    /// <summary>The menu with a title's id, if it is on the bar.</summary>
    /// <param name="titleId">The <see cref="StringId.Id" /> of the menu's title.</param>
    /// <returns>The menu, or <see langword="null" />.</returns>
    public MenuGroup? Find(string titleId) {
        ArgumentException.ThrowIfNullOrEmpty(titleId);

        foreach (var menu in menus) {
            if (string.Equals(menu.Title.Id, titleId, StringComparison.Ordinal)) {
                return menu;
            }
        }

        return null;
    }

    /// <summary>Adds a menu, or hands back the one already there under the same id.</summary>
    /// <param name="title">Its name on the bar.</param>
    /// <returns>The menu, whose entries are added with <see cref="MenuGroup.Add(string)" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Find-or-create, and the "or" is what makes the bar independent of load order.</b> A
    ///     menu is named by its title's id, so the application's Tools menu and a plugin's
    ///     <c>[EditorMenu("Tools/…")]</c> are the same menu — whichever of the two asks first. When
    ///     both created unconditionally, the bar came up with two menus called Tools and which one
    ///     held which lines depended on whether the modules activated before the application
    ///     described its own bar.
    ///     <para>
    ///         That is Unity's fourth mechanism kept honestly: menus compose because nobody owns the
    ///         tree. A caller that needs to know whether it created one asks <see cref="Find" />
    ///         first — which is what <c>PluginContext.AddMenu</c> does, so unloading a plugin cannot
    ///         take the application's menu off the bar with it.
    ///     </para>
    /// </remarks>
    public MenuGroup AddMenu(StringId title) {
        if (Find(title.Id) is { } existing) {
            return existing;
        }

        var group = new MenuGroup(title);
        menus.Add(group);

        return group;
    }

    /// <summary>Adds a menu at a place along the bar.</summary>
    /// <param name="index">Where, clamped to the ends.</param>
    /// <param name="title">Its name on the bar.</param>
    /// <returns>The menu.</returns>
    /// <remarks>
    ///     For the application's own menus, which are registered after the shell has already put
    ///     File, Edit, View and Help on the bar — and which belong <i>among</i> them rather than
    ///     after Help. Clamped rather than checked, because the position is a preference about where
    ///     a menu reads best and not an index into something the caller owns: a shell that gains a
    ///     menu should not turn an application's "third from the left" into an exception.
    /// </remarks>
    /// <inheritdoc cref="AddMenu" path="/remarks" />
    public MenuGroup InsertMenu(int index, StringId title) {
        // ⚠ A menu already on the bar keeps the place it has rather than moving to the requested
        // index. The index is a preference about where a menu reads best; moving one because a
        // second caller named it would let load order decide the bar's layout, which is the thing
        // this pair exists to stop.
        if (Find(title.Id) is { } existing) {
            return existing;
        }

        var group = new MenuGroup(title);
        menus.Insert(Math.Clamp(index, 0, menus.Count), group);

        return group;
    }

    /// <summary>Takes a menu off the bar.</summary>
    /// <param name="group">Which one, as <see cref="AddMenu" /> or <see cref="InsertMenu" /> returned it.</param>
    /// <returns>Whether it was on it.</returns>
    /// <remarks>What unloading a plugin that added a menu of its own does.</remarks>
    public bool Remove(MenuGroup group) {
        ArgumentNullException.ThrowIfNull(group);
        return menus.Remove(group);
    }
}
