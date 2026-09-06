// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.IO;
using Vixen.Core.IO.Watch;

namespace Vixen.Editor.Core;

/// <summary>What one open document did about its file changing underneath it.</summary>
public enum ExternalEditOutcome {
    /// <summary>Read again. It was clean, so there was nothing in memory that disk did not also have.</summary>
    Reloaded,

    /// <summary>Left alone because it has unsaved edits. It is <see cref="EditorDocument.IsStale" /> now.</summary>
    Kept,

    /// <summary>Left alone because this kind of document cannot re-read its file. Also stale.</summary>
    Unsupported,

    /// <summary>It tried and could not — a half-written file, an unreadable one. What it had still stands.</summary>
    Failed
}

/// <summary>One open document and what the change to its file did to it.</summary>
/// <param name="Document">The document.</param>
/// <param name="Outcome">What happened.</param>
public readonly record struct ExternalEdit(EditorDocument Document, ExternalEditOutcome Outcome);

/// <summary>The editor's answer to a file it has open being changed by something else.</summary>
/// <remarks>
///     <para>
///         <b>The seam that was missing.</b> The asset database, the project browser, the build panel
///         and the shader library have all followed the watcher for as long as there has been one —
///         but every one of them reads the drained list for a <i>count</i> and rescans. Nothing asked
///         which file, so nothing could ask which document. A <c>.vxcompositor</c> edited in a text
///         editor beside the running editor changed the database, the tree and the build, and did not
///         change the panel that was open on it.
///     </para>
///     <para>
///         <b>Two directions, and this class is both of them.</b> Outward: the editor's own saves
///         must not come back through the watcher as somebody else's edit, which is what
///         <c>IFileWatcher.Suppress</c> is for and why <see cref="EditorProject.DocumentSaving" />
///         fires before the write rather than after it. Inward: a change that really did come from
///         outside has to reach the document open on that file. Both are one object because they are
///         one question — "is this change ours?" — and answering it in two places is how the answers
///         drift.
///     </para>
///     <para>
///         <b>The policy, which is the only decision here.</b> A document that can re-read its file
///         and has no unsaved edits is reloaded, silently: what was in memory was the file's previous
///         contents and nothing else, so there is nothing to lose and no one to ask. A document with
///         unsaved edits is <em>not</em> reloaded. It is marked <see cref="EditorDocument.IsStale" />
///         and reported through <see cref="Applied" />, and both copies go on existing until somebody
///         picks one.
///     </para>
///     <para>
///         That follows <c>EditorFrames.Reframe</c>'s precedent — prefer the state a person can still
///         act on — and it is the asymmetry that decides it: an edit that exists only in this
///         process's memory is gone the moment it is overwritten, and a file on disk is not. Reloading
///         over unsaved work destroys the only copy of it; declining to reload costs a person one
///         click and leaves both copies intact. Reload-when-clean and say-so-otherwise is also what
///         every editor that has ever done this does, so it is the behaviour a person arrives already
///         expecting.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately not decided here is which copy wins.</b> That is a question
///         only a person can answer, and this is the non-destructive half of asking it: the flag on
///         the document, the report on the event, and <see cref="EditorDocument.Reload" /> and
///         <see cref="EditorDocument.Save" /> as the two answers. The editor's head puts both in
///         front of somebody — a notification naming the file, Ctrl+S to keep theirs, and
///         <c>file.revert</c> to take the file's, which asks before it discards anything.
///     </para>
///     <para>
///         What is not built is the <em>banner</em>: the offer across the document itself rather
///         than in the corner of the window, so that the choice is made where the conflict is. That
///         is a panel and not a mechanism, and everything it would need is already public.
///     </para>
///     <para>
///         ⚠ <b>Deletion is not a reload.</b> A file that has gone would read back as empty — see
///         <c>AssetFile.Read</c>, which is right for opening an asset somebody has just created and
///         catastrophic here. A document whose file was deleted keeps what it has, which is now the
///         only copy of it, and <see cref="EditorDocument.Save" /> is how it comes back.
///     </para>
/// </remarks>
public sealed class ExternalEdits : IDisposable {
    readonly EditorProject project;
    readonly IFileWatcher? watcher;
    readonly string watchedDirectory;
    readonly VirtualPath mount;

