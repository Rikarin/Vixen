// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Assets;

/// <summary>Doc 50's source-control service: what the project's version control says, and the two verbs that change it.</summary>
/// <remarks>
///     <para>
///         <b>A façade that outlives the provider's discovery (#1505).</b> Whether the project is in a
///         working tree is found out lazily, on the pool, by the first status sweep — so there is no
///         provider to publish when the editor is built, and there may never be one. This is published
///         instead, once, and its answers move under it: <see cref="IsKnown" /> turns true when a sweep
///         lands, and stays false for a project that is not under source control.
///     </para>
///     <para>
///         ⚠ <b>Swept when the project is rescanned, not when a panel is open.</b> The editor sweeps once
///         at startup and again on every <c>IProjectSession.Rescanned</c> — the file watcher draining,
///         Refresh, a revert — so Show History and Revert know whether they are available before the
///         content browser has ever been opened. Every provider call runs on the thread pool, never on the
///         frame thread; <see cref="Swept" /> and <see cref="Reverted" /> are raised on the frame thread.
///     </para>
///     <para>
///         ⚠ <b>Neither <see cref="Revert" /> nor <see cref="Restore" /> asks, and neither can be undone.</b>
///         Both write files on disk, and the undo stack holds documents. Asking — with the files named in
///         the question — is the command's, which is a name for these; a script putting back what it
///         generated has nobody to ask.
///     </para>
/// </remarks>
public interface ISourceControl {
    /// <summary>What the provider is called in a menu or a notification, or null when there is none (yet).</summary>
    string? Name { get; }

    /// <summary>Whether the project has been looked at — after which a null <see cref="Name" /> means it is not in a working tree.</summary>
    bool IsSought { get; }

    /// <summary>Whether a status sweep has landed, which is what a source-control verb's enablement asks.</summary>
    bool IsKnown { get; }

    /// <summary>What source control says about a path, a folder taking the strongest answer under it.</summary>
    /// <param name="path">Project-relative, forward slashes.</param>
    /// <returns>The status, or <see cref="SourceControlStatus.Unknown" /> before the first sweep and without a provider.</returns>
    SourceControlStatus Status(string path);

    /// <summary>Asks for a fresh sweep of the whole working tree, off the frame thread.</summary>
    /// <remarks>One asked for while another is in flight is taken when that one lands, rather than dropped.</remarks>
    void Sweep();

    /// <summary>Raised on the frame thread after a sweep's answer has been taken.</summary>
    event Action? Swept;

    /// <summary>The commits that touched a file, newest first.</summary>
    /// <param name="path">Project-relative, forward slashes.</param>
    /// <param name="limit">At most this many.</param>
    /// <returns>The revisions; empty without a provider or for a file it has never seen. Answers off the frame thread.</returns>
    ValueTask<IReadOnlyList<SourceControlRevision>> HistoryAsync(string path, int limit);

    /// <summary>What changed in a file, at a revision or since the last commit.</summary>
    /// <param name="path">Project-relative, forward slashes.</param>
    /// <param name="revision">A <see cref="SourceControlRevision.Id" />, or empty for the working tree's own changes.</param>
    /// <returns>The patch, or the sentence that stands in for one. Answers off the frame thread.</returns>
    ValueTask<SourceControlDiff> DiffAsync(string path, string revision);

    /// <summary>Throws away the changes to some files, without asking, then rescans and says so.</summary>
    /// <param name="paths">Project-relative, forward slashes.</param>
    /// <remarks>
    ///     Each failure is reported as it arrives; <see cref="Reverted" /> is raised once, after the last
    ///     answer, when the project has been rescanned. Nothing happens without a provider.
    /// </remarks>
    void Revert(IReadOnlyList<string> paths);

    /// <summary>Writes an old version of a file over the working tree's, without asking, then rescans and says so.</summary>
    /// <param name="path">Project-relative, forward slashes.</param>
    /// <param name="revision">Which version.</param>
    /// <remarks>
    ///     ⚠ The result is an ordinary uncommitted change, which <see cref="Revert" /> undoes — that, and not
    ///     the undo stack, is what makes it safe.
    /// </remarks>
    void Restore(string path, SourceControlRevision revision);

    /// <summary>Raised on the frame thread after a <see cref="Revert" /> or <see cref="Restore" /> has finished and the project has been rescanned.</summary>
    event Action? Reverted;
}
