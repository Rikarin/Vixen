// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.IO.Watch;

/// <summary>Watches part of the virtual file system and reports settled changes.</summary>
public interface IFileWatcher : IDisposable {
    /// <summary>The virtual path this watcher covers.</summary>
    VirtualPath Root { get; }

    /// <summary>How long a path must be quiet before its change is reported.</summary>
    TimeSpan Debounce { get; set; }

    /// <summary>
    ///     Whether events were lost since the last <see cref="ClearOverflow" />, meaning the only
    ///     correct response is to rescan.
    /// </summary>
    bool HasOverflowed { get; }

    /// <summary>Ignores events for a path that this program is about to write.</summary>
    /// <param name="path">The virtual path.</param>
    void Suppress(VirtualPath path);

    /// <summary>Takes every change whose debounce window has closed.</summary>
    /// <param name="into">Where to put them.</param>
    /// <returns>How many changes were produced.</returns>
    int Drain(ICollection<FileChange> into);

    /// <summary>Acknowledges an overflow after rescanning.</summary>
    void ClearOverflow();
}

/// <summary>Watches a directory on disk, reporting changes as virtual paths.</summary>
/// <remarks>
///     <para>
///         <b>On not writing three backends.</b> <c>docs/plan/03-core-foundation.md</c> asks for
///         per-platform backends over FSEvents, inotify and <c>ReadDirectoryChangesW</c>. The BCL's
///         <see cref="FileSystemWatcher" /> is already exactly that — those three APIs behind one
///         type, maintained by people who have to keep it working on OS versions that do not exist
///         yet. Reimplementing it would be three P/Invoke surfaces bought with nothing.
///     </para>
///     <para>
///         What the BCL does not do is any of the things that make watching usable: it reports the
///         four writes a text editor makes as four events, reports an atomic save as a change to a
///         temporary file plus a rename, and reports the program's own writes back to it. That is
///         <see cref="FileChangeCoalescer" />'s job, and it is where the behaviour and the tests are.
///     </para>
///     <para>
///         <b>Changes are pulled, not pushed.</b> <see cref="Drain" /> is called from wherever the
///         consumer wants the effects to land — a frame boundary, between the simulation and the
///         render — rather than the watcher raising events on a platform thread at a moment nobody
///         chose. This is the same reason <c>MainThreadDispatcher</c> drains at defined points.
///     </para>
/// </remarks>
public sealed class FileWatcher : IFileWatcher {
    readonly FileSystemWatcher watcher;
    readonly FileChangeCoalescer coalescer = new();
    readonly Lock gate = new();
    readonly string osRoot;

    /// <inheritdoc />
    public VirtualPath Root { get; }

    /// <inheritdoc />
    public TimeSpan Debounce {
        get {
            lock (gate) {
                return coalescer.Debounce;
            }
        }
        set {
            lock (gate) {
                coalescer.Debounce = value;
            }
        }
    }

    /// <inheritdoc />
    public bool HasOverflowed { get; private set; }

    /// <summary>How many raw events were dropped because the coalescer's buffer was full.</summary>
    public long DroppedCount {
        get {
            lock (gate) {
                return coalescer.DroppedCount;
            }
        }
    }

