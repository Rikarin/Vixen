// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;

namespace Vixen.Editor.Ui;

/// <summary>Which severities the console is showing.</summary>
/// <remarks>
///     Four buttons rather than six, because <c>Trace</c> and <c>Debug</c> are one thing to a person
///     reading a console and <c>Critical</c> is an error that was worse. A filter with a button per
///     <see cref="LogLevel" /> is one where the difference between Trace and Debug is a decision the
///     reader has to make before they can see anything.
/// </remarks>
[Flags]
public enum ConsoleLevels : byte {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Trace and Debug.</summary>
    Verbose = 1 << 0,

    /// <summary>Information.</summary>
    Info = 1 << 1,

    /// <summary>Warning.</summary>
    Warning = 1 << 2,

    /// <summary>Error and Critical.</summary>
    Error = 1 << 3,

    /// <summary>What the console starts on: everything but the verbose stream.</summary>
    Default = Info | Warning | Error,

    /// <summary>All four.</summary>
    All = Verbose | Info | Warning | Error
}

/// <summary>One line of the console: a record, and how many identical ones it stands for.</summary>
/// <param name="Record">The newest record the row represents.</param>
/// <param name="Repeats">
///     How many records the row stands for. One unless duplicates are being collapsed.
/// </param>
public readonly record struct ConsoleRow(LogRecord Record, int Repeats);

/// <summary>What the console is showing, and why it is not the whole ring.</summary>
/// <remarks>
///     <para>
///         <b>The half of the console that is not chrome, and the half worth testing.</b> Which
///         records are visible, what a level toggle means, what "collapse duplicates" collapses and
///         what the badges count are all decisions with answers, and none of them needs a
///         <c>UiDocument</c> to check. <see cref="ConsoleView" /> is the rows and the buttons over
///         this.
///     </para>
///     <para>
///         ⚠ <b>It must not allocate per line, and doc 20 says so in as many words: "a game logging
///         per frame into a panel that keeps strings is a leak with a UI".</b> Three things make that
///         true. The sink's ring is fixed-size and already holds the records; this pulls only what is
///         new through <see cref="RingBufferSink.CopySince" /> into a buffer it keeps, rather than
///         snapshotting a hundred thousand records to find the four that arrived; and the visible set
///         is a list of <i>indices</i>, so a filter that matches nothing still costs nothing.
///     </para>
///     <para>
///         ⚠ <b>It keeps its own bounded copy rather than reading the ring on demand.</b> A console
///         filtered to errors has to know about errors that scrolled out of the last screenful, and a
///         ring read positionally would renumber underneath the filter every time a record arrived.
///         The copy is references to records the sink already allocated, capped at
///         <see cref="Capacity" />, and trimmed in blocks — a per-record <c>RemoveAt(0)</c> on a list
///         of ten thousand is the shape of the problem this paragraph is about.
///     </para>
/// </remarks>
public sealed class ConsoleModel {
    /// <summary>How many records a console keeps when no capacity is given.</summary>
    /// <remarks>
    ///     ⚠ <b>A tenth of the sink's default, on purpose.</b> The ring exists so a crash report has
    ///     the thirty seconds before the crash; the console exists so a person can read what just
    ///     happened. Mirroring a hundred thousand records would hold a hundred thousand of them alive
    ///     for as long as the editor runs, for a list nobody scrolls to the end of.
    /// </remarks>
    public const int DefaultCapacity = 10_000;

    /// <summary>How much of the buffer a trim reclaims.</summary>
    /// <remarks>A quarter, because trimming one record per arrival makes every log line an O(n)
    ///     shuffle of the list — which is the cost this class exists to not have.</remarks>
    const float TrimFraction = 0.25f;

    /// <summary>How many records one <see cref="Pull" /> takes at a time.</summary>
    const int PullBatch = 256;

    readonly RingBufferSink sink;
    readonly List<LogRecord> received = [];
    readonly List<int> visible = [];
    readonly List<string> categories = [];
    readonly HashSet<string> known = new(StringComparer.Ordinal);
    readonly LogRecord[] batch = new LogRecord[PullBatch];

    /// <summary>Where a collapsed row for a given message already is, by index into <see cref="visible" />.</summary>
    readonly Dictionary<(LogLevel Level, string Category, string Message), int> collapsed = [];

    /// <summary>How many records each visible row stands for.</summary>
    readonly List<int> repeats = [];

