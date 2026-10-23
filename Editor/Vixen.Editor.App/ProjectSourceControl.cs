// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.Ui;

namespace Vixen.Editor.App;

/// <summary>The editor's <see cref="ISourceControl" />: the provider once found, the statuses its sweeps answered, and the two verbs.</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten (#1505)</b> — this was the sweep half of <c>EditorSourceControl.cs</c>, a
///         partial of <c>EditorApplication</c> closed over its <c>browser</c>, <c>deferred</c> and
///         <c>Shell.Notifications</c>. What changed is what it follows.
///     </para>
///     <para>
///         ⚠ <b>The sweep used to return early unless the content browser was open</b>, so the provider was
///         found — and Revert and Show History were enabled — only once somebody had opened that panel. It
///         now follows <see cref="IProjectSession.Rescanned" /> and takes one sweep when the editor starts.
///         <see cref="IProjectSession.ChangedOnDisk" /> is not followed separately and does not need to be:
///         <c>ProjectSession.FollowDisk</c> raises it only after the rescan that raised
///         <see cref="IProjectSession.Rescanned" />, so a second subscription would be a second
///         <c>git status</c> for the same change. The cost the old gate avoided — one <c>git rev-parse</c>
///         per launch — is on the pool and is now paid for verbs the menu offers, not only for a column.
///     </para>
///     <para>
///         ⚠ <b>Every provider call is on the pool, and every state change on the frame thread.</b> The
///         provider's methods are <c>Task.Run</c>s (<see cref="GitSourceControl" />) and the detection is
///         one here; answers come back through <see cref="Deferred" />, so <see cref="SourceControlStatuses" />
///         — which the browser reads while it draws — is only ever written by the thread drawing it.
///     </para>
/// </remarks>
sealed class ProjectSourceControl : ISourceControl {
    readonly string root;
    readonly IProjectSession session;
    readonly Deferred deferred;
    readonly Action<string, NotificationSeverity, string?> notify;
    readonly Action<string> debug;

    /// <summary>What the browser draws, folded so a folder speaks for what is under it.</summary>
    readonly SourceControlStatuses statuses = new();

    /// <summary>The provider, once one has been found, or null when there is none.</summary>
    ISourceControlProvider? provider;

    /// <summary>Whether a sweep has been asked for and not yet answered.</summary>
    /// <remarks>
    ///     ⚠ Not a lock: a second sweep is not dangerous, it is a second <c>git status</c> over the whole
    ///     working tree, and the watcher can fire several times a second while somebody is saving from
    ///     another program. One asked for meanwhile is remembered in <see cref="again" /> and taken when
    ///     this lands — dropping it, as the application's sweep did, lost the change that asked.
    /// </remarks>
    bool sweeping;

    bool again;

    /// <summary>Builds the service and takes the first sweep.</summary>
    /// <param name="root">The project root, which is where the provider is looked for.</param>
    /// <param name="session">The session whose rescans a sweep follows, and which a revert rescans.</param>
    /// <param name="deferred">The frame thread's queue.</param>
    /// <param name="notify">How a result or a failure is said — a delegate, because the notification centre needs a window.</param>
    /// <param name="debug">Where a sweep that could not be read is logged.</param>
    public ProjectSourceControl(
        string root,
        IProjectSession session,
        Deferred deferred,
        Action<string, NotificationSeverity, string?> notify,
        Action<string> debug
    ) {
        this.root = root;
        this.session = session;
        this.deferred = deferred;
        this.notify = notify;
        this.debug = debug;

        session.Rescanned += _ => Sweep();
        Sweep();
    }

    /// <inheritdoc />
    public string? Name => provider?.Name;

    /// <inheritdoc />
    public bool IsSought { get; private set; }

    /// <inheritdoc />
    public bool IsKnown => statuses.IsKnown;

    /// <inheritdoc />
    public event Action? Swept;

    /// <inheritdoc />
    public event Action? Reverted;

    /// <inheritdoc />
    public SourceControlStatus Status(string path) => statuses.Of(path);

    /// <summary>Uses a provider the caller supplies instead of looking for git.</summary>
    /// <param name="replacement">The provider.</param>
    /// <remarks>
    ///     ⚠ <b>For the harness, and it is the reason the seam is worth having at all.</b> A test over the
    ///     column that shelled out to git would be a test of this machine's git; what the column has to be
    ///     proved to do is draw what the provider said. A detection still in flight when this is called is
    ///     discarded when it lands rather than overwriting it.
    /// </remarks>
    internal void Use(ISourceControlProvider replacement) {
        provider = replacement;
        IsSought = true;
    }

