// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>Every string the shell shows, declared once.</summary>
/// <remarks>
///     <para>
///         The shape <c>Strings.Resource</c> will generate — an id, the source text, and a list of
///         all of them — written by hand until it does, in the same way <c>Samples/02-HelloUi</c>
///         hand-writes the descriptor its generator would emit. Nothing at a call site changes when
///         the generator lands.
///     </para>
///     <para>
///         ⚠ <b><see cref="All" /> is spelled out rather than reflected over.</b> It feeds
///         <see cref="Strings.Template" />, which is what a translator starts from, and a list
///         gathered by walking the fields at run time would be a list this assembly's trimming
///         settings are entitled to shorten. The duplication is the cost of the id table being
///         data; the generator removes it.
///     </para>
/// </remarks>
public static class EditorStrings {
    /// <summary>The <c>File</c> menu.</summary>
    public static StringId MenuFile { get; } = new("editor.menu.file", "File");

    /// <summary>The <c>Edit</c> menu.</summary>
    public static StringId MenuEdit { get; } = new("editor.menu.edit", "Edit");

    /// <summary>The <c>View</c> menu.</summary>
    public static StringId MenuView { get; } = new("editor.menu.view", "View");

    /// <summary>The <c>Window</c> menu.</summary>
    public static StringId MenuWindow { get; } = new("editor.menu.window", "Window");

    /// <summary>The <c>Help</c> menu.</summary>
    public static StringId MenuHelp { get; } = new("editor.menu.help", "Help");

    /// <summary>The <c>Assets</c> menu.</summary>
    public static StringId MenuAssets { get; } = new("editor.menu.assets", "Assets");

    /// <summary>The <c>Entity</c> menu — Unreal's Actor, Unity's GameObject.</summary>
    public static StringId MenuEntity { get; } = new("editor.menu.entity", "Entity");

    /// <summary>The <c>Play</c> menu.</summary>
    public static StringId MenuPlay { get; } = new("editor.menu.play", "Play");

    /// <summary>The <c>Build</c> menu.</summary>
    public static StringId MenuBuild { get; } = new("editor.menu.build", "Build");

    /// <summary>The <c>Tools</c> menu.</summary>
    public static StringId MenuTools { get; } = new("editor.menu.tools", "Tools");

    /// <summary>The <c>Open Recent</c> submenu.</summary>
    public static StringId MenuRecent { get; } = new("editor.menu.recent", "Open Recent");

    /// <summary>The <c>Create</c> submenu, under Assets.</summary>
    public static StringId MenuCreate { get; } = new("editor.menu.create", "Create");

    /// <summary>The <c>Layout</c> submenu.</summary>
    public static StringId MenuLayout { get; } = new("editor.menu.layout", "Layout");

    /// <summary>The <c>Panels</c> submenu.</summary>
    public static StringId MenuPanels { get; } = new("editor.menu.panels", "Panels");

    /// <summary>The command category a file command is filed under in the palette.</summary>
    public static StringId CategoryFile { get; } = new("editor.category.file", "File");

    /// <summary>Ditto, for editing.</summary>
    public static StringId CategoryEdit { get; } = new("editor.category.edit", "Edit");

    /// <summary>Ditto, for the view.</summary>
    public static StringId CategoryView { get; } = new("editor.category.view", "View");

    /// <summary>Ditto, for panels.</summary>
    public static StringId CategoryPanel { get; } = new("editor.category.panel", "Panel");

    /// <summary>Ditto, for help.</summary>
    public static StringId CategoryHelp { get; } = new("editor.category.help", "Help");

    /// <summary>Makes a new project.</summary>
    public static StringId CommandNewProject { get; } = new("editor.command.file.new-project", "New Project…");

    /// <summary>Opens one.</summary>
    public static StringId CommandOpenProject { get; } = new("editor.command.file.open-project", "Open Project…");

    /// <summary>Saves the open document.</summary>
    public static StringId CommandSave { get; } = new("editor.command.file.save", "Save");

    /// <summary>Saves all of them.</summary>
    public static StringId CommandSaveAll { get; } = new("editor.command.file.save-all", "Save All");

    /// <summary>Closes the editor.</summary>
    public static StringId CommandExit { get; } = new("editor.command.file.exit", "Exit");

    /// <summary>Undoes the last change.</summary>
    public static StringId CommandUndo { get; } = new("editor.command.edit.undo", "Undo");

    /// <summary>Redoes it.</summary>
    public static StringId CommandRedo { get; } = new("editor.command.edit.redo", "Redo");

    /// <summary>Opens the settings.</summary>
    public static StringId CommandPreferences { get; } = new("editor.command.edit.preferences", "Preferences…");

    /// <summary>Opens the command palette.</summary>
    public static StringId CommandPalette { get; } = new("editor.command.view.palette", "Command Palette…");

    /// <summary>Puts the arrangement back to the preset it started from.</summary>
    public static StringId CommandResetLayout { get; } = new("editor.command.view.reset-layout", "Reset Layout");

    /// <summary>Saves the arrangement under a name.</summary>
    public static StringId CommandSaveLayout { get; } = new("editor.command.view.save-layout", "Save Layout…");

