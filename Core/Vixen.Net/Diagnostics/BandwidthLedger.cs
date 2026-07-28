// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;

namespace Vixen.Net.Diagnostics;

/// <summary>What one thing cost.</summary>
/// <param name="Name">What it is.</param>
/// <param name="Bits">How many bits went on it.</param>
/// <param name="Count">How many times it was sent.</param>
public readonly record struct BandwidthEntry(string Name, long Bits, long Count) {
    /// <summary>The cost in bytes, which is what a bandwidth budget is written in.</summary>
    public double Bytes => Bits / 8d;

    /// <summary>The mean cost of one of these, in bits.</summary>
    public double MeanBits => Count == 0 ? 0 : Bits / (double)Count;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name}: {Bytes:N0} B over {Count:N0} ({MeanBits:N1} bits each)");
}

/// <summary>Where the bandwidth went.</summary>
/// <remarks>
///     <para>
///         <b>"What is eating my thirty kilobits" is the question, and it has four answers.</b> Which
///         component type, which <i>field</i> of it, which remote call, and which connection. A total
///         is not an answer to any of them, and a profiler that only reports the total is one whose
///         first use is to tell you that you need a better profiler.
///     </para>
///     <para>
///         <b>Counted in bits, reported in bytes.</b> Everything here is bit-packed, so rounding each
///         record up to a byte before adding it up would invent up to seven bits per record — which
///         on a snapshot of a dozen small records is most of the answer.
///     </para>
///     <para>
///         Off unless something attaches one. When attached the cost is a dictionary increment per
///         record, which is the right side of the trade for a thing you want on when the bug happens
///         — the same argument <c>Vixen.Core.Diagnostics</c>' profiler makes for itself. Per-object
///         attribution is the exception and is separately opt-in, because it is the one whose table
///         grows with the size of the world rather than with the number of component types.
///     </para>
/// </remarks>
public sealed class BandwidthLedger {
    readonly Dictionary<string, Tally> byComponent = [];
    readonly Dictionary<string, Tally> byField = [];
    readonly Dictionary<string, Tally> byCall = [];
    readonly Dictionary<uint, Tally> byConnection = [];
    readonly Dictionary<uint, Tally> byObject = [];

    /// <summary>Whether to attribute to individual networked objects as well as to types.</summary>
    /// <remarks>
    ///     Off by default. The other tables are bounded by how many component types and remote calls
    ///     a game declares; this one is bounded by how many objects exist, which is the number the
    ///     game exists to make large.
    /// </remarks>
    public bool TrackObjects { get; set; }

    /// <summary>How long has been accounted for, so the totals can be turned into a rate.</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>Everything, in bits.</summary>
    public long TotalBits { get; private set; }

    /// <summary>How many records and calls that was.</summary>
    public long TotalCount { get; private set; }

    /// <summary>Snapshot records sent as a difference.</summary>
    public long DeltaCount { get; private set; }

    /// <summary>Snapshot records sent whole.</summary>
    public long WholeCount { get; private set; }

    /// <summary>What the whole lot comes to, in kilobits a second.</summary>
    public double KilobitsPerSecond => Elapsed <= TimeSpan.Zero ? 0 : TotalBits / Elapsed.TotalSeconds / 1000d;

    /// <summary>Records the time a report covers, so its totals can be read as rates.</summary>
    /// <param name="elapsed">How much time has passed.</param>
    public void Advance(TimeSpan elapsed) => Elapsed += elapsed;

    /// <summary>Takes one replicated value.</summary>
    /// <param name="to">Who it went to.</param>
    /// <param name="of">Which object it was about.</param>
    /// <param name="typeName">Which component.</param>
    /// <param name="bits">What it cost, header included — that is part of the answer too.</param>
    /// <param name="asDelta">Whether it went as a difference or whole.</param>
    public void Record(PlayerId to, NetworkId of, string typeName, int bits, bool asDelta) {
        Add(byComponent, typeName, bits);
        Add(byConnection, to.Value, bits);

        if (TrackObjects) {
            Add(byObject, of.Value, bits);
        }

        if (asDelta) {
            DeltaCount++;
        } else {
            WholeCount++;
        }

        TotalBits += bits;
        TotalCount++;
    }

