// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Ui;

namespace Vixen.Editor.Plugin;

/// <summary>Turns a set of menu declarations into lines, submenus and separators, in one pass.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D5, and the one piece of the declaration path both tiers share.</b>
///         <c>[EditorMenu]</c> and <c>[EditorSubmenu]</c> are read two ways — by the reflective scan
///         a disk plugin or a project script gets, and by <c>Vixen.Editor.Plugin.Generator</c>, which
///         writes an in-tree module's <c>Declared.Register</c> at compile time. Where a path lands,
///         which submenu two declarations share, what order the lines come in and where a separator
///         goes is decided here and only here, so the two readers cannot disagree about any of it:
///         what they can disagree about is what they found, and <c>DeclaredParityTests</c> holds them
///         to that.
///     </para>
///     <para>
///         ⚠ <b>Collected, then applied.</b> <c>Priority</c> orders declarations against each other,
///         and a declaration a reader finds later may belong above one it found first — so nothing is
///         placed until <see cref="Apply" /> has seen them all. Registering as they arrive would make
///         the order the one types happened to be enumerated in.
///     </para>
///     <para>
///         ⚠ <b>Everything it adds goes through the context's registration scope</b>, so it is taken
///         out when the plugin is unloaded — the lines, the separators and the submenus it created,
///         and none of what it merely joined.
///     </para>
/// </remarks>
/// <param name="context">The plugin's context, which everything is registered through.</param>
public sealed class DeclaredMenus(PluginContext context) {
    readonly PluginContext context = context ?? throw new ArgumentNullException(nameof(context));
    readonly List<SubmenuDeclaration> submenus = [];
    readonly List<LineDeclaration> lines = [];
    readonly List<CommandDeclaration> commands = [];

    bool applied;

    /// <summary>The prefix a command declared with no <c>Id</c> is given.</summary>
    /// <remarks>
    ///     ⚠ <b>"scripts", for a plugin's too, and that is the historical id.</b> The scan derived it
    ///     this way before plugins were scanned at all, and a user's keybinding is stored against the
    ///     id — renaming the prefix would silently drop every one of them.
    /// </remarks>
    public const string DerivedCommandPrefix = "scripts";

    /// <summary>Declares a submenu: what it is called and where among its siblings it goes.</summary>
    /// <param name="path">Where it is — <c>"Scene/Geometry"</c>. The first part is a menu on the bar.</param>
    /// <param name="title">What its line says, or null to use the path's last part.</param>
    /// <param name="after">The title id of the sibling it goes after, or null to append it.</param>
    /// <param name="priority">Which of two declarations is placed first. Lower is first.</param>
    /// <exception cref="PluginException">The path names no submenu.</exception>
    public void Submenu(string path, StringId? title = null, string? after = null, int priority = 0) {
        var parts = Parts(path, 2, "a submenu", "\"Scene/Geometry\"");

        submenus.Add(new(parts, title, string.IsNullOrEmpty(after) ? null : after, priority, submenus.Count));
    }

    /// <summary>Declares a verb: a command, and its line at the end of a path.</summary>
    /// <param name="path">Where the line goes — <c>"Tools/Bake"</c>. The last part is the line's text.</param>
    /// <param name="run">What it does.</param>
    /// <param name="id">The command id, or null to derive one from the path.</param>
    /// <param name="title">What it is called, or null to use the path's last part.</param>
    /// <param name="priority">Which of two lines in one menu comes first. Lower is higher up.</param>
    /// <param name="separator">Whether a separator goes above the line.</param>
    /// <exception cref="PluginException">The path names no line.</exception>
    public void Command(
        string path,
        Action run,
        string? id = null,
        StringId? title = null,
        int priority = 0,
        bool separator = false
    ) {
        ArgumentNullException.ThrowIfNull(run);

        var parts = Parts(path, 2, "a menu line", "\"Tools/Do It\"");
        var command = string.IsNullOrEmpty(id) ? Derive(DerivedCommandPrefix, path) : id;

        commands.Add(new(command, title ?? new StringId("editor.command." + command, parts[^1]), run));
        lines.Add(new(parts[..^1], [command], priority, separator, lines.Count));
    }