    /// <inheritdoc />
    public void Sweep() {
        if (sweeping) {
            again = true;
            return;
        }

        sweeping = true;
        again = false;

        var seeking = !IsSought;

        deferred.When(
            SweepAsync(provider, seeking, root),
            swept => {
                sweeping = false;

                if (seeking && !IsSought) {
                    provider = swept.Provider;
                    IsSought = true;
                }

                // ⚠ The provider changed under the sweep — the harness's `Use` — so its answer is about
                // somebody else's working tree. Take another rather than accept it.
                if (!ReferenceEquals(swept.Provider, provider)) {
                    Sweep();
                    return;
                }

                // ⚠ Null is "there is no provider", which is not the same as "nothing has changed" —
                // accepting an empty sweep would mark every file in a project that is not in git as
                // unmodified, and draw a clean column for a claim nobody made.
                if (swept.Statuses is { } answer) {
                    statuses.Accept(answer);
                    Swept?.Invoke();
                }

                if (again) {
                    Sweep();
                }
            },
            failure => {
                sweeping = false;

                // ⚠ A log line rather than a notification. Every project that is not in git reaches
                // this, and an editor that toasted about it on every refresh would be telling people off
                // for a decision they made deliberately.
                debug("Source control status could not be read: " + failure.Message);

                if (again) {
                    Sweep();
                }
            },

            // ⚠ And a cancelled sweep releases the latch too (#1512). `When` runs neither handler above
            // for one, so a provider whose status came back cancelled left `sweeping` set for good and
            // every later sweep folded into `again` — a column that never drew again.
            () => {
                sweeping = false;
                debug("Source control status was cancelled before it answered.");

                if (again) {
                    Sweep();
                }
            }
        );
    }

    /// <summary>Finds a provider if asked to, then asks it for the whole working tree.</summary>
    /// <returns>The provider the answer is from, and what it said — null when there is no provider.</returns>
    static async ValueTask<(ISourceControlProvider? Provider, IReadOnlyDictionary<string, SourceControlStatus>? Statuses)> SweepAsync(
        ISourceControlProvider? provider,
        bool seeking,
        string root
    ) {
        if (seeking) {
            // ⚠ On the pool. `For` starts a process, and a network share or a repository whose index is
            // locked is seconds of a frame thread that is drawing a window.
            provider = await Task.Run(() => (ISourceControlProvider?)GitSourceControl.For(root)).ConfigureAwait(false);
        }

        return provider is null ? (null, null) : (provider, await provider.StatusAsync().ConfigureAwait(false));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SourceControlRevision>> HistoryAsync(string path, int limit) =>
        provider is { } found ? found.HistoryAsync(path, limit) : new((IReadOnlyList<SourceControlRevision>)[]);

    /// <inheritdoc />
    public ValueTask<SourceControlDiff> DiffAsync(string path, string revision) =>
        provider is { } found
            ? found.DiffAsync(path, revision)
            : new(new SourceControlDiff(false, "This project is not in a working tree."));

    /// <inheritdoc />
    public void Revert(IReadOnlyList<string> paths) {
        ArgumentNullException.ThrowIfNull(paths);

        if (provider is not { } found || paths.Count == 0) {
            return;
        }

        var remaining = paths.Count;
        var failures = 0;

        foreach (var path in paths) {
            deferred.When(
                found.RevertAsync(path),
                message => {
                    if (message is not null) {
                        failures++;
                        notify("Could not revert", NotificationSeverity.Error, message);
                    }

                    Answered();
                },
                failure => {
                    failures++;
                    notify("Could not revert", NotificationSeverity.Error, failure.Message);
                    Answered();
                },

                // ⚠ Counted as well (#1512): a cancelled revert ran neither handler above and left
                // `remaining` above zero, so the rescan every other revert was waiting on never ran.
                () => {
                    failures++;
                    notify("Could not revert", NotificationSeverity.Error, $"Reverting '{path}' was cancelled.");
                    Answered();
                }
            );
        }

        // ⚠ The rescan is after the last answer rather than after the loop, because every revert is a
        // separate process and the loop has finished long before any of them have. A rescan on the
        // frame the loop ended shows the files as they were.
        void Answered() {
            if (--remaining > 0) {
                return;
            }

            Rescanned();

            if (failures == 0) {
                notify(paths.Count == 1 ? "1 file reverted" : $"{paths.Count} files reverted", NotificationSeverity.Success, null);
            }
        }
    }

    /// <inheritdoc />
    public void Restore(string path, SourceControlRevision revision) {
        ArgumentNullException.ThrowIfNull(revision);

        if (provider is not { } found || path.Length == 0) {
            return;
        }

        deferred.When(
            found.RestoreAsync(path, revision.Id),
            message => {
                if (message is not null) {
                    notify("Could not restore", NotificationSeverity.Error, message);
                    return;
                }

                Rescanned();
                notify($"{Path.GetFileName(path)} restored from {revision.ShortId}", NotificationSeverity.Success, null);
            },
            failure => notify("Could not restore", NotificationSeverity.Error, failure.Message)
        );
    }

    /// <summary>Rescans the project — which sweeps, through <see cref="IProjectSession.Rescanned" /> — and says so.</summary>
    void Rescanned() {
        session.Rescan();
        Reverted?.Invoke();
    }
}