    /// <summary>Takes what each field of a value cost, within a record already recorded.</summary>
    /// <param name="typeName">Which component.</param>
    /// <param name="lanes">Its layout.</param>
    /// <param name="costs">What each lane cost, as the delta codec measured it.</param>
    /// <remarks>
    ///     Not added to the totals: these bits are inside a record <see cref="Record" /> already
    ///     counted, and counting them twice would make the report add up to more than went out. This
    ///     table answers a different question — <i>within</i> a component, which field is expensive —
    ///     and it is the one that tells you a rotation is costing three times its position.
    /// </remarks>
    public void RecordFields(string typeName, ReadOnlySpan<Messaging.WireLane> lanes, ReadOnlySpan<int> costs) {
        for (var i = 0; i < lanes.Length && i < costs.Length; i++) {
            Add(byField, $"{typeName}.{lanes[i].Name}", costs[i]);
        }
    }

    /// <summary>Takes one remote call.</summary>
    /// <param name="with">The connection it crossed.</param>
    /// <param name="method">Which call, as the manifest names it.</param>
    /// <param name="bits">What it cost.</param>
    public void RecordCall(PlayerId with, string method, int bits) {
        Add(byCall, method, bits);
        Add(byConnection, with.Value, bits);

        TotalBits += bits;
        TotalCount++;
    }

    /// <summary>The most expensive component types.</summary>
    /// <param name="count">How many to return.</param>
    /// <returns>Them, dearest first.</returns>
    public IReadOnlyList<BandwidthEntry> TopComponents(int count = 10) => Top(byComponent, count);

    /// <summary>The most expensive fields, across every component.</summary>
    /// <param name="count">How many to return.</param>
    /// <returns>Them, dearest first.</returns>
    public IReadOnlyList<BandwidthEntry> TopFields(int count = 10) => Top(byField, count);

    /// <summary>The most expensive remote calls.</summary>
    /// <param name="count">How many to return.</param>
    /// <returns>Them, dearest first.</returns>
    public IReadOnlyList<BandwidthEntry> TopCalls(int count = 10) => Top(byCall, count);

    /// <summary>The most expensive networked objects. Empty unless <see cref="TrackObjects" />.</summary>
    /// <param name="count">How many to return.</param>
    /// <returns>Them, dearest first.</returns>
    public IReadOnlyList<BandwidthEntry> TopObjects(int count = 10) =>
        Top(byObject, count, id => string.Create(CultureInfo.InvariantCulture, $"net {id}"));

    /// <summary>What each connection has been sent.</summary>
    /// <param name="count">How many to return.</param>
    /// <returns>Them, dearest first.</returns>
    public IReadOnlyList<BandwidthEntry> TopConnections(int count = 10) =>
        Top(byConnection, count, id => string.Create(CultureInfo.InvariantCulture, $"player {id}"));

    /// <summary>Forgets everything, for a report over the next stretch rather than the whole run.</summary>
    public void Reset() {
        byComponent.Clear();
        byField.Clear();
        byCall.Clear();
        byConnection.Clear();
        byObject.Clear();
        Elapsed = TimeSpan.Zero;
        TotalBits = 0;
        TotalCount = 0;
        DeltaCount = 0;
        WholeCount = 0;
    }

    static void Add<TKey>(Dictionary<TKey, Tally> into, TKey key, int bits) where TKey : notnull {
        if (!into.TryGetValue(key, out var tally)) {
            tally = new();
            into[key] = tally;
        }

        tally.Bits += bits;
        tally.Count++;
    }

    static List<BandwidthEntry> Top(Dictionary<string, Tally> from, int count) =>
        Top(from, count, name => name);

    static List<BandwidthEntry> Top<TKey>(Dictionary<TKey, Tally> from, int count, Func<TKey, string> name)
        where TKey : notnull {
        var entries = new List<BandwidthEntry>(from.Count);

        foreach (var (key, tally) in from) {
            entries.Add(new(name(key), tally.Bits, tally.Count));
        }

        // Dearest first, and by name within a tie so two runs of the same thing report in the same
        // order — a diagnostic that reshuffles between runs cannot be diffed.
        entries.Sort(
            (left, right) => left.Bits == right.Bits
                ? string.CompareOrdinal(left.Name, right.Name)
                : right.Bits.CompareTo(left.Bits)
        );

        if (entries.Count > count) {
            entries.RemoveRange(count, entries.Count - count);
        }

        return entries;
    }

    sealed class Tally {
        public long Bits { get; set; }
        public long Count { get; set; }
    }
}