    long sequence;
    ConsoleLevels levels = ConsoleLevels.Default;
    string? search;
    string? category;
    bool collapse;

    /// <summary>Watches a sink.</summary>
    /// <param name="sink">The ring the editor's logging goes into.</param>
    /// <param name="capacity">How many records to keep.</param>
    public ConsoleModel(RingBufferSink sink, int capacity = DefaultCapacity) {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        this.sink = sink;
        Capacity = capacity;

        // ⚠ Started at the sink's current end rather than at zero. A console opened an hour into a
        // session would otherwise replay the hour — which is not wrong, exactly, but it is a panel
        // that takes a visible moment to open and shows a screenful of history nobody asked for.
        // `Sink.Snapshot()` is how a caller that does want the history gets it.
        sequence = sink.Written;
    }

    /// <summary>How many records it keeps.</summary>
    public int Capacity { get; }

    /// <summary>Raised when the visible set changed.</summary>
    /// <remarks>What the view redraws from, so it does not rebuild rows on a frame where nothing
    ///     was logged.</remarks>
    public event Action<ConsoleModel>? Changed;

    /// <summary>Which severities are shown.</summary>
    public ConsoleLevels Levels {
        get => levels;

        set {
            if (levels != value) {
                levels = value;
                Refilter();
            }
        }
    }

    /// <summary>What the search box says, or <see langword="null" />.</summary>
    /// <remarks>Matched against the message and the category, case-insensitively — the two things
    ///     somebody types into a console's search box.</remarks>
    public string? Search {
        get => search;

        set {
            var normalised = string.IsNullOrWhiteSpace(value) ? null : value;

            if (!string.Equals(search, normalised, StringComparison.Ordinal)) {
                search = normalised;
                Refilter();
            }
        }
    }

    /// <summary>Which category is shown, or <see langword="null" /> for all of them.</summary>
    public string? Category {
        get => category;

        set {
            if (!string.Equals(category, value, StringComparison.Ordinal)) {
                category = value;
                Refilter();
            }
        }
    }

    /// <summary>Whether identical lines are folded into one row with a count.</summary>
    /// <remarks>
    ///     ⚠ <b>Identical anywhere, not merely adjacent.</b> A frame loop logging the same warning
    ///     interleaved with three others produces four repeating lines, and collapsing only runs of
    ///     the same line would fold none of them — which is the case the feature exists for.
    /// </remarks>
    public bool Collapse {
        get => collapse;

        set {
            if (collapse != value) {
                collapse = value;
                Refilter();
            }
        }
    }

    /// <summary>How many rows are visible.</summary>
    public int Count => visible.Count;

    /// <summary>A visible row.</summary>
    /// <param name="index">Which one, newest last.</param>
    public ConsoleRow this[int index] => new(received[visible[index]], repeats[index]);

    /// <summary>Every category seen since the console was cleared, in the order they first appeared.</summary>
    public IReadOnlyList<string> Categories => categories;

    /// <summary>How many errors have been received, whatever the filter says.</summary>
    /// <remarks>
    ///     ⚠ <b>The badges count what arrived, not what is shown.</b> A console filtered to errors
    ///     whose warning badge went to zero would be one that cannot tell "there are no warnings"
    ///     from "warnings are hidden" — which is the entire question somebody clicks a badge to ask.
    /// </remarks>
    public int Errors { get; private set; }

    /// <inheritdoc cref="Errors" />
    public int Warnings { get; private set; }

    /// <inheritdoc cref="Errors" />
    public int Infos { get; private set; }

    /// <inheritdoc cref="Errors" />
    public int Verbose { get; private set; }

    /// <summary>How many records were logged faster than this could keep up with.</summary>
    /// <remarks>
    ///     ⚠ <b>Worth showing, because the alternative is a console that quietly lies.</b> A ring
    ///     that has wrapped is missing its beginning, and "there is no line about the failure" and
    ///     "the line about the failure was overwritten" have very different fixes.
    /// </remarks>
    public long Dropped => sink.DroppedCount;