    /// <summary>Declares a line for a command something else registers — a mode's, say.</summary>
    /// <param name="path">The menu it goes in — <c>"Scene/Geometry"</c>. Every part is a menu.</param>
    /// <param name="commandId">The command.</param>
    /// <param name="priority">Which of two lines in one menu comes first. Lower is higher up.</param>
    /// <param name="separator">Whether a separator goes above the line.</param>
    /// <exception cref="PluginException">The path is empty.</exception>
    public void Line(string path, string commandId, int priority = 0, bool separator = false) {
        ArgumentException.ThrowIfNullOrEmpty(commandId);

        lines.Add(new(Parts(path, 1, "a menu", "\"Scene\""), [commandId], priority, separator, lines.Count));
    }

    /// <summary>Declares a run of lines for commands something else registers, in the order given.</summary>
    /// <param name="path">The menu they go in. Every part is a menu.</param>
    /// <param name="commandIds">The commands. Read once, now.</param>
    /// <param name="priority">Where the run goes among the menu's other lines. Lower is higher up.</param>
    /// <param name="separator">Whether a separator goes above the run.</param>
    /// <exception cref="PluginException">The path is empty.</exception>
    /// <remarks>
    ///     A family of commands whose ids are computed rather than declared one by one — a command per
    ///     shape kind — is one declaration rather than twelve, and stays in the order its author chose.
    /// </remarks>
    public void Lines(string path, IEnumerable<string> commandIds, int priority = 0, bool separator = false) {
        ArgumentNullException.ThrowIfNull(commandIds);

        lines.Add(new(Parts(path, 1, "a menu", "\"Scene\""), [.. commandIds], priority, separator, lines.Count));
    }

    /// <summary>Registers every command and places every line and submenu declared so far.</summary>
    /// <exception cref="InvalidOperationException">It has been applied already.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Submenus first, parents before children, then lines.</b> A line names its menu by
    ///         path; the declared submenu at that path has to exist by then or the line would make an
    ///         undeclared one with a derived title in its place.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A path finds before it creates.</b> A declared submenu joins one already there with
    ///         the same title id; an undeclared part of a path joins a submenu with its derived id or
    ///         its text — so <c>"Tools/My Thing/A"</c> and <c>"Tools/My Thing/B"</c> land in one
    ///         <i>My Thing</i>. Menus compose because nobody owns the tree.
    ///     </para>
    /// </remarks>
    public void Apply() {
        if (applied) {
            throw new InvalidOperationException("These declarations have been applied already.");
        }

        applied = true;

        var known = new Dictionary<string, MenuGroup>(StringComparer.Ordinal);
        var added = new List<(MenuGroup Group, MenuEntry Entry)>();

        foreach (var command in commands) {
            context.AddCommand(command.Id, command.Title, command.Run);
        }

        foreach (var declared in submenus
                     .OrderBy(static item => item.Parts.Length)
                     .ThenBy(static item => item.Priority)
                     .ThenBy(static item => item.Sequence)) {
            var parent = Resolve(declared.Parts, declared.Parts.Length - 1, known, added);
            var segment = declared.Parts[^1];
            var title = declared.Title ?? new StringId(DerivedSubmenuId(declared.Parts), segment);

            // A declared submenu joins one already there only by its title id. Matching it by text
            // would put Blockout's "Create" into whatever other "Create" the menu already has.
            if (Child(parent, title.Id) is not { } group) {
                var at = declared.After is { } after && parent.IndexOfSubmenu(after) is >= 0 and var found
                    ? found + 1
                    : parent.Entries.Count;

                group = parent.InsertSubmenu(at, title);
                added.Add((parent, parent.Entries[at]));
            }

            known[Key(declared.Parts, declared.Parts.Length)] = group;
        }

        // Per menu, lowest priority first, and declaration order among equals — which is what makes
        // a run of lines with no priority come out in the order they were written.
        foreach (var line in lines.OrderBy(static item => item.Priority).ThenBy(static item => item.Sequence)) {
            var group = Resolve(line.Parts, line.Parts.Length, known, added);

            if (line.Separator && group.Entries.Count > 0 && group.Entries[^1] is not MenuSeparator) {
                group.AddSeparator();
                added.Add((group, group.Entries[^1]));
            }

            foreach (var id in line.CommandIds) {
                group.Add(id);
                added.Add((group, group.Entries[^1]));
            }
        }

        if (added.Count == 0) {
            return;
        }

        // One removal for the whole pass, newest first, and one rebuild either side. Sixty lines that
        // each rebuilt the bar on the way in and again on the way out would be sixty layouts of a
        // menu nobody is looking at.
        context.OnUnload(
            () => {
                for (var index = added.Count - 1; index >= 0; index--) {
                    added[index].Group.Remove(added[index].Entry);
                }

                context.Shell.MenuBar.Rebuild();
            }
        );

        context.Shell.MenuBar.Rebuild();
    }

