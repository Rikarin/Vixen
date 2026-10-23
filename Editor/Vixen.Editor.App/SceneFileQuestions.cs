// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Platform;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>File ▸ Save, New Scene, Open Scene and Save As: the lines over <see cref="SceneFiles" />' verbs, and the questions in front of them.</summary>
/// <remarks>
///     <para>
///         <b>Moved out of <c>EditorParity.cs</c> (#1296)</b>, once the scene's file was a service. The
///         verbs are <see cref="SceneFiles" />'; what is here is what asks first — whether to discard
///         unsaved changes, and which file — through the shell's dialog and the published native pickers,
///         the way <c>SceneQuestions</c> and <c>LibraryQuestions</c> ask for theirs.
///     </para>
///     <para>
///         ⚠ <b>Not here: Save All.</b> It writes every document and then applies the settings window's,
///         which pushes the content target and retitles the window — the application's — and the close
///         prompt's Save is the same method. It stays on the root with closing.
///     </para>
/// </remarks>
static class SceneFileQuestions {
    /// <summary>Registers the three lines and their chords.</summary>
    /// <param name="context">The scene module's context.</param>
    /// <param name="files">What each line ends in.</param>
    public static void Register(PluginContext context, SceneFiles files) {
        var shell = context.Shell;
        var project = context.Services.Require<EditorProject>();
        var deferred = context.Services.Require<Deferred>();

        context.Services.TryGet(out INativeDialogs? dialogs);

        // Enabled only when there is something to write, so the menu item greys itself out from the
        // document's own dirty signal rather than from anything here deciding when.
        context.AddCommand(
            new EditorCommand("file.save", EditorStrings.CommandSave, () => files.Save()) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => files.Active.Document.HasUnsavedChanges.Value
            }
        );

        // ⚠ **Ctrl+S is `document.save`'s and not `file.save`'s (#1494).** Every asset tab answers
        // `document.save` on the route — `IWorkspace.Show` installs `DocumentCommands` on its panel —
        // and the dispatcher asks the focus before it asks this registry, so the chord saves the
        // document whose tab the focus is in. This registration is the fallback for a focus in no
        // document's panel, and there it is the scene's save, which is what Ctrl+S always did.
        context.AddCommand(
            new EditorCommand(DocumentCommands.Save, EditorStrings.CommandDocumentSave, () => files.Save()) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => files.Active.Document.HasUnsavedChanges.Value
            }
        );

        context.AddCommand(
            new EditorCommand(
                "file.new-scene",
                EditorStrings.CommandFileNewScene,
                () => Discarding.Confirm(
                    shell,
                    files.Active.Document.HasUnsavedChanges.Value,
                    "Discard unsaved changes?",
                    Path.GetFileName(files.Active.Writes) + " has changes that have not been written.",
                    files.New,
                    "Discard"
                )
            ) {
                Category = EditorStrings.CategoryFile
            }
        );

        context.AddCommand(
            new EditorCommand("file.open-scene", EditorStrings.CommandFileOpenScene, () => Open(shell, project, files, dialogs, deferred)) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => dialogs is not null
            }
        );

        context.AddCommand(
            new EditorCommand("file.save-as", EditorStrings.CommandFileSaveAs, () => SaveAs(shell, project, files, dialogs, deferred)) {
                Category = EditorStrings.CategoryFile,
                Enablement = () => dialogs is not null
            }
        );

        context.AddDefaultBinding(DocumentCommands.Save, new KeyChord(InputKey.S, ModifierKeys.Control));
        context.AddDefaultBinding("file.new-scene", new KeyChord(InputKey.N, ModifierKeys.Control));
        context.AddDefaultBinding("file.open-scene", new KeyChord(InputKey.O, ModifierKeys.Control));
        context.AddDefaultBinding("file.save-as", new KeyChord(InputKey.S, ModifierKeys.Control | ModifierKeys.Shift));
    }

    /// <summary>Asks whether to discard, then which file, and loads it over the active scene.</summary>
    static void Open(EditorShell shell, EditorProject project, SceneFiles files, INativeDialogs? dialogs, Deferred deferred) {
        if (dialogs is null) {
            return;
        }

        Discarding.Confirm(
            shell,
            files.Active.Document.HasUnsavedChanges.Value,
            "Discard unsaved changes?",
            Path.GetFileName(files.Active.Writes) + " has changes that have not been written.",
            () => deferred.When(
                dialogs.OpenFileAsync(
                    new FileDialogOptions {
                        Title = "Open Scene",
                        InitialDirectory = project.Paths.Assets,
                        Filters = [new FileFilter("Vixen scene", SceneSerializer.Extension.TrimStart('.'))]
                    }
                ),
                path => {
                    if (path is not null) {
                        files.Load(path);
                    }
                },
                failure => shell.Notifications.Show("Could not open the scene", NotificationSeverity.Error, failure.Message)
            ),
            "Discard"
        );
    }

    /// <summary>Asks where to put the active scene, and writes it there from now on.</summary>
    static void SaveAs(EditorShell shell, EditorProject project, SceneFiles files, INativeDialogs? dialogs, Deferred deferred) {
        if (dialogs is null) {
            return;
        }

        var writes = files.Active.Writes;

        deferred.When(
            dialogs.SaveFileAsync(
                new FileDialogOptions {
                    Title = "Save Scene As",
                    InitialDirectory = Path.GetDirectoryName(writes) ?? project.Paths.Assets,
                    SuggestedFileName = Path.GetFileName(writes),
                    Filters = [new FileFilter("Vixen scene", SceneSerializer.Extension.TrimStart('.'))]
                }
            ),
            path => {
                if (path is not null) {
                    files.SaveAs(path);
                }
            },
            failure => shell.Notifications.Show("Could not save the scene", NotificationSeverity.Error, failure.Message)
        );
    }
}
