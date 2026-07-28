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

    /// <summary>Adds a set of commands decided when the menu is built.</summary>
    /// <param name="commandIds">What to show.</param>
    /// <returns>This group.</returns>
    public MenuGroup AddDynamic(Func<IEnumerable<string>> commandIds) {
        ArgumentNullException.ThrowIfNull(commandIds);

        entries.Add(new MenuDynamic(commandIds));
        return this;
    }
}

/// <summary>The whole menu bar, described.</summary>
public sealed class MenuModel {
    readonly List<MenuGroup> menus = [];

    /// <summary>The menus along the bar, in order.</summary>
    public IReadOnlyList<MenuGroup> Menus => menus;

    /// <summary>Adds a menu.</summary>
    /// <param name="title">Its name on the bar.</param>
    /// <returns>The menu, whose entries are added with <see cref="MenuGroup.Add(string)" />.</returns>
    public MenuGroup AddMenu(StringId title) {
        var group = new MenuGroup(title);
        menus.Add(group);

        return group;
    }
}