    /// <summary>The id something with no declared one gets.</summary>
    /// <param name="prefix">What it starts with.</param>
    /// <param name="text">What a person reads — a path, a title.</param>
    /// <returns><c>prefix.text</c>, lowercased, with slashes as dots and spaces as hyphens.</returns>
    /// <remarks>
    ///     ⚠ <b>Derived from what a person reads, which means renaming it drops their keybinding.</b>
    ///     That is the cost of not making every author invent an id, and every attribute's <c>Id</c> is
    ///     how anybody who minds opts out. Public because the generator writes the same ids at compile
    ///     time that the scan derives at load, and one function is how they stay the same ids.
    /// </remarks>
    public static string Derive(string prefix, string text) {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(text);

        return prefix + "." + text.Replace('/', '.').Replace(' ', '-').ToLowerInvariant();
    }

    MenuGroup Resolve(
        string[] parts,
        int count,
        Dictionary<string, MenuGroup> known,
        List<(MenuGroup Group, MenuEntry Entry)> added
    ) {
        var key = Key(parts, 1);

        if (!known.TryGetValue(key, out var group)) {
            var top = "editor.menu." + parts[0].ToLowerInvariant();

            group = context.FindMenu(top) ?? context.AddMenu(new StringId(top, parts[0]));
            known[key] = group;
        }

        for (var level = 1; level < count; level++) {
            key = Key(parts, level + 1);

            if (known.TryGetValue(key, out var found)) {
                group = found;
                continue;
            }

            var id = DerivedSubmenuId(parts[..(level + 1)]);
            var segment = parts[level];

            if ((Child(group, id) ?? Child(group, segment, byText: true)) is not { } child) {
                child = group.AddSubmenu(new StringId(id, segment));
                added.Add((group, group.Entries[^1]));
            }

            known[key] = child;
            group = child;
        }

        return group;
    }

    static MenuGroup? Child(MenuGroup parent, string match, bool byText = false) {
        foreach (var entry in parent.Entries) {
            if (entry is MenuSubmenu { Group: var group }
                && string.Equals(byText ? group.Title.Source : group.Title.Id, match, StringComparison.Ordinal)) {
                return group;
            }
        }

        return null;
    }

    static string DerivedSubmenuId(string[] parts) => Derive("editor.menu", string.Join('/', parts));

    static string Key(string[] parts, int count) => string.Join('/', parts, 0, count);

    static string[] Parts(string path, int minimum, string what, string example) {
        ArgumentNullException.ThrowIfNull(path);

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < minimum) {
            throw new PluginException(
                $"'{path}' is not a path to {what}. It needs at least {minimum} part"
                + (minimum == 1 ? "" : "s") + $" — {example}."
            );
        }

        return parts;
    }

    readonly record struct SubmenuDeclaration(string[] Parts, StringId? Title, string? After, int Priority, int Sequence);

    readonly record struct LineDeclaration(
        string[] Parts,
        string[] CommandIds,
        int Priority,
        bool Separator,
        int Sequence
    );

    readonly record struct CommandDeclaration(string Id, StringId Title, Action Run);
}
