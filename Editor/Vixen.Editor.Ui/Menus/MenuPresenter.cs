// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>A menu bar built from a model, a registry and a keymap, and rebuilt when any of them changes.</summary>
/// <remarks>
///     <para>
///         <b>The menu is a view, and this is the projection.</b> Doc 11's claim that "menus,
///         toolbars, context menus and the command palette are all views over the command registry"
///         is either this class or a lie — the alternative is four places that each know Save is
///         called Save, which is how an editor ends up with a toolbar button that stays enabled
///         after the menu item went grey.
///     </para>
///     <para>
///         ⚠ <b>Enablement is applied as the menu opens, not as it is built — and this class no
///         longer applies it.</b> A menu built at start-up and never touched again would show
///         whatever was true then; opening is the last moment before the user reads it, and it is
///         cheap, one predicate per visible line. What changed is who does it: every line is bound
///         to its id through <c>ButtonBase.Command</c>, and <c>Menu</c> refreshes its bound items
///         from its own <c>OpenChanged</c>. Same moment, same predicate, one implementation.
///     </para>
///     <para>
///         ⚠ <b>Which means the registry has to be answerable in the document.</b> A bound id
///         resolves through <c>CommandRoute</c>, whose last link is
///         <see cref="UiDocument.ApplicationCommandResponder" /> — so a presenter built over a
///         registry nothing installed would grey every line with nothing failing anywhere. The
///         constructor and both <c>Context</c> overloads install it when the document has not
///         already been told, which <c>EditorShell</c> does for itself.
///     </para>
///     <para>
///         ⚠ <b>A rebuild replaces the bar rather than editing it.</b> <see cref="MenuBar" /> can be
///         added to and not removed from, and its menus are children of the document root rather
///         than of the bar — so a presenter that tried to edit in place would leak a menu overlay
///         per rebuild, invisible and still listening for pointer events.
///     </para>
///     <para>
///         ⚠ <b>And the replacement is put back where the first one was.</b> Adding a child appends
///         it, and a rebuild is triggered by every command anybody registers — so a bar built into
///         an empty shell and rebuilt once the workspace and the status bar are in it comes back
///         underneath both of them. In a column that is a menu bar along the bottom of the window,
///         arriving on whichever frame the application happened to add its last command.
///     </para>
/// </remarks>
public sealed class MenuPresenter : IDisposable {
    readonly List<Menu> menus = [];
    readonly UiElement host;
    readonly CommandRegistry commands;
    readonly KeyMap keys;

    readonly Action<CommandRegistry> onCommandsChanged;
    readonly Action<KeyMap> onKeysChanged;
    readonly Action<StringCatalog> onLanguageChanged;

    /// <summary>Which of the host's children the bar is, whatever else has been added since.</summary>
    readonly int slot;

    MenuBar? bar;
    bool disposed;

    /// <summary>Builds a menu bar into an element.</summary>
    /// <param name="host">Where the bar goes.</param>
    /// <param name="model">What is on it.</param>
    /// <param name="commands">What its lines run.</param>
    /// <param name="keys">What the shortcuts on it say.</param>
    public MenuPresenter(UiElement host, MenuModel model, CommandRegistry commands, KeyMap keys) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(keys);

        this.host = host;
        this.commands = commands;
        this.keys = keys;

        // ⚠ See `ToolbarPresenter`'s constructor: a bar of bound items is only as good as the chain
        // that answers them, and one built over a registry nothing installed greys every line with
        // no error anywhere. `??=`, so a host that has already said what answers outranks this.
        host.Document.ApplicationCommandResponder ??= commands;

        // Taken before the first bar exists, so it is the position the host was handed over for
        // rather than the position the bar happens to be at — which is the same thing on the first
        // build and the thing that has to be restored on every one after it.
        slot = host.Children.Count;

        Model = model;
        Rebuild();

        onCommandsChanged = _ => Rebuild();
        onKeysChanged = _ => Rebuild();
        onLanguageChanged = _ => Rebuild();

        commands.Changed += onCommandsChanged;
        keys.Changed += onKeysChanged;

