// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO.Watch;

namespace Vixen.Editor.Core;

/// <summary>Doc 50 § D2's Project service: which project is open, what is on disk, and which opens next.</summary>
/// <remarks>
///     <para>
///         <b>The project's use cases as methods, published to plugins.</b> Rescanning the asset
///         database and asking for another project used to be done by whoever needed it — the content
///         browser scanned and saved the database itself, and the disk watcher did the same three
///         calls again when the browser was closed. One object does it now, and a module reaches it
///         with <c>Require&lt;IProjectSession&gt;()</c>.
///     </para>
///     <para>
///         ⚠ <b>Opening is a request and not a call.</b> The editor is rebuilt over the new root by
///         the host — the reopen loop is the composition root's job (doc 50 § D2) — so
///         <see cref="RequestProject" /> sets <see cref="PendingProject" /> and asks to close, through
///         the same unsaved-work prompt the window's close button uses.
///     </para>
/// </remarks>
public interface IProjectSession {
    /// <summary>The open project.</summary>
    EditorProject Project { get; }

    /// <summary>Where the editor should reopen, or <see langword="null" /> to stay where it is.</summary>
    string? PendingProject { get; }

    /// <summary>Closes this project and reopens the editor over another, asking about unsaved work first.</summary>
    /// <param name="root">The new project's root directory, which need not exist yet.</param>
    void RequestProject(string root);

    /// <summary>The projects this user has opened, newest first — what File ▸ Open Recent and the startup browser list.</summary>
    IReadOnlyList<RecentProject> Recent { get; }

    /// <summary>Writes the game template into a directory and asks to reopen the editor over it.</summary>
    /// <param name="root">The new project's root directory, created if it is not there.</param>
    /// <remarks>
    ///     A directory that already holds some of the template's files is opened rather than clobbered,
    ///     and says what was left alone — see <see cref="ProjectScaffold.Write" />.
    /// </remarks>
    void CreateProject(string root);

    /// <summary>Rescans the asset database, saves its index and rebuilds the reference index.</summary>
    /// <returns>What the scan found.</returns>
    ScanReport Rescan();

    /// <summary>Raised after every <see cref="Rescan" />, with what it found.</summary>
    event Action<ScanReport>? Rescanned;

    /// <summary>Whether something is watching the project's assets for changes made outside the editor.</summary>
    /// <remarks>
    ///     False for a project with no assets directory yet, on a share the platform cannot watch, and in
    ///     a harness that asked for none — the project still opens, and <see cref="Rescan" /> still works.
    /// </remarks>
    bool IsWatching { get; }

    /// <summary>
    ///     Raised once for each burst of changes made to the assets outside the editor, after the rescan
    ///     they caused — with what changed and whether the watcher lost events.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The list is reused and is only valid during the call.</b> An overflow means the list
    ///     cannot be trusted to name everything that changed; treat it as "anything may have".
    /// </remarks>
    event Action<IReadOnlyList<FileChange>, bool>? ChangedOnDisk;

    /// <summary>Builds the project's own code and loads it, keeping every open scene's authored behaviours across the reload.</summary>
    /// <returns>
    ///     <see langword="false" /> when the build failed, which has been reported; <see langword="true" />
    ///     when it built, and also for a project with no code to build.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It runs a compiler</b>, which takes seconds on a cold restore — the editor calls it on its
    ///         first frame, not while it is starting.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No build removes an authored behaviour the author did not remove (#1490).</b> A failed
    ///         build keeps the last one that worked; one with nothing to fall back on holds each open scene's
    ///         behaviours until the code builds, says so, and refuses to save a scene without its own (#1496).
    ///     </para>
    /// </remarks>
    bool BuildCode();

    /// <summary>The project's own code: which <c>.csproj</c> builds it and the assembly loaded from it.</summary>
    /// <remarks>
    ///     Read-only — <see cref="BuildCode" /> is the way to rebuild it. See <see cref="IProjectCode" />.
    /// </remarks>
    IProjectCode Code { get; }

    /// <summary>
    ///     Raised after <see cref="BuildCode" /> loaded the project's code and put the authored behaviours
    ///     back — after a failed build too, when the last build that worked was loaded again.
    /// </summary>
    event Action? CodeBuilt;
}
