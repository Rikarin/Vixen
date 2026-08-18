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
///         ⚠ <b>What is deliberately not built here is the prompt.</b> Choosing between the two
///         copies is a question only a person can answer, and asking it is a shell affordance — a
///         notification with two buttons, a banner across the document, a diff. This is the
///         non-destructive half: the flag on the document, the report on the event, and
///         <see cref="EditorDocument.Reload" /> and <see cref="EditorDocument.Save" /> as the two
///         answers, both already public and both already correct. The editor's head posts a
///         notification naming the file; a person who wants the disk version reloads, and a person
///         who wants theirs saves. What a repository owner may still want is the banner, and that is
///         a panel rather than a mechanism.
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
        var documents = project.Documents;

        for (var index = 0; index < documents.Count; index++) {
            var document = documents[index];

            if (!document.CanReload || document.IsDirty.Value) {
                continue;
            }

            var outcome = document.Reload() ? ExternalEditOutcome.Reloaded : ExternalEditOutcome.Failed;

            Applied?.Invoke(new(document, outcome));

            if (outcome == ExternalEditOutcome.Reloaded) {
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

        var reloaded = false;

        try {
            reloaded = document.Reload();
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            // ⚠ Kept rather than blanked, which is `Reframe`'s rule in the other half of the editor:
            // a file being read while something else is still writing it is an ordinary race, and a
            // document that emptied itself over one would lose the contents to a transient. The
            // document stays stale, so the next change — or a person — tries again.
        }

        Applied?.Invoke(new(document, reloaded ? ExternalEditOutcome.Reloaded : ExternalEditOutcome.Failed));
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
