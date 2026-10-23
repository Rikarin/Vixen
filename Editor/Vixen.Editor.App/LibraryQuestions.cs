// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Platform;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The content library's verbs that ask something first: Create, Import Files, Export Package, Clean Library.</summary>
/// <remarks>
///     <para>
///         <b>Moved out of <c>EditorParity.cs</c> (#1296), because none of them asks through anything
///         private.</b> They stayed on the application as "the asset lines that ask through this
///         editor's dialogs" — but the drawn chooser is <c>EditorShell.ChooseAsync</c>, the native
///         pickers and the frame queue their answers come back through are published, and every verb
///         ends in <see cref="IContentLibrary" />. <c>LibraryModule</c> registers them, beside the
///         verbs that ask nothing, the way <c>SceneQuestions</c> sits beside <c>SceneModule</c>.
///     </para>
///     <para>
///         ⚠ <b>Not here: Rename, Move To and New Folder.</b> All three are enabled only while the
///         content browser is open and the first starts its inline editor — they are the browser
///         panel's, which is still the application's.
///     </para>
/// </remarks>
static class LibraryQuestions {
    /// <summary>Registers the four verbs.</summary>
    /// <param name="context">The library module's context.</param>
    /// <param name="library">What each verb ends in.</param>
    public static void Register(PluginContext context, IContentLibrary library) {
        var shell = context.Shell;
        var project = library.Project;
        var registry = context.Services.Require<IEditorRegistry>();
        var deferred = context.Services.Require<Deferred>();

        context.Services.TryGet(out INativeDialogs? dialogs);

        // ⚠ Not a template gallery, and it never needed to be. Every `assets.create-*` verb writes a
        // sensible empty file of its kind — see `BuiltInAssetKinds` — so the general line's whole job
        // is asking *which one*, over the same registry the Create submenu is built from. A kind a
        // plugin contributed is in the dialog for free, which is the half a literal list of templates
        // would have got wrong.
        context.AddCommand(
            new EditorCommand("assets.create", EditorStrings.CommandAssetsCreate, () => ChooseKind(shell, registry)) {
                Category = EditorStrings.CategoryAssets
            }
        );

        context.AddCommand(
            new EditorCommand(
                "assets.import-files",
                EditorStrings.CommandAssetsImportFiles,
                () => ImportFiles(shell, library, dialogs, deferred)
            ) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => dialogs is not null && !library.IsImporting
            }
        );

        // ⚠ The walk this waited on is `assets.select-dependencies`, and it has been a real verb
        // since E5. What a package needs over that verb is the *closure* rather than one step of it:
        // Select Dependencies answers "what does this point at" for a person about to look at the
        // answer, and an archive that carried only that would unpack a material whose textures are
        // missing.
        context.AddCommand(
            new EditorCommand(
                "file.export-package",
                EditorStrings.CommandFileExportPackage,
                () => ExportPackage(shell, library, dialogs, deferred)
            ) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => dialogs is not null && project.Selection.Count > 0
            }
        );

        // Reimport of the selection is declared and disabled, with the reason (moved from the application).
        context.AddCommand(
            new EditorCommand("assets.reimport", EditorStrings.CommandAssetsReimport, () => { }) {
                Category = EditorStrings.CategoryAssets,
                Unavailable = EditorStrings.PlannedReasons["assets.reimport"]
            }
        );

        // ⚠ Behind the same busy flag as Reimport All: deleting the cache under a running import is
        // deleting the files it is writing.
        context.AddCommand(
            new EditorCommand("build.clean-library", EditorStrings.CommandBuildCleanLibrary, () => CleanLibrary(shell, project)) {
                Category = EditorStrings.CategoryBuild,
                Enablement = () => !library.IsImporting
            }
        );
    }

    /// <summary>Asks which kind of asset to make, and runs that kind's own verb.</summary>
    /// <remarks>
    ///     ⚠ <b>It runs the command rather than calling <c>IContentLibrary.Create</c> itself.</b> Each
    ///     kind's verb is what the Create submenu, the palette and a key binding all reach, and a second
    ///     path into the same file would be a second place the folder, the naming and the "does it
    ///     open" answer are decided — which is how two ways of making an animation graph come to
    ///     produce two different files.
    /// </remarks>
    static void ChooseKind(EditorShell shell, IEditorRegistry registry) {
        List<NewAssetKind> kinds = [.. registry.All<NewAssetKind>().OrderBy(static kind => kind.Order)];

        if (kinds.Count == 0) {
            return;
        }

        _ = Ask();

        async Task Ask() {
            var chosen = await shell.ChooseAsync(
                "What kind of asset?",
                kinds,
                static kind => kind.Title.Text,
                static kind => kind.Extension
            ).ConfigureAwait(true);

            if (chosen is not null) {
                shell.Commands.Execute(chosen.Id);
            }
        }
    }

    /// <summary>Copies files chosen from the OS into the project, then imports.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Copied rather than referenced.</b> An asset outside the project tree is one the
    ///         content build cannot find, the reference index cannot name and a colleague does not have
    ///         — every engine that allowed it spent years telling people why their build was broken.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Into the folder the browser is showing rather than into <c>Assets/</c></b>, which is
    ///         the smaller half of doc 20 § B3's "choose a destination". A person who has navigated to
    ///         <c>Textures/</c> and asked to import has already said where. That folder is
    ///         <see cref="IContentLibrary.Shown" />'s, which outlives the panel — so with the browser
    ///         closed it is the folder it last showed, where it used to be the root.
    ///     </para>
    /// </remarks>
    static void ImportFiles(EditorShell shell, IContentLibrary library, INativeDialogs? dialogs, Deferred deferred) {
        if (dialogs is null) {
            return;
        }

        var folder = library.Shown.Folder;

        deferred.When(
            dialogs.OpenFilesAsync(new FileDialogOptions { Title = "Import Assets", AllowsMultipleSelection = true }),
            paths => library.Import(paths, folder),
            failure => shell.Notifications.Show("Could not import", NotificationSeverity.Error, failure.Message)
        );
    }

    /// <summary>Asks where to put a package of the selection, and writes one there.</summary>
    static void ExportPackage(EditorShell shell, IContentLibrary library, INativeDialogs? dialogs, Deferred deferred) {
        if (dialogs is null) {
            return;
        }

        var project = library.Project;
        var assets = library.Closure(project.Selection);

        if (assets.Count == 0) {
            return;
        }

        deferred.When(
            dialogs.SaveFileAsync(
                new FileDialogOptions {
                    Title = "Export Package",
                    InitialDirectory = project.Paths.Root,
                    SuggestedFileName = Path.GetFileName(project.Paths.Root.TrimEnd(Path.DirectorySeparatorChar))
                        + ContentLibrary.PackageExtension,
                    Filters = [new FileFilter("Vixen package", ContentLibrary.PackageExtension.TrimStart('.'))]
                }
            ),
            path => {
                if (path is { Length: > 0 }) {
                    library.ExportPackage(path, assets);
                }
            },
            failure => shell.Notifications.Show("Could not export", NotificationSeverity.Error, failure.Message)
        );
    }

    /// <summary>Throws away the import cache and the artefacts, so the next build starts clean.</summary>
    static void CleanLibrary(EditorShell shell, EditorProject project) {
        try {
            if (Directory.Exists(project.Paths.Library)) {
                Directory.Delete(project.Paths.Library, recursive: true);
            }

            shell.Notifications.Success("Library cleared");
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            shell.Notifications.Show("Could not clear Library", NotificationSeverity.Error, exception.Message);
        }
    }
}