    /// <summary>Takes whatever has been logged since the last call.</summary>
    /// <returns>Whether anything arrived.</returns>
    /// <remarks>Called once a frame by the view. Cheap when nothing was logged: one lock, one
    ///     comparison, no allocation.</remarks>
    public bool Pull() {
        var any = false;
        int taken;

        while ((taken = sink.CopySince(ref sequence, batch)) > 0) {
            for (var index = 0; index < taken; index++) {
                Accept(batch[index]);
            }

            any = true;

            // A burst larger than the ring is one where the tail is all that survived, and the loop
            // above has already caught up to it — but a burst larger than the batch and smaller than
            // the ring needs another turn.
            if (taken < batch.Length) {
                break;
            }
        }

        if (any) {
            Trim();
            Changed?.Invoke(this);
        }

        return any;
    }

    /// <summary>Throws away everything the console is showing.</summary>
    /// <remarks>
    ///     ⚠ <b>The sink is cleared too, and that is the honest reading of the button.</b> A Clear
    ///     that emptied only the panel would leave the crash reporter's ring holding what the user
    ///     believes they discarded — and the next console opened in the same session would show it
    ///     again.
    /// </remarks>
    public void Clear() {
        sink.Clear();
        sequence = sink.Written;

        received.Clear();
        visible.Clear();
        repeats.Clear();
        collapsed.Clear();
        categories.Clear();
        known.Clear();

        Errors = 0;
        Warnings = 0;
        Infos = 0;
        Verbose = 0;

        Changed?.Invoke(this);
    }

    /// <summary>Which of the four buttons a level belongs to.</summary>
    public static ConsoleLevels BucketOf(LogLevel level) =>
        level switch {
            LogLevel.Critical or LogLevel.Error => ConsoleLevels.Error,
            LogLevel.Warning => ConsoleLevels.Warning,
            LogLevel.Information => ConsoleLevels.Info,
            _ => ConsoleLevels.Verbose
        };

    void Accept(LogRecord record) {
        received.Add(record);

        switch (BucketOf(record.Level)) {
            case ConsoleLevels.Error:
                Errors++;
                break;

            case ConsoleLevels.Warning:
                Warnings++;
                break;

            case ConsoleLevels.Info:
                Infos++;
                break;

            default:
                Verbose++;
                break;
        }

        if (known.Add(record.Category)) {
            categories.Add(record.Category);
        }

        if (Matches(record)) {
            Show(received.Count - 1, record);
        }
    }

    /// <summary>Puts a record on screen, folding it into an existing row if it is a duplicate.</summary>
    void Show(int index, LogRecord record) {
        if (collapse) {
            var key = (record.Level, record.Category, record.Message);

            if (collapsed.TryGetValue(key, out var row)) {
                // ⚠ The *newest* record wins the row, not the first. A collapsed line is read for
                // "is this still happening", and a row frozen at the first occurrence's timestamp
                // answers the opposite question.
                visible[row] = index;
                repeats[row]++;

                Changed?.Invoke(this);
                return;
            }

            collapsed[key] = visible.Count;
        }

        visible.Add(index);
        repeats.Add(1);
    }

    bool Matches(LogRecord record) {
        if ((levels & BucketOf(record.Level)) == 0) {
            return false;
        }

        if (category is not null && !string.Equals(record.Category, category, StringComparison.Ordinal)) {
            return false;
        }

        return search is null
            || record.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
            || record.Category.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rebuilds the visible set from what has been received.</summary>
    /// <remarks>
    ///     A full pass over the buffer, which is what changing a filter costs and is why the filters
    ///     are properties rather than something asked per row: the alternative is running the
    ///     predicate for every row on every frame the console is on screen.
    /// </remarks>
    void Refilter() {
        visible.Clear();
        repeats.Clear();
        collapsed.Clear();

        for (var index = 0; index < received.Count; index++) {
            var record = received[index];

            if (Matches(record)) {
                Show(index, record);
            }
        }

        Changed?.Invoke(this);
    }

    /// <summary>Drops the oldest records once the buffer is over capacity.</summary>
    /// <remarks>
    ///     ⚠ <b>In a block, and the visible set is rebuilt afterwards.</b> Every index in
    ///     <see cref="visible" /> points into <see cref="received" />, so removing from the front
    ///     shifts all of them — and adjusting each one is the same walk as rebuilding, without being
    ///     obviously correct. A trim happens once per <see cref="TrimFraction" /> of the capacity, so
    ///     the walk is amortised over thousands of records.
    /// </remarks>
    void Trim() {
        if (received.Count <= Capacity) {
            return;
        }

        received.RemoveRange(0, Math.Max(received.Count - Capacity, (int) (Capacity * TrimFraction)));
        Refilter();
    }
}
