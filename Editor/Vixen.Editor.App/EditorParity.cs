// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Platform;
using Vixen.Rendering;
using Vixen.Ui;
using Vixen.Ui.Controls;

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

    /// <summary>Everything Part C names that this application owns.</summary>
    void ParityCommands() {
        FileCommands();
        AssetCommands();
        BuildAndToolCommands();
    }

    // ── File ────────────────────────────────────────────────────────────────────────────────────

    void FileCommands() {
        // File ▸ New Project and Open Project are `WorkspaceModule`'s (#1296), over `ProjectChooser`.

        Verb(
            "file.new-scene",
            EditorStrings.CommandFileNewScene,
            EditorStrings.CategoryFile,
            NewScene
        );

        Verb(
            "file.open-scene",
            EditorStrings.CommandFileOpenScene,
            EditorStrings.CategoryFile,
            OpenScene,
            enabled: () => services.CanPick
        );

        Verb(
            "file.save-as",
            EditorStrings.CommandFileSaveAs,
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

        // File ▸ Revert and `document.revert` are `WorkspaceModule`'s (#1296), over `Discarding.Revert`.

        // Import Files and Export Package are `LibraryModule`'s (#1296) — see `LibraryQuestions`.

        Shell.Keys.SetDefault("file.new-scene", new KeyChord(InputKey.N, ModifierKeys.Control));
        Shell.Keys.SetDefault("file.open-scene", new KeyChord(InputKey.O, ModifierKeys.Control));
        Shell.Keys.SetDefault("file.save-as", new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift));
        Shell.Keys.SetDefault("file.save-all", new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Alt));

        // File ▸ Open Recent's projects are `WorkspaceModule`'s (#1296), over `IProjectSession.Recent`.
    }

    // ── Edit ────────────────────────────────────────────────────────────────────────────────────

    // Nothing on the Edit menu is this application's any more.
    //
    // Undo History, Preferences, Keybindings and File ▸ Project Settings are `WorkspaceModule`'s
    // (#1296): lines that open a workspace panel, over `Shell.Workspace`.
    //
    // Delete, Rename, Duplicate, the clipboard lines and the selection verbs are `SceneModule`'s
    // (#1296), over `ISceneEditing` and the outliner it builds.
    //
    // ⚠ `edit.search-everywhere` is registered by the shell, because the overlay is the shell's, and
    // greys itself out where nothing has added a source. The three sources are the modules' (#1518),
    // through `PluginContext.AddSearchSource`: assets `LibraryModule`'s, entities `SceneModule`'s and
    // commands `WorkspaceModule`'s, so switching one off takes its rows with it.
    //
    // `edit.find-references` is `LibraryModule`'s (#1292), over the same walk as the Assets menu's.

    // ── Assets ──────────────────────────────────────────────────────────────────────────────────

    void AssetCommands() {
        // Create (the kind chooser) is `LibraryModule`'s (#1296) — see `LibraryQuestions`.

        // Open is `LibraryModule`'s now (#1292): a name for the published `IWorkspace.Open`, which is
        // this application's open and asks `IDocumentHost.TryOpen` for the document.

        // Bake Mesh Maps — the verb, its panel and its settings — is `MeshMapBaking`'s (#1296).

        // Show in Explorer, New Folder, Delete and Reimport All are `LibraryModule`'s now (#1292): names for
        // `IContentLibrary` verbs, registered through `PluginContext`. Rename and Move To stay here —
        // the first starts the browser panel's inline editor and the second asks through this
        // editor's drawn chooser — and both end in `IContentLibrary.Rename`/`Move`.
        Verb(
            "assets.rename",
            EditorStrings.CommandAssetsRename,
            EditorStrings.CategoryAssets,
            RenameSelectedAsset,
            enabled: () => Browser is not null && project.Selection.Count == 1
        );

        // ⚠ Through a drawn folder chooser rather than a native one, and that is the whole point of
        // the distinction `DialogService` draws. A native picker is about the user's disk; this is a
        // question about the *project*, whose folders carry GUIDs and whose paths are relative — and
        // a native dialog would happily answer with a directory outside the project, which is the
        // one answer `AssetOperations.Move` cannot take.
        Verb(
            "assets.move-to",
            EditorStrings.CommandAssetsMoveTo,
            EditorStrings.CategoryAssets,
            MoveSelectedAssets,
            enabled: () => Browser is not null && project.Selection.Count > 0
        );

        // Revert and Show History are `SourceControlModule`'s (#1505), over the published `ISourceControl`.
        // Reimport (declared, disabled) is `LibraryModule`'s (#1296).

        // Find References and Select Dependencies are `LibraryModule`'s now (#1292): names for
        // `IContentLibrary.Referrers` and `Dependencies`, registered through `PluginContext`.
    }

    /// <summary>Asks which of the project's folders to move the selection into.</summary>
    /// <remarks>
    ///     ⚠ <b>Every folder in the project, flat, by path.</b> A tree would be prettier and would
    ///     need the browser's own tree in a dialog; a sorted list of relative paths is searchable by
    ///     eye, is what the operation actually takes, and cannot express a destination outside the
    ///     project — which is the one answer <c>AssetOperations.Move</c> refuses. The move itself is
    ///     <c>IContentLibrary.Move</c>; the question is this editor's dialog.
    /// </remarks>
    void MoveSelectedAssets() {
        List<AssetId> assets = [.. project.Selection];

        if (assets.Count == 0) {
            return;
        }

        var folders = project.Assets.Entries
            .Where(entry => entry.IsFolder)
            .Select(entry => entry.Path)
            .Append("Assets")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        _ = Ask();

        async Task Ask() {
            var destination = await Shell.ChooseAsync(
                assets.Count == 1 ? "Move to which folder?" : $"Move {assets.Count} assets to which folder?",
                folders,
                folder => folder
            ).ConfigureAwait(true);

            if (destination is not { Length: > 0 } folder) {
                return;
            }

            library.Move(assets, folder);
        }
    }

    // ── Play ────────────────────────────────────────────────────────────────────────────────────

    // Nothing on the Play menu is this application's any more (#1289, #1296). Play, Pause, Step and
    // Stop are names for `IPlaySession`'s verbs, and `PlayModule` registers them through
    // `PluginContext`, the door a third party uses. The three play modes, Mute Audio and the Overlays
    // submenu are its too; so are Options ▸ Maximise (over `ScenePanes.Arrangement`, see
    // `PlayMaximise`) and Options ▸ Clear Console (over `EditorConsole`'s preference), and what
    // entering play says (`PlayModule.Announce`). Tools ▸ Clear Console is `WorkspaceModule`'s.

    // ── Build and Tools ─────────────────────────────────────────────────────────────────────────

    void BuildAndToolCommands() {
        // Build Settings, Build and Run, and the two radio submenus are `DeliveryModule`'s (#1295):
        // names for `IPlayerDelivery`'s verbs, registered through `PluginContext`. This file's job is
        // the bar rather than what is behind it.

        // Deploy is the diagnostics module's, because it opens the device manager E4 built. It is
        // still a Build-menu line — Part C puts it there — and the window is the Tools one.

        // Clean Library is `LibraryModule`'s (#1296): it deletes the import cache, behind the library's busy flag.

        // Rebuild Shaders (declared, disabled) is `DeliveryModule`'s (#1296): it is the player build's shader bundle.

        Verb(
            "tools.plugins",
            EditorStrings.CommandToolsPlugins,
            EditorStrings.CategoryTools,
            () => Shell.Workspace.Open(PluginsPanel)
        );

        // ⚠ This said "the editor loads its shaders once at start-up", and that has been false since
        // `EditorEffects` was constructed: an `.rvn` saved under the project already re-reads every
        // source and drops the resolved table — `EditorFrames.ReloadShaders` on an external edit, and
        // its empty-list case means "everything" precisely so a person can ask. So the verb is the
        // manual door onto a mechanism that was already running, which is what somebody wants after
        // editing a shader through a path the watcher does not see.
        Verb(
            "tools.reload-shaders",
            EditorStrings.CommandToolsReloadShaders,
            EditorStrings.CategoryTools,
            ReloadShadersNow,
            enabled: () => Effects is not null
        );

        // ⚠ The producer end of a chain every other link of which was finished and called by nothing.
        // `EditorStrings.Template`, `StringCatalogYaml.Save`, `StringCatalogYaml.Load` and
        // `Strings.Use` existed, were tested end to end inside one test method, and had no production
        // caller between them — so the signal that exists precisely to re-label a *running* interface
        // had never been written to. This writes the template; `EditorPreferences.Language` reads the
        // translated file back. See https://github.com/Rikarin/Vixen/issues/1229.
        Verb(
            "tools.export-strings",
            EditorStrings.CommandToolsExportStrings,
            EditorStrings.CategoryTools,
            ExportStringTemplate
        );

        // The report is `DiagnosticsCommands`' too, and it is the one line here that E4 only half
        // finished: it carries the log, the memory arenas, the scene's counts and the last capture,
        // and says in the file that the minidump and the undo history are E6's.
    }

    // ── Help ────────────────────────────────────────────────────────────────────────────────────

    // The Help menu's verbs are `HelpModule`'s (#1296): registered through `PluginContext` over the
    // published `IExternalLinks` and `EditorDataDirectory`, which is everything they read of this class.

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
    ///         The Scene menu is inserted by <c>SceneModule</c>'s <see cref="SceneMenuBar" />, later.
    ///     </para>
    /// </remarks>
    void ParityMenus() {
        var after = Index(EditorStrings.MenuEdit);

        var assets = Shell.Menus.InsertMenu(++after, EditorStrings.MenuAssets);

        // ⚠ The authoring surfaces' asset kinds are on the Create submenu and not behind a dialog,
        // because a format nobody can make a file of is a format nobody can reach. `assets.create`
        // stays beside them as the general "from a template" line it always named.
        assets.AddSubmenu(EditorStrings.MenuCreate)
            .Add("assets.new-folder", "assets.create")
            .AddSeparator()
            .AddDynamic(() => CreatableIds);

        assets.AddSeparator()
            .Add("assets.show-in-explorer", "assets.open", "assets.rename", "assets.delete", "assets.move-to")
            .AddSeparator()
            .Add("assets.revert", "assets.history")
            .AddSeparator()
            .Add("assets.reimport", "assets.reimport-all", "assets.bake-mesh-maps")
            .AddSeparator()
            .Add("assets.find-references", "assets.select-dependencies")
            .AddSeparator()

            // ⚠ Named on the Assets menu as well as in Window, and the second is not enough on its
            // own. Every panel gets a `view.panel.*` toggle for free, so Addressables was already
            // *listed* — under Window, among two dozen others, which is where you look for a panel
            // you know exists and not for a feature you are wondering whether the editor has. What
            // ships an asset belongs beside the other things that do.
            .Add(EditorShell.PanelCommand(AddressablesPanel))
            .AddSeparator()
            .Add("assets.refresh", "assets.import", "assets.build");

        var entity = Shell.Menus.InsertMenu(++after, EditorStrings.MenuEntity);

        entity.Add("scene.create-entity", "entity.create-child");
        Creatable(entity);

        entity.Add("entity.create-audio", "entity.create-ui", "entity.create-vfx")
            .AddSeparator()
            .Add("entity.make-prefab", "entity.unpack-prefab", "entity.apply-overrides")
            .AddSeparator()
            .Add("entity.group", "entity.group-lod", "entity.ungroup", "entity.set-parent", "entity.clear-parent")
            .AddSeparator()
            .Add("entity.align-with-view", "entity.move-to-view", "entity.snap-to-floor")
            .AddSeparator()

            // Part D § Transform, which listed these without a ✅ while everything around them had
            // one — and had no line for any of them.
            .Add("entity.reset-transform", "entity.copy-transform", "entity.paste-transform")
            .Add("entity.align", "entity.distribute", "entity.relative-transform")
            .AddSeparator()
            .Add("scene.focus")
            .AddSeparator()
            .Add("entity.toggle-active", "entity.toggle-hidden", "entity.toggle-lock");

        // Consecutive, with no gap left for Scene: `SceneMenuBar` inserts it between Entity and Play
        // afterwards, which shifts these three along by one. Leaving a hole here instead would mean
        // this method knowing what the next one is going to do.
        var play = Shell.Menus.InsertMenu(++after, EditorStrings.MenuPlay);

        play.Add("play.play", "play.pause", "play.step", "play.stop").AddSeparator();

        play.AddSubmenu(EditorStrings.MenuPlayMode)
            .Add("play.mode-in-editor", "play.mode-standalone", "play.mode-server");

        play.AddSubmenu(EditorStrings.MenuPlayOptions)
            .Add("play.maximise", "play.mute-audio", "play.clear-console");

        play.AddSubmenu(EditorStrings.MenuPlayOverlays)
            .Add(PlayModule.SwitchableOverlays().Select(PlayModule.OverlayCommand).ToArray());

        // ⚠ Counted from Window rather than carried on from Play, because Part C puts Window
        // between them: File, Edit, Assets, Entity, Scene, Play, Window, Build, Tools, Help. The
        // shell owns Window, so the only way to land after it is to find it.
        after = Index(EditorStrings.MenuWindow);

        var build = Shell.Menus.InsertMenu(++after, EditorStrings.MenuBuild);

        build.Add("build.settings").AddSeparator().Add("assets.build", "build.run").AddSeparator();
        build.AddSubmenu(EditorStrings.MenuBuildTarget).Add(DeliveryModule.BuildIds.Targets);

        // ⚠ Four lines where Part C names two, and the label is Part C's word for them. Doc 17's
        // variants are the axis a player build actually has — Development is an optimised build that
        // keeps its profiler, and Server is a Release one with no window — and the compiler
        // configuration is derived from the variant rather than chosen beside it. A menu of Debug
        // and Release over a setting of four would leave two of them unreachable and unmarkable.
        build.AddSubmenu(EditorStrings.MenuBuildConfiguration).Add(DeliveryModule.BuildIds.Variants);

        build.AddSubmenu(EditorStrings.MenuBuildDeploy).Add("build.deploy");
        build.AddSeparator().Add("build.clean-library", "build.rebuild-shaders");

        // ⚠ Six lines where Part C names five. The GPU timeline is a panel of its own rather than a
        // tab inside the profiler, because it is a different measurement of a different device with a
        // different failure mode — a machine whose graphics queue cannot be timed still profiles its
        // CPU perfectly well, and a tab that was empty on that machine would read as a broken
        // profiler rather than as an untimeable GPU.
        Shell.Menus.InsertMenu(++after, EditorStrings.MenuTools)
            .Add("tools.profiler", "tools.gpu", "tools.frame-debugger", "tools.memory", "tools.statistics")
            .Add("tools.network", "tools.remote-inspector")
            .AddSeparator()
            .Add("tools.plugins", "plugins.reload")
            .AddSeparator()
            .Add("tools.reload-shaders", "tools.reload-styles", "view.clear-console")
            .AddSeparator()
            .Add("tools.export-strings", "tools.diagnostics-report");
    }

    /// <summary>The mode bar doc 20's A1 asks for, with the two modes doc 24's P0 ships.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Select first, because the first mode registered is the one the editor starts in
    ///         and the one <c>EditorModes.Remove</c> falls back to.</b> Select claims no context, no
    ///         panel, no toolbar and no input, so a viewport in it behaves exactly as the viewport did
    ///         before modes existed — which is the bar doc 20 sets for shipping the seam with a mode
    ///         set that is not final.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Blockout second, and what it proves is the arbitration rather than a tool.</b> It
    ///         owns <c>1</c>, <c>2</c>, <c>3</c> and <c>4</c> — object, vertex, edge and face, the
    ///         binding every modelling tool has had for thirty years — while it is active, and
    ///         releases them to view-bookmark recall when it is not. Doc 24's B2 is that this cannot
    ///         be retrofitted: both claims on those keys are right, and a mode is the only thing that
    ///         resolves them without making one of them worse.
    ///     </para>
    /// </remarks>
    void RegisterModes() {
        Shell.Modes.Add(new SelectMode());

        // ⚠ Blockout is not registered here any more, and that is doc 36 § P3. It registers itself
        // through `PluginContext` — the same door a third party comes through — and what it needs of
        // this application it asks for through `PluginServices`: the editing state, the work plane,
        // a mesh baker and a mesh source. See `BlockoutModule`, and `PluginPoints` for the four
        // services that answer it.
        //
        // ⚠ Here rather than beside the plugin loading, because the mode bar's order is this line's.
        // Blockout is the editor's second mode and Terrain its third, and a module activated after
        // the terrain panels had registered theirs would come up third.
        //
        // ⚠ Once the executable is split off this line moves to it, along with this assembly's
        // reference to `Vixen.Editor.Blockout` — which is the whole of what is left to do for this
        // feature.
        foreach (var (id, name, module) in modules) {
            plugins.Activate(id, name, module);
        }

        //
        // ⚠ Entering a mode claims the context without waiting for a press in the pane. Somebody who
        // has just clicked Blockout has aimed at the viewport, and a mode whose toolbar buttons were
        // greyed until the viewport had also been clicked would be one where the first thing you do
        // in a new mode does nothing.
        //
        // ⚠ Leaving one hands the context back to the scene *only if a mode still had it*. Switching
        // to Select while the content browser has the focus must not claim that the viewport does —
        // and any press in any panel overwrites this anyway, which is what makes the guard enough.
        Shell.Modes.Changed += modes => {
            if (modes.Context is { } claimed) {
                Shell.Context = claimed;
            } else if (IsModeContext(Shell.Context)) {
                Shell.Context = SceneContext;
            }
        };
    }

    /// <summary>Whether a context is one some registered mode claims.</summary>
    bool IsModeContext(string? context) =>
        context is not null
        && Shell.Modes.Modes.Any(mode => string.Equals(mode.Context, context, StringComparison.Ordinal));

    /// <summary>The window's own strip: what is about the application rather than about a pane.</summary>
    /// <remarks>
    ///     ⚠ <b>The gizmo controls are deliberately <i>not</i> here, and they used to be.</b> This
    ///     bar carried Translate/Rotate/Scale, the space, pivot and snap toggles and a Gizmo dropdown
    ///     — every one of which is also on the strip floating over the scene pane, six inches below
    ///     and pointing at the same commands. Two copies of one control is not merely redundant: they
    ///     are two places to look for the state, and the one that is not beside the viewport is the
    ///     one that is read wrong, because a four-pane layout has four gizmo modes and a window has
    ///     one bar. <c>ViewportChrome</c> shows the <i>focused</i> pane's, which is the only strip
    ///     that can be telling the truth.
    ///
    ///     What is left is what belongs to the window: the palette, the two verbs that write to disk,
    ///     the transport, and the layout.
    /// </remarks>
    void ParityToolbar() {
        Shell.Toolbar.Show(
            new ToolbarButton("view.palette"),
            new ToolbarSeparator(),
            new ToolbarButton("file.save"),
            new ToolbarButton("assets.build"),
            new ToolbarSeparator(),

            // ⚠ Boxed, and for a different reason from the gizmo modes on `ViewportChrome`'s strip.
            // Those are one *choice* and the box says so; the transport is one *control* — a
            // transport bar is a single object in every editor, every player and every tape machine
            // there has ever been, and four buttons with gaps between them read as four unrelated
            // verbs that happen to be adjacent. What still tells them apart is colour, which is why
            // the box does not have to.
            new ToolbarBox("play.play", "play.pause", "play.step", "play.stop"),
            new ToolbarSeparator(),
            new ToolbarDropdown(
                EditorStrings.ToolbarLayout,
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

    /// <summary>Registers a command that is declared, disabled, and says which milestone builds it.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the shape of doc 20's E0 exit criterion and not a placeholder.</b> "No menu
    ///     line is silently missing" is a claim about what a professional finds in their first week:
    ///     a bar where every verb they know has a home, and where the ones that are not there yet say
    ///     so rather than leaving them to conclude the engine cannot. Replacing one of these with a
    ///     real implementation is deleting three lines.
    /// </remarks>
    void Planned(string id, StringId title, StringId category) =>
        Shell.Commands.Add(
            new EditorCommand(id, title, () => { }) {
                Category = category,
                Unavailable = EditorStrings.PlannedReasons[id]
            }
        );

    // ── What the verbs do ───────────────────────────────────────────────────────────────────────

    /// <summary>Empties the scene and starts again, asking first if there is anything to lose.</summary>
    void NewScene() =>
        Confirm(
            scene.HasUnsavedChanges.Value,
            "Discard unsaved changes?",
            Path.GetFileName(scenePath) + " has changes that have not been written.",
            () => {
                scene.Delete([.. scene.Roots]);
                scene.Stack.Clear();
                scene.Stack.MarkClean();

                scene.Add("Scene Root", LocalTransform.Identity);
                sceneEditing.Restructured();
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
            scene.HasUnsavedChanges.Value,
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

    /// <summary>Loads a scene file over the open scene, which then writes there.</summary>
    /// <param name="path">The chosen file.</param>
    /// <remarks>
    ///     What Open Scene's answer does, named so a test can give the answer without a dialog. Refused,
    ///     like <see cref="SaveSceneAs(string)" />, onto a file another document edits, and it closes the
    ///     tab named after the file the scene leaves.
    /// </remarks>
    internal void LoadScene(string path) {
        if (EditedElsewhere(path, scene) is { } holder) {
            RefuseSecondDocument(path, holder, "Could not open the scene");

            return;
        }

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

            sceneEditing.Restructured();
            CloseTabsTheSceneLeft();
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

                SaveSceneAs(path);
            },
            failure => Shell.Notifications.Show("Could not save the scene", NotificationSeverity.Error, failure.Message)
        );
    }

    /// <summary>Writes the scene to a file of the user's choosing, and writes it there from now on.</summary>
    /// <param name="path">The chosen file.</param>
    /// <remarks>
    ///     What the dialog's answer does, named so a test can give the answer without a dialog. ⚠
    ///     <b>Refused onto a file another document edits</b> — see <see cref="EditedElsewhere" /> for
    ///     which ones count — and it closes any tab named after the file the scene leaves (#1416), see
    ///     <see cref="CloseTabsTheSceneLeft" />.
    /// </remarks>
    internal void SaveSceneAs(string path) {
        if (EditedElsewhere(path, scene) is { } holder) {
            RefuseSecondDocument(path, holder, "Could not save the scene");

            return;
        }

        scenePath = path;
        scene.Writer = new SceneFileWriter(path);
        scene.SetTitle(Path.GetFileNameWithoutExtension(path));

        SaveScene();
        project.Assets.Scan();
        CloseTabsTheSceneLeft();
    }

    /// <summary>Writes every open document that has changes.</summary>
    void SaveAll() {
        try {
            // ⚠ Project settings are saved too now (#1310), so what the window's Apply does after a
            // write — push the content target, retitle, clear its own dirty mark — has to follow, or
            // the file says one thing and the running editor another until the next launch.
            // ⚠ The settings document's answer once the window has made one (#1310), and the store's
            // before: the window's own dirty flag follows the document by itself now, so there is
            // nothing here to clear.
            var settings = settingsDocument?.HasUnsavedChanges.Peek() ?? project.Settings.HasUnsavedChanges;
            var written = project.SaveAll();

            if (settings) {
                ApplyProjectSettings();
            }

            Shell.Notifications.Success($"{written} document(s) saved");
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not save everything", NotificationSeverity.Error, exception.Message);
        }
    }

    /// <summary>Renames the selected asset, in place in the browser's own tree.</summary>
    /// <remarks>
    ///     Through the tree's inline editor rather than a dialog, so the menu line, F2 and a
    ///     double-click on a folder all do the same thing — and so the commit goes through the one
    ///     handler that calls <c>AssetOperations.Rename</c>. It is the same arrangement the outliner
    ///     uses for an entity, for the same reason.
    /// </remarks>
    void RenameSelectedAsset() {
        if (project.Selection.Count > 0) {
            Browser?.BeginRename(project.Selection[0]);
        }
    }

    /// <summary>Recompiles every shader source, whether or not a file was seen to change.</summary>
    /// <remarks>
    ///     ⚠ <b>It reports the refusal rather than swallowing it.</b> The watcher's path writes a
    ///     line to the log, which is right for something nobody asked for; a person who pressed the
    ///     menu line is owed the compiler's answer where they are looking — and "reloaded" over a
    ///     shader that would not compile is the message that costs somebody an afternoon.
    /// </remarks>
    void ReloadShadersNow() {
        if (Effects is null) {
            return;
        }

        ReloadShaders([]);

        if (Effects.Refusal is { } refusal) {
            Shell.Notifications.Show("Shaders reloaded and refused", NotificationSeverity.Error, refusal);
            return;
        }

        Shell.Notifications.Success($"{Effects.SourceCount} shader source(s) reloaded");
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

        // ⚠ The external-tool setting doc 20's A7 names, now that there is a preferences window to
        // hold it. What is still honest is the limit: a stack frame carries a file and a line only
        // in a build with symbols beside it, so what this can offer the tool is the project root.
        // With no tool configured it reveals the folder, which is what it did before.
        if (preferences.ExternalEditor is { Length: > 0 } tool) {
            OpenInExternalEditor(tool, project.Paths.Root, line: 0);
            return;
        }

        links.Open(new Uri(project.Paths.Root).AbsoluteUri);
    }

    /// <summary>Runs the configured external tool over a file.</summary>
    /// <remarks>
    ///     ⚠ <b>Started detached and never waited on.</b> An editor that blocked its frame loop on
    ///     somebody's IDE launching would be one that appears to hang for the four seconds a cold
    ///     start takes — and the process is deliberately not tracked afterwards, because the user's
    ///     editor outliving this one is the normal case.
    /// </remarks>
    void OpenInExternalEditor(string tool, string file, int line) {
        var command = tool
            .Replace("{file}", file, StringComparison.Ordinal)
            .Replace("{line}", line.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        // The first token is the program and the rest are its arguments, which is the smallest rule
        // that handles `code -g {file}:{line}` and `rider --line {line} {file}` without a shell.
        var parts = command.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) {
            return;
        }

        try {
            using var process = new System.Diagnostics.Process();

            process.StartInfo.FileName = parts[0];
            process.StartInfo.Arguments = parts.Length > 1 ? parts[1] : string.Empty;
            process.StartInfo.UseShellExecute = false;

            process.Start();
        } catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) {
            Shell.Notifications.Show(
                "Could not run the external editor",
                NotificationSeverity.Warning,
                exception.Message + " — check Preferences ▸ General."
            );
        }
    }

    // ── Selection and hierarchy verbs ───────────────────────────────────────────────────────────

    // ⚠ The verbs that were here are `SceneEditing`'s now (#1290) — `ISceneEditing`, published to
    // plugins — and `SceneModule` registers the Entity menu's lines over them, Set Parent's chooser
    // (`SceneQuestions`) and the two marks included (#1296).

    // ── Play mode ───────────────────────────────────────────────────────────────────────────────

    /// <summary>What leaving play does to the rest of the editor, after the scene has been restored.</summary>
    void PlayLeft() {
        sceneEditing.Restructured();

        // The arrangement play maximised, and the console it cleared, are `PlayModule`'s (#1296), which
        // subscribes after this does — see `PlayMaximise`.
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
                    // ⚠ A cancelled prompt is a decision, not a deferred one. `RequestProject` sets
                    // a pending root before asking, and leaving it set would make the *next* close —
                    // the one where they meant to quit — silently reopen the editor over a project
                    // they backed out of choosing.
                    session.Decline();
                    break;
            }
        }
    }

    /// <summary>What has changes in it, named, for the prompt to show.</summary>
    string Unsaved() {
        var names = project.Documents
            .Where(document => document.HasUnsavedChanges.Value)
            .Select(document => document.Title.Peek())
            .ToList();

        // Named, because it is counted (#1310): a prompt asking about "unsaved changes" with no
        // document dirty would send somebody looking through every tab for an edit that is not in one.
        // ⚠ Once the window has made a settings document it is one of the documents above, under the
        // same name, so the store is asked only before that.
        if (settingsDocument is null && project.Settings.HasUnsavedChanges) {
            names.Add(EditorStrings.PanelProjectSettings.Text);
        }

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