    /// <summary>Takes the panel the user is in out into a window of its own.</summary>
    public static StringId CommandFloatPanel { get; } = new("editor.command.view.float-panel", "Float Panel");

    /// <summary>Switches between the light and dark themes.</summary>
    public static StringId CommandToggleTheme { get; } = new("editor.command.view.toggle-theme", "Toggle Dark Theme");

    /// <summary>Closes the panel the user is in.</summary>
    public static StringId CommandClosePanel { get; } = new("editor.command.view.close-panel", "Close Panel");

    /// <summary>Moves to the next tab of its group.</summary>
    public static StringId CommandNextTab { get; } = new("editor.command.view.next-tab", "Next Tab");

    /// <summary>And the previous one.</summary>
    public static StringId CommandPreviousTab { get; } = new("editor.command.view.previous-tab", "Previous Tab");

    /// <summary>Says what version this is.</summary>
    public static StringId CommandAbout { get; } = new("editor.command.help.about", "About Vixen");

    /// <summary>Opens the manual.</summary>
    public static StringId CommandDocumentation { get; } = new("editor.command.help.documentation", "Documentation");

    /// <summary>What the palette's field says when it is empty.</summary>
    public static StringId PalettePlaceholder { get; } = new("editor.palette.placeholder", "Type a command, asset or setting…");

    /// <summary>What the palette says when nothing matches.</summary>
    public static StringId PaletteEmpty { get; } = new("editor.palette.empty", "No matches");

    /// <summary>The heading over the running background work.</summary>
    public static StringId TasksTitle { get; } = new("editor.tasks.title", "Background Tasks");

    /// <summary>What the task list says when there is none.</summary>
    public static StringId TasksIdle { get; } = new("editor.tasks.idle", "Nothing running");

    /// <summary>Stops one.</summary>
    public static StringId TasksCancel { get; } = new("editor.tasks.cancel", "Cancel");

    /// <summary>What a task that was cancelled reports.</summary>
    public static StringId TasksCancelled { get; } = new("editor.tasks.cancelled", "Cancelled");

    /// <summary>What a task that threw reports.</summary>
    public static StringId TasksFailed { get; } = new("editor.tasks.failed", "Failed");

    /// <summary>The heading over the messages that have been shown.</summary>
    public static StringId NotificationsTitle { get; } = new("editor.notifications.title", "Notifications");

    /// <summary>What the notification list says when there are none.</summary>
    public static StringId NotificationsEmpty { get; } = new("editor.notifications.empty", "No notifications");

    /// <summary>Throws them all away.</summary>
    public static StringId NotificationsClear { get; } = new("editor.notifications.clear", "Clear All");

    /// <summary>What the shell reports after putting the arrangement back.</summary>
    public static StringId LayoutReset { get; } = new("editor.notice.layout-reset", "Layout reset");

    /// <summary>The confirming button on a dialog that asks nothing more specific.</summary>
    public static StringId DialogOk { get; } = new("editor.dialog.ok", "OK");

    /// <summary>The one that backs out of it.</summary>
    public static StringId DialogCancel { get; } = new("editor.dialog.cancel", "Cancel");

    /// <summary>What the status bar calls the selection when there is more than one thing in it.</summary>
    /// <remarks>
    ///     ⚠ <b>A format string with a placeholder, which is what makes it translatable at all.</b>
    ///     "3" and " selected" concatenated is a sentence no translator can reorder, and there are
    ///     languages where the number does not come first.
    /// </remarks>
    public static StringId StatusSelection { get; } = new("editor.status.selection", "{0} selected");

    /// <summary>What the status bar's frame-time cell says.</summary>
    public static StringId StatusFrameTime { get; } = new("editor.status.frame-time", "{0} ms");

    /// <summary>What it reports when a keybinding could not be taken.</summary>
    public static StringId KeyBindingConflict { get; } = new("editor.notice.binding-conflict", "That shortcut is already taken");

    /// <summary>Every string above, for a translator to start from.</summary>
    public static IReadOnlyList<StringId> All { get; } = [
        MenuFile,
        MenuEdit,
        MenuView,
        MenuWindow,
        MenuHelp,
        MenuAssets,
        MenuEntity,
        MenuPlay,
        MenuBuild,
        MenuTools,
        MenuRecent,
        MenuCreate,
        MenuLayout,
        MenuPanels,
        CategoryFile,
        CategoryEdit,
        CategoryView,
        CategoryPanel,
        CategoryHelp,
        CommandNewProject,
        CommandOpenProject,
        CommandSave,
        CommandSaveAll,
        CommandExit,
        CommandUndo,
        CommandRedo,
        CommandPreferences,
        CommandPalette,
        CommandResetLayout,
        CommandSaveLayout,
        CommandFloatPanel,
        CommandToggleTheme,
        CommandClosePanel,
        CommandNextTab,
        CommandPreviousTab,
        CommandAbout,
        CommandDocumentation,
        PalettePlaceholder,
        PaletteEmpty,
        TasksTitle,
        TasksIdle,
        TasksCancel,
        TasksCancelled,
        TasksFailed,
        NotificationsTitle,
        NotificationsEmpty,
        NotificationsClear,
        LayoutReset,
        DialogOk,
        DialogCancel,
        StatusSelection,
        StatusFrameTime,
        KeyBindingConflict
    ];
}
