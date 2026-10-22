// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The editor's layout presets, as a module.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D3, the Workspace context's first slice</b> (#1296). The presets were
///         <c>EditorApplication.Layouts</c>, seven <c>Shell.RegisterLayout</c> calls; they are
///         <see cref="PluginContext.AddLayout" />s now, which take themselves back on unload, and the
///         one preset made of another feature's panels — Profiling — is that feature's module's.
///     </para>
///     <para>
///         <b>And Save Layout and Reload Styles</b> (#1296), which waited on <see cref="IWorkspace" />:
///         they read the user store and the theme the application used to hold, and since #1294 the
///         workspace holds both — so the module names the service's verbs and reaches nothing of the
///         application's. Switching the module off takes both lines off the menus with it.
///     </para>
///     <para>
///         <b>And File ▸ Recent Documents</b> (#656), over <see cref="IWorkspace.Recent" /> — the list
///         one level below the editor, keyed on <c>IEditableDocument.Location</c>. The workspace
///         notes every document it shows; this tracks the editor's own scene, whose location Save As
///         moves (#1493).
///     </para>
///     <para>
///         <b>And File ▸ Open Recent's projects</b> (#1296), over <see cref="IProjectSession.Recent" />
///         and <see cref="IProjectSession.RequestProject" />. ⚠ The list is the Project context's and
///         the menu beside Recent Documents is this module's: doc 50 § D3 names no project module, and
///         a second module for one submenu would be a module for its own sake.
///     </para>
/// </remarks>
sealed class WorkspaceModule : IEditorPlugin {
    /// <summary>What the host activates it under.</summary>
    public const string ModuleId = "vixen.workspace";

    /// <summary>What the plugin manager calls it.</summary>
    public const string ModuleName = "Workspace";

    /// <summary>What an empty Recent Documents submenu shows.</summary>
    const string NoRecent = "file.no-recent-documents";

    /// <summary>The Window menu's full-screen line, which no host can answer yet.</summary>
    const string FullScreen = "view.full-screen";

    static readonly string[] NoRecentLine = [NoRecent];

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var workspace = context.Services.Require<IWorkspace>();

        context.AddCommand(
            new EditorCommand("view.save-layout", EditorStrings.CommandSaveLayout, () => workspace.SaveLayout()) {
                Category = EditorStrings.CategoryView
            }
        );

        context.AddCommand(
            new EditorCommand("tools.reload-styles", EditorStrings.CommandToolsReloadStyles, workspace.ReloadStyles) {
                Category = EditorStrings.CategoryTools
            }
        );

        // ⚠ Declared and unavailable, and the Window menu's — the shell names it there, as it names
        // Save Layout. Full screen is a property of an OS window and `EditorShell` is deliberately a
        // document with no window: what is missing is a way for the editor to reach one, not the verb.
        context.AddCommand(
            new EditorCommand(FullScreen, EditorStrings.CommandViewFullScreen, () => { }) {
                Category = EditorStrings.CategoryView,
                Unavailable = EditorStrings.PlannedReasons[FullScreen]
            }
        );

        // The five doc 11 names. They differ in which panels they show and how the middle is split,
        // which is the whole of what a layout preset is — the shapes come from `LayoutPresets` and
        // the panel lists are this application's.
        // ⚠ Project is a tab *behind* the console rather than under the hierarchy, and the two halves
        // of that are one decision. A content browser is read in tiles — a grid of thumbnails wants
        // width, and a quarter-width left column gives it two tiles a row — while the hierarchy is a
        // list of names and wants height. Stacking them in the left column made each of them the
        // other's problem. The console keeps the front tab because it is the one that has something
        // to say without being asked.
        context.AddLayout(
            "Default",
            EditorStrings.LayoutDefault,
            () => LayoutPresets.Standard(
                ["hierarchy"],
                ["scene"],
                // ⚠ Build Settings beside the inspector rather than in the middle. It is a form that
                // is read down, which is the shape the right-hand column already has — and it is
                // opened *against* a scene rather than instead of one, so a preset that gave it the
                // centre would put the viewport behind a tab.
                ["inspector", DeliveryModule.BuildPanel],
                ["console", "project"]
            )
        );

