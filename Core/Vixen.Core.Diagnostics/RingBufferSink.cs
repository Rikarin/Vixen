// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Collections;

namespace Vixen.Core.Diagnostics;

/// <summary>One log line, as it sits in the ring.</summary>
/// <param name="Timestamp">When it was written.</param>
/// <param name="Level">Its severity.</param>
/// <param name="EventId">The stable numeric id — see <c>docs/manual/log-events.md</c>.</param>
/// <param name="Category">The logger's category, which is the type that logged it.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="Exception">The exception, if one was attached.</param>
/// <param name="ThreadId">Which managed thread wrote it.</param>
/// <param name="SuppressedCount">
///     How many identical records a <see cref="LogRateLimiter" /> dropped since the last one that
///     got through — the <c>N</c> in <c>… (repeated N times)</c>, and zero when nothing was
///     suppressed or no limiter is attached.
/// </param>
public sealed record LogRecord(
    DateTimeOffset Timestamp,
    LogLevel Level,
    EventId EventId,
    string Category,
    string Message,
    Exception? Exception,
    int ThreadId,
    int SuppressedCount = 0
);

/// <summary>
///     The always-on log sink: the last N records in a ring, in memory, in every build. The editor
///     console reads it live and the crash reporter dumps it.
/// </summary>
/// <remarks>
///     <para>
///         A log that only exists on disk is a log nobody has when it matters — the interesting
///         moment is usually the thirty seconds before a crash, and asking a player for a file is
///         asking for nothing. A bounded ring costs a fixed amount of memory and always has that
///         thirty seconds.
///     </para>
///     <para>
///         Per-category minimum levels come from <see cref="LogFilter" />, so "turn on verbose asset
///         loading without drowning in render spam" works. Rate limiting is available and off:
///         everything else is a view of the log and can afford to lose repeats, whereas this is the
///         record the crash reporter dumps, and a ring that dropped the four thousand identical
///         lines before the crash would be hiding the shape of the failure.
///     </para>
///     <para>
///         <b>Where this is not yet what doc 13 asks for.</b> Records hold a formatted
///         <see cref="string" />, not UTF-8 bytes with the structured fields intact, so an enabled
///         log line allocates. The disabled path already allocates nothing — a <c>[LoggerMessage]</c>
///         method returns before touching its arguments — and the enabled path is not in any hot
///         loop, because <c>[HotPath]</c> methods are barred from logging. Packing records into a
///         byte ring is the optimisation to make when a profile asks for it, and doing it now would
///         be guessing at the field layout the editor console wants.
///     </para>
/// </remarks>
public sealed class RingBufferSink : LogRecordSink {
    /// <summary>How many records the ring holds when no capacity is given.</summary>
    public const int DefaultCapacity = 100_000;

    readonly RingBuffer<LogRecord> records;
    readonly Lock gate = new();

    /// <summary>How many records have ever been accepted, which is <see cref="Written" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted here rather than derived from <c>OverwrittenCount + Count</c>, because
    ///     <see cref="Clear" /> makes that sum go backwards.</b> The ring keeps its overwritten count
    ///     across a clear and resets its live count to zero, so the sum drops by however much was in
    ///     it — and the records enqueued afterwards would be handed sequence numbers a reader had
    ///     already seen and would therefore skip. A counter that only ever goes up is the whole
    ///     contract <see cref="CopySince" /> rests on.
    /// </remarks>
    long written;

    /// <summary>How many records the ring holds.</summary>
    public int Capacity => records.Capacity;

    /// <summary>How many records it currently holds.</summary>
    public int Count {
        get {
            lock (gate) {
                return records.Count;
            }
        }
    }

    /// <summary>
    ///     How many records have been overwritten. Distinguishes "the log is missing its beginning"
    ///     from "nothing was logged", which a bare ring cannot.
    /// </summary>
    public long DroppedCount {
        get {
            lock (gate) {
                return records.OverwrittenCount;
            }
        }
    }

