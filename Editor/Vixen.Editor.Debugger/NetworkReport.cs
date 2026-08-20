// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Net.Diagnostics;
using Vixen.Ui.Reactive;

namespace Vixen.Editor.Debugger;

/// <summary>What a <see cref="BandwidthLedger" /> adds up to, at one instant.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every field is a number, and that is the point of the type rather than an accident of
///         it.</b> A record of scalars has real value equality, so the signal holding one refuses an
///         equal reading — a server sitting idle produces no notification at all, and the panel does
///         no work for the frames where nothing was sent. The moment a list were added here that
///         would stop being true: two lists with the same contents are two different objects, so
///         every reading would notify whether or not anything had moved. The tables live in
///         <see cref="NetworkTable" /> for exactly that reason.
///     </para>
///     <para>
///         ⚠ <b>Internal, deliberately.</b> Nothing outside the panel reads it — the tests assert on
///         the elements, which is the only thing that proves a markup panel updated at all — and a
///         public view model would be a second vocabulary for numbers <c>Vixen.Net</c> already
///         names. Every field below is a property the ledger already exposes.
///     </para>
/// </remarks>
/// <param name="Attached">Whether there is a ledger at all.</param>
/// <param name="KilobitsPerSecond">What the whole lot comes to, as a rate.</param>
/// <param name="TotalBits">Everything accounted for.</param>
/// <param name="TotalCount">How many records and calls that was.</param>
/// <param name="DeltaCount">Snapshot records sent as a difference.</param>
/// <param name="WholeCount">Snapshot records sent whole.</param>
/// <param name="Elapsed">How long the reading covers.</param>
/// <param name="TracksObjects">Whether per-object attribution is on.</param>
readonly record struct NetworkReport(
    bool Attached,
    double KilobitsPerSecond,
    long TotalBits,
    long TotalCount,
    long DeltaCount,
    long WholeCount,
    TimeSpan Elapsed,
    bool TracksObjects
) {
    /// <summary>Nothing attached, which is what an editor with no game running shows.</summary>
    public static NetworkReport Empty { get; }

    /// <summary>Takes a reading.</summary>
    /// <param name="ledger">The ledger, or null when nothing is attached.</param>
    /// <returns>The reading.</returns>
    public static NetworkReport Of(BandwidthLedger? ledger) =>
        ledger is null
            ? Empty
            : new(
                Attached: true,
                ledger.KilobitsPerSecond,
                ledger.TotalBits,
                ledger.TotalCount,
                ledger.DeltaCount,
                ledger.WholeCount,
                ledger.Elapsed,
                ledger.TrackObjects
            );

    /// <summary>What the whole lot came to, in bytes.</summary>
    public double Bytes => TotalBits / 8d;

    /// <summary>How many replicated values were sent, however they went.</summary>
    public long RecordCount => DeltaCount + WholeCount;

    /// <summary>How many of those went as a difference rather than whole, from zero to one.</summary>
    /// <remarks>
    ///     The number a delta encoder exists to move, and the one that says whether it is working. A
    ///     ratio rather than a count because the count grows for as long as the server is up: "four
    ///     million deltas" says nothing, "eleven per cent of records went whole" says the baselines
    ///     are being lost.
    /// </remarks>
    public float DeltaShare => RecordCount == 0 ? 0f : (float) (DeltaCount / (double) RecordCount);

    /// <summary>A count of bytes, in the units a bandwidth budget is read in.</summary>
    /// <param name="bytes">The count.</param>
    /// <returns>It, as text.</returns>
    /// <remarks>
    ///     Binary units, because a snapshot buffer and an MTU are both powers of two and decimal
    ///     kilobytes against them would be a report whose numbers never line up with the ones in the
    ///     transport's own remarks. The rate is the exception and is decimal on purpose: kilobits a
    ///     second is the network's own unit and is a thousand bits everywhere.
    /// </remarks>
    public static string Size(double bytes) {
        if (bytes < 1024d) {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} B");
        }

        if (bytes < 1024d * 1024d) {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:N1} KiB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):N1} MiB");
    }
}

/// <summary>One of the ledger's five answers to "where did it go", as a live column of rows.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An object that holds a signal, and that is what the <c>@for</c> key rule asks for.</b>
///         <c>BuildContext.For</c> reuses a surviving key's region and does <i>not</i> re-run the
///         body, so a loop over five immutable table values would rebuild all five whole tables every
///         time a single byte moved anywhere — the entries are freshly allocated lists, so a value
///         key changes on every reading. Five objects made once and never replaced are keys that
///         always survive; what changes underneath them is <see cref="Entries" />, which is a signal,
///         so the inner loop follows it and each row keeps or loses its own region on its own value.
///     </para>
///     <para>
///         ⚠ <b><see cref="Largest" /> is read off the sorted list rather than carried beside it.</b>
///         Every <c>Top…</c> method returns its entries dearest first, so the first row is the
///         largest by construction — a second field saying so would be a second thing to keep in
///         step with the first.
///     </para>
/// </remarks>
/// <param name="heading">What the column is a breakdown by.</param>
sealed class NetworkTable(string heading) {
    readonly Signal<IReadOnlyList<BandwidthEntry>> entries = new([]);
    readonly Signal<string> prefix = new(string.Empty);

    /// <summary>What the column is a breakdown by.</summary>
    public string Heading { get; } = heading;

