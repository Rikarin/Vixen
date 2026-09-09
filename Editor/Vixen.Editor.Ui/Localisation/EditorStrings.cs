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
    /// <remarks>
    ///     ⚠ <b>The text is the scene's and not the word "Save", because the running editor's
    ///     <c>file.save</c> said <c>Save Scene</c> and this said <c>Save</c>.</b> Registered with a
    ///     hand-built <c>new StringId("editor.command.save", "Save Scene")</c>, so a translator's
    ///     template carried <c>editor.command.file.save</c> and the editor looked up
    ///     <c>editor.command.save</c> — the <c>CommandUndo</c>/<c>CommandRedo</c> defect
    ///     <c>CheckStrings</c> was written for, still live, and invisible to it because the census
    ///     only measured ids nothing declared.
    /// </remarks>
    public static StringId CommandSave { get; } = new("editor.command.file.save", "Save Scene");

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

    /// <summary>What the mode strip calls the mode that is no mode.</summary>
    public static StringId ModeSelect { get; } = new("editor.mode.select", "Select");

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

    /// <summary>The button that backs out of a dialog the shell put up.</summary>
    /// <remarks>
    ///     ⚠ <b>There is no <c>DialogOk</c> beside it, and the asymmetry is the point.</b> The
    ///     confirming button on a shell dialog always says something more specific than OK — Open,
    ///     Replace, Discard — so a generic one was declared, never used, and sat in every
    ///     translator's template as a word the editor does not say. The control set's own default
    ///     confirm is <c>ControlStrings.DialogConfirm</c>, which is a different string in a different
    ///     assembly's file.
    /// </remarks>
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

    /// <summary>The <c>Assets</c> command category.</summary>
    public static StringId CategoryAssets { get; } = new("editor.category.assets", "Assets");

    /// <summary>The <c>Blockout</c> command category.</summary>
    public static StringId CategoryBlockout { get; } = new("editor.category.blockout", "Blockout");

    /// <summary>The <c>Build</c> command category.</summary>
    public static StringId CategoryBuild { get; } = new("editor.category.build", "Build");

    /// <summary>The <c>Create</c> command category.</summary>
    public static StringId CategoryCreate { get; } = new("editor.category.create", "Create");

    /// <summary>The <c>Entity</c> command category.</summary>
    public static StringId CategoryEntity { get; } = new("editor.category.entity", "Entity");

    /// <summary>The <c>Foliage</c> command category.</summary>
    public static StringId CategoryFoliage { get; } = new("editor.category.foliage", "Foliage");

    /// <summary>The <c>Play</c> command category.</summary>
    public static StringId CategoryPlay { get; } = new("editor.category.play", "Play");

    /// <summary>The <c>Scene</c> command category.</summary>
    public static StringId CategoryScene { get; } = new("editor.category.scene", "Scene");

    /// <summary>The <c>Terrain</c> command category.</summary>
    public static StringId CategoryTerrain { get; } = new("editor.category.terrain", "Terrain");

    /// <summary>The <c>Tools</c> command category.</summary>
    public static StringId CategoryTools { get; } = new("editor.category.tools", "Tools");

    /// <summary>The <c>Water</c> command category.</summary>
    public static StringId CategoryWater { get; } = new("editor.category.water", "Water");

    /// <summary>The <c>Bake Mesh Maps…</c> command.</summary>
    public static StringId CommandAssetsBakeMeshMaps { get; } =
        new("editor.command.assets.bake-mesh-maps", "Bake Mesh Maps…");

    /// <summary>The <c>New Asset…</c> command.</summary>
    public static StringId CommandAssetsCreate { get; } = new("editor.command.assets.create", "New Asset…");

    /// <summary>The <c>Revert to Source Control</c> command.</summary>
    public static StringId CommandAssetsRevert { get; } =
        new("editor.command.assets.revert", "Revert to Source Control");

    /// <summary>The <c>Delete</c> command.</summary>
    public static StringId CommandAssetsDelete { get; } = new("editor.command.assets.delete", "Delete");

    /// <summary>The <c>Find References</c> command.</summary>
    public static StringId CommandAssetsFindReferences { get; } =
        new("editor.command.assets.find-references", "Find References");

    /// <summary>The <c>Import Assets…</c> command.</summary>
    public static StringId CommandAssetsImportFiles { get; } =
        new("editor.command.assets.import-files", "Import Assets…");

    /// <summary>The <c>Move To…</c> command.</summary>
    public static StringId CommandAssetsMoveTo { get; } = new("editor.command.assets.move-to", "Move To…");

    /// <summary>The <c>New Folder</c> command.</summary>
    public static StringId CommandAssetsNewFolder { get; } = new("editor.command.assets.new-folder", "New Folder");

    /// <summary>The <c>Open</c> command.</summary>
    public static StringId CommandAssetsOpen { get; } = new("editor.command.assets.open", "Open");

    /// <summary>The <c>Reimport</c> command.</summary>
    public static StringId CommandAssetsReimport { get; } = new("editor.command.assets.reimport", "Reimport");

    /// <summary>The <c>Reimport All</c> command.</summary>
    public static StringId CommandAssetsReimportAll { get; } =
        new("editor.command.assets.reimport-all", "Reimport All");

    /// <summary>The <c>Rename</c> command.</summary>
    public static StringId CommandAssetsRename { get; } = new("editor.command.assets.rename", "Rename");

    /// <summary>The <c>Select Dependencies</c> command.</summary>
    public static StringId CommandAssetsSelectDependencies { get; } =
        new("editor.command.assets.select-dependencies", "Select Dependencies");

    /// <summary>The <c>Show in File Manager</c> command.</summary>
    public static StringId CommandAssetsShowInExplorer { get; } =
        new("editor.command.assets.show-in-explorer", "Show in File Manager");

    /// <summary>The <c>Enter / Leave Mesh</c> command.</summary>
    public static StringId CommandBlockoutToggleMesh { get; } =
        new("editor.command.blockout.toggle-mesh", "Enter / Leave Mesh");

    /// <summary>The <c>Build Content</c> command.</summary>
    public static StringId CommandBuildContent { get; } = new("editor.command.build-content", "Build Content");

    /// <summary>The <c>Clean Library</c> command.</summary>
    public static StringId CommandBuildCleanLibrary { get; } =
        new("editor.command.build.clean-library", "Clean Library");

    /// <summary>The <c>Rebuild Shaders</c> command.</summary>
    public static StringId CommandBuildRebuildShaders { get; } =
        new("editor.command.build.rebuild-shaders", "Rebuild Shaders");

    /// <summary>The <c>Build and Run</c> command.</summary>
    public static StringId CommandBuildRun { get; } = new("editor.command.build.run", "Build and Run");

    /// <summary>The <c>Build Settings…</c> command.</summary>
    public static StringId CommandBuildSettings { get; } = new("editor.command.build.settings", "Build Settings…");

    /// <summary>The <c>Camera</c> command.</summary>
    public static StringId CommandCreateCamera { get; } = new("editor.command.create-camera", "Camera");

    /// <summary>The <c>Create Empty</c> command.</summary>
    public static StringId CommandCreateEntity { get; } = new("editor.command.create-entity", "Create Empty");

    /// <summary>The <c>Deselect All</c> command.</summary>
    public static StringId CommandEditDeselectAll { get; } = new("editor.command.edit.deselect-all", "Deselect All");

    /// <summary>The <c>Duplicate</c> command.</summary>
    public static StringId CommandEditDuplicate { get; } = new("editor.command.edit.duplicate", "Duplicate");

    /// <summary>The <c>Find References</c> command.</summary>
    public static StringId CommandEditFindReferences { get; } =
        new("editor.command.edit.find-references", "Find References");

    /// <summary>The <c>Invert Selection</c> command.</summary>
    public static StringId CommandEditInvertSelection { get; } =
        new("editor.command.edit.invert-selection", "Invert Selection");

    /// <summary>The <c>Isolate Selection</c> command.</summary>
    public static StringId CommandEditIsolate { get; } = new("editor.command.edit.isolate", "Isolate Selection");

    /// <summary>The <c>Keyboard Shortcuts…</c> command.</summary>
    public static StringId CommandEditKeybindings { get; } =
        new("editor.command.edit.keybindings", "Keyboard Shortcuts…");

    /// <summary>The <c>Recall Selection Set…</c> command.</summary>
    public static StringId CommandEditRecallSelectionSet { get; } =
        new("editor.command.edit.recall-selection-set", "Recall Selection Set…");

    /// <summary>The <c>Save Selection Set…</c> command.</summary>
    public static StringId CommandEditSaveSelectionSet { get; } =
        new("editor.command.edit.save-selection-set", "Save Selection Set…");

    /// <summary>The <c>Select All</c> command.</summary>
    public static StringId CommandEditSelectAll { get; } = new("editor.command.edit.select-all", "Select All");

    /// <summary>The <c>Select By Name…</c> command.</summary>
    public static StringId CommandEditSelectByName { get; } =
        new("editor.command.edit.select-by-name", "Select By Name…");

    /// <summary>The <c>Select By Component…</c> command.</summary>
    public static StringId CommandEditSelectByType { get; } =
        new("editor.command.edit.select-by-type", "Select By Component…");

    /// <summary>The <c>Select Children</c> command.</summary>
    public static StringId CommandEditSelectChildren { get; } =
        new("editor.command.edit.select-children", "Select Children");

    /// <summary>The <c>Select Parent</c> command.</summary>
    public static StringId CommandEditSelectParent { get; } = new("editor.command.edit.select-parent", "Select Parent");

    /// <summary>The <c>Undo History…</c> command.</summary>
    public static StringId CommandEditUndoHistory { get; } = new("editor.command.edit.undo-history", "Undo History…");

    /// <summary>The <c>Align…</c> command.</summary>
    public static StringId CommandEntityAlign { get; } = new("editor.command.entity.align", "Align…");

    /// <summary>The <c>Align With View</c> command.</summary>
    public static StringId CommandEntityAlignWithView { get; } =
        new("editor.command.entity.align-with-view", "Align With View");

    /// <summary>The <c>Apply Overrides</c> command.</summary>
    public static StringId CommandEntityApplyOverrides { get; } =
        new("editor.command.entity.apply-overrides", "Apply Overrides");

    /// <summary>The <c>Clear Parent</c> command.</summary>
    public static StringId CommandEntityClearParent { get; } =
        new("editor.command.entity.clear-parent", "Clear Parent");

    /// <summary>The <c>Copy Transform</c> command.</summary>
    public static StringId CommandEntityCopyTransform { get; } =
        new("editor.command.entity.copy-transform", "Copy Transform");

    /// <summary>The <c>Audio Source</c> command.</summary>
    public static StringId CommandEntityCreateAudio { get; } =
        new("editor.command.entity.create-audio", "Audio Source");

    /// <summary>The <c>Create Empty Child</c> command.</summary>
    public static StringId CommandEntityCreateChild { get; } =
        new("editor.command.entity.create-child", "Create Empty Child");

    /// <summary>The <c>UI Canvas</c> command.</summary>
    public static StringId CommandEntityCreateUi { get; } = new("editor.command.entity.create-ui", "UI Canvas");

    /// <summary>The <c>VFX Emitter</c> command.</summary>
    public static StringId CommandEntityCreateVfx { get; } = new("editor.command.entity.create-vfx", "VFX Emitter");

    /// <summary>The <c>Distribute…</c> command.</summary>
    public static StringId CommandEntityDistribute { get; } = new("editor.command.entity.distribute", "Distribute…");

    /// <summary>The <c>Group</c> command.</summary>
    public static StringId CommandEntityGroup { get; } = new("editor.command.entity.group", "Group");

    /// <summary>The <c>Make Prefab…</c> command.</summary>
    public static StringId CommandEntityMakePrefab { get; } = new("editor.command.entity.make-prefab", "Make Prefab…");

    /// <summary>The <c>Move To View</c> command.</summary>
    public static StringId CommandEntityMoveToView { get; } = new("editor.command.entity.move-to-view", "Move To View");

    /// <summary>The <c>Paste Transform</c> command.</summary>
    public static StringId CommandEntityPasteTransform { get; } =
        new("editor.command.entity.paste-transform", "Paste Transform");

    /// <summary>The <c>Relative Transform Entry</c> command.</summary>
    public static StringId CommandEntityRelativeTransform { get; } =
        new("editor.command.entity.relative-transform", "Relative Transform Entry");

    /// <summary>The <c>Reset Transform</c> command.</summary>
    public static StringId CommandEntityResetTransform { get; } =
        new("editor.command.entity.reset-transform", "Reset Transform");

    /// <summary>The <c>Set Parent</c> command.</summary>
    public static StringId CommandEntitySetParent { get; } = new("editor.command.entity.set-parent", "Set Parent");

    /// <summary>The <c>Snap To Floor</c> command.</summary>
    public static StringId CommandEntitySnapToFloor { get; } =
        new("editor.command.entity.snap-to-floor", "Snap To Floor");

    /// <summary>The <c>Toggle Active</c> command.</summary>
    public static StringId CommandEntityToggleActive { get; } =
        new("editor.command.entity.toggle-active", "Toggle Active");

    /// <summary>The <c>Toggle Visibility</c> command.</summary>
    public static StringId CommandEntityToggleHidden { get; } =
        new("editor.command.entity.toggle-hidden", "Toggle Visibility");

    /// <summary>The <c>Toggle Lock</c> command.</summary>
    public static StringId CommandEntityToggleLock { get; } = new("editor.command.entity.toggle-lock", "Toggle Lock");

    /// <summary>The <c>Ungroup</c> command.</summary>
    public static StringId CommandEntityUngroup { get; } = new("editor.command.entity.ungroup", "Ungroup");

    /// <summary>The <c>Unpack Prefab</c> command.</summary>
    public static StringId CommandEntityUnpackPrefab { get; } =
        new("editor.command.entity.unpack-prefab", "Unpack Prefab");

    /// <summary>The <c>Export Package…</c> command.</summary>
    public static StringId CommandFileExportPackage { get; } =
        new("editor.command.file.export-package", "Export Package…");

    /// <summary>The <c>New Scene</c> command.</summary>
    public static StringId CommandFileNewScene { get; } = new("editor.command.file.new-scene", "New Scene");

    /// <summary>The <c>No Recent Projects</c> command.</summary>
    public static StringId CommandFileNoRecent { get; } = new("editor.command.file.no-recent", "No Recent Projects");

    /// <summary>The <c>Open Scene…</c> command.</summary>
    public static StringId CommandFileOpenScene { get; } = new("editor.command.file.open-scene", "Open Scene…");

    /// <summary>The <c>Project Settings…</c> command.</summary>
    public static StringId CommandFileProjectSettings { get; } =
        new("editor.command.file.project-settings", "Project Settings…");

    /// <summary>The <c>Revert to Saved</c> command.</summary>
    public static StringId CommandFileRevert { get; } = new("editor.command.file.revert", "Revert to Saved");

    /// <summary>The <c>Save Scene As…</c> command.</summary>
    public static StringId CommandFileSaveAs { get; } = new("editor.command.file.save-as", "Save Scene As…");

    /// <summary>The <c>API Reference</c> command.</summary>
    public static StringId CommandHelpApiReference { get; } = new("editor.command.help.api-reference", "API Reference");

    /// <summary>The <c>Release Notes</c> command.</summary>
    public static StringId CommandHelpReleaseNotes { get; } = new("editor.command.help.release-notes", "Release Notes");

    /// <summary>The <c>Report a Bug…</c> command.</summary>
    public static StringId CommandHelpReportBug { get; } = new("editor.command.help.report-bug", "Report a Bug…");

    /// <summary>The <c>Show Log Folder</c> command.</summary>
    public static StringId CommandHelpShowLogFolder { get; } =
        new("editor.command.help.show-log-folder", "Show Log Folder");

    /// <summary>The <c>Import Assets</c> command.</summary>
    public static StringId CommandImportAssets { get; } = new("editor.command.import-assets", "Import Assets");

    /// <summary>The <c>Clear Console on Play</c> command.</summary>
    public static StringId CommandPlayClearConsole { get; } =
        new("editor.command.play.clear-console", "Clear Console on Play");

    /// <summary>The <c>Maximise on Play</c> command.</summary>
    public static StringId CommandPlayMaximise { get; } = new("editor.command.play.maximise", "Maximise on Play");

    /// <summary>The <c>In Editor</c> command.</summary>
    public static StringId CommandPlayModeInEditor { get; } = new("editor.command.play.mode-in-editor", "In Editor");

    /// <summary>The <c>Server and Clients</c> command.</summary>
    public static StringId CommandPlayModeServer { get; } =
        new("editor.command.play.mode-server", "Server and Clients");

    /// <summary>The <c>Standalone Process</c> command.</summary>
    public static StringId CommandPlayModeStandalone { get; } =
        new("editor.command.play.mode-standalone", "Standalone Process");

    /// <summary>The <c>Mute Audio</c> command.</summary>
    public static StringId CommandPlayMuteAudio { get; } = new("editor.command.play.mute-audio", "Mute Audio");

    /// <summary>The <c>Radial Menu</c> command.</summary>
    public static StringId CommandRadialMenu { get; } = new("editor.command.radial-menu", "Radial Menu");

    /// <summary>The <c>Refresh Assets</c> command.</summary>
    public static StringId CommandRefreshAssets { get; } = new("editor.command.refresh-assets", "Refresh Assets");

    /// <summary>The <c>Reload Plugins</c> command.</summary>
    public static StringId CommandReloadPlugins { get; } = new("editor.command.reload-plugins", "Reload Plugins");

    /// <summary>The <c>Scene Context Menu</c> command.</summary>
    public static StringId CommandSceneContextMenu { get; } =
        new("editor.command.scene-context-menu", "Scene Context Menu");

    /// <summary>The <c>Layers and Tags…</c> command.</summary>
    public static StringId CommandSceneLayers { get; } = new("editor.command.scene.layers", "Layers and Tags…");

    /// <summary>The <c>Lighting…</c> command.</summary>
    public static StringId CommandSceneLighting { get; } = new("editor.command.scene.lighting", "Lighting…");

    /// <summary>The <c>Navigation…</c> command.</summary>
    public static StringId CommandSceneNavigation { get; } = new("editor.command.scene.navigation", "Navigation…");

    /// <summary>The <c>Open Scene Additively…</c> command.</summary>
    public static StringId CommandSceneOpenAdditive { get; } =
        new("editor.command.scene.open-additive", "Open Scene Additively…");

    /// <summary>The <c>Save All Scenes</c> command.</summary>
    public static StringId CommandSceneSaveAllScenes { get; } =
        new("editor.command.scene.save-all-scenes", "Save All Scenes");

    /// <summary>The <c>Scenes</c> command.</summary>
    public static StringId CommandSceneScenes { get; } = new("editor.command.scene.scenes", "Scenes");

    /// <summary>The <c>World Settings…</c> command.</summary>
    public static StringId CommandSceneWorldSettings { get; } =
        new("editor.command.scene.world-settings", "World Settings…");

    /// <summary>The <c>Rebuild Editor Scripts</c> command.</summary>
    public static StringId CommandScriptsRebuild { get; } =
        new("editor.command.scripts.rebuild", "Rebuild Editor Scripts");

    /// <summary>The <c>Generate Diagnostics Report…</c> command.</summary>
    public static StringId CommandToolsDiagnosticsReport { get; } =
        new("editor.command.tools.diagnostics-report", "Generate Diagnostics Report…");

    /// <summary>The <c>Plugins…</c> command.</summary>
    public static StringId CommandToolsPlugins { get; } = new("editor.command.tools.plugins", "Plugins…");

    /// <summary>The <c>Reload Shaders</c> command.</summary>
    public static StringId CommandToolsReloadShaders { get; } =
        new("editor.command.tools.reload-shaders", "Reload Shaders");

    /// <summary>The <c>Reload Styles</c> command.</summary>
    public static StringId CommandToolsReloadStyles { get; } =
        new("editor.command.tools.reload-styles", "Reload Styles");

    /// <summary>The <c>Clear Console</c> command.</summary>
    public static StringId CommandViewClearConsole { get; } = new("editor.command.view.clear-console", "Clear Console");

    /// <summary>The <c>Full Screen</c> command.</summary>
    public static StringId CommandViewFullScreen { get; } = new("editor.command.view.full-screen", "Full Screen");

    /// <summary>The <c>Animation</c> workspace layout.</summary>
    public static StringId LayoutAnimation { get; } = new("editor.layout.animation", "Animation");

    /// <summary>The <c>Debug</c> workspace layout.</summary>
    public static StringId LayoutDebug { get; } = new("editor.layout.debug", "Debug");

    /// <summary>The <c>Default</c> workspace layout.</summary>
    public static StringId LayoutDefault { get; } = new("editor.layout.default", "Default");

    /// <summary>The <c>Profiling</c> workspace layout.</summary>
    public static StringId LayoutProfiling { get; } = new("editor.layout.profiling", "Profiling");

    /// <summary>The <c>Scene</c> workspace layout.</summary>
    public static StringId LayoutScene { get; } = new("editor.layout.scene", "Scene");

    /// <summary>The <c>Sequencing</c> workspace layout.</summary>
    public static StringId LayoutSequencing { get; } = new("editor.layout.sequencing", "Sequencing");

    /// <summary>The <c>Shading</c> workspace layout.</summary>
    public static StringId LayoutShading { get; } = new("editor.layout.shading", "Shading");

    /// <summary>The <c>Boolean</c> menu.</summary>
    public static StringId MenuBlockoutBoolean { get; } = new("editor.menu.blockout-boolean", "Boolean");

    /// <summary>The <c>Create</c> menu.</summary>
    public static StringId MenuBlockoutCreate { get; } = new("editor.menu.blockout-create", "Create");

    /// <summary>The <c>Handoff</c> menu.</summary>
    public static StringId MenuBlockoutHandoff { get; } = new("editor.menu.blockout-handoff", "Handoff");

    /// <summary>The <c>Shape</c> menu.</summary>
    public static StringId MenuBlockoutShape { get; } = new("editor.menu.blockout-shape", "Shape");

    /// <summary>The <c>Surfaces</c> menu.</summary>
    public static StringId MenuBlockoutSurfaces { get; } = new("editor.menu.blockout-surfaces", "Surfaces");

    /// <summary>The <c>Bookmarks</c> menu.</summary>
    public static StringId MenuBookmarks { get; } = new("editor.menu.bookmarks", "Bookmarks");

    /// <summary>The <c>Project</c> menu.</summary>
    public static StringId MenuBrowser { get; } = new("editor.menu.browser", "Project");

    /// <summary>The <c>Configuration</c> menu.</summary>
    public static StringId MenuBuildConfiguration { get; } = new("editor.menu.build-configuration", "Configuration");

    /// <summary>The <c>Deploy</c> menu.</summary>
    public static StringId MenuBuildDeploy { get; } = new("editor.menu.build-deploy", "Deploy");

    /// <summary>The <c>Target</c> menu.</summary>
    public static StringId MenuBuildTarget { get; } = new("editor.menu.build-target", "Target");

    /// <summary>The <c>Camera</c> menu.</summary>
    public static StringId MenuCamera { get; } = new("editor.menu.camera", "Camera");

    /// <summary>The <c>Light</c> menu.</summary>
    public static StringId MenuCreateLight { get; } = new("editor.menu.create-light", "Light");

    /// <summary>The <c>3D Object</c> menu.</summary>
    public static StringId MenuCreateShape { get; } = new("editor.menu.create-shape", "3D Object");

    /// <summary>The <c>Select Elements</c> menu.</summary>
    public static StringId MenuElements { get; } = new("editor.menu.elements", "Select Elements");

    /// <summary>The <c>Geometry</c> menu.</summary>
    public static StringId MenuGeometry { get; } = new("editor.menu.geometry", "Geometry");

    /// <summary>The <c>Gizmo</c> menu.</summary>
    public static StringId MenuGizmo { get; } = new("editor.menu.gizmo", "Gizmo");

    /// <summary>The <c>Hierarchy</c> menu.</summary>
    public static StringId MenuHierarchy { get; } = new("editor.menu.hierarchy", "Hierarchy");

    /// <summary>The <c>Navigation</c> menu.</summary>
    public static StringId MenuNavigation { get; } = new("editor.menu.navigation", "Navigation");

    /// <summary>The <c>Viewport Layout</c> menu.</summary>
    public static StringId MenuPanes { get; } = new("editor.menu.panes", "Viewport Layout");

    /// <summary>The <c>Mode</c> menu.</summary>
    public static StringId MenuPlayMode { get; } = new("editor.menu.play-mode", "Mode");

    /// <summary>The <c>Options</c> menu.</summary>
    public static StringId MenuPlayOptions { get; } = new("editor.menu.play-options", "Options");

    /// <summary>The <c>Measure</c> menu.</summary>
    public static StringId MenuPrecision { get; } = new("editor.menu.precision", "Measure");

    /// <summary>The <c>Scene</c> menu.</summary>
    public static StringId MenuScene { get; } = new("editor.menu.scene", "Scene");

    /// <summary>The <c>Show</c> menu.</summary>
    public static StringId MenuShow { get; } = new("editor.menu.show", "Show");

    /// <summary>The <c>Snapping</c> menu.</summary>
    public static StringId MenuSnap { get; } = new("editor.menu.snap", "Snapping");

    /// <summary>The <c>Camera Speed</c> menu.</summary>
    public static StringId MenuSpeed { get; } = new("editor.menu.speed", "Camera Speed");

    /// <summary>The <c>View Mode</c> menu.</summary>
    public static StringId MenuViewMode { get; } = new("editor.menu.view-mode", "View Mode");

    /// <summary>The <c>Work Plane</c> menu.</summary>
    public static StringId MenuWorkPlane { get; } = new("editor.menu.work-plane", "Work Plane");

    /// <summary>What the mode strip calls the <c>Blockout</c> mode.</summary>
    public static StringId ModeBlockout { get; } = new("editor.mode.blockout", "Blockout");

    /// <summary>What the mode strip calls the <c>Foliage</c> mode.</summary>
    public static StringId ModeFoliage { get; } = new("editor.mode.foliage", "Foliage");

    /// <summary>What the mode strip calls the <c>Terrain</c> mode.</summary>
    public static StringId ModeTerrain { get; } = new("editor.mode.terrain", "Terrain");

    /// <summary>What the mode strip calls the <c>Water</c> mode.</summary>
    public static StringId ModeWater { get; } = new("editor.mode.water", "Water");

    /// <summary>The <c>Addressables</c> panel.</summary>
    public static StringId PanelAddressables { get; } = new("editor.panel.addressables", "Addressables");

    /// <summary>The <c>Agent Debugger</c> panel.</summary>
    public static StringId PanelAiDebugger { get; } = new("editor.panel.ai-debugger", "Agent Debugger");

    /// <summary>The <c>Blockout</c> panel.</summary>
    public static StringId PanelBlockout { get; } = new("editor.panel.blockout", "Blockout");

    /// <summary>The <c>Build Settings</c> panel.</summary>
    public static StringId PanelBuild { get; } = new("editor.panel.build", "Build Settings");

    /// <summary>The <c>Console</c> panel.</summary>
    public static StringId PanelConsole { get; } = new("editor.panel.console", "Console");

    /// <summary>The <c>Devices</c> panel.</summary>
    public static StringId PanelDevices { get; } = new("editor.panel.devices", "Devices");

    /// <summary>The <c>Foliage</c> panel.</summary>
    public static StringId PanelFoliage { get; } = new("editor.panel.foliage", "Foliage");

    /// <summary>The <c>Frame Debugger</c> panel.</summary>
    public static StringId PanelFrameDebugger { get; } = new("editor.panel.frame-debugger", "Frame Debugger");

    /// <summary>The <c>GPU</c> panel.</summary>
    public static StringId PanelGpu { get; } = new("editor.panel.gpu", "GPU");

    /// <summary>The <c>Grass</c> panel.</summary>
    public static StringId PanelGrass { get; } = new("editor.panel.grass", "Grass");

    /// <summary>The <c>Growth</c> panel.</summary>
    public static StringId PanelGrowth { get; } = new("editor.panel.growth", "Growth");

    /// <summary>The <c>Hierarchy</c> panel.</summary>
    public static StringId PanelHierarchy { get; } = new("editor.panel.hierarchy", "Hierarchy");

    /// <summary>The <c>Input Debug</c> panel.</summary>
    public static StringId PanelInputDebug { get; } = new("editor.panel.input-debug", "Input Debug");

    /// <summary>The <c>Inspector</c> panel.</summary>
    public static StringId PanelInspector { get; } = new("editor.panel.inspector", "Inspector");

    /// <summary>The <c>Inspector 2</c> panel.</summary>
    public static StringId PanelInspector2 { get; } = new("editor.panel.inspector2", "Inspector 2");

    /// <summary>The <c>Layer Stack</c> panel.</summary>
    public static StringId PanelLayerStack { get; } = new("editor.panel.layer-stack", "Layer Stack");

    /// <summary>The <c>Lighting</c> panel.</summary>
    public static StringId PanelLighting { get; } = new("editor.panel.lighting", "Lighting");

    /// <summary>The <c>Memory</c> panel.</summary>
    public static StringId PanelMemory { get; } = new("editor.panel.memory", "Memory");

    /// <summary>The <c>Bake Mesh Maps</c> panel.</summary>
    public static StringId PanelMeshMapBake { get; } = new("editor.panel.mesh-map-bake", "Bake Mesh Maps");

    /// <summary>The <c>Navigation</c> panel.</summary>
    public static StringId PanelNavigation { get; } = new("editor.panel.navigation", "Navigation");

    /// <summary>The <c>Network</c> panel.</summary>
    public static StringId PanelNetwork { get; } = new("editor.panel.network", "Network");

    /// <summary>The <c>Profiler</c> panel.</summary>
    public static StringId PanelProfiler { get; } = new("editor.panel.profiler", "Profiler");

    /// <summary>The <c>Project</c> panel.</summary>
    public static StringId PanelProject { get; } = new("editor.panel.project", "Project");

    /// <summary>The <c>Remote Inspector</c> panel.</summary>
    public static StringId PanelRemoteInspector { get; } = new("editor.panel.remote-inspector", "Remote Inspector");

    /// <summary>The <c>Scene</c> panel.</summary>
    public static StringId PanelScene { get; } = new("editor.panel.scene", "Scene");

    /// <summary>The <c>Scenes</c> panel.</summary>
    public static StringId PanelScenes { get; } = new("editor.panel.scenes", "Scenes");

    /// <summary>The <c>Editor Scripts</c> panel.</summary>
    public static StringId PanelScripts { get; } = new("editor.panel.scripts", "Editor Scripts");

    /// <summary>The <c>Splines</c> panel.</summary>
    public static StringId PanelSplines { get; } = new("editor.panel.splines", "Splines");

    /// <summary>The <c>Statistics</c> panel.</summary>
    public static StringId PanelStatistics { get; } = new("editor.panel.statistics", "Statistics");

    /// <summary>The <c>Terrain</c> panel.</summary>
    public static StringId PanelTerrain { get; } = new("editor.panel.terrain", "Terrain");

    /// <summary>The <c>Texture Graph</c> panel.</summary>
    public static StringId PanelTextureGraph { get; } = new("editor.panel.texture-graph", "Texture Graph");

    /// <summary>The <c>Paint</c> panel.</summary>
    public static StringId PanelTexturePaint { get; } = new("editor.panel.texture-paint", "Paint");

    /// <summary>The <c>Paint (3D)</c> panel.</summary>
    public static StringId PanelTexturePaint3d { get; } = new("editor.panel.texture-paint-3d", "Paint (3D)");

    /// <summary>The <c>UI Diagnostics</c> panel.</summary>
    public static StringId PanelUiDiagnostics { get; } = new("editor.panel.ui-diagnostics", "UI Diagnostics");

    /// <summary>The <c>Water</c> panel.</summary>
    public static StringId PanelWater { get; } = new("editor.panel.water", "Water");

    /// <summary>The <c>Water Zone</c> panel.</summary>
    public static StringId PanelWaterZone { get; } = new("editor.panel.water.zone", "Water Zone");

    /// <summary>The <c>World Settings</c> panel.</summary>
    public static StringId PanelWorld { get; } = new("editor.panel.world", "World Settings");

    /// <summary>The <c>Appearance</c> settings page.</summary>
    public static StringId SettingsAppearance { get; } = new("editor.settings.appearance", "Appearance");

    /// <summary>The <c>Content Build</c> settings page.</summary>
    public static StringId SettingsContent { get; } = new("editor.settings.content", "Content Build");

    /// <summary>The <c>General</c> settings page.</summary>
    public static StringId SettingsGeneral { get; } = new("editor.settings.general", "General");

    /// <summary>The <c>Keybindings</c> settings page.</summary>
    public static StringId SettingsKeybindings { get; } = new("editor.settings.keybindings", "Keybindings");

    /// <summary>The <c>Plugins</c> settings page.</summary>
    public static StringId SettingsPlugins { get; } = new("editor.settings.plugins", "Plugins");

    /// <summary>The <c>Project</c> settings page.</summary>
    public static StringId SettingsProject { get; } = new("editor.settings.project", "Project");

    /// <summary>The <c>Scene View</c> settings page.</summary>
    public static StringId SettingsSceneView { get; } = new("editor.settings.scene-view", "Scene View");

    /// <summary>The <c>Layout</c> toolbar control.</summary>
    public static StringId ToolbarLayout { get; } = new("editor.toolbar.layout", "Layout");

    /// <summary>The <c>Panes</c> viewport overlay heading.</summary>
    public static StringId ViewportLayout { get; } = new("editor.viewport.layout", "Panes");

    /// <summary>The <c>Measure</c> viewport overlay heading.</summary>
    public static StringId ViewportPrecision { get; } = new("editor.viewport.precision", "Measure");

    /// <summary>The <c>Show</c> viewport overlay heading.</summary>
    public static StringId ViewportShow { get; } = new("editor.viewport.show", "Show");

    /// <summary>The <c>Snap</c> viewport overlay heading.</summary>
    public static StringId ViewportSnap { get; } = new("editor.viewport.snap", "Snap");

    /// <summary>The <c>Speed</c> viewport overlay heading.</summary>
    public static StringId ViewportSpeed { get; } = new("editor.viewport.speed", "Speed");

    /// <summary>The <c>View</c> viewport overlay heading.</summary>
    public static StringId ViewportViewMode { get; } = new("editor.viewport.view-mode", "View");

    /// <summary>The <c>Plane</c> viewport overlay heading.</summary>
    public static StringId ViewportWorkPlane { get; } = new("editor.viewport.work-plane", "Plane");

    /// <summary>Every string above, for a translator to start from.</summary>
    public static IReadOnlyList<StringId> All { get; } = [
        MenuFile,
        MenuEdit,
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
        ModeSelect,
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
        NotificationsClear,
        LayoutReset,
        LayoutNotRestored,
        DialogCancel,
        StatusSelection,
        StatusFrameTime,
        ConsoleClear,
        ConsoleCollapse,
        ConsoleClearOnPlay,
        ConsoleSearch,
        ConsoleAllCategories,
        ConsoleNoSelection,
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
        ProjectsMissing,
        CategoryAssets,
        CategoryBlockout,
        CategoryBuild,
        CategoryCreate,
        CategoryEntity,
        CategoryFoliage,
        CategoryPlay,
        CategoryScene,
        CategoryTerrain,
        CategoryTools,
        CategoryWater,
        CommandAssetsBakeMeshMaps,
        CommandAssetsCreate,
        CommandAssetsRevert,
        CommandAssetsDelete,
        CommandAssetsFindReferences,
        CommandAssetsImportFiles,
        CommandAssetsMoveTo,
        CommandAssetsNewFolder,
        CommandAssetsOpen,
        CommandAssetsReimport,
        CommandAssetsReimportAll,
        CommandAssetsRename,
        CommandAssetsSelectDependencies,
        CommandAssetsShowInExplorer,
        CommandBlockoutToggleMesh,
        CommandBuildContent,
        CommandBuildCleanLibrary,
        CommandBuildRebuildShaders,
        CommandBuildRun,
        CommandBuildSettings,
        CommandCreateCamera,
        CommandCreateEntity,
        CommandEditDeselectAll,
        CommandEditDuplicate,
        CommandEditFindReferences,
        CommandEditInvertSelection,
        CommandEditIsolate,
        CommandEditKeybindings,
        CommandEditRecallSelectionSet,
        CommandEditSaveSelectionSet,
        CommandEditSelectAll,
        CommandEditSelectByName,
        CommandEditSelectByType,
        CommandEditSelectChildren,
        CommandEditSelectParent,
        CommandEditUndoHistory,
        CommandEntityAlign,
        CommandEntityAlignWithView,
        CommandEntityApplyOverrides,
        CommandEntityClearParent,
        CommandEntityCopyTransform,
        CommandEntityCreateAudio,
        CommandEntityCreateChild,
        CommandEntityCreateUi,
        CommandEntityCreateVfx,
        CommandEntityDistribute,
        CommandEntityGroup,
        CommandEntityMakePrefab,
        CommandEntityMoveToView,
        CommandEntityPasteTransform,
        CommandEntityRelativeTransform,
        CommandEntityResetTransform,
        CommandEntitySetParent,
        CommandEntitySnapToFloor,
        CommandEntityToggleActive,
        CommandEntityToggleHidden,
        CommandEntityToggleLock,
        CommandEntityUngroup,
        CommandEntityUnpackPrefab,
        CommandFileExportPackage,
        CommandFileNewScene,
        CommandFileNoRecent,
        CommandFileOpenScene,
        CommandFileProjectSettings,
        CommandFileRevert,
        CommandFileSaveAs,
        CommandHelpApiReference,
        CommandHelpReleaseNotes,
        CommandHelpReportBug,
        CommandHelpShowLogFolder,
        CommandImportAssets,
        CommandPlayClearConsole,
        CommandPlayMaximise,
        CommandPlayModeInEditor,
        CommandPlayModeServer,
        CommandPlayModeStandalone,
        CommandPlayMuteAudio,
        CommandRadialMenu,
        CommandRefreshAssets,
        CommandReloadPlugins,
        CommandSceneContextMenu,
        CommandSceneLayers,
        CommandSceneLighting,
        CommandSceneNavigation,
        CommandSceneOpenAdditive,
        CommandSceneSaveAllScenes,
        CommandSceneScenes,
        CommandSceneWorldSettings,
        CommandScriptsRebuild,
        CommandToolsDiagnosticsReport,
        CommandToolsPlugins,
        CommandToolsReloadShaders,
        CommandToolsReloadStyles,
        CommandViewClearConsole,
        CommandViewFullScreen,
        LayoutAnimation,
        LayoutDebug,
        LayoutDefault,
        LayoutProfiling,
        LayoutScene,
        LayoutSequencing,
        LayoutShading,
        MenuBlockoutBoolean,
        MenuBlockoutCreate,
        MenuBlockoutHandoff,
        MenuBlockoutShape,
        MenuBlockoutSurfaces,
        MenuBookmarks,
        MenuBrowser,
        MenuBuildConfiguration,
        MenuBuildDeploy,
        MenuBuildTarget,
        MenuCamera,
        MenuCreateLight,
        MenuCreateShape,
        MenuElements,
        MenuGeometry,
        MenuGizmo,
        MenuHierarchy,
        MenuNavigation,
        MenuPanes,
        MenuPlayMode,
        MenuPlayOptions,
        MenuPrecision,
        MenuScene,
        MenuShow,
        MenuSnap,
        MenuSpeed,
        MenuViewMode,
        MenuWorkPlane,
        ModeBlockout,
        ModeFoliage,
        ModeTerrain,
        ModeWater,
        PanelAddressables,
        PanelAiDebugger,
        PanelBlockout,
        PanelBuild,
        PanelConsole,
        PanelDevices,
        PanelFoliage,
        PanelFrameDebugger,
        PanelGpu,
        PanelGrass,
        PanelGrowth,
        PanelHierarchy,
        PanelInputDebug,
        PanelInspector,
        PanelInspector2,
        PanelLayerStack,
        PanelLighting,
        PanelMemory,
        PanelMeshMapBake,
        PanelNavigation,
        PanelNetwork,
        PanelProfiler,
        PanelProject,
        PanelRemoteInspector,
        PanelScene,
        PanelScenes,
        PanelScripts,
        PanelSplines,
        PanelStatistics,
        PanelTerrain,
        PanelTextureGraph,
        PanelTexturePaint,
        PanelTexturePaint3d,
        PanelUiDiagnostics,
        PanelWater,
        PanelWaterZone,
        PanelWorld,
        SettingsAppearance,
        SettingsContent,
        SettingsGeneral,
        SettingsKeybindings,
        SettingsPlugins,
        SettingsProject,
        SettingsSceneView,
        ToolbarLayout,
        ViewportLayout,
        ViewportPrecision,
        ViewportShow,
        ViewportSnap,
        ViewportSpeed,
        ViewportViewMode,
        ViewportWorkPlane
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