        context.AddLayout(
            "Scene",
            EditorStrings.LayoutScene,
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"])
        );

        context.AddLayout(
            "Shading",
            EditorStrings.LayoutShading,
            () => LayoutPresets.Standard(["project"], ["scene"], ["inspector"], ["console"])
        );

        context.AddLayout(
            "Animation",
            EditorStrings.LayoutAnimation,
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"], ["console"])
        );

        context.AddLayout(
            "Debug",
            EditorStrings.LayoutDebug,
            () => LayoutPresets.Split(["scene"], ["console"], 0.6f)
        );

        // ⚠ And the second of the two A6 owes, now that B5 exists. Its shape is the Profiling
        // preset's argument turned round (that one is the diagnostics module's, #1296): a cinematic is authored *against* the viewport, so the
        // scene keeps the width and the tracks get a wide, short strip under it — a timeline in a
        // right-hand slot is one where a two-second shot is forty pixels.
        context.AddLayout(
            "Sequencing",
            EditorStrings.LayoutSequencing,
            () => LayoutPresets.Standard(["hierarchy", "scenes"], ["scene"], ["inspector"], ["console"])
        );

        context.Shell.Workspace.DefaultPreset = "Default";

        RecentDocuments(context, workspace);
        RecentProjects(context);
    }

    /// <summary>What an empty Open Recent submenu shows.</summary>
    const string NoRecentProject = "file.no-recent";

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
    ///         <see cref="IProjectSession.RequestProject" /> — so there is no moment at which the history changes
    ///         under a live menu, and registering from inside the menu's own builder would be a
    ///         registration that rebuilds the menu it is being built for.
    ///     </para>
    ///     <para>
    ///         The project that is already open is left out: a line that reopens what you are
    ///         looking at is one people choose once.
    ///     </para>
    /// </remarks>
    static void RecentProjects(PluginContext context) {
        var session = context.Services.Require<IProjectSession>();
        var shell = context.Shell;

        List<string> ids = [];

        // The fallback line is declared and disabled, so an empty submenu reads as empty, not broken.
        context.AddCommand(
            new EditorCommand(NoRecentProject, EditorStrings.CommandFileNoRecent, () => { }) {
                Category = EditorStrings.CategoryFile,
                Unavailable = EditorStrings.PlannedReasons[NoRecentProject]
            }
        );

        foreach (var entry in session.Recent) {
            if (string.Equals(entry.Path, session.Project.Paths.Root, StringComparison.Ordinal)) {
                continue;
            }

            var path = entry.Path;

            // ⚠ Asked once. `Exists` is a stat call, and the list is where a path to an unmounted
            // share lives — the one place where asking four times is four chances to block.
            var exists = entry.Exists;

            context.AddCommand(
                new EditorCommand(
                    RecentCommand(path),

                    // ⚠ The user's name for it, never a translator's word — see `Untranslated`.
                    Untranslated.Title(entry.Name),
                    () => session.RequestProject(path)
                ) {
                    Category = EditorStrings.CategoryFile,

                    // A project on a volume that is not mounted is greyed rather than absent, for
                    // `ProjectHistory`'s reason: forgetting it is the one thing there is no way back
                    // from.
                    Enablement = () => exists
                }
            );

            ids.Add(RecentCommand(path));
        }

        // The submenu is a dynamic over ids, so the fallback line is what an empty list shows —
        // a submenu that opens onto nothing at all reads as a broken menu rather than an empty one.
        shell.Recent = ids.Count == 0 ? () => [NoRecentProject] : () => ids;

        // ⚠ Taken back with the module, or a disabled module would leave a submenu of commands it
        // has just unregistered.
        context.OnUnload(() => shell.Recent = null);
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

    /// <summary>File ▸ Recent Documents: the documents of this project recently opened or saved.</summary>
    /// <param name="context">The module's context.</param>
    /// <param name="workspace">The workspace, which holds the list and opens a document in a tab.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Commands rather than paths, with an id derived from the path</b>, for
    ///         <c>EditorParity.RecentProjectCommands</c>' reasons: a line needs a title, an enablement
    ///         and a place in the palette, and a positional id would move a keybinding to another file
    ///         every time the order changed. ⚠ Unlike the projects', this list changes under a live
    ///         menu — opening a document reorders it — so a location's command is registered the
    ///         first time it appears and kept, and the submenu asks which ids to show each time it
    ///         opens.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only this project's documents, and a missing one greyed rather than dropped.</b> The
    ///         list is the user's across projects; a document of another project cannot be opened
    ///         without opening that project, and a file a branch switch removed is the entry somebody
    ///         wants back when they switch again.
    ///     </para>
    /// </remarks>
    static void RecentDocuments(PluginContext context, IWorkspace workspace) {
        var project = context.Services.Require<EditorProject>();
        var shell = context.Shell;
        List<string> shown = [];

        // The editor's own scene is no tab's document, so the workspace never shows it; its location
        // is its writer's file and moves with Save As, which is why it is followed and not read.
        if (context.Services.TryGet<SceneDocument>(out var scene)) {
            context.Owns(workspace.Recent.Track(scene, shell.Document.Effects));
        }

        context.AddCommand(
            new EditorCommand(NoRecent, EditorStrings.CommandFileNoRecentDocuments, () => { }) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => false
            }
        );

        void Refresh(RecentDocuments list) {
            shown.Clear();

            foreach (var location in list.Locations) {
                if (!Inside(project, location)) {
                    continue;
                }

                var id = CommandFor(location);

                if (shell.Commands[id] is null) {
                    context.AddCommand(
                        new EditorCommand(
                            id,

                            // ⚠ A file name, never a translator's word — see `Untranslated`.
                            Untranslated.Title(Path.GetFileName(location)),
                            () => {
                                if (Find(project, location) is { } entry) {
                                    workspace.Open(entry.Guid);
                                }
                            }
                        ) {
                            Category = EditorStrings.CategoryFile,
                            Enablement = () => Find(project, location) is not null
                        }
                    );
                }

                if (!shown.Contains(id)) {
                    shown.Add(id);
                }
            }
        }

        Refresh(workspace.Recent);
        workspace.Recent.Changed += Refresh;
        context.OnUnload(() => workspace.Recent.Changed -= Refresh);

        if (context.FindMenu(EditorStrings.MenuFile.Id) is { } file) {
            var after = file.IndexOfSubmenu(EditorStrings.MenuRecent.Id);

            context.AddSubmenu(file, EditorStrings.MenuRecentDocuments, after < 0 ? -1 : after + 1)
                .AddDynamic(() => shown.Count == 0 ? NoRecentLine : shown);
        }
    }

    /// <summary>Whether a location is a file of this project.</summary>
    static bool Inside(EditorProject project, string location) {
        var relative = Path.GetRelativePath(project.Paths.Root, location);

        return !Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal);
    }

    /// <summary>The asset a location is, while it is one.</summary>
    static AssetEntry? Find(EditorProject project, string location) =>
        project.Assets.TryGetByPath(project.Paths.Relative(location), out var entry) && !entry.IsFolder ? entry : null;

    /// <summary>What the command that reopens a document is called: derived from its path, never its position.</summary>
    static string CommandFor(string location) {
        // FNV-1a over the full path, as `RecentCommand` does for projects — stable across runs, which
        // `string.GetHashCode` is not.
        var hash = 2166136261u;

        foreach (var character in Path.GetFullPath(location)) {
            hash = (hash ^ character) * 16777619u;
        }

        return string.Create(CultureInfo.InvariantCulture, $"file.recent-document.{hash:x8}");
    }
}
