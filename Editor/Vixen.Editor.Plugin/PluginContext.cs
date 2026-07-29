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
}
