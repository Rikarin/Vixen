// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Platform;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>The menu bar doc 20's Part C describes, and the verbs behind it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's E0 in one file: every line of every menu is present, and every one of them is
///         either implemented or explicitly disabled with the reason.</b> That second half is the
///         part worth being firm about. Doc 20's first bar is that "a verb that is not implemented is
///         <i>visibly</i> not implemented rather than absent", and the mechanism is
///         <see cref="EditorCommand.Unavailable" />: the command is registered, appears in its menu
///         and in the palette, greys itself out, and carries the sentence saying which milestone
///         builds it. A bar with Build and Run missing reads as an engine that cannot; a bar with it
///         greyed and a reason reads as one that will.
///     </para>
///     <para>
///         ⚠ <b>Registered before the menus are described, which is the order the whole shell
///         depends on.</b> <c>MenuPresenter</c> skips an entry naming a command nothing registered —
///         that is what lets <c>EditorShell</c> name <c>file.save</c> without owning it — and it is
///         what would silently swallow every line here if this ran the other way round.
///     </para>
///     <para>
///         ⚠ <b>The scoped verbs are scoped, not guessed.</b> Cut, Copy, Paste, Duplicate, Delete
///         and Rename mean one thing in the outliner and another in the content browser; both want
///         the same keys. Each declares its <see cref="EditorCommand.Context" />, the shell tracks
///         which panel has the focus, and <c>KeyMap</c> files the two Deletes under different
///         contexts so neither has to give up the key.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>The context id of the outliner, which is where an entity verb means something.</summary>
    internal const string SceneContext = "scene";

    /// <summary>And of the content browser, where the same verb means an asset.</summary>
    internal const string AssetContext = "project";

    /// <summary>And of the console, where nothing yet means anything but where the focus is real.</summary>
    /// <remarks>
    ///     Declared even though no command is scoped to it, because the point of tracking a context
    ///     is that leaving one is as meaningful as entering it: clicking a console row must stop
    ///     Delete meaning "delete the selected entity".
    /// </remarks>
    internal const string ConsoleContext = "console";

    static readonly StringId CategoryAssets = new("editor.category.assets", "Assets");
    static readonly StringId CategoryEntity = new("editor.category.entity", "Entity");
    static readonly StringId CategoryPlay = new("editor.category.play", "Play");
    static readonly StringId CategoryBuild = new("editor.category.build", "Build");
    static readonly StringId CategoryTools = new("editor.category.tools", "Tools");

    /// <summary>Where the manual lives, until there is a documentation site to point at.</summary>
    const string DocumentationUrl = "https://github.com/Rikarin/Vixen/tree/master/docs";

    /// <summary>Everything Part C names that this application owns.</summary>
    void ParityCommands() {
        FileCommands();
        EditingCommands();
        AssetCommands();
        EntityCommands();
        PlayCommands();
        BuildAndToolCommands();
        HelpCommands();
    }

    // ── File ────────────────────────────────────────────────────────────────────────────────────

    void FileCommands() {
        // ⚠ The two the shell's default menu has named since it was written and nothing registered,
        // which is doc 20's first finding. Both need a project to be swapped underneath a live
        // editor — a world, a scene, an asset database and every open document — and doc 20 puts
        // that behind the startup Project Browser in E3. Declared and disabled is the honest state:
        // the File menu has the lines a person looks for, and choosing one says what is missing.
        Planned(
            "file.new-project",
            EditorStrings.CommandNewProject,
            EditorStrings.CategoryFile,
            "Creating and switching projects arrives with the startup Project Browser."
        );

        Planned(
            "file.open-project",
            EditorStrings.CommandOpenProject,
            EditorStrings.CategoryFile,
            "Opening another project in place arrives with the startup Project Browser."
        );

        Planned(
            "file.no-recent",
            new StringId("editor.command.file.no-recent", "No Recent Projects"),
            EditorStrings.CategoryFile,
            "Recent projects are recorded once a project can be opened without restarting."
        );

        Verb(
            "file.new-scene",
            new StringId("editor.command.file.new-scene", "New Scene"),
            EditorStrings.CategoryFile,
            NewScene
        );

        Verb(
            "file.open-scene",
            new StringId("editor.command.file.open-scene", "Open Scene…"),
            EditorStrings.CategoryFile,
            OpenScene,
            enabled: () => services.CanPick
        );

        Verb(
            "file.save-as",
            new StringId("editor.command.file.save-as", "Save Scene As…"),
            EditorStrings.CategoryFile,
            SaveSceneAs,
            enabled: () => services.CanPick
        );

        Verb(
            "file.save-all",
            EditorStrings.CommandSaveAll,
            EditorStrings.CategoryFile,
            SaveAll,
            enabled: () => project.HasUnsavedChanges.Value
        );

        Verb(
            "assets.import-files",
            new StringId("editor.command.assets.import-files", "Import Assets…"),
            EditorStrings.CategoryFile,
            ImportFiles,
            enabled: () => services.CanPick && !content.IsBusy
        );

        Planned(
            "file.export-package",
            new StringId("editor.command.file.export-package", "Export Package…"),
            EditorStrings.CategoryFile,
            "Package export needs the dependency walk the content browser's Select Dependencies builds."
        );

        Planned(
            "file.project-settings",
            new StringId("editor.command.file.project-settings", "Project Settings…"),
            EditorStrings.CategoryFile,
            "The Project Settings window is milestone E3."
        );

        Shell.Keys.SetDefault("file.new-scene", new KeyChord(InputKey.N, ModifierKeys.Control));
        Shell.Keys.SetDefault("file.open-scene", new KeyChord(InputKey.O, ModifierKeys.Control));
        Shell.Keys.SetDefault("file.save-as", new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift));
        Shell.Keys.SetDefault("file.save-all", new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Alt));

        // The submenu is a dynamic over ids, so the fallback line is what an empty list shows —
        // a submenu that opens onto nothing at all reads as a broken menu rather than an empty one.
        Shell.Recent = () => ["file.no-recent"];
    }

    // ── Edit ────────────────────────────────────────────────────────────────────────────────────

    void EditingCommands() {
        Planned(
            "edit.undo-history",
            new StringId("editor.command.edit.undo-history", "Undo History…"),
            EditorStrings.CategoryEdit,
            "A window over the command stack is milestone E3."
        );

        // ⚠ Scoped to the outliner, which is what lets the content browser's twin have the same key.
        // Without a context the second Delete could not be bound at all, and an enablement predicate
        // guessing from "is anything selected" gets it wrong the moment both panels have a selection
        // — which after clicking an asset and then a row is most of the time.
        Scoped(
            "edit.delete",
            "Delete",
            SceneContext,
            () => scene.Delete([.. scene.Selection]),
            () => scene.Selection.Count > 0
        );

        Scoped("edit.rename", "Rename", SceneContext, Rename, () => hierarchy is not null && scene.Selection.Count > 0);

        Shell.Keys.SetDefault("edit.delete", new KeyChord(InputKey.Delete, ModifierKeys.None));
        Shell.Keys.SetDefault("edit.rename", new KeyChord(InputKey.F2, ModifierKeys.None));

        // ⚠ Cut, Copy, Paste and Duplicate need a subtree *clone*, which the engine does not have.
        // `SubtreeSnapshot` looks like the answer and is not: it takes a subtree by destroying it and
        // gives back the same handles, which is what an undone delete wants and the opposite of what
        // a paste wants. A real clipboard is `World.CopyComponentsFrom` into fresh entities with the
        // hierarchy components excluded and stable ids re-minted — a piece of work with its own
        // correctness argument, and doc 20 files it under the outliner's milestone rather than here.
        foreach (var (id, title, key) in Clipboard()) {
            Planned(
                id,
                new StringId("editor.command." + id, title),
                EditorStrings.CategoryEdit,
                "Cloning a subtree needs a component-wise copy the engine does not have yet. Milestone E1."
            );

            if (key.IsBound) {
                Shell.Keys.SetDefault(id, key);
            }
        }

        Verb(
            "edit.select-all",
            new StringId("editor.command.edit.select-all", "Select All"),
            EditorStrings.CategoryEdit,
            () => scene.Selection.Set([.. scene.Entities])
        );

        Verb(
            "edit.deselect-all",
            new StringId("editor.command.edit.deselect-all", "Deselect All"),
            EditorStrings.CategoryEdit,
            DeselectEntities,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "edit.invert-selection",
            new StringId("editor.command.edit.invert-selection", "Invert Selection"),
            EditorStrings.CategoryEdit,
            InvertSelection
        );

        Verb(
            "edit.select-children",
            new StringId("editor.command.edit.select-children", "Select Children"),
            EditorStrings.CategoryEdit,
            SelectChildren,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "edit.select-parent",
            new StringId("editor.command.edit.select-parent", "Select Parent"),
            EditorStrings.CategoryEdit,
            SelectParent,
            enabled: () => scene.Selection.Count > 0
        );

        Shell.Keys.SetDefault("edit.select-all", new KeyChord(InputKey.A, ModifierKeys.Control));

        Shell.Keys.SetDefault(
            "edit.deselect-all",
            new KeyChord(InputKey.A, ModifierKeys.Control | ModifierKeys.Shift)
        );

        Planned(
            "edit.search-everywhere",
            new StringId("editor.command.edit.search-everywhere", "Search Everywhere…"),
            EditorStrings.CategoryEdit,
            "Search over content, entities and settings is milestone E3."
        );

        Planned(
            "edit.find-references",
            new StringId("editor.command.edit.find-references", "Find References"),
            EditorStrings.CategoryEdit,
            "The reference index answers this already; the panel that shows it is milestone E3."
        );

        Planned(
            "edit.preferences",
            EditorStrings.CommandPreferences,
            EditorStrings.CategoryEdit,
            "The Preferences window is milestone E3. Scene navigation preferences are on the Scene menu."
        );

        Planned(
            "edit.keybindings",
            new StringId("editor.command.edit.keybindings", "Keyboard Shortcuts…"),
            EditorStrings.CategoryEdit,
            "The keybinding editor is milestone E3. Bindings can be edited in keymap.yaml."
        );
    }

    // ── Assets ──────────────────────────────────────────────────────────────────────────────────

    void AssetCommands() {
        Planned(
            "assets.create",
            new StringId("editor.command.assets.create", "New Asset…"),
            CategoryAssets,
            "Creating assets from templates arrives with the content browser, milestone E1."
        );

        Verb(
            "assets.open",
            new StringId("editor.command.assets.open", "Open"),
            CategoryAssets,
            OpenSelectedAsset,
            enabled: () => project.Selection.Count > 0
        );

        Verb(
            "assets.show-in-explorer",
            new StringId("editor.command.assets.show-in-explorer", "Show in File Manager"),
            CategoryAssets,
            ShowSelectedAsset,
            enabled: () => project.Selection.Count > 0 && services.OpenUrl is not null
        );

        // ⚠ Rename, Delete and Move are the three that must not be done naively, and doc 20 says so
        // in as many words: renaming an asset moves a file and rewrites every referrer, and deleting
        // one has to report what breaks before it does it. `ReferenceIndex` answers the query and
        // the rewrite does not exist — which is why the browser's rows are read-only today, and why
        // these are declared rather than wired to something that would corrupt a project.
        Planned(
            "assets.rename",
            new StringId("editor.command.assets.rename", "Rename"),
            CategoryAssets,
            "Renaming an asset has to rewrite every referrer. Milestone E1."
        );

        Planned(
            "assets.delete",
            new StringId("editor.command.assets.delete", "Delete"),
            CategoryAssets,
            "Deleting an asset has to report what breaks first. Milestone E1."
        );

        Planned(
            "assets.move-to",
            new StringId("editor.command.assets.move-to", "Move To…"),
            CategoryAssets,
            "Moving an asset has to rewrite every referrer. Milestone E1."
        );

        Planned(
            "assets.reimport",
            new StringId("editor.command.assets.reimport", "Reimport"),
            CategoryAssets,
            "Per-asset reimport needs the importer registry to outlive a run. Reimport All works today."
        );

        Verb(
            "assets.reimport-all",
            new StringId("editor.command.assets.reimport-all", "Reimport All"),
            CategoryAssets,
            content.Import,
            enabled: () => !content.IsBusy
        );

        Planned(
            "assets.find-references",
            new StringId("editor.command.assets.find-references", "Find References"),
            CategoryAssets,
            "The reference index answers this already; the panel that shows it is milestone E3."
        );

        Planned(
            "assets.select-dependencies",
            new StringId("editor.command.assets.select-dependencies", "Select Dependencies"),
            CategoryAssets,
            "Selecting an asset's dependencies arrives with the content browser, milestone E1."
        );

    }

    // ── Entity ──────────────────────────────────────────────────────────────────────────────────

    void EntityCommands() {
        Verb(
            "entity.create-child",
            new StringId("editor.command.entity.create-child", "Create Empty Child"),
            CategoryEntity,
            CreateChild,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "entity.group",
            new StringId("editor.command.entity.group", "Group"),
            CategoryEntity,
            Group,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "entity.clear-parent",
            new StringId("editor.command.entity.clear-parent", "Clear Parent"),
            CategoryEntity,
            ClearParent,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "entity.align-with-view",
            new StringId("editor.command.entity.align-with-view", "Align With View"),
            CategoryEntity,
            AlignWithView,
            enabled: () => viewport is not null && scene.Selection.Count > 0
        );

        Shell.Keys.SetDefault("entity.create-child", new KeyChord(InputKey.N, ModifierKeys.Alt | ModifierKeys.Shift));

        Shell.Keys.SetDefault(
            "scene.create-entity",
            new KeyChord(InputKey.N, ModifierKeys.Control | ModifierKeys.Shift)
        );

        Shell.Keys.SetDefault("entity.group", new KeyChord(InputKey.G, ModifierKeys.Control));

        Planned(
            "entity.create-audio",
            new StringId("editor.command.entity.create-audio", "Audio Source"),
            CategoryEntity,
            "There is no audio-source component yet; a line that made an empty called Audio would lie."
        );

        Planned(
            "entity.create-ui",
            new StringId("editor.command.entity.create-ui", "UI Canvas"),
            CategoryEntity,
            "Vixen.Ui is a document tree with no world-space bridge yet."
        );

        Planned(
            "entity.create-vfx",
            new StringId("editor.command.entity.create-vfx", "VFX Emitter"),
            CategoryEntity,
            "The VFX graph is not reachable from the editor yet. Milestone E5."
        );

        Planned(
            "entity.make-prefab",
            new StringId("editor.command.entity.make-prefab", "Make Prefab…"),
            CategoryEntity,
            "Prefab instance links are not written to a scene yet, so an instance would be an ordinary subtree."
        );

        Planned(
            "entity.unpack-prefab",
            new StringId("editor.command.entity.unpack-prefab", "Unpack Prefab"),
            CategoryEntity,
            "Prefab instance links are not written to a scene yet."
        );

        Planned(
            "entity.apply-overrides",
            new StringId("editor.command.entity.apply-overrides", "Apply Overrides"),
            CategoryEntity,
            "Per-override apply and revert live in the inspector; the scene-wide verb needs instance links."
        );

        Planned(
            "entity.ungroup",
            new StringId("editor.command.entity.ungroup", "Ungroup"),
            CategoryEntity,
            "Ungrouping has to reparent children and delete the group in one undoable step."
        );

        Planned(
            "entity.set-parent",
            new StringId("editor.command.entity.set-parent", "Set Parent"),
            CategoryEntity,
            "Reparenting by menu needs the entity picker the outliner's drag will bring. Milestone E1."
        );

        Planned(
            "entity.move-to-view",
            new StringId("editor.command.entity.move-to-view", "Move To View"),
            CategoryEntity,
            "Placing an entity at the view's pivot needs the picking stage driven by a real target. Milestone E2."
        );

        Planned(
            "entity.snap-to-floor",
            new StringId("editor.command.entity.snap-to-floor", "Snap To Floor"),
            CategoryEntity,
            "Surface snapping needs the picking readback. Milestone E2."
        );

        Planned(
            "entity.toggle-active",
            new StringId("editor.command.entity.toggle-active", "Toggle Active"),
            CategoryEntity,
            "There is no enabled flag on an entity yet."
        );

        Planned(
            "entity.toggle-lock",
            new StringId("editor.command.entity.toggle-lock", "Toggle Lock"),
            CategoryEntity,
            "Per-entity visibility and lock arrive with the outliner's columns, milestone E1."
        );
    }

    // ── Play ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The four transport verbs, over the controller that already exists.</summary>
    /// <remarks>
    ///     ⚠ <b>Entering play mode snapshots the world and leaving it restores the snapshot, and
    ///     that is <i>all</i> it does today.</b> The editor runs no system graph — see
    ///     <c>EditorApplication</c>'s own remarks about the world being a document — so nothing moves
    ///     while it is playing. What is real is the part doc 20 calls out as better than Unity's: the
    ///     restore is honest, it says so before entering, and a selection made in play mode is
    ///     translated back through <c>WorldSnapshot.Restore</c>'s handle map rather than being lost.
    ///     Ticking the simulation is Phase 6's, and it attaches here.
    /// </remarks>
    void PlayCommands() {
        // ⚠ The four carry an icon and a colour class, and the transport is the one strip where that
        // is not decoration. It is the most-clicked control in either reference editor, it is read at
        // a glance rather than looked at, and — the part that matters most — "am I in play mode" has
        // to be answerable without reading anything. A row of identical grey glyphs answers none of
        // those. The theme fills the button when the command is on, so Play is a green button with a
        // white triangle while the game is running and a green triangle when it is not.
        Transport("play.play", "Play", EditorIcons.Play, EnterPlay, () => !play.IsPlaying, () => play.IsPlaying);

        Transport(
            "play.pause",
            "Pause",
            EditorIcons.Pause,
            () => {
                if (play.State == PlayState.Paused) {
                    play.Resume();
                } else {
                    play.Pause();
                }
            },
            () => play.IsPlaying,
            () => play.State == PlayState.Paused
        );

        Transport(
            "play.step",
            "Step Frame",
            EditorIcons.Step,
            () => play.Step(),
            () => play.State == PlayState.Paused
        );

        Transport("play.stop", "Stop", EditorIcons.Stop, LeavePlay, () => play.IsPlaying);

        Shell.Keys.SetDefault("play.play", new KeyChord(InputKey.F5, ModifierKeys.None));
        Shell.Keys.SetDefault("play.pause", new KeyChord(InputKey.P, ModifierKeys.Control | ModifierKeys.Shift));
        Shell.Keys.SetDefault("play.step", new KeyChord(InputKey.F10, ModifierKeys.None));
        Shell.Keys.SetDefault("play.stop", new KeyChord(InputKey.F5, ModifierKeys.Shift));

        Planned(
            "play.mode-in-editor",
            new StringId("editor.command.play.mode-in-editor", "In Editor"),
            CategoryPlay,
            "Choosing a play topology needs the standalone and server paths hosted from the editor."
        );

        Planned(
            "play.mode-standalone",
            new StringId("editor.command.play.mode-standalone", "Standalone Process"),
            CategoryPlay,
            "Launching a standalone player from the editor needs the build settings window. Milestone E6."
        );

        Planned(
            "play.mode-server",
            new StringId("editor.command.play.mode-server", "Server and Clients"),
            CategoryPlay,
            "PlayerSessions has the topology; hosting it from the editor is milestone E6."
        );

        Planned(
            "play.maximise",
            new StringId("editor.command.play.maximise", "Maximise on Play"),
            CategoryPlay,
            "Maximising the viewport needs the multi-viewport host. Milestone E2."
        );

        Planned(
            "play.mute-audio",
            new StringId("editor.command.play.mute-audio", "Mute Audio"),
            CategoryPlay,
            "The editor does not drive the audio engine yet."
        );

        // ⚠ A tick over the console's own preference rather than a second copy of it. Doc 20's rule
        // for the three navigation preferences applies here for the same reason: two writers to one
        // setting is how a menu tick and a panel's toggle come to disagree.
        Verb(
            "play.clear-console",
            new StringId("editor.command.play.clear-console", "Clear Console on Play"),
            CategoryPlay,
            () => {
                if (console is { } view) {
                    view.ClearsOnPlay = !view.ClearsOnPlay;
                }
            },
            enabled: () => console is not null,
            on: () => console is { ClearsOnPlay: true }
        );

        Verb(
            "view.clear-console",
            new StringId("editor.command.view.clear-console", "Clear Console"),
            CategoryPlay,
            () => console?.Clear(),
            enabled: () => console is not null
        );
    }

    // ── Build and Tools ─────────────────────────────────────────────────────────────────────────

    void BuildAndToolCommands() {
        Planned(
            "build.settings",
            new StringId("editor.command.build.settings", "Build Settings…"),
            CategoryBuild,
            "The Build Settings window is milestone E6."
        );

        Planned(
            "build.run",
            new StringId("editor.command.build.run", "Build and Run"),
            CategoryBuild,
            "Building a player needs the build settings window. Milestone E6."
        );

        Planned(
            "build.target",
            new StringId("editor.command.build.target", "Target…"),
            CategoryBuild,
            $"The content build targets this machine ({content.Target}). Choosing another is milestone E6."
        );

        Planned(
            "build.configuration",
            new StringId("editor.command.build.configuration", "Configuration…"),
            CategoryBuild,
            "Debug and release players are milestone E6."
        );

        Planned(
            "build.deploy",
            new StringId("editor.command.build.deploy", "Deploy…"),
            CategoryBuild,
            "The device manager is milestone E4."
        );

        Verb(
            "build.clean-library",
            new StringId("editor.command.build.clean-library", "Clean Library"),
            CategoryBuild,
            CleanLibrary,
            enabled: () => !content.IsBusy
        );

        Planned(
            "build.rebuild-shaders",
            new StringId("editor.command.build.rebuild-shaders", "Rebuild Shaders"),
            CategoryBuild,
            "Shader compilation runs inside the content import; a separate pass is milestone E6."
        );

        foreach (var (id, title, reason) in Diagnostics()) {
            Planned(id, title, CategoryTools, reason);
        }

        // ⚠ On the Window menu, which the shell owns and which names it — so the shell would have a
        // dangling id if this were left out. It is registered here rather than there because full
        // screen is a property of an OS window and `EditorShell` is deliberately a document with no
        // window: what is missing is a way for the application to reach one, not the verb itself.
        Planned(
            "view.full-screen",
            new StringId("editor.command.view.full-screen", "Full Screen"),
            EditorStrings.CategoryView,
            "The application has no handle on its window yet; the host owns it."
        );

        Planned(
            "tools.plugins",
            new StringId("editor.command.tools.plugins", "Plugins…"),
            CategoryTools,
            "The plugin manager is milestone E3. Reload Plugins works today."
        );

        Planned(
            "tools.reload-shaders",
            new StringId("editor.command.tools.reload-shaders", "Reload Shaders"),
            CategoryTools,
            "The editor loads its shaders once at start-up; hot reload is milestone E6."
        );

        Verb(
            "tools.reload-styles",
            new StringId("editor.command.tools.reload-styles", "Reload Styles"),
            CategoryTools,
            ReloadStyles
        );

        Planned(
            "tools.diagnostics-report",
            new StringId("editor.command.tools.diagnostics-report", "Generate Diagnostics Report…"),
            CategoryTools,
            "A report needs the profiler and the crash reporter. Milestones E4 and E6."
        );
    }

    /// <summary>The four clipboard verbs and the keys everybody expects them on.</summary>
    static (string Id, string Title, KeyChord Key)[] Clipboard() => [
        ("edit.cut", "Cut", new KeyChord(InputKey.X, ModifierKeys.Control)),
        ("edit.copy", "Copy", new KeyChord(InputKey.C, ModifierKeys.Control)),
        ("edit.paste", "Paste", new KeyChord(InputKey.V, ModifierKeys.Control)),
        ("edit.paste-as-child", "Paste As Child", KeyChord.None),
        ("edit.duplicate", "Duplicate", new KeyChord(InputKey.D, ModifierKeys.Control))
    ];

    /// <summary>The five diagnostics windows doc 20's B4 lists, none of which has a project yet.</summary>
    static (string Id, StringId Title, string Reason)[] Diagnostics() => [
        (
            "tools.profiler",
            new StringId("editor.command.tools.profiler", "Profiler"),
            "Vixen.Editor.Profiler does not exist as a project yet. Milestone E4."
        ),
        (
            "tools.frame-debugger",
            new StringId("editor.command.tools.frame-debugger", "Frame Debugger"),
            "Command-stream capture is milestone E4."
        ),
        (
            "tools.memory",
            new StringId("editor.command.tools.memory", "Memory"),
            "Allocator instrumentation is milestone E4."
        ),
        (
            "tools.statistics",
            new StringId("editor.command.tools.statistics", "Statistics"),
            "Scene statistics are milestone E4."
        ),
        (
            "tools.remote-inspector",
            new StringId("editor.command.tools.remote-inspector", "Remote Inspector"),
            "Attaching to a running build is milestone E4."
        )
    ];

    // ── Help ────────────────────────────────────────────────────────────────────────────────────

    void HelpCommands() {
        // The fifth of doc 20's dangling ids, and the shell has named it since it was written.
        Verb(
            "help.documentation",
            EditorStrings.CommandDocumentation,
            EditorStrings.CategoryHelp,
            () => Browse(DocumentationUrl),
            enabled: () => services.OpenUrl is not null
        );

        Verb(
            "help.api-reference",
            new StringId("editor.command.help.api-reference", "API Reference"),
            EditorStrings.CategoryHelp,
            () => Browse(DocumentationUrl + "/README.md"),
            enabled: () => services.OpenUrl is not null
        );

        Verb(
            "help.release-notes",
            new StringId("editor.command.help.release-notes", "Release Notes"),
            EditorStrings.CategoryHelp,
            () => Browse(DocumentationUrl + "/14-roadmap.md"),
            enabled: () => services.OpenUrl is not null
        );

        Verb(
            "help.report-bug",
            new StringId("editor.command.help.report-bug", "Report a Bug…"),
            EditorStrings.CategoryHelp,
            () => Browse("https://github.com/Rikarin/Vixen/issues/new"),
            enabled: () => services.OpenUrl is not null
        );

        Verb(
            "help.show-log-folder",
            new StringId("editor.command.help.show-log-folder", "Show Log Folder"),
            EditorStrings.CategoryHelp,
            () => Browse(new Uri(dataDirectory).AbsoluteUri),
            enabled: () => services.OpenUrl is not null
        );
    }

    // ── The menus ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The five menus doc 20's Part C names that are made of this application's verbs.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Inserted rather than appended, and each one relative to the last.</b> Part C's
    ///         order is File, Edit, Assets, Entity, Scene, Play, Window, Build, Tools, Help; the
    ///         shell puts File, Edit, Window and Help on the bar, and appending would give File,
    ///         Edit, Window, Help, Assets, Entity, … — a bar where the most-used menus in a 3D editor
    ///         are past the point where people stop looking.
    ///     </para>
    ///     <para>
    ///         Positioned by finding a menu the shell owns rather than by a literal index, so that a
    ///         menu added to the shell's default bar does not silently move these somewhere else.
    ///         The Scene menu is inserted by <see cref="SceneMenu" />, which runs after this.
    ///     </para>
    /// </remarks>
    void ParityMenus() {
        var after = Index(EditorStrings.MenuEdit);

        var assets = Shell.Menus.InsertMenu(++after, EditorStrings.MenuAssets);

        assets.AddSubmenu(EditorStrings.MenuCreate).Add("assets.create");

        assets.AddSeparator()
            .Add("assets.show-in-explorer", "assets.open", "assets.rename", "assets.delete", "assets.move-to")
            .AddSeparator()
            .Add("assets.reimport", "assets.reimport-all")
            .AddSeparator()
            .Add("assets.find-references", "assets.select-dependencies")
            .AddSeparator()
            .Add("assets.refresh", "assets.import", "assets.build");

        var entity = Shell.Menus.InsertMenu(++after, EditorStrings.MenuEntity);

        entity.Add("scene.create-entity", "entity.create-child");
        Creatable(entity);

        entity.Add("entity.create-audio", "entity.create-ui", "entity.create-vfx")
            .AddSeparator()
            .Add("entity.make-prefab", "entity.unpack-prefab", "entity.apply-overrides")
            .AddSeparator()
            .Add("entity.group", "entity.ungroup", "entity.set-parent", "entity.clear-parent")
            .AddSeparator()
            .Add("entity.align-with-view", "entity.move-to-view", "entity.snap-to-floor")
            .AddSeparator()
            .Add("scene.focus")
            .AddSeparator()
            .Add("entity.toggle-active", "entity.toggle-lock");

        // Consecutive, with no gap left for Scene: `SceneMenu` inserts it between Entity and Play
        // afterwards, which shifts these three along by one. Leaving a hole here instead would mean
        // this method knowing what the next one is going to do.
        var play = Shell.Menus.InsertMenu(++after, EditorStrings.MenuPlay);

        play.Add("play.play", "play.pause", "play.step", "play.stop").AddSeparator();

        play.AddSubmenu(new StringId("editor.menu.play-mode", "Mode"))
            .Add("play.mode-in-editor", "play.mode-standalone", "play.mode-server");

        play.AddSubmenu(new StringId("editor.menu.play-options", "Options"))
            .Add("play.maximise", "play.mute-audio", "play.clear-console");

        // ⚠ Counted from Window rather than carried on from Play, because Part C puts Window
        // between them: File, Edit, Assets, Entity, Scene, Play, Window, Build, Tools, Help. The
        // shell owns Window, so the only way to land after it is to find it.
        after = Index(EditorStrings.MenuWindow);

        var build = Shell.Menus.InsertMenu(++after, EditorStrings.MenuBuild);

        build.Add("build.settings").AddSeparator().Add("assets.build", "build.run").AddSeparator();
        build.AddSubmenu(new StringId("editor.menu.build-target", "Target")).Add("build.target");
        build.AddSubmenu(new StringId("editor.menu.build-configuration", "Configuration")).Add("build.configuration");
        build.AddSubmenu(new StringId("editor.menu.build-deploy", "Deploy")).Add("build.deploy");
        build.AddSeparator().Add("build.clean-library", "build.rebuild-shaders");

        Shell.Menus.InsertMenu(++after, EditorStrings.MenuTools)
            .Add("tools.profiler", "tools.frame-debugger", "tools.memory", "tools.statistics")
            .Add("tools.remote-inspector")
            .AddSeparator()
            .Add("tools.plugins", "plugins.reload")
            .AddSeparator()
            .Add("tools.reload-shaders", "tools.reload-styles", "view.clear-console")
            .AddSeparator()
            .Add("tools.diagnostics-report");
    }

    /// <summary>The toolbar doc 20's A1 describes: five sections rather than one flat strip.</summary>
    /// <remarks>
    ///     ⚠ <b>The transform modes are a segmented control and the rest are not.</b> Doc 20's
    ///     objection to the old strip is precise: Translate, Rotate and Scale drawn as three adjacent
    ///     buttons say nothing about being one choice. They are the only group here because they are
    ///     the only set on the bar that is genuinely exclusive — space, snap and grid are three
    ///     independent toggles and drawing them boxed together would claim otherwise.
    /// </remarks>
    void ParityToolbar() {
        Shell.Toolbar.Show(
            new ToolbarButton("view.palette"),
            new ToolbarSeparator(),
            new ToolbarButton("file.save"),
            new ToolbarButton("assets.build"),
            new ToolbarSeparator(),
            new ToolbarGroup("scene.translate", "scene.rotate", "scene.scale"),
            new ToolbarButton("scene.toggle-space"),
            new ToolbarButton("scene.toggle-pivot"),
            new ToolbarButton("scene.toggle-snap"),
            new ToolbarDropdown(
                new StringId("editor.toolbar.gizmo", "Gizmo"),
                "settings",
                "scene.toggle-space",
                "scene.toggle-pivot",
                "scene.toggle-snap",
                null,
                "scene.toggle-grid",
                "scene.toggle-projection"
            ),
            new ToolbarSeparator(),

            // ⚠ Four buttons and not a segmented group, which is the same argument as the one above
            // read the other way. Translate/Rotate/Scale are boxed because they are one choice; Play,
            // Pause, Step and Stop are two toggles and two actions, and boxing them would claim an
            // exclusivity they do not have. What tells them apart here is colour, not a border.
            new ToolbarButton("play.play"),
            new ToolbarButton("play.pause"),
            new ToolbarButton("play.step"),
            new ToolbarButton("play.stop"),
            new ToolbarSeparator(),
            new ToolbarDropdown(
                new StringId("editor.toolbar.layout", "Layout"),
                "layout",
                "view.save-layout",
                "view.reset-layout",
                null,
                "view.toggle-theme"
            )
        );
    }

    // ── Registration helpers ────────────────────────────────────────────────────────────────────

    /// <summary>Registers a command that does something.</summary>
    void Verb(
        string id,
        StringId title,
        StringId category,
        Action run,
        Func<bool>? enabled = null,
        Func<bool>? on = null
    ) =>
        Shell.Commands.Add(
            new EditorCommand(id, title, run) {
                Category = category,
                Enablement = enabled,
                Checked = on
            }
        );

    /// <summary>Registers one of the four transport verbs: an icon, a colour, and a state.</summary>
    /// <remarks>
    ///     The class is the command's and the rules are the theme's — see
    ///     <see cref="EditorCommand.ClassName" /> — so the toolbar stays a view over ids and does not
    ///     acquire a list of which buttons are green.
    /// </remarks>
    void Transport(
        string id,
        string title,
        PathBuilder icon,
        Action run,
        Func<bool>? enabled = null,
        Func<bool>? on = null
    ) =>
        Shell.Commands.Add(
            new EditorCommand(id, new StringId("editor.command." + id, title), run) {
                Category = CategoryPlay,
                Icon = icon,
                ClassName = "transport-" + id["play.".Length..],
                Enablement = enabled,
                Checked = on
            }
        );

    /// <summary>Registers one of a pair of verbs that mean different things in different panels.</summary>
    /// <remarks>
    ///     The context is what lets its twin have the same key. See the class's remarks, and
    ///     <see cref="EditorCommand.Context" /> for why the alternative — an enablement predicate
    ///     guessing from the selection — gets it wrong exactly when both panels have one.
    /// </remarks>
    void Scoped(string id, string title, string context, Action run, Func<bool>? enabled = null) =>
        Shell.Commands.Add(
            new EditorCommand(id, new StringId("editor.command." + id, title), run) {
                Category = EditorStrings.CategoryEdit,
                Context = context,
                Enablement = enabled
            }
        );

    /// <summary>Registers a command that is declared, disabled, and says which milestone builds it.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the shape of doc 20's E0 exit criterion and not a placeholder.</b> "No menu
    ///     line is silently missing" is a claim about what a professional finds in their first week:
    ///     a bar where every verb they know has a home, and where the ones that are not there yet say
    ///     so rather than leaving them to conclude the engine cannot. Replacing one of these with a
    ///     real implementation is deleting three lines.
    /// </remarks>
    void Planned(string id, StringId title, StringId category, string reason) =>
        Shell.Commands.Add(
            new EditorCommand(id, title, () => { }) {
                Category = category,
                Unavailable = new StringId("editor.planned." + id, reason)
            }
        );

    // ── What the verbs do ───────────────────────────────────────────────────────────────────────

    /// <summary>Empties the scene and starts again, asking first if there is anything to lose.</summary>
    void NewScene() =>
        Confirm(
            scene.IsDirty.Value,
            "Discard unsaved changes?",
            Path.GetFileName(scenePath) + " has changes that have not been written.",
            () => {
                scene.Delete([.. scene.Roots]);
                scene.Stack.Clear();
                scene.Stack.MarkClean();

                scene.Add("Scene Root", LocalTransform.Identity);
                hierarchyStale = true;
            },
            confirm: "Discard"
        );

    /// <summary>Picks a scene file and loads it over the open one.</summary>
    /// <remarks>
    ///     ⚠ <b>Into the same document, and the path moves with it.</b> The scene panel, the gizmo,
    ///     the inspector and the picker all hold this document; swapping the object would leave four
    ///     panels looking at the old one until each was rebuilt. What changes is the contents and the
    ///     writer — which is also what makes the next Save write where the file came from.
    /// </remarks>
    void OpenScene() {
        if (services.Dialogs is not { } dialogs) {
            return;
        }

        Confirm(
            scene.IsDirty.Value,
            "Discard unsaved changes?",
            Path.GetFileName(scenePath) + " has changes that have not been written.",
            () => deferred.When(
                dialogs.OpenFileAsync(
                    new FileDialogOptions {
                        Title = "Open Scene",
                        InitialDirectory = project.Paths.Assets,
                        Filters = [new FileFilter("Vixen scene", SceneSerializer.Extension.TrimStart('.'))]
                    }
                ),
                path => {
                    if (path is null) {
                        return;
                    }

                    LoadScene(path);
                },
                failure => Shell.Notifications.Show("Could not open the scene", NotificationSeverity.Error, failure.Message)
            ),
            confirm: "Discard"
        );
    }

    void LoadScene(string path) {
        try {
            scene.Selection.Clear();
            scene.Delete([.. scene.Roots]);

            if (SceneSerializer.Load(scene, path) == 0) {
                Shell.Notifications.Show(
                    "Nothing was loaded",
                    NotificationSeverity.Warning,
                    Path.GetFileName(path) + " has no entities in it."
                );
            }

            scenePath = path;
            scene.Writer = new SceneFileWriter(path);
            scene.SetTitle(Path.GetFileNameWithoutExtension(path));

            scene.Stack.Clear();
            scene.Stack.MarkClean();

            hierarchyStale = true;
            Shell.Notifications.Success(Path.GetFileName(path));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not open the scene", NotificationSeverity.Error, exception.Message);
        }
    }

    /// <summary>Asks where to put the scene, and writes it there from now on.</summary>
    void SaveSceneAs() {
        if (services.Dialogs is not { } dialogs) {
            return;
        }

        deferred.When(
            dialogs.SaveFileAsync(
                new FileDialogOptions {
                    Title = "Save Scene As",
                    InitialDirectory = Path.GetDirectoryName(scenePath) ?? project.Paths.Assets,
                    SuggestedFileName = Path.GetFileName(scenePath),
                    Filters = [new FileFilter("Vixen scene", SceneSerializer.Extension.TrimStart('.'))]
                }
            ),
            path => {
                if (path is null) {
                    return;
                }

                scenePath = path;
                scene.Writer = new SceneFileWriter(path);
                scene.SetTitle(Path.GetFileNameWithoutExtension(path));

                SaveScene();
                project.Assets.Scan();
            },
            failure => Shell.Notifications.Show("Could not save the scene", NotificationSeverity.Error, failure.Message)
        );
    }

    /// <summary>Writes every open document that has changes.</summary>
    void SaveAll() {
        try {
            var written = project.SaveAll();
            Shell.Notifications.Success($"{written} document(s) saved");
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not save everything", NotificationSeverity.Error, exception.Message);
        }
    }

    /// <summary>Copies files chosen from the OS into the project, then imports.</summary>
    /// <remarks>
    ///     ⚠ <b>Copied rather than referenced.</b> An asset outside the project tree is one the
    ///     content build cannot find, the reference index cannot name and a colleague does not have —
    ///     every engine that allowed it spent years telling people why their build was broken.
    /// </remarks>
    void ImportFiles() {
        if (services.Dialogs is not { } dialogs) {
            return;
        }

        deferred.When(
            dialogs.OpenFilesAsync(new FileDialogOptions { Title = "Import Assets", AllowsMultipleSelection = true }),
            paths => {
                if (paths.Count == 0) {
                    return;
                }

                var copied = 0;

                try {
                    Directory.CreateDirectory(project.Paths.Assets);

                    foreach (var path in paths) {
                        var destination = Path.Combine(project.Paths.Assets, Path.GetFileName(path));

                        // ⚠ Never over an existing file. Two textures called `wood.png` from two
                        // folders is the ordinary case, and silently replacing the one already in the
                        // project is a change nothing records and undo cannot reach.
                        if (File.Exists(destination)) {
                            Shell.Notifications.Show(
                                Path.GetFileName(path) + " is already in the project",
                                NotificationSeverity.Warning,
                                "Rename it, or import it into a folder of its own."
                            );

                            continue;
                        }

                        File.Copy(path, destination);
                        copied++;
                    }
                } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                    Shell.Notifications.Show("Could not copy the files", NotificationSeverity.Error, exception.Message);
                    return;
                }

                if (copied > 0) {
                    project.Assets.Scan();
                    browser?.Rescan();
                    content.Import();
                }
            },
            failure => Shell.Notifications.Show("Could not import", NotificationSeverity.Error, failure.Message)
        );
    }

    void OpenSelectedAsset() {
        if (project.Selection.Count > 0) {
            Open(project.Selection[0]);
        }
    }

    void ShowSelectedAsset() {
        if (project.Selection.Count == 0 || !project.Assets.TryGetByGuid(project.Selection[0], out var entry)) {
            return;
        }

        var full = Path.Combine(project.Paths.Root, entry.Path);

        // The folder rather than the file: every desktop opens a directory URI in its file manager
        // and none of them agree on how to ask for a file to be revealed.
        Browse(new Uri(Path.GetDirectoryName(full) ?? project.Paths.Root).AbsoluteUri);
    }

    /// <summary>Throws away the import cache and the artefacts, so the next build starts clean.</summary>
    void CleanLibrary() {
        try {
            if (Directory.Exists(project.Paths.Library)) {
                Directory.Delete(project.Paths.Library, recursive: true);
            }

            Shell.Notifications.Success("Library cleared");
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not clear Library", NotificationSeverity.Error, exception.Message);
        }
    }

    /// <summary>Loads the five user-agent sheets again, so a theme edit shows without a restart.</summary>
    /// <remarks>
    ///     The one Tools verb that is genuinely free: the sheets are strings in this process and
    ///     <c>Install</c> is idempotent, so re-running the five is a restyle rather than a reload.
    /// </remarks>
    void ReloadStyles() {
        Shell.Theme.LoadTokens(store.Read("theme.yaml"));
        Shell.Notifications.Success("Styles reloaded");
    }

    /// <summary>Opens the source a console line came from, as far as anything here can tell.</summary>
    /// <remarks>
    ///     ⚠ <b>The folder, not the file and not the line, and that is the honest limit today.</b>
    ///     Doc 20 asks for double-click-to-open-source "through the external-tool setting" — a
    ///     preference that arrives with the Preferences window in E3 — and a stack frame carries a
    ///     file and a line only in a build with symbols beside it. What is available is the URL
    ///     opener the host already has, so a record with no exception says so rather than appearing
    ///     to do nothing.
    /// </remarks>
    void Reveal(LogRecord record) {
        if (record.Exception is null) {
            Shell.Notifications.Show(
                "Nothing to open",
                NotificationSeverity.Info,
                "That line has no exception, so there is no source to go to."
            );

            return;
        }

        Browse(new Uri(project.Paths.Root).AbsoluteUri);
    }

    void Browse(string url) {
        if (services.OpenUrl is not { } open || !open(url)) {
            Shell.Notifications.Show("Could not open the link", NotificationSeverity.Warning, url);
        }
    }

    // ── Selection and hierarchy verbs ───────────────────────────────────────────────────────────

    void InvertSelection() {
        var selected = scene.Selection.ToHashSet();
        scene.Selection.Set([.. scene.Entities.Where(entity => !selected.Contains(entity))]);
    }

    void SelectChildren() {
        List<Entity> children = [];

        foreach (var entity in scene.Selection) {
            // ⚠ Enumerated rather than added as a range. `ChildrenOf` gives back a struct sequence
            // over the sibling list, which is what keeps walking a hierarchy allocation-free — and
            // `AddRange` would box it into an enumerator per parent for no reason at all.
            foreach (var child in Hierarchy.ChildrenOf(world, entity)) {
                children.Add(child);
            }
        }

        if (children.Count > 0) {
            scene.Selection.Set(children);
        }
    }

    void SelectParent() {
        List<Entity> parents = [];

        foreach (var entity in scene.Selection) {
            if (Hierarchy.ParentOf(world, entity) is var parent && parent != Entity.Null && !parents.Contains(parent)) {
                parents.Add(parent);
            }
        }

        if (parents.Count > 0) {
            scene.Selection.Set(parents);
        }
    }

    void CreateChild() {
        if (scene.Selection.Count == 0) {
            return;
        }

        var created = scene.Create("Entity", LocalTransform.Identity, scene.Selection[0]);
        scene.Selection.Set([created]);
    }

    /// <summary>Puts an empty over the selection and hangs everything selected under it.</summary>
    /// <remarks>
    ///     ⚠ <b>Under the first selected entity's parent, not at the root.</b> Grouping three
    ///     children of a rig and having the group appear beside the rig is the behaviour that makes
    ///     Ctrl+G untrustworthy — the group belongs where the things being grouped already were.
    /// </remarks>
    void Group() {
        if (scene.Selection.Count == 0) {
            return;
        }

        var members = scene.Selection.ToList();
        var parent = Hierarchy.ParentOf(world, members[0]);

        var group = scene.Create("Group", LocalTransform.Identity, parent);

        foreach (var member in members) {
            scene.Reparent(member, group);
        }

        scene.Selection.Set([group]);
    }

    void ClearParent() {
        foreach (var entity in scene.Selection.ToList()) {
            scene.Reparent(entity, Entity.Null);
        }
    }

    /// <summary>Puts the selection where the camera is, facing the way it faces.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the stack as one command, so one Ctrl+Z puts all of them back.</b> A
    ///     multi-selection aligned entity by entity would be one undo step per object, which is the
    ///     shape of every "undo did not undo what I did" report. The write goes to
    ///     <c>LocalTransform</c> and <c>TransformSystem</c> turns it into the matrix the viewport
    ///     draws from — see <c>EditorApplication.ResolveTransforms</c>.
    /// </remarks>
    void AlignWithView() {
        if (viewport is not { } pane) {
            return;
        }

        var targets = scene.Selection
            .Where(entity => world.Has<LocalTransform>(entity))
            .Select(entity => (Entity: entity, Was: world.Read<LocalTransform>(entity)))
            .ToList();

        if (targets.Count == 0) {
            return;
        }

        var position = pane.Camera.Position;

        // ⚠ From the camera's own yaw and pitch rather than from `EditorCamera.Rotation`, which is a
        // basis matrix and which nothing here can turn back into a quaternion — the maths library
        // deliberately has no matrix-to-quaternion path. The two agree: yaw about +Y then pitch
        // about +X takes −Z to exactly the vector `EditorCamera.Forward` computes, which is the
        // engine's forward (Conventions.md § Handedness).
        var rotation = Quaternion.FromYawPitchRoll(pane.Camera.Yaw, pane.Camera.Pitch, 0f);

        scene.Stack.Execute(
            new DelegateCommand(
                "Align With View",
                _ => {
                    foreach (var (entity, was) in targets) {
                        world.Set(entity, was with { Position = position, Rotation = rotation });
                    }
                },
                _ => {
                    foreach (var (entity, was) in targets) {
                        world.Set(entity, was);
                    }
                }
            )
        );
    }

    // ── Play mode ───────────────────────────────────────────────────────────────────────────────

    void EnterPlay() {
        if (!play.Play()) {
            return;
        }

        // Before the notification, so the line saying what play mode does is the first thing in the
        // console rather than the last thing before it was emptied.
        if (console is { ClearsOnPlay: true } view) {
            view.Clear();
        }

        // ⚠ Said before it matters rather than after it has cost something. Doc 20 calls this the one
        // place where being different from Unity is better: the rule is that play-mode edits are
        // discarded, and an editor that says so on the way in is honest where a silent loss is the
        // single most complained-about behaviour in that editor.
        Shell.Notifications.Show(
            "Play mode",
            NotificationSeverity.Info,
            "Changes made while playing are discarded when you stop."
        );
    }

    void LeavePlay() {
        var restored = play.Stop(scene.Selection);

        scene.Selection.Set(restored);
        hierarchyStale = true;
    }

    // ── Save on close ───────────────────────────────────────────────────────────────────────────

    /// <summary>Asks about unsaved work, and closes when it has an answer.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 20 is blunt about this and it is right: an editor that loses an afternoon
    ///         once is one nobody opens again.</b> Every document already knows whether it is dirty;
    ///         what was missing was the thing that asks. Save writes and closes, Discard closes, and
    ///         backing out leaves the editor open — which is also what the window's close button now
    ///         goes through.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asked once, however many times the button is pressed.</b> A close request while
    ///         the prompt is on screen would queue a second identical prompt behind the first, and
    ///         answering the first would then be met by its twin.
    ///     </para>
    /// </remarks>
    public void RequestClose() {
        if (IsClosing || closing) {
            return;
        }

        if (!project.HasUnsavedChanges.Value) {
            IsClosing = true;
            return;
        }

        closing = true;

        _ = Ask();

        async Task Ask() {
            var answer = await Shell.Dialogs.ChooseAsync(
                "Save changes before closing?",
                Unsaved(),
                "Cancel",
                "Discard",
                "Save"
            ).ConfigureAwait(true);

            closing = false;

            switch (answer) {
                case 2:
                    SaveAll();
                    IsClosing = true;
                    break;

                case 1:
                    IsClosing = true;
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>What has changes in it, named, for the prompt to show.</summary>
    string Unsaved() {
        var names = project.Documents
            .Where(document => document.IsDirty.Value)
            .Select(document => document.Title.Peek())
            .ToList();

        return names.Count switch {
            0 => "There are unsaved changes.",
            1 => names[0] + " has unsaved changes.",
            _ => string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", and more" : string.Empty)
                + " have unsaved changes."
        };
    }

    /// <summary>Runs something, asking first if there is unsaved work to lose.</summary>
    void Confirm(bool ask, string title, string message, Action then, string confirm) {
        if (!ask) {
            then();
            return;
        }

        _ = Run();

        async Task Run() {
            if (await Shell.Dialogs.ConfirmAsync(title, message, confirm, danger: true).ConfigureAwait(true)) {
                then();
            }
        }
    }
}