        // ⚠ `Strings.Changed` is static, so this subscription outlives the document unless it is
        // taken back. A presenter left subscribed rebuilds a menu bar into a disposed document the
        // next time anybody switches language, which is an exception from a stack that mentions
        // neither the language nor the window it came from. Every other subscription here is to
        // something the shell owns and dies with it.
        Strings.Changed += onLanguageChanged;
    }

    /// <summary>What is on the bar.</summary>
    public MenuModel Model { get; }

    /// <summary>The bar itself, which is replaced by every rebuild.</summary>
    public MenuBar Bar => bar!;

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        commands.Changed -= onCommandsChanged;
        keys.Changed -= onKeysChanged;
        Strings.Changed -= onLanguageChanged;
    }

    /// <summary>Throws the bar away and builds it again from the model.</summary>
    public void Rebuild() {
        if (disposed) {
            return;
        }

        // The menus first: they are the document root's children rather than the bar's, so removing
        // the bar would leave them behind — open, dismissable and attached to nothing.
        //
        // ⚠ Skipping the ones already gone, because `Menu.OnRemoved` takes a menu's submenus with
        // it and this list is flat — removing a parent menu therefore removes entries further along
        // it. Removal is final and asking twice throws, so the guard is the adaptation the hook
        // requires rather than defensive noise.
        foreach (var menu in menus) {
            if (!menu.IsRemoved) {
                menu.Remove();
            }
        }

        menus.Clear();
        bar?.Remove();

        bar = host.Add<MenuBar>();

        // ⚠ Back to where the first one went. `Add` appends, and by the time a command registered
        // after start-up triggers a rebuild the host has a workspace and a status bar in it — so
        // without this the bar reappears below both, which in the shell's column is a menu bar along
        // the bottom edge of the window. Clamped rather than trusted: the host is somebody else's,
        // and the only thing this knows is that the bar was meant to come before whatever was added
        // after it.
        if (bar.IndexInParent > slot) {
            host.Document.Move(bar, Math.Min(slot, host.Children.Count - 1));
        }

        // ⚠ Every menu on the bar, empty or not, unlike the submenus below. A menu is on the bar
        // because somebody described it there, and one that is empty this second is one whose
        // commands have not been registered yet — a plugin's, a document-dependent set — which is
        // precisely the case the model is built to tolerate. Dropping it would move the menus beside
        // it along the bar every time something registered, which is worse than an empty dropdown.
        foreach (var group in Model.Menus) {
            Fill(Bar.AddMenu(group.Title.Text), group);
        }
    }

    /// <summary>Whether a group would produce any lines at all right now.</summary>
    /// <remarks>
    ///     Asked at build time and not cached, because it is the same question the fill is about to
    ///     answer and the registry is a dictionary lookup per entry. A dynamic entry is asked, which
    ///     means its producer runs twice per rebuild — they are all `Select` over a list the shell
    ///     already holds.
    /// </remarks>
    static bool HasContent(MenuGroup group, CommandRegistry commands) {
        foreach (var entry in group.Entries) {
            var any = entry switch {
                MenuCommand(var id) => commands.TryGet(id, out _),
                MenuSubmenu(var child) => HasContent(child, commands),
                MenuDynamic(var ids) => ids().Any(id => commands.TryGet(id, out _)),
                _ => false
            };

            if (any) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds a context menu over a set of commands.</summary>
    /// <param name="document">The document it floats in.</param>
    /// <param name="commands">What can be run.</param>
    /// <param name="keys">What the shortcuts say.</param>
    /// <param name="commandIds">The lines, with <c>null</c> for a separator.</param>
    /// <returns>The menu, ready to be attached to something or opened at a point.</returns>
    /// <remarks>
    ///     Static and disposable-by-removal rather than a presenter of its own: a context menu
    ///     belongs to whatever raised it, is built when it is raised, and is not around long enough
    ///     for a command to be renamed underneath it.
    /// </remarks>
    public static ContextMenu Context(
        UiDocument document,
        CommandRegistry commands,
        KeyMap keys,
        params ReadOnlySpan<string?> commandIds
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(keys);

        document.ApplicationCommandResponder ??= commands;

        var menu = document.Root.Add<ContextMenu>();

        foreach (var id in commandIds) {
            if (id is null) {
                menu.AddSeparator();
                continue;
            }

            if (commands.TryGet(id, out var command)) {
                Line(menu, command, keys);
            }
        }

        return menu;
    }

    /// <summary>Builds a context menu from a described group, submenus and all.</summary>
    /// <param name="document">The document it floats in.</param>
    /// <param name="group">What is on it.</param>
    /// <param name="commands">What its lines run.</param>
    /// <param name="keys">What the shortcuts on it say.</param>
    /// <returns>The menu, ready to be attached to something or opened at a point.</returns>
    /// <remarks>
    ///     <para>
    ///         The overload above takes a flat list of ids, which is most context menus. This one is
    ///         for the ones with a submenu in them — the hierarchy's "3D Object" is eight commands
    ///         that would otherwise be eight lines of a thirteen-line menu.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Built once and kept, unlike a menu bar, which is rebuilt whenever the registry
    ///         changes.</b> A context menu is attached to a panel that outlives any single opening,
    ///         and rebuilding it would mean re-attaching it; the enablement is still applied as it
    ///         opens, so the part that goes stale is only the set of lines. A caller who registers
    ///         commands after building one gets a menu missing them, which is why every caller here
    ///         builds after <c>Commands</c> has run.
    ///     </para>
    /// </remarks>
    public static ContextMenu Context(
        UiDocument document,
        MenuGroup group,
        CommandRegistry commands,
        KeyMap keys
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(keys);

        document.ApplicationCommandResponder ??= commands;

        var menu = document.Root.Add<ContextMenu>();

        Fill(menu, group, commands, keys, null);
        return menu;
    }

    void Fill(Menu menu, MenuGroup group) => Fill(menu, group, commands, keys, menus);

    /// <summary>Puts a group's entries into a menu, and its submenus into menus of their own.</summary>
    /// <param name="menu">The menu.</param>
    /// <param name="group">What goes in it.</param>
    /// <param name="commands">What its lines run.</param>
    /// <param name="keys">What the shortcuts say.</param>
    /// <param name="track">
    ///     Every menu built, for a caller that rebuilds — or <see langword="null" /> for one that
    ///     does not. A context menu is removed with the panel that owns it and needs no list.
    /// </param>
    static void Fill(
        Menu menu,
        MenuGroup group,
        CommandRegistry commands,
        KeyMap keys,
        List<Menu>? track
    ) {
        track?.Add(menu);

        // ⚠ Separators are held back rather than added, and only emitted once something follows
        // them. A menu whose entries are ids, some of which no longer resolve, otherwise comes out
        // with a rule at the top, two in a row in the middle, and one hanging off the bottom.
        var pending = false;
        var any = false;

        foreach (var entry in group.Entries) {
            switch (entry) {
                case MenuSeparator:
                    pending = any;
                    break;

                // Empty submenus are skipped for the same reason empty menus are: a line with an
                // arrow on it that opens onto nothing is worse than no line.
                case MenuSubmenu(var child) when HasContent(child, commands):
                    Rule(menu, ref pending);
                    Fill(menu.AddSubmenu(child.Title.Text), child, commands, keys, track);

                    any = true;
                    break;

                case MenuCommand(var id) when commands.TryGet(id, out var command):
                    Rule(menu, ref pending);
                    Line(menu, command, keys);

                    any = true;
                    break;

                case MenuDynamic(var ids):
                    foreach (var id in ids()) {
                        if (!commands.TryGet(id, out var command)) {
                            continue;
                        }

                        Rule(menu, ref pending);
                        Line(menu, command, keys);

                        any = true;
                    }

                    break;

                default:
                    break;
            }
        }
    }

    static void Rule(Menu menu, ref bool pending) {
        if (pending) {
            menu.AddSeparator();
            pending = false;
        }
    }

    /// <summary>Adds one line for a command.</summary>
    /// <remarks>
    ///     ⚠ <b>The mark's geometry is set here, once, and not when the state changes.</b> An
    ///     <c>Icon</c> with no geometry draws nothing, so a menu that only toggled the mark's
    ///     <c>display</c> — which is what this did — showed a checked command with an empty twelve
    ///     pixel gutter and no tick in it. Which shape it is depends on whether the command is one
    ///     of a set: a tick for a toggle, a dot for a member of a
    ///     <see cref="EditorCommand.RadioGroup" />, because three ticks is not how a choice reads.
    /// </remarks>
    static MenuItem Line(Menu menu, EditorCommand command, KeyMap keys) {
        var item = menu.AddItem(command.CurrentTitle.Text);

        if (command.Checked is not null) {
            item.Mark.Geometry = command.RadioGroup is null ? ControlIcons.Check : EditorIcons.RadioMark;
        }

        // ⚠ The whole of the wiring, and it replaces two things rather than one: the click handler
        // that used to run the id, and the pass over every line that used to grey it. `Menu` asks
        // the route about each bound item as it opens — the same moment `Enable` ran — so the line
        // is right the instant it is on screen rather than on the frame after.
        //
        // After the mark's geometry, because setting it resolves at once and a checkable command
        // whose mark did not exist yet would be shown ticked with an empty gutter.
        item.Command = command.Id;

        // ⚠ Swapped out of the table's vocabulary before it is drawn: the keymap holds Ctrl+S and a
        // Mac has to read ⌘S, which is the key its user will actually press. See
        // `KeyChord.ForPlatform`.
        if (keys.ChordFor(command.Id) is { IsBound: true } chord) {
            var shown = chord.ForPlatform();
            item.ShowShortcut(shown.Key, shown.Modifiers);
        }

        return item;
    }
}
