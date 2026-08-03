// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Plugin;

/// <summary>What a plugin is handed: the editor, and a scope that remembers what it added.</summary>
/// <remarks>
///     <para>
///         <b>Every <c>Add…</c> here does two things — it registers, and it records the undo.</b>
///         That is the difference between this and reaching for <c>context.Shell.Commands.Add</c>
///         directly, which is allowed and is occasionally right: what you give up is unloadability,
///         because a command the editor still holds is a reference into the plugin's assembly and
///         the assembly is then loaded for the rest of the session. <see cref="OnUnload" /> is how
///         anything registered the long way is brought back into the scope.
///     </para>
///     <para>
///         ⚠ <b>A registration that collides throws, and the loader lets it.</b>
///         <c>CommandRegistry.Add</c> refuses a second <c>file.save</c> rather than replacing it or
///         ignoring it, so a plugin naming a command somebody already owns fails to activate and is
///         reported with both names in the message. Prefixing your ids with your plugin id is the
///         way not to find this out from a user.
///     </para>
///     <para>
///         ⚠ <b>Everything here runs on the frame thread.</b> The shell's registries are not
///         thread-safe and nothing in the editor's loop locks them. A plugin doing real work should
///         put it on <c>Shell.Tasks</c>, which is the background-task manager the importer and the
///         content build already use, and touch the interface from the continuation the manager
///         pumps.
///     </para>
/// </remarks>
public sealed class PluginContext {
    internal PluginContext(
        PluginDescriptor descriptor,
        EditorShell shell,
        PluginServices services,
        PluginRegistrations registrations
    ) {
        Descriptor = descriptor;
        Shell = shell;
        Services = services;
        Registrations = registrations;
    }

    /// <summary>The plugin, as it was found on disk.</summary>
    public PluginDescriptor Descriptor { get; }

    /// <summary>What it says about itself.</summary>
    public PluginManifest Manifest => Descriptor.Manifest;

    /// <summary>The plugin's own folder, which is where its assets and its settings belong.</summary>
    public string Directory => Descriptor.Directory;

    /// <summary>The editor's chrome: commands, panels, menus, notifications, background tasks.</summary>
    public EditorShell Shell { get; }

    /// <summary>The extension points that are not the shell's. See <see cref="PluginServices" />.</summary>
    public PluginServices Services { get; }

    /// <summary>What will be undone when the plugin is unloaded.</summary>
    public PluginRegistrations Registrations { get; }

    /// <summary>Adds a command, and takes it out again on unload.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The command.</returns>
    public EditorCommand AddCommand(EditorCommand command) {
        ArgumentNullException.ThrowIfNull(command);

        Shell.Commands.Add(command);
        Registrations.Add(() => Shell.Commands.Remove(command.Id));

        return command;
    }

    /// <summary>Adds a command built from its parts.</summary>
    /// <param name="id">Its id. Prefix it with the plugin's id.</param>
    /// <param name="title">What it is called on screen.</param>
    /// <param name="run">What it does.</param>
    /// <returns>The command.</returns>
    public EditorCommand AddCommand(string id, StringId title, Action run) =>
        AddCommand(new EditorCommand(id, title, run));

    /// <summary>Adds a panel and the command that shows it, and takes both out again on unload.</summary>
    /// <param name="descriptor">What the panel is.</param>
    /// <returns>The descriptor.</returns>
    /// <remarks>
    ///     ⚠ <b>Unloading closes it if it is open.</b> A docked panel whose contents were built by a
    ///     plugin that is no longer there would be an empty frame the user cannot get rid of, and
    ///     its elements are references into the plugin's assembly besides. The saved layout keeps
    ///     the panel's place, so reloading the plugin puts it back where it was — the same bargain
    ///     the keymap makes with a plugin's shortcut.
    /// </remarks>
    public PanelDescriptor AddPanel(PanelDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(descriptor);

        Shell.RegisterPanel(descriptor);
        Registrations.Add(() => Shell.UnregisterPanel(descriptor.Id));

        return descriptor;
    }

    /// <summary>Adds a panel from its parts.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="title">What its tab says.</param>
    /// <param name="build">Fills it.</param>
    /// <returns>The descriptor.</returns>
    public PanelDescriptor AddPanel(string id, StringId title, Action<DockPanel> build) =>
        AddPanel(new PanelDescriptor(id, title, build));

    /// <summary>Adds an editor mode and its button on the mode bar, and takes both out on unload.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The mode.</returns>
    /// <remarks>
    ///     <para>
    ///         The extension point doc 20's A1 asks for by name: a mode is "a statement about what the
    ///         viewport's input means right now", which is precisely the thing a terrain sculptor, a
    ///         foliage painter or a level-design toolset needs and precisely the thing that cannot be
    ///         expressed as a command.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Unloading the mode you are in leaves it first.</b> <c>EditorModes.Remove</c> falls
    ///         back to the first remaining mode, so a plugin unloaded mid-gesture cannot leave the
    ///         viewport's input meaning something that is no longer loaded — and the mode's own
    ///         <c>Unregister</c> is what takes its commands out, which is what keeps the plugin's
    ///         assembly collectable.
    ///     </para>
    /// </remarks>
    public IEditorMode AddMode(IEditorMode mode) {
        ArgumentNullException.ThrowIfNull(mode);

        Shell.Modes.Add(mode);
        Registrations.Add(() => Shell.Modes.Remove(mode.Id));

        return mode;
    }

