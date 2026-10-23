// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Engine.Transforms;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>One scene open in the editor: its document, its file, and its settings.</summary>
/// <param name="Document">The document.</param>
/// <param name="Path">Where it was opened from, absolute.</param>
/// <remarks>
///     ⚠ <b>The path is here rather than on the document, because a document does not know where
///     it is written</b> — it has an <c>ISceneWriter</c>, which is deliberately an interface so
///     that a test can write into memory. The editor is what knows about files.
/// </remarks>
sealed record EditorScene(SceneDocument Document, string Path) {
    /// <summary>The file the scene writes now, which is not always <see cref="Path" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Save As and Open Scene move the writer and leave <see cref="Path" /> where it
    ///     was</b>, so anything asking "is this file open" asks this and not that (#1416).
    /// </remarks>
    public string Writes => Document.Writer is SceneFileWriter writer ? writer.Path : Path;

    /// <summary>Its world settings.</summary>
    public WorldSettings Settings { get; set; } = new();
}

/// <summary>The file behind each open scene, and the verbs that point the active one at another file.</summary>
/// <remarks>
///     <para>
///         <b>The main scene's file as a service (#1296).</b> New, Open and Save As Scene acted on the
///         application's <c>scene</c>, <c>scenePath</c> and <c>sceneFiles</c> and on <see cref="AssetTabs" />'
///         file questions, which is why they could not leave <c>EditorParity.cs</c>. The record of each
///         open scene's file is this class's now, the verbs are its methods, and <c>SceneModule</c>
///         registers the File menu's lines over them and asks the questions in front of them.
///     </para>
///     <para>
///         ⚠ <b><c>scenePath</c> is gone, because it duplicated the writer.</b> It was set by Save As,
///         Open Scene and every switch of the active scene — the last to the path the scene was
///         <i>opened</i> from, so after Save As and a switch away and back, Save's notice and Save As's
///         suggested name named the file the scene no longer writes. What the scene writes is
///         <see cref="EditorScene.Writes" />, and it is asked.
///     </para>
///     <para>
///         ⚠ <b>Which scenes are open, in what order, and which is active are <see cref="ISceneEditing" />'s
///         (#1290)</b> — this is only the file beside each entry. Every verb acts on
///         <see cref="ISceneEditing.Active" />, which is the scene the application follows.
///     </para>
/// </remarks>
sealed class SceneFiles {
    readonly Dictionary<SceneDocument, EditorScene> files = [];
    readonly EditorShell shell;
    readonly EditorProject project;
    readonly SceneEditing editing;
    readonly AssetTabs tabs;

    /// <summary>Records files for the scenes a service keeps.</summary>
    /// <param name="shell">The shell, whose notifications say what happened.</param>
    /// <param name="project">The project, rescanned after a scene is written somewhere new.</param>
    /// <param name="editing">The open scenes, and which is active.</param>
    /// <param name="tabs">The asset tabs, whose documents a scene may not be pointed over.</param>
    public SceneFiles(EditorShell shell, EditorProject project, SceneEditing editing, AssetTabs tabs) {
        this.shell = shell;
        this.project = project;
        this.editing = editing;
        this.tabs = tabs;
    }

    /// <summary>Every open scene's file, in the service's order.</summary>
    public IReadOnlyList<EditorScene> Scenes => [.. editing.Scenes.Select(document => files[document])];

    /// <summary>The active scene's file.</summary>
    public EditorScene Active => Of(editing.Active);

    /// <summary>A scene's file, or one read off its writer when the scene was never recorded.</summary>
    /// <param name="document">The scene.</param>
    /// <returns>Its record.</returns>
    public EditorScene Of(SceneDocument document) =>
        files.TryGetValue(document, out var open)
            ? open
            : new(document, document.Writer is SceneFileWriter writer ? writer.Path : string.Empty);

    /// <summary>Records the file a scene was opened from, and reads the world settings beside it.</summary>
    /// <param name="document">The scene.</param>
    /// <param name="path">Its file.</param>
    /// <returns>The record.</returns>
    public EditorScene Track(SceneDocument document, string path) =>
        files[document] = new(document, path) { Settings = WorldSettings.Load(path) };