    bool disposed;

    /// <summary>The project whose documents this follows.</summary>
    public EditorProject Project => project;

    /// <summary>Raised once per open document a drained change reached, whatever it did about it.</summary>
    /// <remarks>
    ///     Every outcome, not only the ones that need somebody. A reload is worth a console line, and
    ///     a subscriber that only heard about failures could not tell "nothing was open on it" from
    ///     "the watcher stopped working".
    /// </remarks>
    public event Action<ExternalEdit>? Applied;

    /// <summary>Follows a project's documents, and keeps its own saves out of a watcher's way.</summary>
    /// <param name="project">The project.</param>
    /// <param name="watcher">
    ///     The watcher the changes will come from, or <see langword="null" /> for a project with none
    ///     — a share the platform cannot watch, or a test. Routing still works without one; what is
    ///     lost is the suppression, and with it the guarantee that a save does not round-trip.
    /// </param>
    /// <param name="watchedDirectory">
    ///     The directory <paramref name="watcher" /> covers, which is what its virtual paths are
    ///     relative to. Defaults to the project's <c>Assets/</c>, which is what the editor watches.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>The directory is a parameter because <see cref="IFileWatcher" /> does not publish
    ///     it.</b> The interface gives the virtual root and not the OS directory it is mounted at, so
    ///     the two halves of the mapping cannot both be derived from it. Guessing would be a second
    ///     opinion of a decision <c>EditorApplication.Watch</c> already made.
    /// </remarks>
    public ExternalEdits(EditorProject project, IFileWatcher? watcher = null, string? watchedDirectory = null) {
        ArgumentNullException.ThrowIfNull(project);

        this.project = project;
        this.watcher = watcher;
        this.watchedDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(watchedDirectory ?? project.Paths.Assets)
        );