    /// <summary>Adds a named arrangement and the command that applies it.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="title">What the menu line says.</param>
    /// <param name="layout">Builds it.</param>
    public void AddLayout(string name, StringId title, Func<DockLayout> layout) {
        Shell.RegisterLayout(name, title, layout);
        Registrations.Add(() => Shell.UnregisterLayout(name));
    }

    /// <summary>Adds a menu to the bar, and takes it off again on unload.</summary>
    /// <param name="title">Its name on the bar.</param>
    /// <param name="index">Where along the bar, clamped to the ends. Defaults to just before Help.</param>
    /// <returns>The menu, whose entries are added with <see cref="MenuGroup.Add(string)" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The bar is rebuilt, here and on every other menu change.</b> A menu described after
    ///     the presenter built the bar is a menu nobody sees, and a plugin activating three seconds
    ///     after start-up is always after the presenter built the bar.
    /// </remarks>
    public MenuGroup AddMenu(StringId title, int index = -1) {
        var group = index < 0
            ? Shell.Menus.InsertMenu(Math.Max(Shell.Menus.Menus.Count - 1, 0), title)
            : Shell.Menus.InsertMenu(index, title);

        Registrations.Add(
            () => {
                Shell.Menus.Remove(group);
                Shell.MenuBar.Rebuild();
            }
        );

        Shell.MenuBar.Rebuild();
        return group;
    }

    /// <summary>One of the editor's own menus, by the id its title carries.</summary>
    /// <param name="titleId">The <see cref="StringId" />'s id — <c>editor.menu.scene</c>, say.</param>
    /// <returns>The menu, or <see langword="null" /> when this host has no such menu.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>What a feature needs in order to put its verbs where they belong.</b> A mode's
    ///         commands go in the menu the thing they act on already has — the blockout tools in
    ///         Scene, the diagnostics panels in Tools — because a top-level menu per feature is a
    ///         menu bar that grows a heading for every plugin somebody installs, and
    ///         <c>IEditorMode</c>'s own remarks say why a menu that appears and disappears with a
    ///         mode is worse still.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>By id and not by displayed name</b>, so a localised editor finds the same menu —
    ///         and returning null rather than creating one, because a plugin that silently made a
    ///         second "Scene" menu when it misspelled the id would be a plugin whose entries are
    ///         somewhere nobody looks.
    ///     </para>
    /// </remarks>
    public MenuGroup? FindMenu(string titleId) {
        ArgumentException.ThrowIfNullOrEmpty(titleId);

        foreach (var menu in Shell.Menus.Menus) {
            if (string.Equals(menu.Title.Id, titleId, StringComparison.Ordinal)) {
                return menu;
            }
        }

        return null;
    }

    /// <summary>Adds a submenu to a menu, and takes it off again on unload.</summary>
    /// <param name="parent">Which menu. One from <see cref="FindMenu" />, or from <see cref="AddMenu" />.</param>
    /// <param name="title">What its line says.</param>
    /// <param name="index">Where among the lines, clamped to the ends, or -1 for the end.</param>
    /// <returns>The submenu, whose entries are added the same way as a menu's.</returns>
    /// <remarks>
    ///     ⚠ <b>Say where, using <see cref="MenuGroup.IndexOfSubmenu" /> rather than a number.</b> A
    ///     feature that could only append would reorder the menu it is joining the moment it stopped
    ///     being compiled in, which is a visible change to somebody's editor caused by a refactor
    ///     they cannot see.
    /// </remarks>
    public MenuGroup AddSubmenu(MenuGroup parent, StringId title, int index = -1) {
        ArgumentNullException.ThrowIfNull(parent);

        var group = index < 0 ? parent.AddSubmenu(title) : parent.InsertSubmenu(index, title);
        var entry = parent.Entries[index < 0 ? ^1 : Math.Clamp(index, 0, parent.Entries.Count - 1)];

        Registrations.Add(
            () => {
                parent.Remove(entry);
                Shell.MenuBar.Rebuild();
            }
        );

        Shell.MenuBar.Rebuild();
        return group;
    }

    /// <summary>Adds a line to an existing menu, and takes it out again on unload.</summary>
    /// <param name="group">Which menu. <c>Shell.View</c>, or one from <see cref="AddMenu" />.</param>
    /// <param name="commandId">What it runs.</param>
    /// <remarks>
    ///     For putting an entry in one of the editor's own menus, which is where a plugin's command
    ///     usually belongs — a Tools menu per plugin is how a menu bar becomes unusable.
    /// </remarks>
    public void AddMenuItem(MenuGroup group, string commandId) {
        ArgumentNullException.ThrowIfNull(group);

        var entry = group.Add(commandId).Entries[^1];

        Registrations.Add(
            () => {
                group.Remove(entry);
                Shell.MenuBar.Rebuild();
            }
        );

        Shell.MenuBar.Rebuild();
    }