    /// <summary>Forgets the file of every scene the service no longer has open.</summary>
    public void ForgetClosed() {
        foreach (var closed in files.Keys.Where(document => !editing.Scenes.Contains(document)).ToList()) {
            files.Remove(closed);
        }
    }

    /// <summary>Empties the active scene and starts again with a root, as one clean, history-less state.</summary>
    /// <remarks>Without asking: whether to discard unsaved changes is the caller's question, put before the call.</remarks>
    public void New() {
        var scene = editing.Active;

        scene.Delete([.. scene.Roots]);
        scene.Stack.Clear();
        scene.Stack.MarkClean();

        scene.Add("Scene Root", LocalTransform.Identity);
        editing.Restructured();
    }

    /// <summary>Loads a scene file over the active scene, which then writes there.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Whether it was loaded.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Into the same document, and the writer moves with it.</b> The scene panel, the gizmo,
    ///         the inspector and the picker all hold this document; swapping the object would leave four
    ///         panels looking at the old one until each was rebuilt.
    ///     </para>
    ///     <para>
    ///         Refused, like <see cref="SaveAs" />, onto a file another document edits, and it closes the
    ///         tab named after the file the scene leaves. Without asking, as <see cref="New" />.
    ///     </para>
    /// </remarks>
    public bool Load(string path) {
        var scene = editing.Active;

        if (tabs.EditedElsewhere(path, scene) is { } holder) {
            tabs.RefuseSecondDocument(path, holder, "Could not open the scene");

            return false;
        }

        try {
            scene.Selection.Clear();
            scene.Delete([.. scene.Roots]);

            if (SceneSerializer.Load(scene, path) == 0) {
                shell.Notifications.Show(
                    "Nothing was loaded",
                    NotificationSeverity.Warning,
                    System.IO.Path.GetFileName(path) + " has no entities in it."
                );
            }

            scene.Writer = new SceneFileWriter(path);
            scene.SetTitle(System.IO.Path.GetFileNameWithoutExtension(path));

            scene.Stack.Clear();
            scene.Stack.MarkClean();

            editing.Restructured();
            tabs.CloseTabsTheSceneLeft();
            shell.Notifications.Success(System.IO.Path.GetFileName(path));

            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            shell.Notifications.Show("Could not open the scene", NotificationSeverity.Error, exception.Message);

            return false;
        }
    }

    /// <summary>Writes the active scene to another file, and writes it there from now on.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Whether the scene was pointed there; the write itself reports its own failure.</returns>
    /// <remarks>
    ///     ⚠ <b>Refused onto a file another document edits</b> — see <see cref="AssetTabs.EditedElsewhere" />
    ///     for which ones count — and it closes any tab named after the file the scene leaves (#1416), see
    ///     <see cref="AssetTabs.CloseTabsTheSceneLeft" />.
    /// </remarks>
    public bool SaveAs(string path) {
        var scene = editing.Active;

        if (tabs.EditedElsewhere(path, scene) is { } holder) {
            tabs.RefuseSecondDocument(path, holder, "Could not save the scene");

            return false;
        }

        scene.Writer = new SceneFileWriter(path);
        scene.SetTitle(System.IO.Path.GetFileNameWithoutExtension(path));

        Save();
        project.Assets.Scan();
        tabs.CloseTabsTheSceneLeft();

        return true;
    }

    /// <summary>Writes the active scene, and says so.</summary>
    /// <returns>Whether it was written.</returns>
    /// <remarks>
    ///     ⚠ <b>A failed save is a notification and not an exception.</b> A full disk or a read-only
    ///     working tree is an ordinary thing to meet, and an editor that took the process down with
    ///     the unsaved work still in it would be the worst possible response to it. The sidecars go with
    ///     it: whoever owns one subscribes to <c>EditorDocument.Saved</c>, which throws through, so a
    ///     sidecar that could not be written is a failed save rather than a silent half of one.
    /// </remarks>
    public bool Save() {
        try {
            editing.Active.Save();
            shell.Notifications.Success(System.IO.Path.GetFileName(Active.Writes));

            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            shell.Notifications.Show("Could not save the scene", NotificationSeverity.Error, exception.Message);

            return false;
        }
    }
}
