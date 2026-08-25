// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

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

    /// <summary>Ditto, for the verbs that enter an editor mode.</summary>
    public static StringId CategoryMode { get; } = new("editor.category.mode", "Mode");

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

    /// <summary>Tab: enter the next editor mode along the strip.</summary>
    public static StringId NextMode { get; } = new("editor.command.mode.next", "Next Mode");

    /// <summary>Opens the search over assets, entities and settings.</summary>
    public static StringId CommandSearchEverywhere { get; } =
        new("editor.command.edit.search-everywhere", "Search Everywhere…");

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

    /// <summary>What start-up reports when the saved arrangement could not be put back.</summary>
    /// <remarks>
    ///     A corrupt layout file and a layout naming only panels that have gone both end here, and
    ///     the notice does not distinguish them: what somebody needs to know is that the arrangement
    ///     on screen is the default rather than theirs, and that re-arranging it is the way back.
    /// </remarks>
    public static StringId LayoutNotRestored { get; } =
        new("editor.notice.layout-not-restored", "Saved layout could not be restored — showing the default");

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

    /// <summary>Empties the console and the ring behind it.</summary>
    public static StringId ConsoleClear { get; } = new("editor.console.clear", "Clear");

    /// <summary>Folds identical lines into one row with a count.</summary>
    public static StringId ConsoleCollapse { get; } = new("editor.console.collapse", "Collapse");

    /// <summary>Empties it on the way into play mode.</summary>
    public static StringId ConsoleClearOnPlay { get; } = new("editor.console.clear-on-play", "Clear on Play");

    /// <summary>What the console's search box says when it is empty.</summary>
    public static StringId ConsoleSearch { get; } = new("editor.console.search", "Filter…");

    /// <summary>The category picker's "no filter" choice.</summary>
    public static StringId ConsoleAllCategories { get; } = new("editor.console.all-categories", "All Categories");

    /// <summary>What the detail pane says when no line is selected.</summary>
    public static StringId ConsoleNoSelection { get; } =
        new("editor.console.no-selection", "Select a line to see the whole record.");

    /// <summary>What it reports when a keybinding could not be taken.</summary>
    public static StringId KeyBindingConflict { get; } = new("editor.notice.binding-conflict", "That shortcut is already taken");

    /// <summary>The keybinding editor's tab.</summary>
    public static StringId PanelKeys { get; } = new("editor.panel.keybindings", "Keyboard Shortcuts");

    /// <summary>The message log's.</summary>
    public static StringId PanelMessages { get; } = new("editor.panel.messages", "Message Log");

    /// <summary>The preferences window's.</summary>
    public static StringId PanelPreferences { get; } = new("editor.panel.preferences", "Preferences");

    /// <summary>The project settings window's.</summary>
    public static StringId PanelProjectSettings { get; } = new("editor.panel.project-settings", "Project Settings");

    /// <summary>The plugin manager's.</summary>
    public static StringId PanelPlugins { get; } = new("editor.panel.plugins", "Plugins");

    /// <summary>The undo history's.</summary>
    public static StringId PanelHistory { get; } = new("editor.panel.history", "Undo History");

    /// <summary>What the keybinding editor's filter box says when it is empty.</summary>
    public static StringId KeysFilter { get; } = new("editor.keys.filter", "Filter commands…");

    /// <summary>Puts the panel into capture mode.</summary>
    public static StringId KeysRecord { get; } = new("editor.keys.record", "Press a Key…");

    /// <summary>What that button says while it is waiting.</summary>
    public static StringId KeysRecording { get; } = new("editor.keys.recording", "Waiting…");

    /// <summary>Unbinds the selected command.</summary>
    public static StringId KeysClear { get; } = new("editor.keys.clear", "Unbind");

    /// <summary>Puts one row back to the layer underneath.</summary>
    public static StringId KeysResetRow { get; } = new("editor.keys.reset-row", "Reset");

    /// <summary>Puts every row back.</summary>
    public static StringId KeysResetAll { get; } = new("editor.keys.reset-all", "Reset All");

    /// <summary>Reads a keymap file in.</summary>
    public static StringId KeysImport { get; } = new("editor.keys.import", "Import…");

    /// <summary>Writes one out.</summary>
    public static StringId KeysExport { get; } = new("editor.keys.export", "Export…");

    /// <summary>The command column.</summary>
    public static StringId KeysColumnCommand { get; } = new("editor.keys.column.command", "Command");

    /// <summary>The category column.</summary>
    public static StringId KeysColumnCategory { get; } = new("editor.keys.column.category", "Category");

    /// <summary>The shortcut column.</summary>
    public static StringId KeysColumnBinding { get; } = new("editor.keys.column.binding", "Shortcut");

    /// <summary>The column saying which layer a binding came from.</summary>
    public static StringId KeysColumnSource { get; } = new("editor.keys.column.source", "Source");

    /// <summary>What that column says for a binding the application shipped.</summary>
    public static StringId KeysSourceDefault { get; } = new("editor.keys.source.default", "Default");

    /// <summary>And for one the user made.</summary>
    public static StringId KeysSourceUser { get; } = new("editor.keys.source.user", "Yours");

    /// <summary>What the status line says with no row chosen.</summary>
    public static StringId KeysPickRow { get; } = new("editor.keys.pick-row", "Choose a command to rebind it.");

    /// <summary>And with one chosen.</summary>
    public static StringId KeysReady { get; } = new("editor.keys.ready", "Press a Key, or double-click the row.");

    /// <summary>And while it is waiting for one.</summary>
    public static StringId KeysWaiting { get; } =
        new("editor.keys.waiting", "Press the shortcut you want. Escape cancels.");

    /// <summary>What it says when the chord is taken.</summary>
    public static StringId KeysConflict { get; } =
        new("editor.keys.conflict", "{0} is already {1}. Press it again to take it.");

    /// <summary>What it says when a keymap file names a preset this editor has not got.</summary>
    public static StringId KeysUnknownPreset { get; } =
        new("editor.keys.unknown-preset", "There is no keymap preset called '{0}'.");

    /// <summary>The message log's "no filter" choice.</summary>
    public static StringId MessagesAllLevels { get; } = new("editor.messages.all-levels", "All Messages");

    /// <summary>Its errors-only choice.</summary>
    public static StringId MessagesErrors { get; } = new("editor.messages.errors", "Errors");

    /// <summary>Its warnings-only choice.</summary>
    public static StringId MessagesWarnings { get; } = new("editor.messages.warnings", "Warnings");

    /// <summary>Its successes-only choice.</summary>
    public static StringId MessagesSuccesses { get; } = new("editor.messages.successes", "Successes");

    /// <summary>Its information-only choice.</summary>
    public static StringId MessagesInfos { get; } = new("editor.messages.infos", "Information");

    /// <summary>What its detail pane says when no line is selected.</summary>
    public static StringId MessagesNoSelection { get; } =
        new("editor.messages.no-selection", "Select a message to see the whole of it.");

    /// <summary>What the settings window's search box says when it is empty.</summary>
    public static StringId SettingsSearch { get; } = new("editor.settings.search", "Search settings…");

    /// <summary>Puts one page back to its defaults.</summary>
    public static StringId SettingsResetPage { get; } = new("editor.settings.reset-page", "Reset Page");

    /// <summary>Throws away what has been typed since the last write.</summary>
    public static StringId SettingsRevert { get; } = new("editor.settings.revert", "Revert");

    /// <summary>Writes it.</summary>
    public static StringId SettingsApply { get; } = new("editor.settings.apply", "Apply");

    /// <summary>What the pane says when the search has matched nothing.</summary>
    public static StringId SettingsNoPage { get; } = new("editor.settings.no-page", "No settings match that.");

    /// <summary>What the plugin manager's filter box says when it is empty.</summary>
    public static StringId PluginsFilter { get; } = new("editor.plugins.filter", "Filter plugins…");

    /// <summary>Switches the selected plugin off.</summary>
    public static StringId PluginsDisable { get; } = new("editor.plugins.disable", "Disable");

    /// <summary>And back on.</summary>
    public static StringId PluginsEnable { get; } = new("editor.plugins.enable", "Enable");

    /// <summary>Unloads it and loads it again from disk.</summary>
    public static StringId PluginsReload { get; } = new("editor.plugins.reload", "Reload");

    /// <summary>The name column.</summary>
    public static StringId PluginsColumnName { get; } = new("editor.plugins.column.name", "Plugin");

    /// <summary>The id column.</summary>
    public static StringId PluginsColumnId { get; } = new("editor.plugins.column.id", "Id");

    /// <summary>The version column.</summary>
    public static StringId PluginsColumnVersion { get; } = new("editor.plugins.column.version", "Version");

    /// <summary>The state column.</summary>
    public static StringId PluginsColumnState { get; } = new("editor.plugins.column.state", "State");

    /// <summary>The author column.</summary>
    public static StringId PluginsColumnAuthor { get; } = new("editor.plugins.column.author", "Author");

    /// <summary>What the state column says for a running plugin.</summary>
    public static StringId PluginsStateActive { get; } = new("editor.plugins.state.active", "Active");

    /// <summary>For one that is switched off.</summary>
    public static StringId PluginsStateDisabled { get; } = new("editor.plugins.state.disabled", "Disabled");

    /// <summary>For one that did not start.</summary>
    public static StringId PluginsStateFailed { get; } = new("editor.plugins.state.failed", "Failed");

    /// <summary>For one that has been taken back out.</summary>
    public static StringId PluginsStateUnloaded { get; } = new("editor.plugins.state.unloaded", "Unloaded");

    /// <summary>What the detail line says when nothing is installed.</summary>
    public static StringId PluginsNone { get; } =
        new("editor.plugins.none", "No plugins are installed. Put one in the project's Plugins folder.");

    /// <summary>And when nothing is selected.</summary>
    public static StringId PluginsPickRow { get; } = new("editor.plugins.pick-row", "Choose a plugin.");

    /// <summary>And for one the user switched off.</summary>
    public static StringId PluginsSwitchedOff { get; } =
        new("editor.plugins.switched-off", "You switched this off. Enable puts it back and starts it.");

    /// <summary>And for one its own manifest switches off.</summary>
    public static StringId PluginsManifestOff { get; } =
        new("editor.plugins.manifest-off", "Its plugin.yaml says enabled: false, which is the author's switch rather than yours.");

    /// <summary>What the undo history's strip says.</summary>
    public static StringId HistoryHint { get; } =
        new("editor.history.hint", "Choosing a step undoes back to it.");

    /// <summary>What the history calls the point before anything was done.</summary>
    public static StringId HistoryOriginal { get; } = new("editor.history.original", "Opened");

    /// <summary>The heading over the startup project browser.</summary>
    public static StringId ProjectsTitle { get; } = new("editor.projects.title", "Open a Project");

    /// <summary>What it says when nothing has been opened yet.</summary>
    public static StringId ProjectsEmpty { get; } =
        new("editor.projects.empty", "No projects yet. Browse for one, or start a new one.");

    /// <summary>Opens one that is already on disk.</summary>
    public static StringId ProjectsBrowse { get; } = new("editor.projects.browse", "Browse…");

    /// <summary>Starts one.</summary>
    public static StringId ProjectsNew { get; } = new("editor.projects.new", "New Project…");

    /// <summary>What a row says when the directory has gone.</summary>
    public static StringId ProjectsMissing { get; } = new("editor.projects.missing", "not found");

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
        NextMode,
        CategoryMode,
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
        CommandSearchEverywhere,
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
        LayoutNotRestored,
        DialogOk,
        DialogCancel,
        StatusSelection,
        StatusFrameTime,
        ConsoleClear,
        ConsoleCollapse,
        ConsoleClearOnPlay,
        ConsoleSearch,
        ConsoleAllCategories,
        ConsoleNoSelection,
        KeyBindingConflict,
        PanelKeys,
        PanelMessages,
        PanelPreferences,
        PanelProjectSettings,
        PanelPlugins,
        PanelHistory,
        KeysFilter,
        KeysRecord,
        KeysRecording,
        KeysClear,
        KeysResetRow,
        KeysResetAll,
        KeysImport,
        KeysExport,
        KeysColumnCommand,
        KeysColumnCategory,
        KeysColumnBinding,
        KeysColumnSource,
        KeysSourceDefault,
        KeysSourceUser,
        KeysPickRow,
        KeysReady,
        KeysWaiting,
        KeysConflict,
        KeysUnknownPreset,
        MessagesAllLevels,
        MessagesErrors,
        MessagesWarnings,
        MessagesSuccesses,
        MessagesInfos,
        MessagesNoSelection,
        SettingsSearch,
        SettingsResetPage,
        SettingsRevert,
        SettingsApply,
        SettingsNoPage,
        PluginsFilter,
        PluginsDisable,
        PluginsEnable,
        PluginsReload,
        PluginsColumnName,
        PluginsColumnId,
        PluginsColumnVersion,
        PluginsColumnState,
        PluginsColumnAuthor,
        PluginsStateActive,
        PluginsStateDisabled,
        PluginsStateFailed,
        PluginsStateUnloaded,
        PluginsNone,
        PluginsPickRow,
        PluginsSwitchedOff,
        PluginsManifestOff,
        HistoryHint,
        HistoryOriginal,
        ProjectsTitle,
        ProjectsEmpty,
        ProjectsBrowse,
        ProjectsNew,
        ProjectsMissing
    ];

    /// <summary>A catalog holding every string the editor declares, for a translator to start from.</summary>
    /// <param name="language">What to call the new catalog.</param>
    /// <returns>The catalog, filled with the source text.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="All" /> and not the control set's declarations.</b> A control's label is
    ///     <c>Vixen.Ui.Controls.ControlStrings</c>'s to export, and an editor that folded them into
    ///     its own template would hand a translator two files that disagree about who owns
    ///     <c>ui.control.dialog.close</c>. <see cref="Strings.Template" /> takes any number of
    ///     declaration lists, so a shell that wants one file passes both.
    /// </remarks>
    public static StringCatalog Template(string language) => Strings.Template(language, All);
}
