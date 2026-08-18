// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.IO.Watch;

/// <summary>
///     Turns the stream of events a filesystem actually produces into the changes a program meant to
///     hear about.
/// </summary>
/// <remarks>
///     <para>
///         Raw filesystem events are not a description of what the user did. Saving one file in a
///         text editor produces, depending on the editor, one write, or a truncate and three writes,
///         or a new temporary file followed by a rename over the original — and a hot-reload pipeline
///         that reacts to each of those separately compiles the same shader four times, twice from a
///         half-written file. This is where that becomes "one file changed".
///     </para>
///     <para>
///         Four things are handled, and each of them is a real editor's real behaviour:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Debouncing.</b> Nothing is reported until it has been quiet for the debounce
///             window, so a truncate-then-write is one change and not two, and a file still being
///             written is not reported at all.
///         </item>
///         <item>
///             <b>Atomic save.</b> A rename onto a path whose source was created inside the same
///             window is the write-elsewhere-then-rename pattern; it is reported as a change to the
///             destination, and the temporary file is not reported at all.
///         </item>
///         <item>
///             <b>Cancelling pairs.</b> Created-then-deleted is nothing. Deleted-then-created is a
///             change. A consumer that reloads on both reads the same file either way, but one that
///             drops a cache on delete would drop it for a file that never went away.
///         </item>
///         <item>
///             <b>Our own writes.</b> A path passed to <see cref="Suppress" /> is ignored for the
///             suppression window, and whatever was already pending for it is dropped. Without the
///             first, the asset pipeline writing an artefact wakes the watcher, which reimports,
///             which writes an artefact. Without the second, anything that touches a file and then
///             saves it inside the debounce gets its own write reported straight back.
///         </item>
///     </list>
///     <para>
///         Time is a parameter rather than a clock. Every method takes the current timestamp, which
///         means the debounce window can be tested exactly instead of approximately — a test for a
///         hundred-millisecond window that sleeps for a hundred milliseconds is a test that fails on
///         a loaded machine.
///     </para>
///     <para>
///         Not thread-safe. <see cref="FileWatcher" /> owns one and holds a lock over it, because the
///         events arrive on whichever thread the platform felt like using.
///     </para>
/// </remarks>
public sealed class FileChangeCoalescer {
    readonly Dictionary<VirtualPath, Pending> pending = [];
    readonly Dictionary<VirtualPath, long> suppressed = [];
    readonly int capacity;
    long sequence;

    /// <summary>How long a path must be quiet before its change is reported.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long a suppressed path stays suppressed.</summary>
    /// <remarks>
    ///     Generous compared to <see cref="Debounce" />, because the gap between writing a file and
    ///     the platform reporting it is not bounded by anything the program controls — FSEvents in
    ///     particular batches with a latency of its own.
    /// </remarks>
    public TimeSpan SuppressionWindow { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How many raw events were dropped because the buffer was full.</summary>
    /// <remarks>
    ///     Non-zero means the consumer is behind and the picture is incomplete, which is a rescan,
    ///     not a warning to ignore.
    /// </remarks>
    public long DroppedCount { get; private set; }

    /// <summary>How many paths are waiting for the debounce window to close.</summary>
    public int PendingCount => pending.Count;

    /// <summary>Creates a coalescer.</summary>
    /// <param name="capacity">How many distinct paths may be pending at once.</param>
    public FileChangeCoalescer(int capacity = 4096) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
    }