    /// <summary>Watches a directory on disk.</summary>
    /// <param name="rootDirectory">The directory to watch, including subdirectories.</param>
    /// <param name="virtualRoot">The virtual path that directory is mounted at.</param>
    /// <exception cref="DirectoryNotFoundException"><paramref name="rootDirectory" /> does not exist.</exception>
    public FileWatcher(string rootDirectory, VirtualPath virtualRoot) {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        if (virtualRoot.IsEmpty) {
            throw new ArgumentException("A watcher needs a virtual root.", nameof(virtualRoot));
        }

        osRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));

        if (!Directory.Exists(osRoot)) {
            throw new DirectoryNotFoundException($"There is no directory at '{osRoot}'.");
        }

        Root = virtualRoot;

        watcher = new(osRoot) {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,

            // The platform buffer, not ours. Overflowing it loses events before this process ever
            // sees them, and the only recovery is a rescan — so it is worth the 64 KB to make that
            // rare rather than routine.
            InternalBufferSize = 64 * 1024
        };

        watcher.Created += (_, arguments) => {
            Record(arguments.FullPath, FileChangeKind.Created);
            SweepNewDirectory(arguments.FullPath);
        };
        watcher.Changed += (_, arguments) => Record(arguments.FullPath, FileChangeKind.Changed);
        watcher.Deleted += (_, arguments) => Record(arguments.FullPath, FileChangeKind.Deleted);
        watcher.Renamed += (_, arguments) => Record(arguments.FullPath, FileChangeKind.Renamed, arguments.OldFullPath);
        watcher.Error += (_, _) => OnOverflow();
        watcher.EnableRaisingEvents = true;
    }

    /// <inheritdoc />
    public void Suppress(VirtualPath path) {
        lock (gate) {
            coalescer.Suppress(path, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    /// <inheritdoc />
    public int Drain(ICollection<FileChange> into) {
        lock (gate) {
            return coalescer.Drain(System.Diagnostics.Stopwatch.GetTimestamp(), into);
        }
    }

    /// <inheritdoc />
    public void ClearOverflow() {
        lock (gate) {
            HasOverflowed = false;
        }
    }

    /// <summary>Stops watching.</summary>
    public void Dispose() {
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    void Record(string fullPath, FileChangeKind kind, string? oldFullPath = null) {
        if (!TryToVirtual(fullPath, out var path)) {
            return;
        }

        var oldPath = default(VirtualPath);

        if (oldFullPath is not null && !TryToVirtual(oldFullPath, out oldPath)) {
            // A rename whose source is unreadable is still a change to the destination; losing the
            // provenance is better than losing the event.
            kind = FileChangeKind.Changed;
        }

        lock (gate) {
            coalescer.Record(new(path, kind, oldPath), System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    /// <summary>Reports whatever is already inside a directory that has just appeared.</summary>
    /// <remarks>
    ///     <para>
    ///         A watch on a subdirectory can only be added once the subdirectory exists, so anything
    ///         written into it between its creation and the watch taking effect is never reported.
    ///         That window is not theoretical: <c>mkdir Assets &amp;&amp; cp a.txt Assets/</c> loses
    ///         the file, and an asset pipeline that missed it would show an empty folder until
    ///         something else happened to touch it.
    ///     </para>
    ///     <para>
    ///         Platform-specific in cause and not in fix. inotify watches inodes and has the race;
    ///         macOS's FSEvents watches paths and does not; Windows watches a directory handle with
    ///         subtree semantics and does not either. Sweeping is correct everywhere and costs a
    ///         directory listing on a directory that was empty a moment ago — and the coalescer
    ///         collapses a duplicate report of a file the platform did deliver.
    ///     </para>
    /// </remarks>
    void SweepNewDirectory(string fullPath) {
        try {
            if (!Directory.Exists(fullPath)) {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.AllDirectories)) {
                Record(entry, FileChangeKind.Created);
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // The directory was removed, or is not readable, between the event and the sweep. Both
            // are ordinary races with whatever is writing, and neither is worth failing a watcher
            // over — the next event, or a rescan, covers it.
        }
    }

    void OnOverflow() {
        lock (gate) {
            HasOverflowed = true;

            // Everything pending is now suspect: the events that would have completed those changes
            // may be the ones that were lost. A rescan supersedes them.
            coalescer.Clear();
        }
    }

    bool TryToVirtual(string fullPath, out VirtualPath path) {
        var relative = Path.GetRelativePath(osRoot, fullPath);

        if (Path.DirectorySeparatorChar != VirtualPath.Separator) {
            relative = relative.Replace(Path.DirectorySeparatorChar, VirtualPath.Separator);
        }

        return VirtualPath.TryCreate(Root.IsRoot ? "/" + relative : Root.Value + "/" + relative, out path);
    }
}
