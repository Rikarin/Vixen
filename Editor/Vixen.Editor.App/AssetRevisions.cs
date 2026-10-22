// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.HotReload;

namespace Vixen.Editor.App;

/// <summary>The revisions panel: the selected asset's history, the diff under the picked row, and Restore.</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten (#1505)</b> — this was the panel half of <c>EditorSourceControl.cs</c>, a
///         partial of <c>EditorApplication</c>; <see cref="SourceControlModule" /> builds it over the
///         published <see cref="ISourceControl" />.
///     </para>
///     <para>
///         <b>Diff and history are one panel and not two, which is the finding that made this worth doing
///         at all.</b> A history row's whole value is what a person does with it — look at what that commit
///         changed, or take that version back — so a history list with no diff under it is a list of
///         sentences, and a diff with no list is a diff of the one thing you already have.
///     </para>
///     <para>
///         ⚠ <b>The panel holds no provider and starts no process.</b> Every git invocation is the service's,
///         on the pool, and its answer comes back through this panel's own <see cref="Deferred" />, pumped in
///         <see cref="Update" /> on the frame thread; the view is handed answers.
///     </para>
/// </remarks>
sealed class AssetRevisions {
    /// <summary>The panel's id.</summary>
    internal const string PanelId = "revisions";

    /// <summary>How far back the panel asks, which is a panel and not an archive.</summary>
    /// <remarks>
    ///     ⚠ <b>A bound rather than everything, and this repository is the argument.</b> Files here have
    ///     four figures of commits behind them; a panel that asked for all of them would spend the time and
    ///     draw a list nobody scrolls to the end of. What a person is looking for — when did this break,
    ///     who last touched it — is in the newest few dozen or it is a question for a git client.
    /// </remarks>
    internal const int RevisionLimit = 50;

    readonly EditorShell shell;
    readonly ISourceControl sourceControl;
    readonly EditorProject project;
    readonly HotReloadHost hotReload;
    readonly Deferred deferred = new();

    /// <summary>The selection the panel last showed, so a change to it is noticed once.</summary>
    readonly List<AssetId> shown = [];

    /// <summary>Builds the panel over the service.</summary>
    /// <param name="shell">The shell whose workspace opens the panel and whose dialogs ask.</param>
    /// <param name="sourceControl">The published service.</param>
    /// <param name="project">The project whose selection the panel follows.</param>
    /// <param name="hotReload">The reload host the view is mounted through.</param>
    public AssetRevisions(EditorShell shell, ISourceControl sourceControl, EditorProject project, HotReloadHost hotReload) {
        this.shell = shell;
        this.sourceControl = sourceControl;
        this.project = project;
        this.hotReload = hotReload;
    }

    /// <summary>The view, while the panel is open; null otherwise.</summary>
    public RevisionsView? View { get; private set; }

    /// <summary>The panel's registration.</summary>
    /// <returns>A descriptor whose factory mounts the view.</returns>
    public PanelDescriptor Descriptor() =>
        new(
            PanelId,
            EditorStrings.PanelRevisions,
            panel => {
                // The view lays out its own toolbar, list and diff pane; a panel that scrolled would put a
                // scroll view around the one the list already is.
                panel.Scrolls = false;

                // ⚠ Mounted through the reload host rather than built directly, which is what the undo
                // history's panel does and for the same reason: a panel built past `HotReloadHost` keeps
                // whatever `Build` body it was constructed with until the editor restarts.
                var view = hotReload.Mount<RevisionsView>(panel);

                view.Picked += revision => LoadDiff(view, revision);
                view.Restoring += revision => Restore(view, revision);

                View = view;
                Show();
            }
        ) {
            Closed = () => View = null
        };

    /// <summary>Opens the panel on whatever is selected — what Show History is a name for.</summary>
    /// <remarks>
    ///     ⚠ <b>Opening it is enough the first time, because the factory asks.</b> The factory calls
    ///     <see cref="Show" /> itself; a panel already open on a different asset does not run it again, so
    ///     this asks as well.
    /// </remarks>
    public void Open() {
        shell.Workspace.Open(PanelId);
        Show();
    }

    /// <summary>Applies answers that came back, and follows the selection while the panel is open.</summary>
    /// <remarks>
    ///     ⚠ <b>On a change of selection rather than on a timer.</b> A history is a <c>git log</c> per asset,
    ///     and a panel that asked for one every frame would run a process every frame. This compares the
    ///     selection with what was last shown, which is a list compare and no process.
    /// </remarks>
    public void Update() {
        deferred.Pump();

        if (View is not null && !shown.SequenceEqual(project.Selection)) {
            Show();
        }
    }

    /// <summary>Shows the selected asset's history, or says why there is none to show.</summary>
    /// <remarks>
    ///     ⚠ <b>Exactly one asset, and a multi-selection says so rather than picking the first.</b> A panel
    ///     that showed the history of whichever file happened to be first in a selection of forty would be
    ///     a panel whose answer is right by accident — and this one has a Restore button on it.
    /// </remarks>
    public void Show() {
        shown.Clear();
        shown.AddRange(project.Selection);

        if (View is not { } view) {
            return;
        }

        if (sourceControl.Name is null) {
            view.Clear(
                sourceControl.IsSought
                    ? "This project is not in a working tree, so there is nothing to show."
                    : EditorStrings.RevisionsPick.Text
            );

            return;
        }

        if (Selected() is not { } path) {
            view.Clear(EditorStrings.RevisionsPick.Text);
            return;
        }

        deferred.When(
            sourceControl.HistoryAsync(path, RevisionLimit),
            found => view.Show(path, found),
            failure => view.Clear($"{path} — {failure.Message}")
        );
    }

    /// <summary>The one selected asset that is a file, or null.</summary>
    string? Selected() {
        string? only = null;

        foreach (var asset in project.Selection) {
            if (!project.Assets.TryGetByGuid(asset, out var entry) || entry.IsFolder) {
                continue;
            }

            if (only is not null) {
                return null;
            }

            only = entry.Path;
        }

        return only;
    }

    void LoadDiff(RevisionsView view, SourceControlRevision revision) {
        if (view.Path.Length == 0) {
            return;
        }

        deferred.When(
            sourceControl.DiffAsync(view.Path, revision.Id),
            view.ShowDiff,
            failure => view.ShowDiff(new SourceControlDiff(false, failure.Message))
        );
    }

    /// <summary>Writes an old version of the shown asset into the working tree, after asking.</summary>
    /// <remarks>
    ///     ⚠ <b>Asked, on Revert's terms and for the same reason.</b> This overwrites a file on disk with
    ///     bytes from a commit, and the undo stack holds documents rather than files — so it cannot be taken
    ///     back from the editor. What makes it safe rather than merely warned about is that the result is an
    ///     ordinary uncommitted change, which Revert then undoes.
    /// </remarks>
    void Restore(RevisionsView view, SourceControlRevision revision) {
        if (sourceControl.Name is not { } name || view.Path is not { Length: > 0 } path) {
            return;
        }

        _ = Ask();

        async Task Ask() {
            var asked = await shell.Dialogs.ConfirmAsync(
                    $"Restore '{System.IO.Path.GetFileName(path)}' from {revision.ShortId}?",
                    $"{name} will write that version over the file in the working tree. "
                    + "The change is left uncommitted, so Revert to Source Control undoes it."
                    + Environment.NewLine
                    + revision.Summary,
                    "Restore",
                    danger: true
                )
                .ConfigureAwait(true);

            if (asked) {
                sourceControl.Restore(path, revision);
            }
        }
    }
}