    /// <summary>Ignores events for a path for the next <see cref="SuppressionWindow" />.</summary>
    /// <param name="path">The path the program is about to write.</param>
    /// <param name="timestamp">The current <see cref="System.Diagnostics.Stopwatch.GetTimestamp" />.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What is already pending for that path goes with it, and that is not tidiness.</b>
    ///         Suppression used to be checked only on the way in, so an event recorded a moment
    ///         <em>before</em> the call survived and drained afterwards — and the window it had to
    ///         land in is the debounce, which is a quarter of a second in the editor. Anything that
    ///         touches a file and then saves it inside that window therefore got its own save
    ///         reported back to it: an import that rewrites a sidecar and then writes the asset, a
    ///         second Ctrl+S following a first, a document saved while an unrelated write to the
    ///         same path was still settling. In the editor the consequence is a document reloading
    ///         itself, which discards its undo history — a whole session's worth, for a race with a
    ///         timer.
    ///     </para>
    ///     <para>
    ///         Dropping it is also the <i>correct</i> answer rather than merely the convenient one.
    ///         A pending event describes contents this program is about to replace with its own; by
    ///         the time anybody could act on it, the file is what we wrote. Reporting it would be
    ///         reporting a state that no longer exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the exact key.</b> A pending rename is filed under its destination, so a
    ///         pending <c>/a.txt → /b.txt</c> is not this path's event even though it names it —
    ///         dropping that would lose a move somebody really made.
    ///     </para>
    /// </remarks>
    public void Suppress(VirtualPath path, long timestamp) {
        suppressed[path] = timestamp + (long)(SuppressionWindow.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        pending.Remove(path);
    }

    /// <summary>Feeds in one raw event.</summary>
    /// <param name="change">What the platform reported.</param>
    /// <param name="timestamp">The current <see cref="System.Diagnostics.Stopwatch.GetTimestamp" />.</param>
    public void Record(FileChange change, long timestamp) {
        if (IsSuppressed(change.Path, timestamp) || (change.Kind == FileChangeKind.Renamed && IsSuppressed(change.OldPath, timestamp))) {
            return;
        }

        if (change.Kind == FileChangeKind.Renamed && TryFoldAtomicSave(change, timestamp)) {
            return;
        }

        if (!pending.TryGetValue(change.Path, out var existing)) {
            if (pending.Count >= capacity) {
                DroppedCount++;
                return;
            }

            pending[change.Path] = new(change.Kind, change.OldPath, timestamp, sequence++);
            return;
        }

        var merged = Merge(existing.Kind, change.Kind);

        if (merged is null) {
            // Created then deleted: the file never existed as far as anyone downstream is concerned.
            pending.Remove(change.Path);
            return;
        }

        pending[change.Path] = existing with {
            Kind = merged.Value,
            OldPath = change.Kind == FileChangeKind.Renamed ? change.OldPath : existing.OldPath,
            LastTimestamp = timestamp
        };
    }

    /// <summary>Takes every change whose debounce window has closed.</summary>
    /// <param name="timestamp">The current <see cref="System.Diagnostics.Stopwatch.GetTimestamp" />.</param>
    /// <param name="into">Where to put them. Appended in the order the paths were first touched.</param>
    /// <returns>How many changes were produced.</returns>
    public int Drain(long timestamp, ICollection<FileChange> into) {
        ArgumentNullException.ThrowIfNull(into);

        if (pending.Count == 0) {
            return 0;
        }

        var window = (long)(Debounce.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        var settled = new List<(long Sequence, FileChange Change)>();

        foreach (var (path, entry) in pending) {
            if (timestamp - entry.LastTimestamp >= window) {
                settled.Add((entry.Sequence, new(path, entry.Kind, entry.OldPath)));
            }
        }

        // First-touched first. A rename's destination should not be reported before the change that
        // produced it just because a dictionary felt like it.
        settled.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));

        foreach (var (_, change) in settled) {
            pending.Remove(change.Path);
            into.Add(change);
        }

        // Expiring here rather than on a timer: this is the only method guaranteed to be called
        // regularly, and a suppression that outlives its window is a missed change.
        if (suppressed.Count > 0) {
            foreach (var (path, until) in suppressed) {
                if (timestamp >= until) {
                    suppressed.Remove(path);
                }
            }
        }

        return settled.Count;
    }

    /// <summary>Forgets everything pending. For a consumer that has just rescanned instead.</summary>
    public void Clear() {
        pending.Clear();
        suppressed.Clear();
    }

    static FileChangeKind? Merge(FileChangeKind first, FileChangeKind second) =>
        (first, second) switch {
            // A file that appeared and then vanished inside one window is not news.
            (FileChangeKind.Created, FileChangeKind.Deleted) => null,

            // Still new, however many times it was written before anyone looked.
            (FileChangeKind.Created, _) => FileChangeKind.Created,

            // Replaced in place — which is what a delete-then-write is, and reporting the delete
            // would have consumers drop caches for a file that is sitting right there.
            (FileChangeKind.Deleted, FileChangeKind.Created) => FileChangeKind.Changed,
            (FileChangeKind.Deleted, _) => FileChangeKind.Deleted,

            // Gone is gone, whatever it was doing beforehand.
            (_, FileChangeKind.Deleted) => FileChangeKind.Deleted,
            (_, FileChangeKind.Renamed) => FileChangeKind.Renamed,
            _ => FileChangeKind.Changed
        };

    bool TryFoldAtomicSave(FileChange change, long timestamp) {
        // The source appeared inside this same window, so it is a scratch file the editor wrote and
        // then moved into place. What the consumer wants to hear is that the destination changed.
        if (!pending.TryGetValue(change.OldPath, out var source) || source.Kind != FileChangeKind.Created) {
            return false;
        }

        pending.Remove(change.OldPath);

        var sequenceNumber = pending.TryGetValue(change.Path, out var destination) ? destination.Sequence : sequence++;
        pending[change.Path] = new(FileChangeKind.Changed, default, timestamp, sequenceNumber);
        return true;
    }

    bool IsSuppressed(VirtualPath path, long timestamp) {
        if (path.IsEmpty || !suppressed.TryGetValue(path, out var until)) {
            return false;
        }

        if (timestamp < until) {
            return true;
        }

        suppressed.Remove(path);
        return false;
    }

    readonly record struct Pending(FileChangeKind Kind, VirtualPath OldPath, long LastTimestamp, long Sequence);
}