        mount = watcher?.Root ?? VirtualPath.Root;
        project.DocumentSaving += OnDocumentSaving;
    }

    /// <summary>Routes what the watcher drained to whichever open documents those files belong to.</summary>
    /// <param name="changes">The drained changes.</param>
    /// <returns>How many open documents a change reached, reloaded or not.</returns>
    /// <remarks>
    ///     ⚠ <b>Call it after the database has rescanned, not before.</b> A path is turned into a
    ///     document through the GUID index, and the index is what a rename moves — so routing a
    ///     rename against a stale index looks up the old path and finds nothing. This is the one
    ///     ordering constraint in the whole seam.
    /// </remarks>
    public int Apply(IReadOnlyList<FileChange> changes) {
        ArgumentNullException.ThrowIfNull(changes);

        var reached = 0;

        for (var index = 0; index < changes.Count; index++) {
            var change = changes[index];

            // ⚠ Every change and every open document, before the routing and including deletions —
            // #922. This is the other question: not "which document is this file" but "which
            // documents care that it moved". A texture graph inlines `Assets/Compounds`, so a
            // compound edited outside the editor reached nothing; and a compound *deleted* has to
            // leave the menu, which the reload path below deliberately never hears about.
            Announce(change.Path);

            if (change.Kind == FileChangeKind.Deleted) {
                continue;
            }

            if (!TryResolve(change.Path, out var document)) {
                continue;
            }

            Follow(document);
            reached++;
        }

        return reached;
    }

    /// <summary>Re-reads every open document that can be, for when the events themselves were lost.</summary>
    /// <returns>How many were reloaded.</returns>
    /// <remarks>
    ///     <para>
    ///         What an overflow gets, on the same argument <c>EditorFrames.ReloadShaders</c> makes:
    ///         the drained list is empty and cannot be trusted to describe what changed, so when in
    ///         doubt, do the work. Reading a handful of open files is cheap next to the rescan that
    ///         is happening anyway.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A dirty document is left alone <em>and not marked stale</em>, which is the one
    ///         place this differs from <see cref="Apply" />.</b> An overflow says events were lost,
    ///         not that this file changed. Marking every unsaved document conflicted because
    ///         something somewhere wrote a burst of files would put a prompt in front of somebody
    ///         about a file nothing touched — and a prompt that is usually wrong is one that gets
    ///         dismissed without reading, including the time it is right.
    ///     </para>
    /// </remarks>
    public int Rescan() {
        var reloaded = 0;

        // ⚠ Over a snapshot, which is the argument `EditorProject.SaveAll` already makes: a reload
        // runs a deriving type's code, and one that opened or closed a document would reorganise the
        // list underneath this loop — skipping whatever moved into the slot it left.
        var documents = project.Documents.ToArray();

        // ⚠ Null, which is this method's whole situation said in one argument: events were lost, so
        // any file may have changed and no path can be named. A document that depends on somebody
        // else's file has to assume the worst here, exactly as this method does about its own.
        Announce(null);

        for (var index = 0; index < documents.Length; index++) {
            var document = documents[index];

            if (!document.IsOpen || !document.CanReload || document.IsDirty.Value) {
                continue;
            }

            var took = TryReload(document);

            Applied?.Invoke(new(document, took ? ExternalEditOutcome.Reloaded : ExternalEditOutcome.Failed));

            if (took) {
                reloaded++;
            }
        }

        return reloaded;
    }

    /// <summary>Stops following the project's saves.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        project.DocumentSaving -= OnDocumentSaving;
    }

    /// <summary>Tells every open document that a file in the project changed.</summary>
    /// <param name="path">The watched path, or null for "events were lost".</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Project-relative, because that is what a document can compare against.</b> A
    ///         <see cref="VirtualPath" /> is relative to the watcher's mount, which is a fact about
    ///         how this editor was configured; <c>ProjectPaths.Relative</c> is the spelling every
    ///         asset in the database is stored under.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Over a snapshot and inside a try, for <see cref="Rescan" />'s reasons plus one.</b>
    ///         An override belongs to a deriving type — a plugin's, in the case this exists for — and
    ///         one that throws would take the frame down over somebody else's text editor pressing
    ///         Ctrl+S. A document that cannot cope with a notification keeps whatever it had.
    ///     </para>
    /// </remarks>
    void Announce(VirtualPath? path) {
        string? relative = null;

        if (path is { } named && !named.IsEmpty && mount.Contains(named)) {
            var trimmed = named.RelativeTo(mount).Value.TrimStart(VirtualPath.Separator);

            if (trimmed.Length > 0) {
                relative = project.Paths.Relative(
                    Path.Combine(watchedDirectory, trimmed.Replace(VirtualPath.Separator, Path.DirectorySeparatorChar))
                );
            }
        }

        var documents = project.Documents.ToArray();

        for (var index = 0; index < documents.Length; index++) {
            try {
                documents[index].OnProjectFileChanged(relative);
            } catch (Exception failure)
                when (failure is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or NotSupportedException) {
                // Kept, on `TryReload`'s argument: a document that could not take the news keeps what
                // it has, and the next change tries again.
            }
        }
    }

    /// <summary>Applies the policy to one document whose file has changed.</summary>
    void Follow(EditorDocument document) {
        if (!document.CanReload) {
            document.MarkStale();
            Applied?.Invoke(new(document, ExternalEditOutcome.Unsupported));

            return;
        }

        if (document.IsDirty.Value) {
            document.MarkStale();
            Applied?.Invoke(new(document, ExternalEditOutcome.Kept));

            return;
        }

        // ⚠ Marked stale first, so a reload that throws or declines leaves the document saying so.
        // Reload clears it on the way out; the ordering is what makes the failure path honest
        // instead of silent.
        document.MarkStale();

        // ⚠ In a local, and it has to be. `Applied?.Invoke(…TryReload(document)…)` would be a reload
        // that only happens when something is subscribed: `?.` short-circuits its whole argument
        // list, so with no listener the document would silently never be re-read. Written that way
        // once, and three tests failed on it.
        var reloaded = TryReload(document);

        Applied?.Invoke(new(document, reloaded ? ExternalEditOutcome.Reloaded : ExternalEditOutcome.Failed));
    }

    /// <summary>Reads a document's file again, and survives it not working.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Four exceptions, and the list is <c>EditorFrames.Reframe</c>'s plus the two a
    ///         file being written under one causes.</b> This runs from the frame, on a path that
    ///         begins with somebody else's text editor pressing Ctrl+S — so an exception out of it
    ///         is an editor taken down by an external program, which is not a failure mode any
    ///         document should be able to have.
    ///     </para>
    ///     <para>
    ///         <c>IOException</c> and <c>UnauthorizedAccessException</c> are the ordinary races: a
    ///         file still being written, or one whose permissions moved. <c>InvalidOperationException</c>
    ///         and <c>NotSupportedException</c> are what a <em>file</em> causes rather than a bug —
    ///         the second is what <c>CompositorBuilder.Build</c> throws for a document written by
    ///         another version of the engine, which is exactly the file somebody pulls from a branch
    ///         while the editor is open.
    ///     </para>
    ///     <para>
    ///         What is kept in every case is the document that is already on screen, which is
    ///         <c>Reframe</c>'s rule: a document that blanked itself over a transient would lose its
    ///         contents to something that fixed itself a moment later. It stays stale, so the next
    ///         change — or a person — tries again.
    ///     </para>
    /// </remarks>
    static bool TryReload(EditorDocument document) {
        try {
            return document.Reload();
        } catch (Exception failure)
            when (failure is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException) {
            return false;
        }
    }

    /// <summary>Tells the watcher to ignore the path a document is about to write.</summary>
    void OnDocumentSaving(EditorDocument document) {
        if (watcher is null || !TryVirtual(document, out var path)) {
            return;
        }

        watcher.Suppress(path);

        // ⚠ And the temporary beside it. `AssetFile.Write` writes `<path>.tmp` and renames it over
        // the target, which the coalescer folds into one change to the target — but only when it
        // sees the rename, and the rename is the event just suppressed. Without this line every save
        // still leaves a create for a file that no longer exists, and every save still costs a full
        // project rescan. Suppressing a path nothing writes costs nothing and expires by itself.
        if (VirtualPath.TryCreate(path.Value + ".tmp", out var temporary)) {
            watcher.Suppress(temporary);
        }
    }

    /// <summary>Finds the open document a watched path belongs to.</summary>
    bool TryResolve(VirtualPath path, [MaybeNullWhen(false)] out EditorDocument document) {
        document = null;

        if (path.IsEmpty || !mount.Contains(path)) {
            return false;
        }

        var relative = path.RelativeTo(mount).Value.TrimStart(VirtualPath.Separator);

        if (relative.Length == 0) {
            return false;
        }

        var absolute = Path.Combine(
            watchedDirectory,
            relative.Replace(VirtualPath.Separator, Path.DirectorySeparatorChar)
        );

        return project.Assets.TryGetByPath(project.Paths.Relative(absolute), out var entry)
            && !entry.IsFolder
            && project.TryGetDocument(entry.Guid, out document);
    }

    /// <summary>Finds the watched path a document's asset is at.</summary>
    bool TryVirtual(EditorDocument document, out VirtualPath path) {
        path = default;

        if (document.Asset.IsEmpty || !project.Assets.TryGetByGuid(document.Asset, out var entry)) {
            // A document over a file the index does not know — one created this session and not yet
            // scanned, or one outside `Assets/`. There is nothing to suppress by name, and the save
            // leaves the document clean, so the reload it may provoke is a no-op rather than a loss.
            return false;
        }

        var relative = Path.GetRelativePath(watchedDirectory, project.Paths.Absolute(entry.Path))
            .Replace(Path.DirectorySeparatorChar, VirtualPath.Separator);

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) {
            return false;
        }

        return VirtualPath.TryCreate(mount.IsRoot ? "/" + relative : mount.Value + "/" + relative, out path);
    }
}
