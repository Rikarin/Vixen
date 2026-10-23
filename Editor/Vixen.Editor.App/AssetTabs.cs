// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The asset tabs: which document each one shows, and how one is opened, reopened and closed.</summary>
/// <param name="shell">The workspace the tabs are panels of.</param>
/// <param name="project">The project the documents are open in.</param>
/// <param name="documents">Which kind opens a file.</param>
/// <param name="views">Which view shows a document, and where a new tab goes.</param>
/// <param name="sceneEditing">The scenes the editor itself has open.</param>
/// <param name="scenes">Those scenes with the files they write, asked on every call.</param>
/// <param name="restore">Whether a restored arrangement may reopen documents, asked on every call.</param>
/// <remarks>
///     <para>
///         ⚠ <b>A panel model in the application assembly, and not on <c>IWorkspace</c></b> (#1294).
///         <c>Vixen.Editor.Ui</c> refuses <c>Vixen.Editor.Core</c>, and resolving a tab's document is
///         Core's words — <c>IDocumentHost.TryOpen</c>, <c>EditorProject.TryGetDocument</c> and the
///         editor's open scenes — so the workspace takes <see cref="Open" /> as a delegate and this
///         holds what used to be two parked fields on <c>EditorApplication</c>.
///     </para>
///     <para>
///         The preferences and the scene list are delegates rather than values because both are
///         replaced under it: the settings window's Revert swaps the preferences object, and the
///         scene list is rebuilt whenever a scene opens or closes.
///     </para>
/// </remarks>
internal sealed class AssetTabs(
    EditorShell shell,
    EditorProject project,
    DocumentHost documents,
    EditorWorkspace views,
    SceneEditing sceneEditing,
    Func<IReadOnlyList<EditorScene>> scenes,
    Func<bool> restore
) {
    /// <summary>Every asset panel registered so far; a registration is never taken back.</summary>
    readonly HashSet<string> assetPanels = new(StringComparer.Ordinal);

    /// <summary>The document each asset tab was last built over, which is what closing it after a move reads.</summary>
    readonly Dictionary<string, EditorDocument> tabDocuments = new(StringComparer.Ordinal);

    /// <summary>Opens an asset in whatever editor claims it, in a panel of its own.</summary>
    /// <param name="asset">Which asset.</param>
    /// <returns>Whether anything claims it; <see langword="false" /> has said so in a notification.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A panel per document, registered on demand and named after the asset's GUID.</b> A
    ///         path would be shorter and would make moving the file leave a panel nobody can reopen;
    ///         the GUID is the identity precisely so that it does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Registered once and reopened afterwards.</b> The workspace refuses a second
    ///         registration under one id, and the document is already found by
    ///         <c>IDocumentHost.TryOpen</c> — so the second double-click brings the tab forward
    ///         rather than building a second view over the same undo stack.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A file nothing claims is a notification, not silence.</b> Double-clicking a
    ///         <c>.txt</c> and having nothing at all happen reads as a broken editor; saying that no
    ///         editor claims it is one sentence and is true.
    ///     </para>
    /// </remarks>
    internal bool Open(AssetId asset) {
        if (!TryGetOpenScene(asset, out var document) && !documents.TryOpen(asset, out document)) {
            shell.Notifications.Show("No editor claims that file.");

            return false;
        }

        var id = EditorWorkspace.PanelOf(asset);

        if (assetPanels.Add(id)) {
            var title = document.Title.Peek();

            shell.RegisterPanel(
                new PanelDescriptor(
                    id,
                    new StringId("editor.panel." + id, title),
                    panel => {
                        // ⚠ The view by the document's type and not by the file's extension (#1294): the
                        // kind and the view are two registrations now, joined by the type the kind declares.
                        if ((TryGetOpenScene(asset, out var open) || project.TryGetDocument(asset, out open))
                            && views.TryGetView(open, out _)) {
                            tabDocuments[id] = open;
                            views.Show(open, panel);

                            // ⚠ #1494: the panel answers the route's three ids now, and the close
                            // `document.close` raises on it is this tab's, which nothing in the
                            // editor heard until now (Revert asking first is `Reverting`, below the
                            // workspace's construction). ⚠ Only a document's close: a quit raised at
                            // the focus bubbles through here too, and closing the tab under it would
                            // answer a question the window asked.
                            //
                            // ⚠ #1501: a dirty document is asked about first, through the framework's
                            // prompt, which refuses the request so the handler below leaves the tab
                            // alone. Installed first for that reason — the handlers run in order. The
                            // editor's own scenes are not asked about: closing a tab over one hides
                            // nothing, because the Scene view still shows it and still saves it.
                            //
                            // ⚠ #1504: the document form, which answers only a document's close. The
                            // window form would answer a quit raised with the focus in this tab by
                            // shutting the tab, and nothing would retry the quit.
                            if (!sceneEditing.Scenes.Any(scene => ReferenceEquals(scene, open))) {
                                DocumentClosePrompt.InstallOnDocument(panel, shell.Dialogs, () => CloseDocumentTab(id, open));
                            }

                            panel.AddHandler<CloseRequestEvent>((_, args) => {
                                if (args.Reason == UiCloseReason.DocumentClosed && !args.IsCancelled) {
                                    shell.Workspace.Close(id);
                                }
                            });
                        }
                    }
                ) {
                    // The other half of the builder: a closed tab shows nothing, so nothing may go on
                    // saying what it showed — `CloseTabsTheSceneLeft` and `EditedElsewhere` both read
                    // this map as "what the user can see".
                    Closed = () => tabDocuments.Remove(id)
                }
            );
        }

        views.Place(id);
        shell.Workspace.Open(id);
        project.Activate(document);

        return true;
    }

    /// <summary>Closes an asset tab the close prompt allowed to go, and the document it showed.</summary>
    /// <param name="id">The tab's panel id.</param>
    /// <param name="document">What it showed.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The document closes with the tab, on this path only</b> (#1501). The prompt calls
    ///         this after Save or Don't Save, and a document left in the project after Don't Save
    ///         would still hold the edits the user just discarded — with no tab showing them, and
    ///         Save All and the quit prompt still offering to write them. A clean tab closed without
    ///         a question keeps its document, so reopening it finds the same undo history.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Called frames after the request</b>, when the dialog is answered, so it checks
    ///         rather than assumes: the tab may have been closed and the document closed some other
    ///         way while the question was on screen.
    ///     </para>
    /// </remarks>
    void CloseDocumentTab(string id, EditorDocument document) {
        shell.Workspace.Close(id);

        if (document.IsOpen) {
            document.Close();
        }
    }

    /// <summary>Finds the scene this editor already has open over an asset's file.</summary>
    /// <param name="asset">Which asset.</param>
    /// <param name="document">The scene editing that file.</param>
    /// <returns>Whether one is open.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>By file, because these scenes have no asset id to be found by</b> (#1395). The
    ///         main scene and every additively opened one are built as
    ///         <c>new SceneDocument(project, world, AssetId.Empty, …)</c> — the path can change under
    ///         them with Save As, so it is held by the writer rather than fixed as an identity — and
    ///         <c>IDocumentHost.TryOpen</c>'s guard against a second document over one file is
    ///         keyed on the id. So double-clicking <c>Main.vxscene</c> in the browser built a second
    ///         <c>SceneDocument</c> over the file the Scene view writes: its own world, its own undo
    ///         history, and a writer of its own on the same path, whichever saved last winning.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The writer's path and not <see cref="EditorScene.Path" />.</b> Save As and Open
    ///         Scene move the main scene's writer and leave the record's path where it was, so the
    ///         record can name a file this document no longer writes.
    ///     </para>
    ///     <para>
    ///         Found, the document tab is two tabs over the editor's own scene — the arrangement
    ///         <c>SceneEditorFactory.CreateView</c> already describes as "two tabs over one document,
    ///         not two documents" — so its Compiled pane compiles what the Scene view edits.
    ///     </para>
    /// </remarks>
    bool TryGetOpenScene(AssetId asset, [NotNullWhen(true)] out EditorDocument? document) {
        document = null;

        if (!project.Assets.TryGetByGuid(asset, out var entry) || entry.IsFolder) {
            return false;
        }

        var file = project.Paths.Absolute(entry.Path);

        foreach (var open in scenes()) {
            if (open.Document.IsOpen && SameFile(open.Writes, file)) {
                document = open.Document;

                return true;
            }
        }

        return false;
    }

    /// <summary>Whether two paths name one file, on this platform's terms.</summary>
    internal static bool SameFile(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase
        );

    /// <summary>Finds a document other than <paramref name="scene" /> that edits a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="scene">The scene asking, which does not count; <see langword="null" /> for none.</param>
    /// <returns>The document, or <see langword="null" /> when no other one edits it.</returns>
    /// <remarks>
    ///     <para>
    ///         Both kinds: the editor's own scenes, found by the file their writer names, and a
    ///         document an asset tab opened, found by its asset's file. Pointing a second one at a
    ///         file either of them holds is the arrangement #1395 removed — two undo histories and two
    ///         writers over one set of bytes, whichever saved last winning.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A clean document no tab shows is closed here rather than counted.</b> Closing an
    ///         asset tab does not close its document — the project keeps it, so reopening the tab
    ///         finds the same undo history — so counting every entry in
    ///         <see cref="EditorProject.Documents" /> refused a file for the rest of the session once
    ///         it had been opened in a tab at all, with a message telling the user to close something
    ///         they could no longer see. A clean one that nothing shows has nothing to lose, and is
    ///         released; one with unsaved changes is still the holder, and
    ///         <see cref="RefuseSecondDocument" /> says how to clear it.
    ///     </para>
    /// </remarks>
    internal EditorDocument? EditedElsewhere(string path, SceneDocument? scene) {
        foreach (var open in scenes()) {
            if (!ReferenceEquals(open.Document, scene) && open.Document.IsOpen && SameFile(open.Writes, path)) {
                return open.Document;
            }
        }

        foreach (var document in project.Documents.ToArray()) {
            if (ReferenceEquals(document, scene)
                || document.Asset.IsEmpty
                || !project.Assets.TryGetByGuid(document.Asset, out var entry)
                || entry.IsFolder
                || !SameFile(project.Paths.Absolute(entry.Path), path)) {
                continue;
            }

            if (!InATab(document) && !document.HasUnsavedChanges.Peek()) {
                document.Close();

                continue;
            }

            return document;
        }

        return null;
    }

    /// <summary>Whether an asset tab the user can see is showing a document.</summary>
    bool InATab(EditorDocument document) =>
        !document.Asset.IsEmpty && shell.Workspace.IsOpen(EditorWorkspace.PanelOf(document.Asset));

    /// <summary>Says why a scene was not pointed at a file another document already edits.</summary>
    /// <remarks>
    ///     Each answer names something the user can do from where they are: a tab to close, or unsaved
    ///     changes to save — a document with changes and no tab is invisible, and "close that first"
    ///     named nothing that could be closed.
    /// </remarks>
    internal void RefuseSecondDocument(string path, EditorDocument holder, string title) {
        var file = Path.GetFileName(path);
        var name = holder.Title.Peek();

        shell.Notifications.Show(
            title,
            NotificationSeverity.Warning,
            InATab(holder) || sceneEditing.Scenes.Any(open => ReferenceEquals(open, holder))
                ? $"{file} is already open as '{name}'. Close that first, so that one document writes the file."
                : $"{file} has unsaved changes in '{name}', whose tab is closed. Save them, or reopen it and revert, so that one document writes the file."
        );
    }

    /// <summary>Closes every asset tab showing an editor scene that no longer writes that asset's file.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A document tab is bound to the file it was opened on</b> (#1416). It is registered
    ///         once under that asset's GUID with the title the file had, and its builder decides what to
    ///         show on every rebuild by asking <see cref="TryGetOpenScene" />, which matches the file
    ///         the scene writes <i>now</i>. So after Save As or Open Scene moved the scene, a tab named
    ///         after the old file kept showing a scene that writes another one — until something rebuilt
    ///         it, when the same tab lost the scene. Which of the two you saw depended on when the panel
    ///         was last built.
    ///     </para>
    ///     <para>
    ///         Closing it is the answer that depends on nothing: the scene is still in the Scene view,
    ///         the file it left is on disk unchanged, and opening that file again is an ordinary
    ///         document of its own, because nothing edits it any more.
    ///     </para>
    /// </remarks>
    internal void CloseTabsTheSceneLeft() {
        foreach (var (id, shown) in tabDocuments.ToArray()) {
            if (shown is not SceneDocument moved || !sceneEditing.Scenes.Contains(moved)) {
                continue;
            }

            if (EditorWorkspace.TryReadPanel(id, out var asset) && TryGetOpenScene(asset, out var now) && ReferenceEquals(now, moved)) {
                continue;
            }

            tabDocuments.Remove(id);
            shell.Workspace.Close(id);
        }
    }

    /// <summary>Reopens the document a restored arrangement names, so the panel can be built.</summary>
    /// <returns>Whether the id was an asset panel this project can open.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 20's A6, and it is one function rather than a list in the layout file.</b> An
    ///         asset editor's panel is registered on demand and named after the asset's GUID, so a
    ///         saved arrangement holding <c>asset.9e8a44c9…</c> named a panel nothing had declared —
    ///         the tab came back missing and the id stayed in the file. <c>DockingWorkspace.Resolve</c>
    ///         asks this before giving up, and <see cref="Open" /> registers the panel as a
    ///         side-effect of opening the document, which is the same path a double-click takes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Off by preference, because reopening six asset editors costs six documents.</b>
    ///         <see cref="EditorPreferences.RestoreOpenDocuments" /> is on by default — "the editor
    ///         comes back how I left it" is what doc 20 asks for — and somebody who wants a clean
    ///         start can say so rather than deleting a layout file.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An asset that has gone answers false rather than throwing.</b> A layout outlives
    ///         the files it names — a branch switch is enough — and the id is left in the arrangement
    ///         on the same terms a plugin's unregistered panel is.
    ///     </para>
    /// </remarks>
    internal bool ReopenDocument(string id) {
        if (!restore()
            || !EditorWorkspace.TryReadPanel(id, out var asset)
            || !project.Assets.TryGetByGuid(asset, out _)) {
            return false;
        }

        Open(asset);
        return assetPanels.Contains(id);
    }
}
