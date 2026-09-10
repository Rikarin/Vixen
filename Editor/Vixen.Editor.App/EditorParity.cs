// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Vixen.Core;
using Vixen.Core.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Ecs.Systems;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Platform;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
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

    /// <summary>Where the manual lives, until there is a documentation site to point at.</summary>
    const string DocumentationUrl = "https://github.com/Rikarin/Vixen/tree/master/docs";

    /// <summary>Everything Part C names that this application owns.</summary>
    void ParityCommands() {
        FileCommands();
        EditingCommands();
        AssetCommands();
        EntityCommands();
        SelectionAndTransformCommands();
        PlayCommands();
        BuildAndToolCommands();
        HelpCommands();
    }

    // ── File ────────────────────────────────────────────────────────────────────────────────────

    void FileCommands() {
        // ⚠ The two the shell's default menu has named since it was written, and doc 20 filed both
        // behind "swapping a project underneath a live editor". They do not swap one: the editor is
        // rebuilt over the new root by the host, which is the path every restart already takes. See
        // `RequestProject`.
        Verb(
            "file.new-project",
            EditorStrings.CommandNewProject,
            EditorStrings.CategoryFile,
            () => PickProjectDirectory("New Project", CreateProject),
            enabled: () => services.CanPick
        );

        Verb(
            "file.open-project",
            EditorStrings.CommandOpenProject,
            EditorStrings.CategoryFile,
            ShowProjectBrowser
        );

        Planned(
            "file.no-recent",
            EditorStrings.CommandFileNoRecent,
            EditorStrings.CategoryFile,
            "Nothing but this project has been opened yet."
        );

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

        Verb(
            "file.revert",
            EditorStrings.CommandFileRevert,
            EditorStrings.CategoryFile,
            Revert,
            enabled: () => project.ActiveDocument.Value is { CanReload: true } document
                && (document.IsDirty.Value || document.IsStale.Value)
        );

        Verb(
            "assets.import-files",
            EditorStrings.CommandAssetsImportFiles,
            EditorStrings.CategoryFile,
            ImportFiles,
            enabled: () => services.CanPick && !content.IsBusy
        );

        // ⚠ The walk this waited on is `assets.select-dependencies`, and it has been a real verb
        // since E5. What a package needs over that verb is the *closure* rather than one step of it:
        // Select Dependencies answers "what does this point at" for a person about to look at the
        // answer, and an archive that carried only that would unpack a material whose textures are
        // missing.
        Verb(
            "file.export-package",
            EditorStrings.CommandFileExportPackage,
            EditorStrings.CategoryFile,
            ExportPackage,
            enabled: () => services.CanPick && project.Selection.Count > 0
        );

        Verb(
            "file.project-settings",
            EditorStrings.CommandFileProjectSettings,
            EditorStrings.CategoryFile,
            () => Shell.Workspace.Open(ProjectSettingsPanel)
        );

        Shell.Keys.SetDefault("file.new-scene", new KeyChord(InputKey.N, ModifierKeys.Control));
        Shell.Keys.SetDefault("file.open-scene", new KeyChord(InputKey.O, ModifierKeys.Control));
        Shell.Keys.SetDefault("file.open-project", new KeyChord(InputKey.O, ModifierKeys.Control | ModifierKeys.Shift));
        Shell.Keys.SetDefault("file.save-as", new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift));
        Shell.Keys.SetDefault("file.save-all", new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Alt));

        RecentProjectCommands();
    }

    /// <summary>One command per project in the history, which is what Open Recent is made of.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Commands rather than paths, which <c>EditorShell.Recent</c> insists on and is
    ///         right to.</b> A dynamic menu is a set of ids because a line has to have a title, an
    ///         enablement and a place in the palette, and only a registered command has all three —
    ///         so "open the project I was in on Tuesday" is findable in the palette by typing its
    ///         name, which is the behaviour that makes the list worth keeping.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Registered once, because the list only changes when the editor reopens.</b>
    ///         Opening another project closes this editor and builds a new one — see
    ///         <see cref="RequestProject" /> — so there is no moment at which the history changes
    ///         under a live menu, and registering from inside the menu's own builder would be a
    ///         registration that rebuilds the menu it is being built for.
    ///     </para>
    ///     <para>
    ///         The project that is already open is left out: a line that reopens what you are
    ///         looking at is one people choose once.
    ///     </para>
    /// </remarks>
    void RecentProjectCommands() {
        List<string> ids = [];

        foreach (var entry in Recent.Entries) {
            if (string.Equals(entry.Path, project.Paths.Root, StringComparison.Ordinal)) {
                continue;
            }

            var path = entry.Path;

            // ⚠ Asked once. `Exists` is a stat call, and the list is where a path to an unmounted
            // share lives — the one place where asking four times is four chances to block.
            var exists = entry.Exists;

            Verb(
                RecentCommand(path),

                // ⚠ A null id, which `Strings.Get` answers with the source text. A directory the
                // user happens to have called "Prototype" is not a string a translator should ever
                // be shown, and giving it a catalogue id would put every project name they have
                // opened into the localisation vocabulary.
                new StringId(null!, entry.Name),
                EditorStrings.CategoryFile,
                () => RequestProject(path),

                // A project on a volume that is not mounted is greyed rather than absent, for
                // `ProjectHistory`'s reason: forgetting it is the one thing there is no way back
                // from.
                enabled: () => exists
            );

            ids.Add(RecentCommand(path));
        }

        // The submenu is a dynamic over ids, so the fallback line is what an empty list shows —
        // a submenu that opens onto nothing at all reads as a broken menu rather than an empty one.
        Shell.Recent = ids.Count == 0 ? () => ["file.no-recent"] : () => ids;
    }

    /// <summary>What the command that reopens a project is called.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the path rather than from the entry's position in the list.</b> A
    ///     positional id — <c>file.recent.0</c> — names a different project every time the order
    ///     changes, which is every time one is opened. A keybinding on it would be a shortcut that
    ///     silently moves to another project, and the keymap file would record an id whose meaning
    ///     changes between sessions.
    /// </remarks>
    static string RecentCommand(string path) {
        // ⚠ FNV-1a rather than `string.GetHashCode`, which is randomised per process — an id that
        // changed on every launch would defeat the whole point — and rather than a cryptographic
        // hash, which this is not: it names a menu line, and a collision costs one duplicate id that
        // the registry refuses out loud.
        var hash = 2166136261u;

        foreach (var character in path) {
            hash = (hash ^ character) * 16777619u;
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"file.recent.{hash:x8}");
    }

    // ── Edit ────────────────────────────────────────────────────────────────────────────────────

    void EditingCommands() {
        Verb(
            "edit.undo-history",
            EditorStrings.CommandEditUndoHistory,
            EditorStrings.CategoryEdit,
            () => Shell.Workspace.Open(HistoryPanel)
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

        // ⚠ This block said "Cut, Copy, Paste and Duplicate need a subtree *clone*, which the engine
        // does not have", and `SceneClone`'s own remarks call themselves "the thing doc 20's
        // clipboard was blocked on". Duplicate is that clone with nothing in between, so it is a verb
        // now. The other three still are not, and the reason is no longer the copy: a clipboard is a
        // *buffer that outlives the selection* — what was cut has to survive being deleted, and a
        // paste has to land somewhere the copy did not come from.
        Verb(
            "edit.duplicate",
            EditorStrings.CommandEditDuplicate,
            EditorStrings.CategoryEdit,
            DuplicateSelection,
            enabled: () => scene.Selection.Count > 0
        );

        foreach (var (id, title, key) in Clipboard()) {
            if (Shell.Commands[id] is null) {
                Planned(
                    id,
                    new StringId("editor.command." + id, title),
                    EditorStrings.CategoryEdit,
                    "A clipboard is a buffer that outlives the selection it was filled from; the editor "
                    + "has the subtree copy (SceneClone) and nowhere to keep one."
                );
            }

            if (key.IsBound) {
                Shell.Keys.SetDefault(id, key);
            }
        }

        Verb(
            "edit.select-all",
            EditorStrings.CommandEditSelectAll,
            EditorStrings.CategoryEdit,
            () => scene.Selection.Set([.. scene.Entities])
        );

        Verb(
            "edit.deselect-all",
            EditorStrings.CommandEditDeselectAll,
            EditorStrings.CategoryEdit,
            DeselectEntities,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "edit.invert-selection",
            EditorStrings.CommandEditInvertSelection,
            EditorStrings.CategoryEdit,
            InvertSelection
        );

        Verb(
            "edit.select-children",
            EditorStrings.CommandEditSelectChildren,
            EditorStrings.CategoryEdit,
            SelectChildren,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "edit.select-parent",
            EditorStrings.CommandEditSelectParent,
            EditorStrings.CategoryEdit,
            SelectParent,
            enabled: () => scene.Selection.Count > 0
        );

        Shell.Keys.SetDefault("edit.select-all", new KeyChord(InputKey.A, ModifierKeys.Control));

        Shell.Keys.SetDefault(
            "edit.deselect-all",
            new KeyChord(InputKey.A, ModifierKeys.Control | ModifierKeys.Shift)
        );

        // ⚠ `edit.search-everywhere` is registered by the shell rather than here, because the
        // overlay is the shell's. What this application adds is the sources — see `SearchSources` —
        // and the command greys itself out where nothing has added any.
        Shell.Search.AddSource(new AssetSearchSource(project, RevealAsset));
        Shell.Search.AddSource(new EntitySearchSource(() => inspected ?? scene, RevealEntity));

        Shell.Search.AddSource(
            new CommandPaletteSource(Shell.Commands, Shell.Keys) {
                // ⚠ Its own category so the block reads "Command" rather than being scattered across
                // File, Edit and Scene. In the palette the command's own category is the useful
                // answer; in a search across four kinds of thing, which *kind* it is comes first.
                Uniform = true
            }
        );

        Verb(
            "edit.find-references",
            EditorStrings.CommandEditFindReferences,
            EditorStrings.CategoryEdit,
            FindReferences,
            enabled: () => project.Selection.Count > 0
        );

        Verb(
            "edit.preferences",
            EditorStrings.CommandPreferences,
            EditorStrings.CategoryEdit,
            () => Shell.Workspace.Open(PreferencesPanel)
        );

        Verb(
            "edit.keybindings",
            EditorStrings.CommandEditKeybindings,
            EditorStrings.CategoryEdit,
            () => Shell.Workspace.Open(EditorShell.KeyBindingsPanel)
        );

        Shell.Keys.SetDefault("edit.preferences", new KeyChord(InputKey.Comma, ModifierKeys.Control));
    }

    // ── Assets ──────────────────────────────────────────────────────────────────────────────────

    void AssetCommands() {
        // ⚠ Not a template gallery, and it never needed to be. Eighteen `assets.create-*` verbs
        // exist and each writes a sensible empty file of its kind — see `BuiltInAssetKinds` — so the
        // general line's whole job is asking *which one*, over the same registry the Create submenu
        // is built from. A kind a plugin contributed is in the dialog for free, which is the half a
        // literal list of templates would have got wrong.
        Verb(
            "assets.create",
            EditorStrings.CommandAssetsCreate,
            EditorStrings.CategoryAssets,
            ChooseAssetKind
        );

        Verb(
            "assets.open",
            EditorStrings.CommandAssetsOpen,
            EditorStrings.CategoryAssets,
            OpenSelectedAsset,
            enabled: () => project.Selection.Count > 0
        );

        // ⚠ Doc 48 § D12's last line, and the reason this verb exists rather than a checkbox on the
        // model importer. A mesh map has to land in `Assets/` as a file an artist can open — § D12
        // says so in one sentence: they want to look at the curvature map when a generator
        // misbehaves. An importer cannot write one there. It writes artefacts into `Library/` under
        // a cache key, and a file it dropped into `Assets/` would be a file the next scan imports,
        // that the import it came out of never declared it read, and that no cache key can see — a
        // hidden cache with a re-entrancy bug on top. Baking is therefore a verb over a selected
        // model, taken through the same scan-then-read-back-the-GUID sequence as every other thing
        // the editor puts in a project.
        //
        // ⚠ And it opens the panel rather than baking, which § D12's last bullet is what changed.
        // A verb that baked at a constant 1024 with every map on was a bake nobody chose — the
        // constant's own comment said "until § D12's bake panel exists" — and a second verb beside
        // it that opened the panel would be two answers to what Bake means, which is doc 20's A4
        // complaint. One settings object, one place to press it. `MeshMapBakeView` is the panel and
        // `BakeSelectedMeshMaps` is still the thing its button calls.
        Verb(
            "assets.bake-mesh-maps",
            EditorStrings.CommandAssetsBakeMeshMaps,
            EditorStrings.CategoryAssets,
            () => Shell.Workspace.Open(MeshMapBakePanel)
        );

        Verb(
            "assets.show-in-explorer",
            EditorStrings.CommandAssetsShowInExplorer,
            EditorStrings.CategoryAssets,
            ShowSelectedAsset,
            enabled: () => project.Selection.Count > 0 && services.OpenUrl is not null
        );

        // ⚠ Doc 20 calls a naive rename "the fastest way to corrupt a project", and it is worth
        // being precise about which naivety. Not a stale path: doc 08 chose a GUID in a prefixed
        // scalar over a path, so a referrer needs nothing done to it. The corruption is leaving the
        // sidecar behind — the next scan then finds an asset with no identity, mints a new one, and
        // every reference in the project dangles with nothing having reported an error.
        // `AssetOperations` is that invariant and nothing else, and it is tested against it.
        Verb(
            "assets.rename",
            EditorStrings.CommandAssetsRename,
            EditorStrings.CategoryAssets,
            RenameSelectedAsset,
            enabled: () => browser is not null && project.Selection.Count == 1
        );

        Verb(
            "assets.delete",
            EditorStrings.CommandAssetsDelete,
            EditorStrings.CategoryAssets,
            DeleteSelectedAssets,
            enabled: () => project.Selection.Count > 0
        );

        Verb(
            "assets.new-folder",
            EditorStrings.CommandAssetsNewFolder,
            EditorStrings.CategoryAssets,
            NewAssetFolder,
            enabled: () => browser is not null
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
            enabled: () => browser is not null && project.Selection.Count > 0
        );

        // ⚠ Doc 20 § B7's second verb, and it is enabled by a *provider* rather than by a
        // preference: an editor opened on a project that is not in a working tree greys this with
        // the reason, which is the shape every other unimplementable line in this file takes. The
        // enablement asks `SourceControl.IsKnown` rather than "is there a provider", because the
        // detection happens on the pool during the first sweep — a line that lit up only after the
        // answer arrived is honest, and one that lit up before it would offer a verb with nothing
        // behind it.
        Verb(
            "assets.revert",
            EditorStrings.CommandAssetsRevert,
            EditorStrings.CategoryAssets,
            RevertToSourceControl,
            enabled: () => SourceControl.IsKnown && project.Selection.Count > 0
        );

        // ⚠ Doc 20 § B7's third and fourth verbs, which arrive as one line because they are one
        // panel. A history row is only worth clicking for what it shows and what it can put back, so
        // "Diff" is not a command of its own — it is what the panel draws under whichever row is
        // picked. One asset, because the panel has a Restore button on it and a panel that showed
        // whichever file happened to be first in a selection of forty would be right by accident.
        Verb(
            "assets.history",
            EditorStrings.CommandAssetsHistory,
            EditorStrings.CategoryAssets,
            ShowRevisionsPanel,
            enabled: () => SourceControl.IsKnown && project.Selection.Count == 1
        );

        Planned(
            "assets.reimport",
            EditorStrings.CommandAssetsReimport,
            EditorStrings.CategoryAssets,
            "Per-asset reimport needs the importer registry to outlive a run. Reimport All works today."
        );

        Verb(
            "assets.reimport-all",
            EditorStrings.CommandAssetsReimportAll,
            EditorStrings.CategoryAssets,
            content.Import,
            enabled: () => !content.IsBusy
        );

        // ⚠ Doc 20's A8: "Find References is the same query and belongs in three places at once."
        // Two of the three are these — the browser's context menu and the Assets menu — and they are
        // the *same command* rather than two, which is what stops them disagreeing. The third,
        // an asset field's own menu, is the inspector's and is not built.
        Verb(
            "assets.find-references",
            EditorStrings.CommandAssetsFindReferences,
            EditorStrings.CategoryAssets,
            FindReferences,
            enabled: () => project.Selection.Count > 0
        );

        Verb(
            "assets.select-dependencies",
            EditorStrings.CommandAssetsSelectDependencies,
            EditorStrings.CategoryAssets,
            SelectDependencies,
            enabled: () => project.Selection.Count > 0
        );
    }

    /// <summary>Selects everything that points at what is selected, and says how much.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The answer is a selection rather than a list, and that is the useful shape.</b>
    ///         <c>ReferenceIndex</c> answers "who points at this" in one lookup; what somebody does
    ///         with the answer is open one of them, delete the lot, or look at what they have in
    ///         common — all three of which are things the browser already does to a selection.
    ///         A read-only list would be a fourth panel that can only be read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A selection of nothing is left alone and reported.</b> Replacing the selection
    ///         with an empty one would look exactly like the command having failed, and "nothing
    ///         references this" is the answer people are usually checking for.
    ///     </para>
    /// </remarks>
    void FindReferences() =>
        Gather(project.References.ReferrersOf, "Nothing references that.", "referrer");

    /// <summary>Selects everything the selection points at.</summary>
    void SelectDependencies() =>
        Gather(
            asset => project.References.ReferencesFrom(asset).Select(reference => reference.Asset),
            "That does not reference anything.",
            "dependency"
        );

    /// <summary>Walks one edge of the reference graph from the selection, and shows what it found.</summary>
    /// <param name="edge">Which way to walk: what points at an asset, or what it points at.</param>
    /// <param name="empty">What to say when the answer is nothing.</param>
    /// <param name="noun">What one result is called.</param>
    /// <remarks>
    ///     ⚠ <b>A set for membership and a list for order.</b> The two verbs differ only in which
    ///     direction they walk, and both can reach the same asset from several selected ones — a
    ///     linear <c>Contains</c> per hit is quadratic in the answer, which a select-all over a
    ///     heavily cross-referenced project is exactly the shape of.
    /// </remarks>
    void Gather(Func<AssetId, IEnumerable<AssetId>> edge, string empty, string noun) {
        List<AssetId> found = [];
        HashSet<AssetId> seen = [];

        foreach (var asset in project.Selection) {
            foreach (var reached in edge(asset)) {
                if (seen.Add(reached)) {
                    found.Add(reached);
                }
            }
        }

        if (found.Count == 0) {
            Shell.Notifications.Show(empty, NotificationSeverity.Info);
            return;
        }

        Select(found);
        Shell.Notifications.Success(found.Count == 1 ? $"1 {noun}" : $"{found.Count} {noun}s");
    }

    /// <summary>Selects some assets and puts the editor in the browser's context.</summary>
    void Select(IReadOnlyList<AssetId> assets) {
        project.Selection.Set(assets);
        Shell.Context = AssetContext;
    }

    /// <summary>Asks which of the project's folders to move the selection into.</summary>
    /// <remarks>
    ///     ⚠ <b>Every folder in the project, flat, by path.</b> A tree would be prettier and would
    ///     need the browser's own tree in a dialog; a sorted list of relative paths is searchable by
    ///     eye, is what the operation actually takes, and cannot express a destination outside the
    ///     project — which is the one answer <c>AssetOperations.Move</c> refuses.
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
            var destination = await ChooseAsync(
                assets.Count == 1 ? "Move to which folder?" : $"Move {assets.Count} assets to which folder?",
                folders,
                folder => folder
            ).ConfigureAwait(true);

            if (destination is not { Length: > 0 } folder) {
                return;
            }

            var failures = 0;

            foreach (var asset in assets) {
                var result = AssetOperations.Move(project, asset, folder);

                if (!result.Ok) {
                    failures++;
                    Shell.Notifications.Show("Could not move", NotificationSeverity.Error, result.Message);
                }
            }

            browser?.Rescan();

            if (failures == 0) {
                Shell.Notifications.Success($"Moved to {folder}");
            }
        }
    }

    // ── Entity ──────────────────────────────────────────────────────────────────────────────────

    void EntityCommands() {
        Verb(
            "entity.create-child",
            EditorStrings.CommandEntityCreateChild,
            EditorStrings.CategoryEntity,
            CreateChild,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "entity.group",
            EditorStrings.CommandEntityGroup,
            EditorStrings.CategoryEntity,
            Group,
            enabled: () => scene.Selection.Count > 0
        );

        // ⚠ Two or more, unlike `entity.group` above, and the difference is what the verb means. A
        // group of one is a perfectly ordinary thing to want; a LOD chain of one is a group whose
        // only level is always the one drawn, which is what an author gets by doing nothing at all.
        Verb(
            "entity.group-lod",
            EditorStrings.CommandEntityGroupLod,
            EditorStrings.CategoryEntity,
            GroupAsLod,
            enabled: () => scene.Selection.Count > 1
        );

        Verb(
            "entity.clear-parent",
            EditorStrings.CommandEntityClearParent,
            EditorStrings.CategoryEntity,
            ClearParent,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "entity.align-with-view",
            EditorStrings.CommandEntityAlignWithView,
            EditorStrings.CategoryEntity,
            AlignWithView,
            enabled: () => Viewport is not null && scene.Selection.Count > 0
        );

        Shell.Keys.SetDefault("entity.create-child", new KeyChord(InputKey.N, ModifierKeys.Alt | ModifierKeys.Shift));

        Shell.Keys.SetDefault(
            "scene.create-entity",
            new KeyChord(InputKey.N, ModifierKeys.Control | ModifierKeys.Shift)
        );

        Shell.Keys.SetDefault("entity.group", new KeyChord(InputKey.G, ModifierKeys.Control));

        Planned(
            "entity.create-audio",
            EditorStrings.CommandEntityCreateAudio,
            EditorStrings.CategoryEntity,
            "There is no audio-source component yet; a line that made an empty called Audio would lie."
        );

        Planned(
            "entity.create-ui",
            EditorStrings.CommandEntityCreateUi,
            EditorStrings.CategoryEntity,
            "Vixen.Ui is a document tree with no world-space bridge yet."
        );

        Planned(
            "entity.create-vfx",
            EditorStrings.CommandEntityCreateVfx,
            EditorStrings.CategoryEntity,
            "The graph is authorable now, but the runtime has no VFX emitter component for an entity "
            + "to carry — an entity called VFX would reference nothing."
        );

        Planned(
            "entity.make-prefab",
            EditorStrings.CommandEntityMakePrefab,
            EditorStrings.CategoryEntity,
            "Prefab instance links are not written to a scene yet, so an instance would be an ordinary subtree."
        );

        Planned(
            "entity.unpack-prefab",
            EditorStrings.CommandEntityUnpackPrefab,
            EditorStrings.CategoryEntity,
            "Prefab instance links are not written to a scene yet."
        );

        Planned(
            "entity.apply-overrides",
            EditorStrings.CommandEntityApplyOverrides,
            EditorStrings.CategoryEntity,
            "Per-override apply and revert live in the inspector; the scene-wide verb needs instance links."
        );

        Planned(
            "entity.ungroup",
            EditorStrings.CommandEntityUngroup,
            EditorStrings.CategoryEntity,
            "Ungrouping has to reparent children and delete the group in one undoable step."
        );

        // ⚠ The picker this waited for was never the outliner's drag. `ChooseAsync` is the editor's
        // one drawn "pick one of these", the drag has been wired for a milestone anyway
        // (`hierarchy.AllowDrag`), and `ReparentCommand` is the undoable move both gestures make —
        // so what was left was a list of candidate parents and one call.
        Verb(
            "entity.set-parent",
            EditorStrings.CommandEntitySetParent,
            EditorStrings.CategoryEntity,
            SetParent,
            enabled: () => scene.Selection.Count > 0
        );

        Verb(
            "entity.move-to-view",
            EditorStrings.CommandEntityMoveToView,
            EditorStrings.CategoryEntity,
            MoveToView,
            enabled: () => Viewport is not null && scene.Selection.Count > 0
        );

        // ⚠ Not blocked on the picking readback after all. What Snap To Floor needs is "what does
        // this ray hit", and `SceneProbe` answers it exactly against the same geometry the viewport
        // draws — which is what makes it the right answer today and the wrong one the day a shader
        // moves a vertex. See `SceneProbe`'s own remarks.
        Verb(
            "entity.snap-to-floor",
            EditorStrings.CommandEntitySnapToFloor,
            EditorStrings.CategoryEntity,
            SnapToFloor,
            enabled: () => scene.Selection.Count > 0
        );

        Shell.Keys.SetDefault("entity.snap-to-floor", new KeyChord(InputKey.End, ModifierKeys.None));

        Planned(
            "entity.toggle-active",
            EditorStrings.CommandEntityToggleActive,
            EditorStrings.CategoryEntity,
            "There is no enabled flag on an entity yet."
        );

        // ⚠ Editor state and not scene state, which is the line both Unreal and Unity draw: hiding
        // something to work on what is behind it must not change what ships. So these write
        // `SceneDocument`'s own sets rather than a component, they are not saved, and they are not
        // undoable — an undo that put an eye back would be one step of history spent on where the
        // user was looking rather than on what they changed, which is `Selection`'s argument.
        Verb(
            "entity.toggle-hidden",
            EditorStrings.CommandEntityToggleHidden,
            EditorStrings.CategoryEntity,
            () => Mark(scene.IsHiddenDirectly, scene.SetHidden),
            enabled: () => scene.Selection.Count > 0,
            on: () => scene.Selection.Count > 0 && scene.IsHiddenDirectly(scene.Selection[0])
        );

        Verb(
            "entity.toggle-lock",
            EditorStrings.CommandEntityToggleLock,
            EditorStrings.CategoryEntity,
            () => Mark(scene.IsLockedDirectly, scene.SetLocked),
            enabled: () => scene.Selection.Count > 0,
            on: () => scene.Selection.Count > 0 && scene.IsLockedDirectly(scene.Selection[0])
        );

        Shell.Keys.SetDefault("entity.toggle-hidden", new KeyChord(InputKey.H, ModifierKeys.None));
        Shell.Keys.SetDefault("entity.toggle-lock", new KeyChord(InputKey.L, ModifierKeys.None));
    }

    /// <summary>Flips a mark across the selection, taking the first entity's state as the answer.</summary>
    /// <remarks>
    ///     ⚠ <b>All of them get what the <i>first</i> one is not, rather than each being flipped.</b>
    ///     Toggling a mixed selection per entity swaps which half is hidden, which is the one outcome
    ///     nobody ever means — and it makes pressing the key twice a no-op that looks like the key
    ///     not working.
    /// </remarks>
    void Mark(Func<Entity, bool> read, Action<Entity, bool> write) {
        if (scene.Selection.Count == 0) {
            return;
        }

        var wanted = !read(scene.Selection[0]);

        foreach (var entity in scene.Selection) {
            write(entity, wanted);
        }

        RefreshMarks();
    }

    // ── Play ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The four transport verbs, over the controller that already exists.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This used to read "entering play mode snapshots the world and leaving it restores
    ///         the snapshot, and that is <i>all</i> it does today" — and it was accurate.</b> Nothing
    ///         moved while it was playing, because <c>ShouldTick</c> had no caller outside its own
    ///         tests. Since 2026-08-21 <c>PlayModeController.Tick</c> steps an <c>EngineLoop</c> and
    ///         <c>EditorApplication.Update</c> calls it. What is still real from before is the part
    ///         doc 20 calls out as better than Unity's: the restore is honest, it says so before
    ///         entering, and a selection made in play mode is translated back through
    ///         <c>WorldSnapshot.Restore</c>'s handle map rather than being lost.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And <see cref="ReportPlayGaps" /> is not decoration on that.</b> The graph is
    ///         the engine's default set and a game's own is not declarable anywhere, so a session
    ///         that said nothing would make the systems it is not running read as the user's bug.
    ///     </para>
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
            EditorStrings.CommandPlayModeInEditor,
            EditorStrings.CategoryPlay,
            "Choosing a play topology needs the standalone and server paths hosted from the editor."
        );

        // ⚠ Both of these named milestone E6, and E6 has shipped — `build.settings` is a window and
        // `build.run` publishes and starts a player. What is actually missing is on this side of the
        // line and is the same thing twice: a *supervised* process. Play is a transport — Pause, Step
        // and Stop have to mean something — and `StartPlayerBuild` hands the artefact to the OS and
        // forgets it, so a play mode over it would be a Play button with three dead buttons beside
        // it. A reason naming a mechanism can be checked against the tree; one naming a schedule
        // stayed on screen for two milestones after its schedule closed.
        Planned(
            "play.mode-standalone",
            EditorStrings.CommandPlayModeStandalone,
            EditorStrings.CategoryPlay,
            "Build and Run starts a player and does not keep it, so there is no process for Pause and "
            + "Stop to reach. Playing standalone needs a supervised child process."
        );

        Planned(
            "play.mode-server",
            EditorStrings.CommandPlayModeServer,
            EditorStrings.CategoryPlay,
            "Nothing constructs a PlayerSessions, so there is no host for the editor to start — the "
            + "type carries the topology and no code makes one."
        );

        // ⚠ A preference rather than an action, which is what the tick says. It changes what the
        // *next* Play does; pressing it while the game is running would be a second, differently
        // spelled maximise, and the one on the Scene menu is that.
        Verb(
            "play.maximise",
            EditorStrings.CommandPlayMaximise,
            EditorStrings.CategoryPlay,
            () => maximiseOnPlay = !maximiseOnPlay,
            on: () => maximiseOnPlay
        );

        Planned(
            "play.mute-audio",
            EditorStrings.CommandPlayMuteAudio,
            EditorStrings.CategoryPlay,
            "The editor does not drive the audio engine yet."
        );

        // ⚠ A tick over the console's own preference rather than a second copy of it. Doc 20's rule
        // for the three navigation preferences applies here for the same reason: two writers to one
        // setting is how a menu tick and a panel's toggle come to disagree.
        Verb(
            "play.clear-console",
            EditorStrings.CommandPlayClearConsole,
            EditorStrings.CategoryPlay,
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
            EditorStrings.CommandViewClearConsole,
            EditorStrings.CategoryPlay,
            () => console?.Clear(),
            enabled: () => console is not null
        );
    }

    // ── Build and Tools ─────────────────────────────────────────────────────────────────────────

    void BuildAndToolCommands() {
        // Build Settings, Build and Run, and the two radio submenus. `EditorBuilds` owns them
        // because they are one setting's worth of state and a build's worth of orchestration, and
        // this file's job is the bar rather than what is behind it.
        PlayerBuildCommands();

        // Deploy is `DiagnosticsCommands`', because it opens the device manager E4 built. It is
        // still a Build-menu line — Part C puts it there — and the window is the Tools one.

        Verb(
            "build.clean-library",
            EditorStrings.CommandBuildCleanLibrary,
            EditorStrings.CategoryBuild,
            CleanLibrary,
            enabled: () => !content.IsBusy
        );

        // ⚠ Still declared-and-disabled, and the reason has moved rather than gone away. The
        // ahead-of-time shader bundle is `ShaderBuildRunner`'s, which links Raven's compiler — a
        // build-time library the editor deliberately does not carry, for the reason
        // Tools/Vixen.ShaderCompiler's README gives. So a player built from here has no bundle, the
        // build log says so for a project that has a manifest, and `vixen build` is what compiles
        // one. What would close this is a compiler service the editor talks to rather than links.
        Planned(
            "build.rebuild-shaders",
            EditorStrings.CommandBuildRebuildShaders,
            EditorStrings.CategoryBuild,
            "The shader bundle is compiled by `vixen build`, which links a compiler the editor does not."
        );

        // ⚠ On the Window menu, which the shell owns and which names it — so the shell would have a
        // dangling id if this were left out. It is registered here rather than there because full
        // screen is a property of an OS window and `EditorShell` is deliberately a document with no
        // window: what is missing is a way for the application to reach one, not the verb itself.
        Planned(
            "view.full-screen",
            EditorStrings.CommandViewFullScreen,
            EditorStrings.CategoryView,
            "The application has no handle on its window yet; the host owns it."
        );

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

        Verb(
            "tools.reload-styles",
            EditorStrings.CommandToolsReloadStyles,
            EditorStrings.CategoryTools,
            ReloadStyles
        );

        // The report is `DiagnosticsCommands`' too, and it is the one line here that E4 only half
        // finished: it carries the log, the memory arenas, the scene's counts and the last capture,
        // and says in the file that the minidump and the undo history are E6's.
    }

    /// <summary>The four clipboard verbs and the keys everybody expects them on.</summary>
    static (string Id, string Title, KeyChord Key)[] Clipboard() => [
        ("edit.cut", "Cut", new KeyChord(InputKey.X, ModifierKeys.Control)),
        ("edit.copy", "Copy", new KeyChord(InputKey.C, ModifierKeys.Control)),
        ("edit.paste", "Paste", new KeyChord(InputKey.V, ModifierKeys.Control)),
        ("edit.paste-as-child", "Paste As Child", KeyChord.None),
        ("edit.duplicate", "Duplicate", new KeyChord(InputKey.D, ModifierKeys.Control))
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
            EditorStrings.CommandHelpApiReference,
            EditorStrings.CategoryHelp,
            () => Browse(DocumentationUrl + "/README.md"),
            enabled: () => services.OpenUrl is not null
        );

        Verb(
            "help.release-notes",
            EditorStrings.CommandHelpReleaseNotes,
            EditorStrings.CategoryHelp,
            () => Browse(DocumentationUrl + "/14-roadmap.md"),
            enabled: () => services.OpenUrl is not null
        );

        Verb(
            "help.report-bug",
            EditorStrings.CommandHelpReportBug,
            EditorStrings.CategoryHelp,
            () => Browse("https://github.com/Rikarin/Vixen/issues/new"),
            enabled: () => services.OpenUrl is not null
        );

        Verb(
            "help.show-log-folder",
            EditorStrings.CommandHelpShowLogFolder,
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

        // Consecutive, with no gap left for Scene: `SceneMenu` inserts it between Entity and Play
        // afterwards, which shifts these three along by one. Leaving a hole here instead would mean
        // this method knowing what the next one is going to do.
        var play = Shell.Menus.InsertMenu(++after, EditorStrings.MenuPlay);

        play.Add("play.play", "play.pause", "play.step", "play.stop").AddSeparator();

        play.AddSubmenu(EditorStrings.MenuPlayMode)
            .Add("play.mode-in-editor", "play.mode-standalone", "play.mode-server");

        play.AddSubmenu(EditorStrings.MenuPlayOptions)
            .Add("play.maximise", "play.mute-audio", "play.clear-console");

        // ⚠ Counted from Window rather than carried on from Play, because Part C puts Window
        // between them: File, Edit, Assets, Entity, Scene, Play, Window, Build, Tools, Help. The
        // shell owns Window, so the only way to land after it is to find it.
        after = Index(EditorStrings.MenuWindow);

        var build = Shell.Menus.InsertMenu(++after, EditorStrings.MenuBuild);

        build.Add("build.settings").AddSeparator().Add("assets.build", "build.run").AddSeparator();
        build.AddSubmenu(EditorStrings.MenuBuildTarget).Add(BuildIds.Targets);

        // ⚠ Four lines where Part C names two, and the label is Part C's word for them. Doc 17's
        // variants are the axis a player build actually has — Development is an optimised build that
        // keeps its profiler, and Server is a Release one with no window — and the compiler
        // configuration is derived from the variant rather than chosen beside it. A menu of Debug
        // and Release over a setting of four would leave two of them unreachable and unmarkable.
        build.AddSubmenu(EditorStrings.MenuBuildConfiguration).Add(BuildIds.Variants);

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
            .Add("tools.diagnostics-report");
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
                Category = EditorStrings.CategoryPlay,
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

        // ⚠ Into the folder the browser is showing rather than into `Assets/`, which is what this
        // did and is the smaller half of doc 20 § B3's "choose a destination". A person who has
        // navigated to `Textures/` and asked to import has already said where; putting the files at
        // the root anyway is a move they then have to undo by hand, and the affordance that told
        // them nothing was the one they used.
        var folder = browser?.Folder ?? AssetTree.RootName;

        deferred.When(
            dialogs.OpenFilesAsync(new FileDialogOptions { Title = "Import Assets", AllowsMultipleSelection = true }),
            paths => BringIn(paths, folder),
            failure => Shell.Notifications.Show("Could not import", NotificationSeverity.Error, failure.Message)
        );
    }

    /// <summary>Copies what was dragged in from the OS into the folder it was dropped on.</summary>
    /// <remarks>
    ///     ⚠ <b>The first consumer of a <c>DropEvent</c> in the editor, and the gesture doc 20 § Part
    ///     D lists as the last ⛔ under Content.</b> The wire and the model both landed under
    ///     <see href="https://github.com/Rikarin/Vixen/issues/654">#654</see> and nothing in this
    ///     application had ever listened, so a folder of textures dragged out of Finder was routed,
    ///     hit-tested and bubbled to a panel with no handler — which looks exactly like a platform
    ///     that cannot do it.
    /// </remarks>
    void ImportDropped(IReadOnlyList<string> paths, string folder) => BringIn(paths, folder);

    /// <summary>Copies files and folders into the project, then scans and imports what arrived.</summary>
    /// <param name="paths">Absolute paths on the user's disk.</param>
    /// <param name="folder">The project-relative folder to land them in.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Copied rather than referenced.</b> An asset outside the project tree is one the
    ///         content build cannot find, the reference index cannot name and a colleague does not
    ///         have — every engine that allowed it spent years telling people why their build was
    ///         broken.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A directory is copied whole, and until it was, the commonest drag there is did
    ///         nothing.</b> "Drag a folder of textures in from Finder" is doc 20's own wording for
    ///         this row, and <c>File.Copy</c> over a directory throws — so the dialog half reported
    ///         "could not copy the files" for the one input every user tries first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A path already inside the project is refused rather than duplicated.</b> The
    ///         browser is a file view of <c>Assets/</c>, and a person dragging a row of it onto
    ///         itself means a move — which <see cref="MoveAssets" /> does with identity intact.
    ///         Copying would mint a second GUID for the same bytes, which is the state the reference
    ///         index cannot repair.
    ///     </para>
    /// </remarks>
    void BringIn(IReadOnlyList<string> paths, string folder) {
        if (paths.Count == 0) {
            return;
        }

        var destination = project.Paths.Absolute(string.IsNullOrEmpty(folder) ? AssetTree.RootName : folder);
        var copied = 0;

        try {
            Directory.CreateDirectory(destination);

            foreach (var path in paths) {
                copied += Bring(path, destination) ? 1 : 0;
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not copy the files", NotificationSeverity.Error, exception.Message);
            return;
        }

        if (copied == 0) {
            return;
        }

        project.Assets.Scan();
        browser?.Rescan();
        content.Import();

        Shell.Notifications.Success(
            copied == 1 ? $"1 asset imported into {folder}" : $"{copied} assets imported into {folder}"
        );
    }

    /// <summary>Copies one file or one directory in, and says why not if it will not.</summary>
    /// <param name="path">What was chosen or dropped.</param>
    /// <param name="destination">The absolute folder it goes into.</param>
    /// <returns>Whether anything was copied.</returns>
    bool Bring(string path, string destination) {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrEmpty(name)) {
            return false;
        }

        var target = Path.Combine(destination, name);

        if (Inside(path, project.Paths.Root)) {
            Shell.Notifications.Show(
                name + " is already in this project",
                NotificationSeverity.Warning,
                "Drag it onto a folder in the browser to move it, which keeps its references."
            );

            return false;
        }

        // ⚠ Never over something already there. Two textures called `wood.png` from two folders is
        // the ordinary case, and silently replacing the one already in the project is a change
        // nothing records and undo cannot reach.
        if (File.Exists(target) || Directory.Exists(target)) {
            Shell.Notifications.Show(
                name + " is already in the project",
                NotificationSeverity.Warning,
                "Rename it, or import it into a folder of its own."
            );

            return false;
        }

        if (Directory.Exists(path)) {
            CopyTree(path, target);
            return true;
        }

        if (!File.Exists(path)) {
            return false;
        }

        File.Copy(path, target);
        return true;
    }

    /// <summary>Copies a directory and everything under it.</summary>
    /// <param name="from">The source directory.</param>
    /// <param name="to">Where it goes, which does not exist yet.</param>
    static void CopyTree(string from, string to) {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from)) {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(from)) {
            CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }

    /// <summary>Whether a path is the given directory or lives under it.</summary>
    /// <param name="path">The path in question.</param>
    /// <param name="directory">The directory it might be under.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>
    ///     ⚠ <b>Compared with a separator on the end, so <c>ProjectTwo</c> is not "inside"
    ///     <c>Project</c>.</b> A prefix test without one is the check that refuses a legitimate
    ///     import from the folder next door and gives a reason that is not true.
    /// </remarks>
    static bool Inside(string path, string directory) {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return full.StartsWith(root, StringComparison.Ordinal)
            || string.Equals(full + Path.DirectorySeparatorChar, root, StringComparison.Ordinal);
    }

    /// <summary>Asks which kind of asset to make, and runs that kind's own verb.</summary>
    /// <remarks>
    ///     ⚠ <b>It runs the command rather than calling <c>CreateAsset</c> itself.</b> Each kind's
    ///     verb is what the Create submenu, the palette and a key binding all reach, and a second
    ///     path into the same file would be a second place the folder, the naming and the "does it
    ///     open" answer are decided — which is how two ways of making an animation graph come to
    ///     produce two different files.
    /// </remarks>
    void ChooseAssetKind() {
        var kinds = AssetKinds;

        if (kinds.Count == 0) {
            return;
        }

        _ = Ask();

        async Task Ask() {
            var chosen = await ChooseAsync(
                "What kind of asset?",
                kinds,
                static kind => kind.Title,
                static kind => kind.Extension
            ).ConfigureAwait(true);

            if (chosen is not null) {
                Shell.Commands.Execute(chosen.Id);
            }
        }
    }

    /// <summary>What a package this editor writes is called.</summary>
    internal const string PackageExtension = ".vxpackage";

    /// <summary>Everything the selection reaches through the reference graph, itself included.</summary>
    /// <param name="roots">Where to start.</param>
    /// <returns>The closure, roots first and each asset once.</returns>
    /// <remarks>
    ///     ⚠ <b>A closure and not one step, and the difference is what a package is for.</b>
    ///     <see cref="SelectDependencies" /> walks one edge because a person is about to look at the
    ///     answer; an archive has to carry the material, the textures it names, and whatever those
    ///     name in turn, or it unpacks into a project with holes in it.
    ///     <para>
    ///         ⚠ <b>A reference cycle is ordinary here rather than exceptional.</b> Two prefabs that
    ///         name each other are a project people actually have, so the walk is over a visited set
    ///         and not a recursion with a depth limit.
    ///     </para>
    /// </remarks>
    internal IReadOnlyList<AssetId> Closure(IEnumerable<AssetId> roots) {
        ArgumentNullException.ThrowIfNull(roots);

        List<AssetId> found = [];
        HashSet<AssetId> seen = [];
        Queue<AssetId> pending = [];

        foreach (var root in roots) {
            if (seen.Add(root)) {
                found.Add(root);
                pending.Enqueue(root);
            }
        }

        while (pending.Count > 0) {
            foreach (var reference in project.References.ReferencesFrom(pending.Dequeue())) {
                if (seen.Add(reference.Asset)) {
                    found.Add(reference.Asset);
                    pending.Enqueue(reference.Asset);
                }
            }
        }

        return found;
    }

    /// <summary>Asks where to put a package of the selection, and writes one there.</summary>
    void ExportPackage() {
        if (services.Dialogs is not { } dialogs) {
            return;
        }

        var assets = Closure(project.Selection);

        if (assets.Count == 0) {
            return;
        }

        deferred.When(
            dialogs.SaveFileAsync(
                new FileDialogOptions {
                    Title = "Export Package",
                    InitialDirectory = project.Paths.Root,
                    SuggestedFileName = Path.GetFileName(project.Paths.Root.TrimEnd(Path.DirectorySeparatorChar))
                        + PackageExtension,
                    Filters = [new FileFilter("Vixen package", PackageExtension.TrimStart('.'))]
                }
            ),
            path => {
                if (path is { Length: > 0 }) {
                    WritePackage(path, assets);
                }
            },
            failure => Shell.Notifications.Show("Could not export", NotificationSeverity.Error, failure.Message)
        );
    }

    /// <summary>Writes the assets into an archive, each under its project-relative path.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The sidecar ships with the asset, and leaving it out would be the defect.</b> An
    ///         identity is in the <c>.meta</c> — a package unpacked without one is scanned into a
    ///         project that mints fresh GUIDs, so every reference inside the package points at
    ///         nothing and the material arrives white. That is exactly the failure the closure was
    ///         computed to avoid, arriving by the other door.
    ///     </para>
    ///     <para>
    ///         Folders carry no bytes, so they are skipped rather than written as empty entries: the
    ///         paths inside the archive already describe the shape of the tree.
    ///     </para>
    /// </remarks>
    internal void WritePackage(string path, IReadOnlyList<AssetId> assets) {
        var written = 0;

        try {
            // ⚠ Truncated rather than opened. `ZipArchiveMode.Create` over an existing file appends
            // to whatever is in it, which for a second export to the same name is an archive with
            // two copies of every asset — and a reader that takes the first one.
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create)) {
                foreach (var asset in assets) {
                    if (!project.Assets.TryGetByGuid(asset, out var entry) || entry.IsFolder) {
                        continue;
                    }

                    var absolute = project.Paths.Absolute(entry.Path);

                    if (!File.Exists(absolute)) {
                        continue;
                    }

                    archive.CreateEntryFromFile(absolute, entry.Path);

                    var meta = AssetMetaFile.PathFor(absolute);

                    if (File.Exists(meta)) {
                        archive.CreateEntryFromFile(meta, AssetMetaFile.PathFor(entry.Path));
                    }

                    written++;
                }
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show("Could not export", NotificationSeverity.Error, exception.Message);
            return;
        }

        Shell.Notifications.Success(
            written == 1 ? "1 asset exported" : $"{written} assets exported"
        );
    }

    void OpenSelectedAsset() {
        if (project.Selection.Count > 0) {
            Open(project.Selection[0]);
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
            browser?.BeginRename(project.Selection[0]);
        }
    }

    /// <summary>Applies a rename typed into the browser, and says why not if it could not.</summary>
    /// <remarks>
    ///     ⚠ <b>The tree is rebuilt either way.</b> A refused rename leaves the row showing the name
    ///     that was typed — the control committed its own editor before this ran — and an outliner
    ///     showing a name the disk does not have is worse than the refusal it is reporting.
    /// </remarks>
    void RenameAsset(AssetId asset, string name) {
        var result = AssetOperations.Rename(project, asset, name);

        if (!result.Ok) {
            Shell.Notifications.Show("Could not rename", NotificationSeverity.Error, result.Message);
        }

        browser?.Rescan();
    }

    /// <summary>Moves assets into a folder that was dropped on, and says why not if it could not.</summary>
    void MoveAssets(IReadOnlyList<AssetId> assets, AssetId folder) {
        if (!project.Assets.TryGetByGuid(folder, out var destination) || !destination.IsFolder) {
            return;
        }

        var failures = 0;

        foreach (var asset in assets) {
            var result = AssetOperations.Move(project, asset, destination.Path);

            if (!result.Ok) {
                failures++;
                Shell.Notifications.Show("Could not move", NotificationSeverity.Error, result.Message);
            }
        }

        browser?.Rescan();

        if (failures == 0 && assets.Count > 0) {
            Shell.Notifications.Success($"Moved to {destination.Name}");
        }
    }

    /// <summary>Deletes the selected assets, having said what would break.</summary>
    /// <remarks>
    ///     ⚠ <b>The list of referrers goes in the question, not in a report afterwards.</b>
    ///     <c>ReferenceIndex</c> has answered "what breaks if I delete this" since it was written and
    ///     nothing asked it; a list of newly-broken scenes shown once the file has gone is not a
    ///     warning. Five names and a count, because a dialog listing four hundred is one nobody reads
    ///     and the number is the part that changes the decision.
    /// </remarks>
    void DeleteSelectedAssets() {
        List<AssetId> assets = [.. project.Selection];

        if (assets.Count == 0) {
            return;
        }

        var names = assets
            .Select(asset => project.Assets.TryGetByGuid(asset, out var entry) ? entry.Name : asset.ToString())
            .ToList();

        var broken = AssetOperations.Breakage(project, assets);

        var what = assets.Count == 1 ? $"Delete '{names[0]}'?" : $"Delete {assets.Count} assets?";

        var message = broken.Count == 0
            ? "Nothing in the project references them. This cannot be undone."
            : $"{Count(broken.Count, "asset")} would be left pointing at nothing:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, broken.Take(5))
            + (broken.Count > 5 ? Environment.NewLine + $"…and {broken.Count - 5} more" : string.Empty);

        Confirm(
            ask: true,
            what,
            message,
            () => {
                foreach (var asset in assets) {
                    var result = AssetOperations.Delete(project, asset);

                    if (!result.Ok) {
                        Shell.Notifications.Show("Could not delete", NotificationSeverity.Error, result.Message);
                    }
                }

                browser?.Rescan();
            },
            "Delete"
        );

        static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }

    /// <summary>Makes a folder beside whatever is selected, and starts renaming it.</summary>
    /// <remarks>
    ///     Inside the selected folder, or beside the selected file — which is what every browser does
    ///     and what somebody who has just clicked a folder means.
    /// </remarks>
    void NewAssetFolder() {
        var parent = "Assets";

        if (project.Selection.Count > 0 && project.Assets.TryGetByGuid(project.Selection[0], out var entry)) {
            parent = entry.IsFolder ? entry.Path : Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? "Assets";
        }

        var result = AssetOperations.CreateFolder(project, parent, "New Folder");

        if (!result.Ok) {
            Shell.Notifications.Show("Could not create the folder", NotificationSeverity.Error, result.Message);
            return;
        }

        browser?.Rescan();
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

    /// <summary>Bakes the selected model's mesh maps into the project.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ The caller <c>MapBaker.Bake</c> did not have.</b> Doc 48 § D12's seven
    ///         measurements were built on <c>BakedMaps</c> and nothing in the repository outside
    ///         <c>MapBakerTests</c> called the bake — not an importer, not a content build, not the
    ///         editor. This line and <c>ContentTasks.BakeMeshMaps</c> behind it are what makes them
    ///         reachable by a person.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The source and the target are the same mesh, and that is the ordinary case
    ///         rather than a simplification.</b> A separate high-poly belongs to the retopology
    ///         flow; asking for the occlusion, curvature and thickness <i>of the mesh you are
    ///         texturing</i> is what every generator in § 4.8 reads, and it is what Painter's bake
    ///         does when nobody supplies a high-poly. A high-poly picker is a bake panel's job.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first mesh with an atlas, and it says which.</b> A model file can hold
    ///         several, the bake is one at a time because it writes one named set, and a verb that
    ///         silently picked one of four would be a verb whose output nobody can account for.
    ///     </para>
    /// </remarks>
    void BakeSelectedMeshMaps() {
        if (project.Selection.Count != 1 || !project.Assets.TryGetByGuid(project.Selection[0], out var entry)) {
            return;
        }

        var absolute = project.Paths.Absolute(entry.Path);

        try {
            if (BakeableMesh(entry, absolute, out var name, out var refusal) is not { } mesh) {
                Shell.Notifications.Show("Nothing to bake into", NotificationSeverity.Warning, refusal);

                return;
            }

            var kernel = ModelGeometry.ToEditMesh(mesh);

            // ⚠ The model's own asset id goes with the name, and it is what keys the set rather
            // than decorating it. The name is whatever the artist called the object in Blender,
            // which is `Cube` in every file nobody renamed — see `MeshMapNaming.ModelKey`.
            content.BakeMeshMaps(
                meshMaps,
                entry.Guid,
                name,
                kernel,
                kernel,
                MeshMapBakeOptions.ToBake(),
                MeshMapBakeOptions.Overwrite
            );
        } catch (Exception failure) when (failure
            is IOException
            or UnauthorizedAccessException
            or ModelFormatException
            or ArgumentException) {
            Shell.Notifications.Show("Could not bake mesh maps", NotificationSeverity.Error, failure.Message);
        }
    }

    /// <summary>The first mesh of a model that has an atlas to bake into, and what to call the set.</summary>
    /// <param name="entry">The selected asset.</param>
    /// <param name="absolute">Where its file is.</param>
    /// <param name="name">What the set is named after, or empty when there is nothing to bake.</param>
    /// <param name="refusal">Why there is nothing, or empty.</param>
    /// <returns>The mesh, or <see langword="null" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The imported chunks first, and the file only for a model this project has never
    ///         imported</b> — the same seam <c>LayerStackMesh.Open</c> takes, and for the reason
    ///         <a href="https://github.com/Rikarin/Vixen/issues/934">#934</a> gives about painting:
    ///         <c>ModelImportSettings.Unwrap</c> generates its coordinates inside
    ///         <c>ModelRetopology.Run</c>, which the <em>importer</em> calls and <c>ModelReader</c>
    ///         does not. Reading the file meant that a model unwrapped at import — the one thing
    ///         somebody does before asking for maps of its atlas — was refused here with "none of its
    ///         meshes carries texture coordinates", about a state they had already created.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the set takes the name the project gave the mesh</b>, which is what
    ///         <c>SubAssetNames</c> may have changed and what <c>TextureSetAsset.Mesh</c> is matched
    ///         against. A bake filed under the file's name beside a stack narrowed by the project's
    ///         would be two names for one mesh, and a generator reading a map by usage would bind
    ///         nothing with both of them present.
    ///     </para>
    /// </remarks>
    MeshData? BakeableMesh(AssetEntry entry, string absolute, out string name, out string refusal) {
        name = "";
        refusal = "";

        var declared = ProjectMeshSource.Declared(absolute);

        if (declared.Count > 0) {
            foreach (var mesh in declared) {
                if (SceneGeometry.TryGet(new(entry.Guid, mesh.Id), out var imported) && HasAtlas(imported)) {
                    name = mesh.Name.Length > 0 ? mesh.Name : Path.GetFileNameWithoutExtension(absolute);

                    return imported;
                }
            }

            refusal = "None of this model's imported meshes carries texture coordinates, and a mesh map is "
                + "a picture of the atlas. Unwrap it — the model importer's Unwrap setting will — and "
                + "import again.";

            return null;
        }

        var read = ModelReader.Read(
            File.ReadAllBytes(absolute),
            Path.GetExtension(absolute),
            Path.GetFileNameWithoutExtension(absolute),
            ModelSettingsOf(absolute)
        );

        if (Array.Find(read.Meshes, HasAtlas) is { } found) {
            name = found.Name.Length > 0 ? found.Name : Path.GetFileNameWithoutExtension(absolute);

            return found;
        }

        refusal = read.Meshes.Length == 0
            ? "That file has no meshes."
            : "None of its meshes carries texture coordinates, and a mesh map is a picture of the atlas. "
            + "This project has never imported it — import it, because the model importer's Unwrap "
            + "setting generates the atlas — or unwrap it in the tool that made it.";

        return null;
    }

    /// <summary>Whether a mesh has one texture coordinate per vertex, which is what a bake lands in.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>Whether it does.</returns>
    /// <remarks>
    ///     ⚠ <b>The lengths have to <em>match</em> and not merely both be non-zero.</b> A file that
    ///     lied about its vertex count gives a short array, and <c>ModelGeometry.ToEditMesh</c> reads
    ///     one coordinate per vertex — so a bake over one would land on coordinates belonging to other
    ///     vertices rather than fail.
    /// </remarks>
    static bool HasAtlas(MeshData mesh) =>
        mesh.TexCoords.Length == mesh.Positions.Length && mesh.TexCoords.Length > 0;

    /// <summary>What the Bake Mesh Maps panel is called in an arrangement.</summary>
    internal const string MeshMapBakePanel = "mesh-map-bake";

    /// <summary>The panel while it is open, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Held for <c>buildView</c>'s reason and released the same way.</b> What it displays —
    ///     which model is selected, whether a content task is running, what the last bake produced —
    ///     changes for reasons that are not edits to the panel, so something has to be able to ask it
    ///     again.
    /// </remarks>
    MeshMapBakeView? bakeView;

    /// <summary>The one copy of what a bake is set to measure, which the verb and the panel share.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exposed for a suite to read, and it is the assertion doc 20's A4 rule needs.</b>
    ///         "The panel and the bake read one object" is not visible from either of them — a panel
    ///         editing a copy looks identical from the panel's own tests, and the failure is a
    ///         resolution somebody set being ignored by the button beside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Out of the settings store rather than a field</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/701">#701</a>. <c>Get&lt;T&gt;</c>
    ///         hands every caller the same instance, which is what keeps the sentence above true
    ///         across the persistence: the panel edits what the verb reads and what
    ///         <see cref="SaveMeshMapBakeSettings" /> writes, and none of the three is a copy.
    ///     </para>
    /// </remarks>
    internal MeshMapBakeSettings MeshMapBakeOptions => project.Settings.Get<MeshMapBakeSettings>();

    /// <summary>Writes the bake settings, which is what every control on the panel does.</summary>
    /// <remarks>
    ///     ⚠ <b>Immediately rather than behind an Apply</b>, for the reason
    ///     <c>EditorBuilds.SaveBuildSettings</c> gives: the Bake button reads these fields, so an edit
    ///     that had not been committed would mean the button baking something other than what is on
    ///     screen. Nothing here costs anything until a bake runs.
    /// </remarks>
    void SaveMeshMapBakeSettings() {
        project.Settings.MarkChanged<MeshMapBakeSettings>();

        try {
            project.Settings.Save<MeshMapBakeSettings>();
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            Shell.Notifications.Show(
                "Could not save the mesh-map bake settings",
                NotificationSeverity.Error,
                failure.Message
            );
        }
    }

    /// <summary>Doc 48 § D12's bake panel.</summary>
    void MeshMapPanels() =>
        Shell.RegisterPanel(
            new PanelDescriptor(
                MeshMapBakePanel,
                EditorStrings.PanelMeshMapBake,
                panel => {
                    var view = panel.Add<MeshMapBakeView>();

                    // ⚠ Pulled, both, and `BuildSettingsView` says why: this factory runs again on
                    // every reopen, so an answer captured once would be from a selection the user
                    // has since changed.
                    view.Chosen = MeshMapSubject;
                    view.Refusal = MeshMapRefusal;

                    view.BakeRequested += _ => BakeSelectedMeshMaps();

                    // ⚠ Every control on the panel writes, and the panel is the only writer of these
                    // settings — no inspector page over the same state, which is doc 20's A4 rule:
                    // two writers is how a settings row and the panel beside it come to disagree.
                    view.Changed = SaveMeshMapBakeSettings;
                    view.Show(MeshMapBakeOptions);
                    view.ShowResult(content.LastBake);

                    bakeView = view;
                }
            ) {
                Closed = () => bakeView = null
            }
        );

    /// <summary>What the panel says would be baked.</summary>
    /// <remarks>
    ///     ⚠ <b>The file, not the mesh inside it.</b> Naming the mesh means reading the model through
    ///     Assimp, which is the bake's own first step and is far too much to do while a panel is
    ///     restating itself — see <see cref="MeshMapRefusal" /> for what this surface may cost.
    /// </remarks>
    string? MeshMapSubject() =>
        project.Selection.Count == 1 && project.Assets.TryGetByGuid(project.Selection[0], out var entry)
            ? Path.GetFileName(entry.Path)
            : null;

    /// <summary>Why the panel's Bake button is greyed, or null when it is not.</summary>
    /// <remarks>
    ///     ⚠ <b>Cheap enough to ask once a frame, which <c>BuildRefusal</c> is not.</b> That one
    ///     enumerates the project root looking for a <c>.csproj</c> and is therefore asked only when
    ///     something happens; this is a count, a dictionary lookup and a set lookup, and the
    ///     selection it depends on has no change event to hang off — see
    ///     <see cref="MeshMapBakeView.Refresh" />, which <c>Update</c> calls while the panel is open.
    /// </remarks>
    string? MeshMapRefusal() =>
        content.IsBusy
            ? "Something is already importing, building or baking."
            : SelectedIsAModel()
                ? null
                : "Select one model in the project browser — "
                + string.Join(", ", ModelExtensions.Order(StringComparer.Ordinal))
                + ".";

    /// <summary>Whether exactly one asset is selected and it is a model file.</summary>
    /// <remarks>
    ///     ⚠ <b>The extension is checked here rather than discovered by failing.</b> A verb enabled
    ///     for every selection and refusing most of them is doc 20's complaint in reverse: it reads
    ///     as an editor that cannot bake, once, per texture the artist happened to have selected.
    ///     The list is <c>ModelImporter</c>'s own <c>[Importer]</c> attribute rather than a second
    ///     copy of it, so a format added there is a format this offers.
    /// </remarks>
    bool SelectedIsAModel() =>
        project.Selection.Count == 1
        && project.Assets.TryGetByGuid(project.Selection[0], out var entry)
        && !entry.IsFolder
        && ModelExtensions.Contains(Path.GetExtension(entry.Path));

    /// <summary>What <c>ModelImporter</c> claims, read once from its own attribute.</summary>
    static readonly HashSet<string> ModelExtensions =
        new(new ModelImporter().Extensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>How the model is imported, so the bake reads the geometry the project uses.</summary>
    /// <remarks>
    ///     ⚠ <b>Defaults where the sidecar cannot be read, rather than a refusal.</b> A model that
    ///     has never been imported has no importer block at all, and that is the commonest moment to
    ///     want a bake — the file has just been dropped in.
    /// </remarks>
    static ModelImportSettings ModelSettingsOf(string absolute) {
        var sidecar = AssetMetaFile.PathFor(absolute);

        try {
            return File.Exists(sidecar) && AssetMetaFile.ReadFile(sidecar).Importer is ModelImportSettings settings
                ? settings
                : new();
        } catch (Exception failure)
            when (failure is IOException or YamlParseException or YamlBindingException or MetaVersionException) {
            return new();
        }
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
        // ⚠ Re-read, and the cached copy the Appearance page seeds from is dropped with it — that
        // page exists to edit this file, so a hot reload that left it showing the old text would be
        // an editor disagreeing with itself about what the theme is.
        tokens = null;

        Shell.Theme.LoadTokens(store.Read(ThemeFile));
        Shell.Notifications.Success("Styles reloaded");
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

        Browse(new Uri(project.Paths.Root).AbsoluteUri);
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

    /// <summary>Shows an asset in the project browser, which is what a search result means.</summary>
    void RevealAsset(AssetId asset) {
        Shell.Workspace.Open("project");
        Select([asset]);
    }

    /// <summary>Selects an entity and frames it, which is what a search result for one means.</summary>
    void RevealEntity(Entity entity) {
        Shell.Workspace.Open("hierarchy");

        (inspected ?? scene).Selection.Set([entity]);
        Shell.Context = SceneContext;

        hierarchyStale = true;
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
    ///     <para>
    ///         ⚠ <b>Under the first selected entity's parent, not at the root.</b> Grouping three
    ///         children of a rig and having the group appear beside the rig is the behaviour that
    ///         makes Ctrl+G untrustworthy — the group belongs where the things being grouped already
    ///         were.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One undo step, and the whole selection moves as one command.</b> A create plus a
    ///         reparent per member is n+1 entries, so Ctrl+Z over a group of five put the fifth
    ///         entity back and left a group holding the other four — issue 1217. The transaction
    ///         covers the create, and the batch <see cref="SceneDocument.Reparent(IEnumerable{Entity}, Entity)" />
    ///         is the overload whose own remarks say why a loop of the single-entity one is wrong.
    ///     </para>
    /// </remarks>
    void Group() {
        if (scene.Selection.Count == 0) {
            return;
        }

        var members = scene.Selection.ToList();
        var parent = Hierarchy.ParentOf(world, members[0]);

        using var batch = scene.Stack.BeginTransaction(EditorStrings.CommandEntityGroup.Text);

        var group = scene.Create("Group", LocalTransform.Identity, parent);

        scene.Reparent(members, group);
        scene.Selection.Set([group]);
    }

    /// <summary>What an entity's <c>LodLevel</c> is written through, so the write is undoable.</summary>
    /// <remarks>
    ///     ⚠ <b>The same seam the component panel writes through, rather than a bare
    ///     <c>World.Add</c>.</b> A command that added the component directly would put a change on
    ///     the scene that Ctrl+Z cannot take back — and undoing the reparent beside it <em>would</em>
    ///     work, so the half-undone state is five meshes back where they were, each still claiming to
    ///     be a level of a group that no longer exists.
    /// </remarks>
    static readonly ComponentBridge<LodLevel> LodLevels = new("LodLevel");

    /// <summary>Turns the selection into a LOD chain under a group of its own.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Issue 1173's item (2).</b> Both components are <c>[Component] [DataContract]</c>,
    ///         so the inspector has always shown them and a scene has always serialised them — and
    ///         that was the whole of the authoring story. A three-level chain was three meshes, one
    ///         empty, three reparents, three <c>LodLevel</c> components typed in by hand and a
    ///         threshold list typed into a fourth.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Selection order is level order, and the first selected is LOD 0.</b> There is no
    ///         other honest source: the meshes are three separate imports with no relationship the
    ///         editor can read, and guessing from triangle counts would be a rule that is right until
    ///         the day somebody's coarse level is denser. The numbers are a field on each child
    ///         afterwards, so a wrong guess costs one edit rather than an undo.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Halving thresholds, and they have to descend.</b> <c>LodRenderFeature.Add</c>
    ///         throws on a list that does not, so a default that merely looked plausible would be a
    ///         command whose output crashes the renderer. Halving from a half of the viewport's
    ///         height is a chain an author edits rather than one they have to invent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One undo step for the whole gesture.</b> A create, n reparents and n component
    ///         writes is 2n+1 commands, and Ctrl+Z that took back one of them would leave a group
    ///         with four levels in it. <c>entity.group</c> beside this one still has that shape —
    ///         filed rather than widened into here.
    ///     </para>
    /// </remarks>
    void GroupAsLod() {
        var members = scene.Selection.Where(world.IsAlive).ToList();

        if (members.Count < 2) {
            return;
        }

        using var batch = scene.Stack.BeginTransaction(EditorStrings.CommandEntityGroupLod.Text);

        var parent = Hierarchy.ParentOf(world, members[0]);

        var group = scene.Create(
            "LOD Group",
            LocalTransform.Identity,
            parent,
            entity => world.Add(entity, new LodGroupComponent { Thresholds = LodThresholds(members.Count) })
        );

        for (var level = 0; level < members.Count; level++) {
            var member = members[level];

            scene.Reparent(member, group);

            scene.Stack.Execute(
                new SetComponentCommand(
                    scene,
                    LodLevels,
                    member,
                    world.Has<LodLevel>(member) ? world.Read<LodLevel>(member) : null,
                    new LodLevel { Level = level },
                    EditorStrings.CommandEntityGroupLod.Text
                )
            );
        }

        scene.Selection.Set([group]);
    }

    /// <summary>The switch points a chain of <paramref name="levels" /> starts with.</summary>
    /// <param name="levels">How many levels the group has.</param>
    /// <returns>
    ///     <paramref name="levels" /> − 1 screen-height fractions, descending — empty for a group of
    ///     one, which <c>LodGroupComponent</c> reads as "one level" rather than as a mistake.
    /// </returns>
    static float[] LodThresholds(int levels) {
        var thresholds = new float[Math.Max(levels - 1, 0)];
        var fraction = 0.5f;

        for (var index = 0; index < thresholds.Length; index++) {
            thresholds[index] = fraction;
            fraction *= 0.5f;
        }

        return thresholds;
    }

    /// <summary>Copies the selection in place, and selects what came out.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No offset, which is what every editor with this key does.</b> A duplicate nudged
    ///         sideways is a duplicate somebody has to put back; a copy exactly on top of the
    ///         original, already selected, is one drag away from wherever they meant. Blockout's
    ///         array tools pass an offset because a row of nine walls is what they are for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The copies become the selection, and the originals stop being it.</b> Pressing
    ///         Ctrl+D twice has to give two copies rather than a copy of a copy of the same thing —
    ///         and leaving the originals selected would make the second press duplicate them again.
    ///     </para>
    /// </remarks>
    void DuplicateSelection() {
        if (scene.Selection.Count == 0) {
            return;
        }

        List<Entity> copies = [];

        if (SceneClone.Duplicate(scene, scene.Selection.ToList(), Vector3.Zero, copies) == 0) {
            return;
        }

        scene.Selection.Set(copies);
        hierarchyStale = true;
    }

    /// <summary>Makes everything selected a root, keeping where each one is in the world.</summary>
    /// <remarks>
    ///     ⚠ <b>One <c>ReparentCommand</c> for the whole selection.</b> A loop of the single-entity
    ///     overload was one undo entry per entity — issue 1217 — so a single Ctrl+Z after unparenting
    ///     five rows put one of them back under its old parent and left the other four at the root.
    /// </remarks>
    void ClearParent() {
        scene.Reparent(scene.Selection.ToList(), Entity.Null);
    }

    /// <summary>One row of the Set Parent dialog.</summary>
    /// <param name="Label">What the row says.</param>
    /// <param name="Parent">Where choosing it would put the selection.</param>
    /// <param name="Refusal">Why it cannot be chosen, or <see langword="null" /> when it can.</param>
    sealed record ParentChoice(string Label, Entity Parent, string? Refusal);

    /// <summary>Asks which entity to hang the selection from, and moves it there undoably.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The impossible parents are listed and greyed rather than left out.</b> Dragging a
    ///         parent onto its own child is a gesture people make on purpose — they are looking for
    ///         the row and expect to find it — and a list that quietly omitted the descendants would
    ///         read as the editor having lost them. The row says which rule refused it, which is the
    ///         same bargain the whole <c>Planned</c> mechanism makes one level up.
    ///     </para>
    ///     <para>
    ///         One <see cref="ReparentCommand" /> for the whole selection, so one Ctrl+Z takes it
    ///         back — the drag in the outliner makes exactly the same call.
    ///     </para>
    /// </remarks>
    void SetParent() {
        if (scene.Selection.Count == 0) {
            return;
        }

        var moving = scene.Selection.ToList();

        // ⚠ Not called "Scene Root", which is what the default scene's own root entity is called.
        // A list where the row meaning "no parent at all" is spelled the same as a row meaning "under
        // that entity" is a list with two different answers under one name.
        List<ParentChoice> choices = [new ParentChoice("(No parent)", Entity.Null, null)];

        foreach (var entity in scene.Entities) {
            choices.Add(new ParentChoice(scene.NameOf(entity), entity, WhyNot(entity)));
        }

        _ = Ask();

        string? WhyNot(Entity candidate) {
            foreach (var entity in moving) {
                if (candidate == entity) {
                    return "That is the entity being moved.";
                }

                if (Hierarchy.IsAncestorOf(world, entity, candidate)) {
                    return $"It is inside {scene.NameOf(entity)}, which is what is moving.";
                }
            }

            // ⚠ Refused only when *every* one of them is already there. A mixed selection with one
            // entity outside still has something to move, and `ReparentCommand` drops the ones that
            // do not — so greying the row would refuse a move that is half meaningful, which is the
            // opposite of that command's own rule.
            return moving.All(entity => Hierarchy.ParentOf(world, entity) == candidate)
                ? "Already the parent."
                : null;
        }

        async Task Ask() {
            var chosen = await ChooseAsync(
                moving.Count == 1 ? "Hang it from what?" : $"Hang {moving.Count} entities from what?",
                choices,
                static choice => choice.Label,
                static choice => choice.Refusal,
                static choice => choice.Refusal is null
            ).ConfigureAwait(true);

            if (chosen is not null && scene.Reparent(moving, chosen.Parent)) {
                hierarchyStale = true;
            }
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
        if (Viewport is not { } pane) {
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

    /// <summary>Puts the selection where the camera is looking.</summary>
    /// <remarks>
    ///     ⚠ <b>At the pivot, not at the eye, and that is what makes it different from Align With
    ///     View.</b> The pivot is the point the view orbits and therefore the point in the middle of
    ///     the pane; the eye is where you are standing. Moving something to the eye puts it inside the
    ///     near plane, which reads as the object having vanished.
    /// </remarks>
    void MoveToView() {
        if (Viewport is not { } pane) {
            return;
        }

        var pivot = pane.Camera.Pivot;

        // The middle of what is selected, so a group keeps its shape rather than collapsing onto one
        // point — the same rule `TransformGizmo`'s centre pivot follows.
        var centre = Vector3.Zero;
        var counted = 0;

        var targets = scene.Selection
            .Where(entity => world.Has<LocalTransform>(entity))
            .Select(entity => (Entity: entity, Was: world.Read<LocalTransform>(entity)))
            .ToList();

        foreach (var (entity, _) in targets) {
            if (world.Has<WorldTransform>(entity)) {
                centre += world.Read<WorldTransform>(entity).Value.Translation;
                counted++;
            }
        }

        if (counted == 0) {
            return;
        }

        var offset = pivot - (centre / counted);
        Displace("Move To View", targets, _ => offset);
    }

    /// <summary>Drops the selection onto whatever is under it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Straight down from each entity's own origin, ignoring the selection.</b> A ray
    ///         that could hit the thing being dropped answers "zero" at once, which is a Snap To Floor
    ///         that never moves anything — and with several things selected, one of them landing on
    ///         another is worse than nothing happening.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An entity with nothing under it falls to the ground plane rather than not
    ///         moving.</b> A block-out scene has no floor geometry at all, which is exactly the scene
    ///         somebody is placing things in; a verb that only worked once there was something to
    ///         land on would be one nobody would find out worked.
    ///     </para>
    /// </remarks>
    void SnapToFloor() {
        var targets = scene.Selection
            .Where(entity => world.Has<LocalTransform>(entity) && world.Has<WorldTransform>(entity))
            .Select(entity => (Entity: entity, Was: world.Read<LocalTransform>(entity)))
            .ToList();

        if (targets.Count == 0) {
            return;
        }

        var ignore = scene.Selection.Items;

        Displace(
            "Snap To Floor",
            targets,
            entity => {
                var from = world.Read<WorldTransform>(entity).Value.Translation;
                var ray = new Ray(from, -Vector3.UnitY);

                var landed = probe.Raycast(ray, ignore, out var hit) ? hit.Point : new Vector3(from.X, 0f, from.Z);

                return landed - from;
            }
        );
    }

    /// <summary>Moves a set of entities by an offset each, undoably.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The offsets are resolved once, when the command is built, rather than inside its
    ///         redo.</b> A redo that re-cast the rays would land on whatever the scene contains
    ///         <i>now</i>, so undoing and redoing a Snap To Floor after moving the floor would put
    ///         things somewhere the history never recorded — which is the one thing an undo stack
    ///         exists to make impossible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A world offset is turned into the parent's space before it is added to a
    ///         <c>LocalTransform</c>.</b> The two are the same only for a root; under a rotated or
    ///         scaled parent, adding a world vector to a local position moves the child along an axis
    ///         that is not the one asked for and by a distance that is not the one asked for. As a
    ///         <i>direction</i> and not a position, because an offset has no origin.
    ///     </para>
    /// </remarks>
    void Displace(
        string name,
        List<(Entity Entity, LocalTransform Was)> targets,
        Func<Entity, Vector3> offsetOf
    ) {
        var offsets = targets.Select(target => Local(target.Entity, offsetOf(target.Entity))).ToArray();

        scene.Stack.Execute(
            new DelegateCommand(
                name,
                _ => {
                    for (var index = 0; index < targets.Count; index++) {
                        var (entity, was) = targets[index];
                        world.Set(entity, was with { Position = was.Position + offsets[index] });
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

    /// <summary>A world-space offset in the space an entity's <c>LocalTransform</c> is written in.</summary>
    Vector3 Local(Entity entity, Vector3 offset) {
        var parent = new Transform(world, entity).Parent;

        return !parent.IsNull
            && world.Has<WorldTransform>(parent)
            && Matrix4x4.Invert(world.Read<WorldTransform>(parent).Value, out var inverse)
                ? Matrix4x4.TransformDirection(offset, inverse)
                : offset;
    }

    // ── Play mode ───────────────────────────────────────────────────────────────────────────────

    void EnterPlay() {
        if (!play.Play()) {
            return;
        }

        // ⚠ Through the same pair maximise itself uses, so stopping restores whatever arrangement the
        // user had rather than an arrangement this remembered separately. Skipped when the panel is
        // already single, because `Remember` would then record Single and stopping would leave the
        // toggle claiming the viewport is maximised when nothing was ever changed.
        if (maximiseOnPlay && arrangement != ViewportArrangement.Single) {
            Arrangement = Remember();
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

        ReportPlayGaps();
    }

    /// <summary>Says what this session runs, and names anything it is not running.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 20's first bar applied to a frame rather than to a menu line: a thing that
    ///         does not happen must be <i>visibly</i> not happening.</b> An in-editor session steps
    ///         an <c>EngineLoop</c>'s default graph — behaviours, coroutines, transforms — and
    ///         nothing else, because every other system a game runs is registered by that game's own
    ///         <c>OnInitialise</c> against a <c>PhysicsScene</c>, an <c>AudioEngine</c>, an
    ///         <c>InputService</c> or a <c>RenderView</c> that an editor either does not have or
    ///         already has a second, differently-aimed one of. A Play button that ran most of a frame
    ///         and said nothing would make the missing part read as a gameplay bug, which is the one
    ///         outcome worse than not stepping at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The project's own systems are named, not counted.</b> "Some systems did not run"
    ///         sends somebody looking through their whole game; a list of three type names sends them
    ///         to the three files. They are found by reflection over the assembly
    ///         <c>ProjectAssemblies</c> already built and loaded, so the list is what this project
    ///         declares rather than what the engine ships.
    ///     </para>
    /// </remarks>
    void ReportPlayGaps() {
        // ⚠ Read off the session rather than written out here, and that is the point of the list
        // existing. This sentence used to name physics as a thing an in-editor session does not run,
        // and an `IPlaySystems` contribution has since made that false for the editor Vixen ships —
        // a fixed sentence would now be a report that lies in the safe direction, which is the one
        // that costs somebody a day.
        var added = play.Session?.Running ?? [];

        log.Write(
            LogLevel.Information,
            $"Play mode runs behaviours, coroutines and transforms{Also(added)}. Anything else a game "
            + "registers imperatively in its own OnInitialise — rather than declaring with "
            + "[GameSystem] — takes a host service the editor was never handed, so an in-editor "
            + "session does not run it."
        );

        // ⚠ The project's systems minus the ones that just ran, and the subtraction is the whole
        // point of #320. This list used to be every `ISystem` the project's assembly declared,
        // because none of them could run; a system that carries `[GameSystem]` and found its
        // services is now in the frame, and naming it as missing would be a report that lies.
        var systems = ProjectSystems().Except(play.Declared.Running, StringComparer.Ordinal).ToArray();
        var unsatisfied = play.Declared.Missing;
        var behaviors = play.Unsupported;
        var refused = play.Refused;

        if (systems.Length == 0 && unsatisfied.Count == 0 && behaviors.Count == 0 && refused.Count == 0) {
            return;
        }

        List<string> lines = [];

        // ⚠ Two different failures, said differently. An undeclared system is one nobody asked the
        // editor to run — the fix is `[GameSystem]`, and saying so is worth more than the list. An
        // unsatisfied one *was* declared and could not be built, which is a service the editor does
        // not have and a much narrower thing to go and look at.
        if (systems.Length > 0) {
            lines.Add(
                $"{systems.Length} system(s) this project declares but does not mark [GameSystem]: "
                + $"{string.Join(", ", systems)}."
            );
        }

        if (unsatisfied.Count > 0) {
            lines.Add($"{unsatisfied.Count} declared system(s) whose services are not here: {string.Join("; ", unsatisfied)}.");
        }

        if (behaviors.Count > 0) {
            lines.Add($"{behaviors.Count} behaviour(s) the session could not take over: {string.Join(", ", behaviors)}.");
        }

        // ⚠ A contribution that threw is the loudest of the three, because it is the editor's own
        // wiring failing rather than a gap the editor never claimed to fill. A session with no
        // physics after this editor started publishing one is a difference somebody will otherwise
        // spend the afternoon looking for in their game.
        if (refused.Count > 0) {
            lines.Add($"{refused.Count} contribution(s) that failed to start: {string.Join(", ", refused)}.");
        }

        var said = "Not running — " + string.Join(" ", lines);

        log.Write(LogLevel.Warning, said);

        Shell.Notifications.Show("Play mode is not running everything", NotificationSeverity.Warning, said);
    }

    /// <summary>", plus physics and terrain collision" — or nothing, for a session with no additions.</summary>
    static string Also(IReadOnlyList<string> added) => added.Count == 0 ? string.Empty : $", plus {Join(added)}";

    /// <summary>A list a person reads: "a", "a and b", "a, b and c".</summary>
    static string Join(IReadOnlyList<string> parts) =>
        parts.Count == 1
            ? parts[0]
            : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    /// <summary>The <c>ISystem</c> types the project's own assembly declares.</summary>
    /// <remarks>
    ///     ⚠ <b>Every <c>ISystem</c> in the assembly, including the ones that just ran.</b> The
    ///     caller subtracts <c>PlayModeController.Declared.Running</c>, so what is left is the set a
    ///     project still registers by hand in its <c>Game.OnInitialise</c> — code no editor runs.
    ///     Until <c>[GameSystem]</c> that was all of them, and this list was the closest true
    ///     statement available; it is now a list of systems whose author has not opted in yet, which
    ///     is a thing they can act on.
    /// </remarks>
    IReadOnlyList<string> ProjectSystems() {
        List<string> found = [];

        if (code.Loaded is not { } assembly) {
            return found;
        }

        Type?[] declared;

        try {
            declared = assembly.GetTypes();
        } catch (ReflectionTypeLoadException partial) {
            // A project referencing something that did not load still has the types that did, and a
            // partial list is a better answer here than none: this is a report, not a gate.
            declared = partial.Types;
        }

        foreach (var type in declared) {
            if (type is { IsClass: true, IsAbstract: false } && typeof(ISystem).IsAssignableFrom(type)) {
                found.Add(type.Name);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    void LeavePlay() {
        var restored = play.Stop(scene.Selection);

        scene.Selection.Set(restored);
        hierarchyStale = true;

        // Whatever the panel was split into before Play maximised it. Null when it was not, which is
        // every session where the preference is off.
        if (restore is { } previous) {
            Arrangement = Take(previous);
        }
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
                    PendingProject = null;
                    break;
            }
        }
    }

    /// <summary>Throws away what the active document holds and reads its file again.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The other half of the stale-document answer, as a gesture.</b>
    ///         <c>ExternalEdits</c> declines to reload a document with unsaved edits and says so;
    ///         Ctrl+S is how a person keeps theirs, and this is how they take the file's. Without it
    ///         the notification's only advice would be to close the tab and open it again, which is
    ///         the same operation spelled as a workaround.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It asks, and only when there is something to lose.</b> A revert of a document
    ///         whose file merely moved on discards nothing and is not worth a modal; a revert of one
    ///         with unsaved edits discards the only copy of them, which is exactly the case
    ///         <see cref="Confirm" /> exists for. That is also why the command is enabled for a
    ///         merely-dirty document with no external edit at all: "put it back the way it was
    ///         saved" is the same operation and has always been a thing people want.
    ///     </para>
    /// </remarks>
    void Revert() {
        if (project.ActiveDocument.Value is not { CanReload: true } document) {
            return;
        }

        var title = document.Title.Peek();

        Confirm(
            document.IsDirty.Value,
            $"Revert '{title}'?",
            "Its unsaved changes and its undo history are discarded, and the file on disk is read "
            + "again. This cannot be undone.",
            () => {
                if (document.Reload()) {
                    return;
                }

                Shell.Notifications.Show(
                    $"'{title}' could not be read again",
                    NotificationSeverity.Warning,
                    "What is on screen is what was there before."
                );
            },
            "Revert"
        );
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