    /// <summary>The rows, dearest first.</summary>
    public IReadOnlyList<BandwidthEntry> Entries => entries.Value;

    /// <summary>What the dearest row cost, which is what the bars are drawn against.</summary>
    public long Largest => entries.Value.Count == 0 ? 0 : entries.Value[0].Bits;

    /// <summary>What every row in this column has in front of its name, and none of them needs.</summary>
    /// <remarks>
    ///     ⚠ <b>A signal, and the reason is the <c>@for</c> key rule read one step further than it is
    ///     usually read.</b> A row keyed on its value keeps its region while its value is unchanged,
    ///     so the effect that writes its name runs once and then only when something it <i>read</i>
    ///     changes. It reads the entry — captured, and unchanged by definition — and this. A plain
    ///     field would therefore leave a surviving row showing a name with the previous reading's
    ///     namespace cut off it, which is not a stale name but a wrong one. Making the store a signal
    ///     is what puts that row back in the dependency graph.
    /// </remarks>
    public string Prefix => prefix.Value;

    /// <summary>A row's name with <see cref="Prefix" /> taken off the front.</summary>
    /// <param name="name">The name.</param>
    /// <returns>What is left of it.</returns>
    public string Short(string name) {
        var shared = Prefix;

        return shared.Length > 0 && name.StartsWith(shared, StringComparison.Ordinal)
            ? name[shared.Length..]
            : name;
    }

    /// <summary>Puts a fresh set of rows in the column.</summary>
    /// <param name="rows">Them.</param>
    public void Show(IReadOnlyList<BandwidthEntry> rows) {
        prefix.Value = Common(rows);
        entries.Value = rows;
    }

    /// <summary>The namespace every row in a column shares, or nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the column rather than from a list of prefixes to strip.</b> The sample
    ///     soak's own report hard-codes <c>Vixen.Net.</c> and its game's namespace, which works for
    ///     that game and no other. What is actually true is that the part of a name every row shares
    ///     carries no information in a column — and cutting it back to a dot means the answer is
    ///     always a namespace and never half an identifier. A column of connection names has no dots
    ///     at all, so nothing is taken off it.
    /// </remarks>
    static string Common(IReadOnlyList<BandwidthEntry> rows) {
        if (rows.Count == 0) {
            return string.Empty;
        }

        var prefix = rows[0].Name.AsSpan();

        for (var index = 1; index < rows.Count && prefix.Length > 0; index++) {
            var other = rows[index].Name.AsSpan();
            var shared = 0;

            while (shared < prefix.Length && shared < other.Length && prefix[shared] == other[shared]) {
                shared++;
            }

            prefix = prefix[..shared];
        }

        var dot = prefix.LastIndexOf('.');

        return dot < 0 ? string.Empty : new(prefix[..(dot + 1)]);
    }
}

/// <summary>One line of the packet pane: a record, and where in the packet it was.</summary>
/// <remarks>
///     ⚠ <b>The slot is in the key and the record alone would not be enough.</b> A
///     <see cref="SnapshotRecord" /> is a record struct, so its value is its identity — which is what
///     a <c>@for</c> key wants — and a well-formed snapshot cannot hold two equal ones, carrying at
///     most one record per object per component type. A <i>malformed</i> one can, and a malformed
///     snapshot is precisely the one somebody opened this panel to look at. Two equal keys in one
///     loop is not something <c>BuildContext.For</c> can be asked to reconcile, so the position in
///     the packet — worth showing anyway — goes in the key.
/// </remarks>
/// <param name="Slot">Where in the packet it was, counting from one.</param>
/// <param name="Record">The record, exactly as the inspector found it.</param>
readonly record struct PacketRow(int Slot, SnapshotRecord Record);

/// <summary>One snapshot, taken apart — or the absence of one.</summary>
/// <remarks>
///     ⚠ <b>A separate signal from the reading above, because the two have different clocks.</b> The
///     ledger's totals move on every tick; a packet changes only when the host has produced a newer
///     one. Held together in one value, an idle capture's rows would be re-evaluated every time a bit
///     of bandwidth moved anywhere — the keys would save the elements, but every binding inside them
///     would run again for nothing.
/// </remarks>
/// <param name="Present">Whether there is a packet at all.</param>
/// <param name="Contents">What was in it.</param>
/// <param name="Rows">Its records, numbered.</param>
readonly record struct NetworkPacket(bool Present, SnapshotContents Contents, IReadOnlyList<PacketRow> Rows) {
    /// <summary>No packet, which is what a host with no capture source shows.</summary>
    /// <remarks>
    ///     ⚠ <b>Spelled out rather than left as <c>default</c>, because the difference is a null
    ///     reference.</b> A record struct's default has null for every reference member, so
    ///     <c>default(NetworkPacket).Rows</c> is null where an empty list is what a loop needs.
    /// </remarks>
    public static NetworkPacket None { get; } = new(Present: false, default, []);

    /// <summary>Numbers the records of a decoded snapshot.</summary>
    /// <param name="contents">What the inspector found.</param>
    /// <returns>The packet.</returns>
    public static NetworkPacket Of(SnapshotContents contents) {
        var rows = new PacketRow[contents.Records.Count];

        for (var index = 0; index < rows.Length; index++) {
            rows[index] = new(index + 1, contents.Records[index]);
        }

        return new(Present: true, contents, rows);
    }
}
