// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Editor.Ui;

namespace Vixen.Editor.App;

/// <summary>Doc 20 § B7: the project's source-control status, and the one verb that changes it.</summary>
/// <remarks>
///     <para>
///         <b>The row said "status column in the browser, and check-out/revert/diff/history over a
///         provider interface with a git implementation", and nothing in the tree spelled any of
///         it.</b> This is the seam, a git provider, the sweep that keeps an answer, and the four
///         verbs: status, revert, and — through <c>RevisionsView</c> — history and diff.
///     </para>
///     <para>
///         ⚠ <b>The last two arrived as one panel rather than as two verbs, which is the finding
///         that made them worth building at all.</b> A history row is only worth clicking for what
///         it shows and what it can put back; a list of commit messages with nothing under it is a
///         list of sentences, and a diff of the one version you already have answers nothing. So
///         "Diff" is not a command of its own — it is what the panel draws under whichever row is
///         picked.
///     </para>
///     <para>
///         ⚠ <b>Nothing here runs until the Project panel is open, and the detection is part of the
///         sweep rather than of startup.</b> Asking git "am I in a working tree" costs a process,
///         and an editor that paid for it on every launch would be paying for a column nobody had
///         looked at yet.
///     </para>
///     <para>
///         ⚠ <b>Swept, rather than watched or polled.</b> The issue this closes worked out why: a
///         provider answering from a cache it does not invalidate is wrong exactly when somebody has
///         checked something out underneath the editor, and a timer is a guess about how often that
///         happens. So the sweep is taken on the two events that already mean "the project changed
///         under us" — the file watcher draining, and the user asking for a refresh — plus once when
///         the panel first appears.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>What the browser draws, folded so a folder speaks for what is under it.</summary>
    readonly SourceControlStatuses statuses = new();

    /// <summary>The provider, once one has been found, or null when there is none.</summary>
    ISourceControl? sourceControl;

    /// <summary>Whether a sweep has been asked for and not yet answered.</summary>
    /// <remarks>
    ///     ⚠ Not a lock: a second sweep is not dangerous, it is a second <c>git status</c> over the
    ///     whole working tree, and the watcher can fire several times a second while somebody is
    ///     saving from another program.
    /// </remarks>
    bool sweeping;

    /// <summary>Whether the project has been looked at yet.</summary>
    bool sought;

    /// <summary>How far back the revisions panel asks, which is a panel and not an archive.</summary>
    /// <remarks>
    ///     ⚠ <b>A bound rather than everything, and this repository is the argument.</b> Files here
    ///     have four figures of commits behind them; a panel that asked for all of them would spend
    ///     the time and draw a list nobody scrolls to the end of. What a person is looking for —
    ///     when did this break, who last touched it — is in the newest few dozen or it is a question
    ///     for a git client.
    /// </remarks>
    internal const int RevisionLimit = 50;

    /// <summary>The revisions panel, while it is open.</summary>
    RevisionsView? revisionsView;

    /// <summary>What the harness reads, because there is no other way to see a status from outside.</summary>
    internal SourceControlStatuses SourceControl => statuses;

    /// <summary>The revisions panel, for the harness. Null unless it is open.</summary>
    internal RevisionsView? Revisions => revisionsView;

    /// <summary>Registers the panel doc 20 § B7's other two verbs needed and did not have.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Diff and history are one panel and not two, which is the finding that made this
    ///         worth doing at all.</b> A history row's whole value is what a person does with it —
    ///         look at what that commit changed, or take that version back — so a history list with
    ///         no diff under it is a list of sentences, and a diff with no list is a diff of the one
    ///         thing you already have. Building either alone is building this twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The panel holds no provider and starts no process.</b> Every git invocation is
    ///         seconds on a cold cache and goes through <c>deferred</c> onto the pool; the view is
    ///         handed answers. That is what makes it testable without a repository, which is the
    ///         same argument <c>UseSourceControl</c> already makes for the column.
    ///     </para>
    /// </remarks>
    void RevisionsPanel() =>
        Shell.RegisterPanel(
            new PanelDescriptor(
                RevisionsPanelId,
                EditorStrings.PanelRevisions,
                panel => {
                    // The view lays out its own toolbar, list and diff pane; a panel that scrolled
                    // would put a scroll view around the one the list already is.
                    panel.Scrolls = false;

                    // ⚠ Mounted through the reload host rather than built directly, which is what
                    // the undo history's panel does one file over and for the same reason: a panel
                    // built past `HotReloadHost` keeps whatever `Build` body it was constructed with
                    // until the editor restarts, so the markup would be authored and not developed.
                    var view = hotReload.Mount<RevisionsView>(panel);

                    view.Picked += revision => LoadDiff(view, revision);
                    view.Restoring += revision => RestoreRevision(view, revision);

                    revisionsView = view;
                    ShowRevisions();
                }
            ) {
                Closed = () => revisionsView = null
            }
        );

    /// <summary>The revisions panel's id.</summary>
    internal const string RevisionsPanelId = "revisions";

    /// <summary>Opens the revisions panel on whatever is selected.</summary>
    /// <remarks>
    ///     ⚠ <b>Opening it is enough, because the factory asks.</b> The panel's factory calls
    ///     <see cref="ShowRevisions" /> itself, so a command that also asked would run two
    ///     <c>git log</c>s for one click — and a command that only asked would do nothing at all
    ///     when the panel was not open, which is the state it is in the first time anybody uses it.
    /// </remarks>
    void ShowRevisionsPanel() {
        Shell.Workspace.Open(RevisionsPanelId);

        // Already open on a different asset: the factory does not run again, so this is what moves it.
        ShowRevisions();
    }

    /// <summary>Shows the selected asset's history, or says why there is none to show.</summary>
    /// <remarks>
    ///     ⚠ <b>Exactly one asset, and a multi-selection says so rather than picking the first.</b>
    ///     A panel that showed the history of whichever file happened to be first in a selection of
    ///     forty would be a panel whose answer is right by accident — and this one has a Restore
    ///     button on it.
    /// </remarks>
    internal void ShowRevisions() {
        if (revisionsView is not { } view) {
            return;
        }

        if (sourceControl is null) {
            view.Clear(
                sought
                    ? "This project is not in a working tree, so there is nothing to show."
                    : EditorStrings.RevisionsPick.Text
            );

            return;
        }

        if (Selected() is not { } path) {
            view.Clear(EditorStrings.RevisionsPick.Text);
            return;
        }

        var provider = sourceControl;

        deferred.When(
            provider.HistoryAsync(path, RevisionLimit),
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
        if (sourceControl is not { } provider || view.Path.Length == 0) {
            return;
        }

        deferred.When(
            provider.DiffAsync(view.Path, revision.Id),
            diff => view.ShowDiff(diff),
            failure => view.ShowDiff(new SourceControlDiff(false, failure.Message))
        );
    }

    /// <summary>Writes an old version of the selected asset into the working tree, after asking.</summary>
    /// <remarks>
    ///     ⚠ <b>Asked, on <c>RevertToSourceControl</c>'s terms and for the same reason.</b> This
    ///     overwrites a file on disk with bytes from a commit, and the undo stack holds documents
    ///     rather than files — so it cannot be taken back from the editor. What makes it safe rather
    ///     than merely warned about is that the result is an ordinary uncommitted change, which
    ///     Revert then undoes.
    /// </remarks>
    void RestoreRevision(RevisionsView view, SourceControlRevision revision) {
        if (sourceControl is not { } provider || view.Path is not { Length: > 0 } path) {
            return;
        }

        Confirm(
            ask: true,
            $"Restore '{Path.GetFileName(path)}' from {revision.ShortId}?",
            $"{provider.Name} will write that version over the file in the working tree. "
            + "The change is left uncommitted, so Revert to Source Control undoes it."
            + Environment.NewLine
            + revision.Summary,
            () => Restore(provider, path, revision),
            "Restore"
        );
    }

    void Restore(ISourceControl provider, string path, SourceControlRevision revision) =>
        deferred.When(
            provider.RestoreAsync(path, revision.Id),
            message => {
                if (message is not null) {
                    Shell.Notifications.Show("Could not restore", NotificationSeverity.Error, message);
                    return;
                }

                browser?.Rescan();
                Sweep();
                Shell.Notifications.Success($"{Path.GetFileName(path)} restored from {revision.ShortId}");
            },
            failure => Shell.Notifications.Show("Could not restore", NotificationSeverity.Error, failure.Message)
        );

    /// <summary>Uses a provider the caller supplies instead of looking for git.</summary>
    /// <param name="provider">The provider.</param>
    /// <remarks>
    ///     ⚠ <b>For the harness, and it is the reason the seam is worth having at all.</b> A test
    ///     over the column that shelled out to git would be a test of this machine's git; what the
    ///     column has to be proved to do is draw what the provider said.
    /// </remarks>
    internal void UseSourceControl(ISourceControl provider) {
        sourceControl = provider;
        sought = true;
    }

    /// <summary>Takes a status sweep, if there is a provider and the answer would be looked at.</summary>
    internal void Sweep() {
        if (sweeping || browser is null) {
            return;
        }

        sweeping = true;

        deferred.When(
            SweepAsync(),
            swept => {
                sweeping = false;

                // ⚠ Null is "there is no provider", which is not the same as "nothing has changed" —
                // accepting an empty sweep would mark every file in a project that is not in git as
                // unmodified, and draw a clean column for a claim nobody made.
                if (swept is null) {
                    return;
                }

                statuses.Accept(swept);
                browser?.Restated();
            },
            failure => {
                sweeping = false;

                // ⚠ A log line rather than a notification. Every project that is not in git reaches
                // this, and an editor that toasted about it on every refresh would be telling people
                // off for a decision they made deliberately.
                log.Write(LogLevel.Debug, "Source control status could not be read: " + failure.Message);
            }
        );
    }

    /// <summary>Finds a provider once, then asks it for the whole working tree.</summary>
    /// <returns>What it said, or null when the project is not under source control.</returns>
    async ValueTask<IReadOnlyDictionary<string, SourceControlStatus>?> SweepAsync() {
        if (!sought) {
            var root = project.Paths.Root;

            // ⚠ On the pool. `For` starts a process, and a network share or a repository whose
            // index is locked is seconds of a frame thread that is drawing a window.
            sourceControl = await Task.Run(() => (ISourceControl?)GitSourceControl.For(root)).ConfigureAwait(false);
            sought = true;
        }

        return sourceControl is null ? null : await sourceControl.StatusAsync().ConfigureAwait(false);
    }

    /// <summary>Throws away the changes to whatever is selected in the browser.</summary>
    /// <remarks>
    ///     ⚠ <b>Asked first, and the question names the files rather than counting them.</b> This is
    ///     the one verb in the editor that destroys work source control has not been told about, and
    ///     it cannot be undone by the undo stack — the file on disk is what changes, and the stack
    ///     holds documents.
    /// </remarks>
    void RevertToSourceControl() {
        if (sourceControl is not { } provider || project.Selection.Count == 0) {
            return;
        }

        List<string> paths = [];

        foreach (var asset in project.Selection) {
            if (project.Assets.TryGetByGuid(asset, out var entry) && !entry.IsFolder) {
                paths.Add(entry.Path);
            }
        }

        if (paths.Count == 0) {
            return;
        }

        var names = string.Join(Environment.NewLine, paths.Take(5));

        Confirm(
            ask: true,
            paths.Count == 1 ? $"Revert '{Path.GetFileName(paths[0])}'?" : $"Revert {paths.Count} files?",
            $"{provider.Name} will put them back as they were committed. This cannot be undone."
            + Environment.NewLine
            + names
            + (paths.Count > 5 ? Environment.NewLine + $"…and {paths.Count - 5} more" : string.Empty),
            () => Revert(provider, paths),
            "Revert"
        );
    }

    void Revert(ISourceControl provider, IReadOnlyList<string> paths) {
        var remaining = paths.Count;
        var failures = 0;

        foreach (var path in paths) {
            deferred.When(
                provider.RevertAsync(path),
                message => {
                    remaining--;

                    if (message is not null) {
                        failures++;
                        Shell.Notifications.Show("Could not revert", NotificationSeverity.Error, message);
                    }

                    // ⚠ The rescan is here rather than after the loop, because every revert is a
                    // separate process and the loop has finished long before any of them have. A
                    // browser refreshed on the frame the loop ended shows the files as they were.
                    if (remaining == 0) {
                        browser?.Rescan();
                        Sweep();

                        if (failures == 0) {
                            Shell.Notifications.Success(
                                paths.Count == 1 ? "1 file reverted" : $"{paths.Count} files reverted"
                            );
                        }
                    }
                },
                failure => {
                    remaining--;
                    Shell.Notifications.Show("Could not revert", NotificationSeverity.Error, failure.Message);
                }
            );
        }
    }
}
