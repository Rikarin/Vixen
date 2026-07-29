// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Editor.Inspector;
using Vixen.Editor.Ui;
using Vixen.Platform;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>Doc 20's A4: two windows, one mechanism, and the pages that go in them.</summary>
/// <remarks>
///     <para>
///         <b><see cref="SettingsView" /> is the mechanism and this is the content.</b> The shell
///         cannot supply the pages — it has no project, no settings store and no inspector — so what
///         lives here is the four things a page needs: which store it is over, what type it draws,
///         what Apply writes, and what Reset means.
///     </para>
///     <para>
///         ⚠ <b>Two pages are drawn from commands rather than from a settings object, and doc 20
///         insists on it.</b> The three scene-navigation preferences and the theme are already
///         ticked commands — palette-searchable, rebindable, on a menu — and a preferences window
///         showing a second copy of their state would be two writers to one setting, which is exactly
///         how a window and a menu tick come to disagree. The page shows the same commands.
///     </para>
///     <para>
///         ⚠ <b>Nothing is written until Apply.</b> The layout file's rule — written on the way down
///         — applies here for the same reason, and the two settings that cost something to change
///         (<see cref="EditorPreferences.UndoDepth" />, which drops history, and the content target,
///         which invalidates an import) are exactly why doc 20 asks for an explicit Apply rather than
///         a save per keystroke.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>What the preferences window edits, which is the user's rather than the project's.</summary>
    EditorPreferences preferences = new();

    /// <summary>The undo history while its panel is open, which is the one of the four that is polled.</summary>
    /// <remarks>
    ///     The other three are not held at all. A settings window and a plugin manager are driven by
    ///     what happens in them, and a field pointing at a closed panel's control is the shape of
    ///     mistake that took the editor down when the Scene tab was closed — see
    ///     <c>EditorApplication.Update</c>.
    /// </remarks>
    UndoHistoryView? historyView;

    /// <summary>What the preferences panel is called in an arrangement.</summary>
    internal const string PreferencesPanel = "preferences";

    /// <summary>And the project settings panel.</summary>
    internal const string ProjectSettingsPanel = "project-settings";

    /// <summary>And the plugin manager.</summary>
    internal const string PluginsPanel = "plugins";

    /// <summary>And the undo history.</summary>
    internal const string HistoryPanel = "history";

    /// <summary>The four panels this milestone adds, over models the editor already had.</summary>
    void SettingsPanels() {
        // ⚠ Subscribed before any layout is applied, because the panel may be in one — and the
        // shell raises this from the factory, which runs again on every reopen.
        Shell.KeyboardBuilt += WireKeymapFiles;

        Shell.RegisterPanel(
            PreferencesPanel,
            EditorStrings.PanelPreferences,
            panel => {
                var view = panel.Add<SettingsView>();

                view.Applied += _ => {
                    SavePreferences();
                    SaveTokens();
                };

                view.Reverted += _ => {
                    pendingTokens = null;
                    LoadPreferences();
                };

                PreferencePages(view);
            }
        );

        Shell.RegisterPanel(
            ProjectSettingsPanel,
            EditorStrings.PanelProjectSettings,
            panel => {
                var view = panel.Add<SettingsView>();

                view.Applied += _ => {
                    project.Settings.SaveAll();
                    Shell.Notifications.Success(EditorStrings.PanelProjectSettings.Text);

                    // The content target is read from the settings every time a build runs, so this
                    // is where a changed one takes effect rather than on the next launch.
                    ApplyProjectSettings();
                };

                view.Reverted += _ => {
                    project.Settings.Reload();
                    ApplyProjectSettings();
                };

                ProjectPages(view);
            }
        );

        Shell.RegisterPanel(
            PluginsPanel,
            EditorStrings.PanelPlugins,
            panel => {
                var view = panel.Add<PluginManagerView>();

                view.Show(plugins);
                view.Toggled += _ => SaveDisabledPlugins();
            }
        );

        Shell.RegisterPanel(
            new PanelDescriptor(
                HistoryPanel,
                EditorStrings.PanelHistory,
                panel => {
                    historyView = panel.Add<UndoHistoryView>();

                    // ⚠ Asked every refresh rather than handed a stack. The inspector arbitrates
                    // between several selections and so must this: a history pointed at the editor's
                    // own scene would show the wrong list the moment somebody opened a material.
                    historyView.Show(() => (inspected ?? scene).Stack);
                }
            ) {
                // ⚠ Otherwise this is polled for the rest of the session — the panel is the one
                // thing in the editor that is driven from `Update` rather than from an event, and a
                // closed one would go on rewriting rows nobody can see.
                Closed = () => historyView = null
            }
        );
    }

    // ── The keymap's two file verbs, which the shell cannot reach ───────────────────────────────

    /// <summary>Wires the keybinding panel's Import and Export to the platform's own picker.</summary>
    /// <remarks>
    ///     ⚠ <b>Disabled rather than absent where there is no picker</b>, which is the rule Open
    ///     Scene and Save As already follow: the capability is a runtime question with a runtime
    ///     answer, and a button that silently does nothing is worse than one that is visibly greyed.
    /// </remarks>
    void WireKeymapFiles(KeyBindingsView view) {
        view.Import.Disabled = !services.CanPick;
        view.Export.Disabled = !services.CanPick;

        view.ImportRequested += _ =>
            Picked(
                dialogs => dialogs.OpenFileAsync(Keymap("Import Keymap")),

                // ⚠ Through `KeyMap.Load`, which is what the user's own file goes through — so an
                // imported map names a preset the same way, drops a stale chord the same way, and
                // cannot put the editor into a state its own file could not.
                path => OnFile(() => Shell.Keys.Load(File.ReadAllText(path)), path, "Could not read the keymap"),
                "Could not import the keymap"
            );

        view.ExportRequested += _ =>
            Picked(
                dialogs => dialogs.SaveFileAsync(Keymap("Export Keymap") with { SuggestedFileName = EditorUserStore.KeyMapFile }),
                path => OnFile(() => File.WriteAllText(path, Shell.Keys.Save()), path, "Could not write the keymap"),
                "Could not export the keymap"
            );

        static FileDialogOptions Keymap(string title) =>
            new() { Title = title, Filters = [new FileFilter("Vixen keymap", "yaml")] };
    }

    /// <summary>Reads or writes a file the user chose, and says so either way.</summary>
    /// <remarks>
    ///     ⚠ <b>A full disk or a read-only directory is an ordinary thing to meet.</b> The same rule
    ///     saving a scene follows: an editor that took the process down with the unsaved work still
    ///     in it would be the worst possible response to it.
    /// </remarks>
    void OnFile(Action work, string path, string failed) {
        try {
            work();
            Shell.Notifications.Success(Path.GetFileName(path));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show(failed, NotificationSeverity.Error, exception.Message);
        }
    }

    // ── Preferences ─────────────────────────────────────────────────────────────────────────────

    void PreferencePages(SettingsView view) {
        view.Add(
            new SettingsCategory(
                "general",
                new StringId("editor.settings.general", "General"),
                pane => Draw(pane, view, preferences)
            ) {
                Reset = () => preferences = new EditorPreferences(),
                Keywords = () => Members<EditorPreferences>()
            }
        );

        view.Add(
            new SettingsCategory(
                "appearance",
                new StringId("editor.settings.appearance", "Appearance"),
                pane => {
                    Toggles(pane, "view.toggle-theme");
                    Tokens(pane, view);
                }
            ) {
                Keywords = () => ["theme", "dark", "light", "colour", "color", "tokens"]
            }
        );

        view.Add(
            new SettingsCategory(
                "scene-view",
                new StringId("editor.settings.scene-view", "Scene View"),
                pane => Toggles(
                    pane,
                    "scene.orbit-around-selection",
                    "scene.zoom-to-cursor",
                    "scene.invert-orbit-y",
                    "scene.toggle-grid",
                    "scene.toggle-projection"
                )
            ) {
                Keywords = () => ["orbit", "zoom", "invert", "grid", "projection", "navigation", "camera"]
            }
        );

        view.Add(
            new SettingsCategory(
                "keybindings",
                new StringId("editor.settings.keybindings", "Keybindings"),
                pane => Opens(
                    pane,
                    EditorStrings.PanelKeys.Text,
                    "The keyboard shortcuts, the three presets, and import and export, are their own panel — "
                    + "it is a table of two hundred rows and belongs somewhere it can be left open.",
                    EditorShell.KeyBindingsPanel
                )
            ) {
                Keywords = () => ["shortcut", "key", "binding", "preset", "unity", "unreal"]
            }
        );

        view.Add(
            new SettingsCategory(
                "plugins",
                new StringId("editor.settings.plugins", "Plugins"),
                pane => Opens(
                    pane,
                    EditorStrings.PanelPlugins.Text,
                    "What is installed, what is running, and what would not start.",
                    PluginsPanel
                )
            ) {
                Keywords = () => ["plugin", "extension", "addon"]
            }
        );
    }

    /// <summary>Reads the user's preferences, and applies the ones something else has to be told about.</summary>
    void LoadPreferences() {
        if (store.Read(EditorUserStore.PreferencesFile) is { } yaml) {
            try {
                preferences = YamlSerializer.Parse<EditorPreferences>(yaml);
            } catch (YamlParseException) {
                // ⚠ Defaults rather than a refusal to start, for `KeyMap.Load`'s reason: a mistyped
                // line in a preferences file must not be an editor that will not open. What the user
                // loses is the setting they can see has gone back to its default.
                preferences = new EditorPreferences();
            }
        }

        ApplyPreferences();
    }

    void SavePreferences() {
        try {
            store.Write(EditorUserStore.PreferencesFile, YamlSerializer.ToYaml(preferences));
            ApplyPreferences();

            Shell.Notifications.Success(EditorStrings.PanelPreferences.Text);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not save the preferences", NotificationSeverity.Error, exception.Message);
        }
    }

    /// <summary>Pushes the preferences into the things that read them.</summary>
    /// <remarks>
    ///     ⚠ <b>Every stack, not only the open scene's.</b> Undo depth is a preference about the
    ///     editor rather than about a document, and a project's global stack — where a rename or a
    ///     move is recorded — is the one somebody is most surprised to find has a different limit.
    /// </remarks>
    void ApplyPreferences() {
        var depth = Math.Max(1, preferences.UndoDepth);

        project.GlobalStack.Capacity = depth;
        scene.Stack.Capacity = depth;

        foreach (var document in project.Documents) {
            document.Stack.Capacity = depth;
        }

        Recent.Limit = Math.Max(1, preferences.RecentProjects);
    }

    // ── Project settings ────────────────────────────────────────────────────────────────────────

    void ProjectPages(SettingsView view) {
        view.Add(
            new SettingsCategory(
                "project",
                new StringId("editor.settings.project", "Project"),
                pane => Draw(pane, view, project.Settings.Get<ProjectInfoSettings>(), project.Settings.MarkChanged<ProjectInfoSettings>)
            ) {
                Reset = project.Settings.Reset<ProjectInfoSettings>,
                Keywords = () => Members<ProjectInfoSettings>()
            }
        );

        view.Add(
            new SettingsCategory(
                "content",
                new StringId("editor.settings.content", "Content Build"),
                pane => {
                    Draw(pane, view, project.Settings.Get<ContentBuildSettings>(), project.Settings.MarkChanged<ContentBuildSettings>);

                    pane.Add<TextBlock>().Text =
                        $"Empty builds for this machine, which is {ProjectWorkspaceTarget}.";
                }
            ) {
                Reset = project.Settings.Reset<ContentBuildSettings>,
                Keywords = () => Members<ContentBuildSettings>()
            }
        );
    }

    /// <summary>Pushes the project's settings into the things that read them.</summary>
    void ApplyProjectSettings() {
        var target = project.Settings.Get<ContentBuildSettings>().Target;

        content.Target = target is { Length: > 0 } ? target : ProjectWorkspaceTarget;
        Retitle();
    }

    /// <summary>What this machine's content target is called.</summary>
    static string ProjectWorkspaceTarget => Assets.Content.ProjectWorkspace.HostTarget;

    /// <summary>What the title bar and About call the project.</summary>
    /// <remarks>
    ///     The product name when the project has one, and the directory's name otherwise. A fallback
    ///     rather than a default, so that a project which has never opened the settings page reads
    ///     exactly as it did before there was one.
    /// </remarks>
    string ProductName =>
        project.Settings.Get<ProjectInfoSettings>().ProductName is { Length: > 0 } named ? named : project.Name;

    // ── The pieces a page is made of ────────────────────────────────────────────────────────────

    /// <summary>Draws a settings object as inspector rows.</summary>
    /// <remarks>
    ///     ⚠ <b>No document, so the writes are direct and are not undoable.</b>
    ///     <see cref="InspectorField" /> says that is the case where the inspector is previewing
    ///     something nobody is editing, and a settings window is the other one: the undo history
    ///     belongs to a scene, and a Ctrl+Z aimed at the viewport that silently changed a project
    ///     setting would be worse than no undo at all. Revert is the verb here.
    /// </remarks>
    static void Draw(UiElement pane, SettingsView view, object settings, Action? edited = null) {
        var inspector = pane.Add<InspectorView>();

        inspector.EditedDocument = null;
        inspector.Inspect(settings);

        inspector.ValueChanged += (_, _) => {
            view.IsDirty = true;

            // ⚠ A settings object is a plain [DataContract] with no change notification of its own,
            // so the store has to be told — `ProjectSettingsStore.MarkChanged` is explicit for
            // exactly that reason, and `SaveAll` writes only what has been marked. Without this the
            // Apply button would clear the dirty flag and write nothing at all.
            edited?.Invoke();
        };
    }

    /// <summary>Draws some commands as toggles, which is what a preference with a command already is.</summary>
    void Toggles(UiElement pane, params ReadOnlySpan<string> ids) {
        foreach (var id in ids) {
            if (!Shell.Commands.TryGet(id, out var command)) {
                continue;
            }

            var button = pane.Add<ToggleButton>();
            var commandId = id;

            button.Label = command.Title.Text;
            button.Size = ControlSize.Small;
            button.IsChecked = command.IsChecked;
            button.Disabled = !Shell.Commands.CanExecute(command);

            // ⚠ Through the registry rather than by writing a field, which is the whole of doc 20's
            // "the preferences window shows the same commands rather than a second copy of the
            // state". The tick is read back from the command afterwards, so a command that refused —
            // a viewport preference with no viewport open — leaves the toggle where it was.
            button.CheckedChanged += (control, _) => {
                Shell.Commands.Execute(commandId);
                control.IsChecked = command.IsChecked;
            };
        }
    }

    /// <summary>A page that is a sentence and a button, for the two categories that are panels.</summary>
    void Opens(UiElement pane, string title, string what, string panelId) {
        pane.Add<TextBlock>().Text = what;

        var button = pane.Add<Button>();

        button.Label = title;
        button.Variant = ControlVariant.Primary;
        button.Size = ControlSize.Small;
        button.Clicked += _ => Shell.Workspace.Open(panelId);
    }

    /// <summary>The theme's own tokens, as the YAML they are stored in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 20's A4 asks for a Colours page, and the colours are already a file.</b>
    ///         <c>ThemeService.LoadTokens</c> reads <c>theme.yaml</c> from the user store and
    ///         <c>tools.reload-styles</c> re-reads it — which meant the only way to recolour the
    ///         editor was to find the file. A text area over the same string closes that loop without
    ///         inventing a second representation of a colour ramp.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Applied on Apply, like everything else on this window.</b> Re-parsing a
    ///         stylesheet on every keystroke would restyle the whole document from a half-typed
    ///         value, which is the one edit where "written on the way down" is visibly right.
    ///     </para>
    /// </remarks>
    void Tokens(UiElement pane, SettingsView view) {
        pane.Add<TextBlock>().Text =
            "Theme tokens, as they are stored in theme.yaml. Apply restyles the editor; an empty file "
            + "is the shipped palette.";

        var editor = pane.Add<TextArea>();

        // ⚠ Read once and kept, because this factory runs on every visit to the page — and the
        // search box alone can bounce between two pages, which would make typing in it a sequence of
        // synchronous disk reads.
        tokens ??= store.Read(ThemeFile) ?? string.Empty;
        editor.Value = pendingTokens ?? tokens;

        // ⚠ Recorded on this object rather than closed over by a handler on `view.Applied`. A page's
        // factory runs again every time it is selected — the search box alone can bounce between two
        // of them — so a subscription made here would be one more per visit, each holding a text area
        // that has since been removed and each writing the file again.
        editor.ValueChanged += (_, value) => {
            pendingTokens = value ?? string.Empty;
            view.IsDirty = true;
        };
    }

    /// <summary>Theme tokens typed into the Appearance page and not yet applied.</summary>
    string? pendingTokens;

    /// <summary>What is on disk, read once.</summary>
    string? tokens;

    /// <summary>Where the user's theme overrides live, which two commands and one page all name.</summary>
    internal const string ThemeFile = "theme.yaml";

    /// <summary>Writes the theme tokens, if any were typed, and restyles the editor.</summary>
    void SaveTokens() {
        if (pendingTokens is not { } text) {
            return;
        }

        pendingTokens = null;
        tokens = text;

        try {
            store.Write(ThemeFile, text);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not save the theme", NotificationSeverity.Error, exception.Message);
            return;
        }

        Shell.Theme.LoadTokens(text.Length == 0 ? null : text);
    }

    // ── Which plugins the user has switched off ─────────────────────────────────────────────────

    /// <summary>Tells the host which plugins to leave alone, before anything is loaded.</summary>
    /// <remarks>
    ///     ⚠ <b>Before, rather than loading everything and unloading the disabled ones.</b> A plugin
    ///     somebody switched off because it broke the editor is exactly the one whose
    ///     <c>Activate</c> must not run — and unloading it afterwards would already have run it.
    /// </remarks>
    void LoadDisabledPlugins() {
        if (store.Read(EditorUserStore.PluginsFile) is not { } yaml) {
            return;
        }

        try {
            if (YamlReader.Read(yaml) is YamlMapping document && document["disabled"] is YamlSequence list) {
                plugins.Suppress(list.OfType<YamlScalar>().Select(entry => entry.Value ?? string.Empty));
            }
        } catch (YamlParseException) {
            // A broken file means no plugin is switched off, which is the state that lets the editor
            // start and lets the manager write a good file over it.
        }
    }

    void SaveDisabledPlugins() {
        var list = new YamlSequence { Style = YamlCollectionStyle.Flow };

        foreach (var id in plugins.Suppressed.Order(StringComparer.Ordinal)) {
            list.Add(new YamlScalar(id, YamlScalarStyle.DoubleQuoted));
        }

        try {
            store.Write(EditorUserStore.PluginsFile, YamlWriter.Write(new YamlMapping().Set("disabled", list)));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not save the plugin list", NotificationSeverity.Error, exception.Message);
        }
    }

    /// <summary>The member names of a settings type, for the search over every setting.</summary>
    static IEnumerable<string> Members<T>() =>
        InspectorRegistry.Find(typeof(T)) is { } descriptor
            ? descriptor.Members.Select(member => member.DisplayName)
            : [];
}