    /// <summary>Suggests a keyboard shortcut for one of the plugin's commands.</summary>
    /// <param name="commandId">The command.</param>
    /// <param name="chord">The chord.</param>
    /// <returns>What happened — a chord somebody already has is refused rather than taken.</returns>
    /// <remarks>
    ///     ⚠ <b>Not undone on unload, deliberately.</b> <c>KeyMap</c> keeps a binding whose command
    ///     has gone away so that reinstalling the plugin restores the user's shortcut instead of
    ///     quietly dropping it, and a default the plugin re-declares on the next load lands on the
    ///     binding that is already there.
    /// </remarks>
    public BindResult AddDefaultBinding(string commandId, KeyChord chord) =>
        Shell.Keys.SetDefault(commandId, chord);

    /// <summary>Records something to undo when the plugin is unloaded.</summary>
    /// <param name="action">How to undo it.</param>
    /// <remarks>
    ///     The escape hatch, and the thing to reach for after registering with anything this class
    ///     has no method for — a drawer, an importer, a node type out of
    ///     <see cref="PluginServices" />. Everything a plugin leaves behind keeps its assembly
    ///     loaded, so a registration with no matching <c>OnUnload</c> is a leak with no symptom.
    /// </remarks>
    public void OnUnload(Action action) => Registrations.Add(action);

    /// <summary>Takes ownership of a registration scope, so unloading disposes it.</summary>
    /// <typeparam name="T">The scope type.</typeparam>
    /// <param name="scope">What was returned by whatever the plugin registered with.</param>
    /// <returns>The same scope, so a plugin can keep it if it also wants to undo the thing early.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The half of doc 36 § D4 that is about contributions</b>, and it is one method rather
    ///         than the eight the table names. <c>IEditorRegistry.Add</c> already hands back the
    ///         removal, so an inspector, a Create ▸ entry, a scene-view tool, a gizmo, a settings page
    ///         and a preview all register the same way:
    ///     </para>
    ///     <code language="csharp">
    ///     var registry = context.Services.Require&lt;IEditorRegistry&gt;();
    ///
    ///     context.Owns(registry.Add(new NewAssetKind("mine.create-thing", "Thing", ".thing", "New Thing")));
    ///     context.Owns(registry.Add(new CustomInspector(typeof(Thing), BuildThing)));
    ///     context.Owns(registry.Add(new SceneTool("mine.paint", "Paint", input)));
    ///     </code>
    ///     <para>
    ///         ⚠ <b>A method per contribution kind would put the whole kind list in this assembly</b>,
    ///         which would mean the plugin contract referencing every feature assembly that owns one —
    ///         the shape of problem F2 reports about the application, one layer down. A contribution
    ///         kind is a record in the assembly that owns it, and nothing here changes when one is
    ///         added.
    ///     </para>
    /// </remarks>
    public T Owns<T>(T scope) where T : IDisposable {
        ArgumentNullException.ThrowIfNull(scope);

        Registrations.Add(() => scope.Dispose());

        return scope;
    }

    /// <summary>Registers with one of the host's own registries, and takes it back out on unload.</summary>
    /// <typeparam name="TService">The registry's type, as <see cref="PluginServices" /> published it.</typeparam>
    /// <param name="register">What to add.</param>
    /// <param name="unregister">How to take it out again.</param>
    /// <returns>The service, so a plugin registering several things does the lookup once.</returns>
    /// <exception cref="PluginException">The host published no service of that type.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The other half of D4, for the extension points that already have a registry.</b>
    ///         Drawers belong to <c>DrawerRegistry</c>, described types to <c>InspectorRegistry</c>,
    ///         importers to the pipeline's own list — each is the one place its thing is declared, and
    ///         copying them into the contribution registry would mean a plugin's drawer landing in
    ///         whichever of two the inspector was not reading. F10 is what that looks like at scale.
    ///     </para>
    ///     <code language="csharp">
    ///     context.With&lt;DrawerRegistry&gt;(
    ///         drawers => drawers.ForType&lt;Thing&gt;(drawer),
    ///         drawers => drawers.Remove(drawer)
    ///     );
    ///     </code>
    ///     <para>
    ///         ⚠ <b>F4 called this "mutating a static", and the fix is that the host says which
    ///         registry.</b> A plugin reaching for <c>DrawerRegistry.Default</c> writes to a process
    ///         global whatever the host intended; asking for the published one means a host running
    ///         two editors, or a test running two plugins, gets two answers rather than one shared one.
    ///     </para>
    /// </remarks>
    public TService With<TService>(Action<TService> register, Action<TService> unregister)
        where TService : class {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(unregister);

        var service = Services.Require<TService>();

        register(service);
        Registrations.Add(() => unregister(service));

        return service;
    }
}
