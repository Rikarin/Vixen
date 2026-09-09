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
///         it.</b> This is the seam, a git provider, the sweep that keeps an answer, and the verb
///         that throws a file's changes away. Diff and history are their own work — both want a
///         panel that does not exist, and writing their signatures here would be designing it
///         sideways.
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

    /// <summary>What the harness reads, because there is no other way to see a status from outside.</summary>
    internal SourceControlStatuses SourceControl => statuses;

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