    /// <summary>Creates a sink holding at most <paramref name="capacity" /> records.</summary>
    /// <param name="capacity">The ring size.</param>
    /// <param name="filter">
    ///     The filter to use, or <see langword="null" /> for one of this sink's own.
    /// </param>
    public RingBufferSink(int capacity = DefaultCapacity, LogFilter? filter = null) : base(filter) =>
        records = new(capacity);

    /// <summary>Copies the records out, oldest first.</summary>
    /// <returns>The current contents of the ring.</returns>
    public LogRecord[] Snapshot() {
        lock (gate) {
            var snapshot = new LogRecord[records.Count];
            records.CopyTo(snapshot);
            return snapshot;
        }
    }

    /// <summary>Copies the newest records out, oldest of them first.</summary>
    /// <param name="destination">Where they go. Its length is how many are wanted.</param>
    /// <returns>How many were written, which is fewer if the ring holds fewer.</returns>
    /// <remarks>
    ///     What a log overlay wants and what <see cref="Snapshot" /> is wrong for: the ring holds a
    ///     hundred thousand records by default and a tail is thirty, so snapshotting once a frame to
    ///     read the end of it would allocate several megabytes a frame to show half a screen of text.
    /// </remarks>
    public int CopyTail(Span<LogRecord> destination) {
        lock (gate) {
            var take = Math.Min(destination.Length, records.Count);
            var first = records.Count - take;

            for (var index = 0; index < take; index++) {
                destination[index] = records[first + index];
            }

            return take;
        }
    }

    /// <summary>How many records have ever reached the ring, the overwritten ones included.</summary>
    /// <remarks>
    ///     A sequence number rather than a count: it never goes backwards, and a reader that
    ///     remembers the value it last saw can ask <see cref="CopySince" /> for exactly what has
    ///     arrived since. <see cref="Count" /> cannot do that, because a full ring's count stops
    ///     changing while records keep coming.
    /// </remarks>
    public long Written {
        get {
            lock (gate) {
                return written;
            }
        }
    }

    /// <summary>Copies the records written since a sequence number.</summary>
    /// <param name="sequence">
    ///     How many records the caller has already seen, advanced past everything this call accounts
    ///     for — including records that were overwritten before it got to them.
    /// </param>
    /// <param name="destination">Where they go. Its length is how many are taken at once.</param>
    /// <returns>How many were written. Zero means the caller is up to date.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>What the editor's console reads, and the reason it does not allocate per line.</b>
    ///         <see cref="Snapshot" /> copies the whole ring — a hundred thousand records by default —
    ///         and a console that called it once a frame to find the four new lines would allocate
    ///         several megabytes a frame to show four rows. This copies only what is new, into a
    ///         buffer the caller keeps.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A caller that falls further behind than the ring is deep loses the difference,
    ///         silently, and that is the correct behaviour.</b> The records are gone — that is what a
    ///         ring is — and <paramref name="sequence" /> is advanced past them so the next call
    ///         returns what still exists rather than looping for ever on records nobody has.
    ///         <see cref="DroppedCount" /> is how a reader notices it happened.
    ///     </para>
    ///     <para>
    ///         Called in a loop until it returns zero: one call takes at most
    ///         <c>destination.Length</c>, so a burst larger than the buffer arrives over several
    ///         calls rather than being truncated.
    ///     </para>
    /// </remarks>
    public int CopySince(ref long sequence, Span<LogRecord> destination) {
        lock (gate) {
            // The sequence number of `records[0]`: everything before it is gone, whether it was
            // overwritten or cleared.
            var oldest = written - records.Count;

            // Clamped at both ends. Below, for a caller that has fallen behind the ring; above, for
            // one holding a sequence from before a `Clear`.
            var from = Math.Clamp(sequence, oldest, written);
            var take = (int) Math.Min(destination.Length, written - from);

            for (var index = 0; index < take; index++) {
                destination[index] = records[(int) (from - oldest) + index];
            }

            sequence = from + take;
            return take;
        }
    }

    /// <summary>Empties the ring. Does not reset <see cref="DroppedCount" />.</summary>
    public void Clear() {
        lock (gate) {
            records.Clear();
        }
    }

    /// <inheritdoc />
    protected override void Write(LogRecord record) {
        lock (gate) {
            records.Enqueue(record);
            written++;
        }
    }
}
