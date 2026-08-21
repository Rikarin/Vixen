// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Net.Diagnostics;
using Vixen.Net.Sessions;
using Vixen.Net.Transport;
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

/// <summary>What the link is doing, at one instant, across everyone in a session.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The same walk <see cref="NetworkMetrics" /> makes, and deliberately not a second
///         way of making it.</b> <c>NetworkMetrics.Sample</c> takes the mean round trip across
///         connected players, the worst of them, and the worst jitter — because those are the three
///         questions a link is asked — and it is called once a tick from the loop that owns the
///         session. This is the panel's copy of that reading, taken from the document's clock
///         instead, and every number in it is a property <c>NetworkPlayer.RoundTrip</c> already has.
///     </para>
///     <para>
///         ⚠ <b>Milliseconds here, seconds there, and that is not an inconsistency.</b> The meter
///         publishes seconds because OpenTelemetry's unit for a duration is the second and a
///         collector aggregating three servers must not have to know which one used which. A panel
///         has one reader and they think in milliseconds.
///     </para>
///     <para>
///         ⚠ <b>A record of scalars, for the reason <see cref="NetworkReport" />'s own remarks
///         give.</b> Value equality means the signal holding one refuses an equal reading, so a
///         still link notifies nothing — and <c>TransportLoss</c> is four <c>long</c>s in a record
///         struct, so carrying it here keeps that true where a list or a class would not.
///     </para>
/// </remarks>
/// <param name="Pointed">
///     Whether the host pointed the panel at anything. ⚠ <b>In the record rather than read off the
///     panel's own property, and the difference is a wrong sentence.</b> "This host supplies no
///     session" and "the session is not running" are two different answers, and the line that says
///     which lives in a region whose bindings re-run only when what they read changes. Read off a
///     plain property it would keep saying the first one after the host had supplied one, because a
///     delegate that returns null produces a reading equal to <see cref="None" /> — and an equal
///     value is a value the signal refuses.
/// </param>
/// <param name="Attached">Whether there is a session at all.</param>
/// <param name="Sampled">
///     Whether any connected player has a round-trip sample. False is not zero: an estimator with no
///     samples has a <c>RoundTrip</c> of zero, and drawing that would be a claim of a perfect link.
/// </param>
/// <param name="Players">Connected players.</param>
/// <param name="Awaiting">Players inside their reconnect window, holding a seat but not connected.</param>
/// <param name="MeanMilliseconds">The mean round trip across connected players who have samples.</param>
/// <param name="WorstMilliseconds">The worst round trip anybody has — the one being complained about.</param>
/// <param name="JitterMilliseconds">The worst jitter anybody has, which is what sizes their buffer.</param>
/// <param name="Counting">
///     Whether the session's transport counts what it lost. ⚠ <b>Absent is not zero.</b> A transport
///     that cannot count losses has not said there are none, so the two loss lanes are not drawn at
///     all rather than drawn flat along the bottom.
/// </param>
/// <param name="Counted">
///     The transport's four running totals, or all zeroes when it keeps none.
///     <para>
///         ⚠ <b>Totals here and shares on the graph, which is the split <c>TransportLoss</c> asks
///         for.</b> The counters are cumulative, so their ratio is a lifetime average — the number
///         that hides the thirty seconds somebody opened this panel about. The ring differences two
///         readings and divides one difference by the other, which is what makes the lane a picture
///         of the link now.
///     </para>
/// </param>
readonly record struct NetworkLink(
    bool Pointed,
    bool Attached,
    bool Sampled,
    int Players,
    int Awaiting,
    double MeanMilliseconds,
    double WorstMilliseconds,
    double JitterMilliseconds,
    bool Counting,
    TransportLoss Counted
) {
    /// <summary>Nothing pointed at anything, which is what a bare editor shows.</summary>
    public static NetworkLink None { get; }

    /// <summary>Takes a reading.</summary>
    /// <param name="source">Where the session comes from, or null when the host supplied none.</param>
    /// <returns>The reading.</returns>
    /// <remarks>
    ///     <para>
    ///         The delegate rather than the session, because whether there <i>is</i> one is half of
    ///         what the reading has to say — see <see cref="Pointed" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The loss counters come off the session's own transport, and the panel asks the
    ///         host for nothing extra.</b> <c>NetworkSession.Transport</c> is the transport the
    ///         session is running on, and <c>ITransport.Loss</c> is null on one that cannot count —
    ///         so a game already pointing this panel at a session on UDP gets the lanes, and one on
    ///         a loopback gets a sentence saying why it does not. A second delegate for the same
    ///         object would be a second thing for every host to remember to wire, and the reason
    ///         most of them would not is that it is not obvious there is anything to wire.
    ///     </para>
    /// </remarks>
    public static NetworkLink Of(Func<NetworkSession?>? source) {
        if (source is null) {
            return None;
        }

        if (source.Invoke() is not { } session) {
            return new(Pointed: true, Attached: false, Sampled: false, 0, 0, 0, 0, 0, Counting: false, default);
        }

        var counted = session.Transport.Loss;

        var connected = 0;
        var awaiting = 0;
        var sampled = 0;
        var total = 0d;
        var worst = 0d;
        var jitter = 0d;

        foreach (var player in session.Players) {
            if (!player.IsConnected) {
                awaiting++;

                continue;
            }

            connected++;

            if (!player.RoundTrip.HasSamples) {
                continue;
            }

            sampled++;

            var trip = player.RoundTrip.RoundTrip.TotalMilliseconds;
            total += trip;
            worst = Math.Max(worst, trip);
            jitter = Math.Max(jitter, player.RoundTrip.Jitter.TotalMilliseconds);
        }

        return new(
            Pointed: true,
            Attached: true,
            sampled > 0,
            connected,
            awaiting,
            // Over the players who have samples rather than over the connected ones: a player who
            // joined this frame has no estimate, and averaging their zero in halves the mean of a
            // two-player game for as long as it takes the first ping to come back.
            sampled == 0 ? 0 : total / sampled,
            worst,
            jitter,
            counted.HasValue,
            counted ?? default
        );
    }
}

/// <summary>One bar of a lane: where it is, how tall it is, and whether it is the newest.</summary>
/// <remarks>
///     ⚠ <b>The height is in the key, and that is what makes the lane cost what it does.</b> A
///     <c>@for</c> key that survives keeps its region and does not re-run the body, so a bar whose
///     value is unchanged is an element nothing touches. <see cref="Slot" /> is in the record for
///     <see cref="PacketRow" />'s reason — two bars of the same height are otherwise two equal keys
///     in one loop, which is not something <c>BuildContext.For</c> can be asked to reconcile.
/// </remarks>
/// <param name="Slot">Where in the ring it is, which is a fixed position on screen.</param>
/// <param name="Class">Empty, or <c>newest</c> for the one the sweep just wrote.</param>
/// <param name="Geometry">Its height, as a declaration list.</param>
readonly record struct TrendBar(int Slot, string Class, string Geometry);

/// <summary>What a lane shows: its bars, and the three numbers written above them.</summary>
/// <remarks>
///     ⚠ <b>One signal holding all of it, per <c>GpuTimelineView</c>'s argument.</b> The bars, the
///     latest reading and the scale all come out of one walk of the ring, and separate signals would
///     mean either walking it three times or writing them in an order the reader has to know about.
/// </remarks>
/// <param name="Latest">The newest sample.</param>
/// <param name="Peak">The largest sample in the ring.</param>
/// <param name="Ceiling">What the bars are drawn against.</param>
/// <param name="Bars">Them.</param>
readonly record struct TrendFace(double Latest, double Peak, double Ceiling, IReadOnlyList<TrendBar> Bars) {
    /// <summary>A lane with nothing in it yet.</summary>
    /// <remarks>Spelled out rather than <c>default</c>, for <see cref="NetworkPacket.None" />'s reason.</remarks>
    public static TrendFace Empty { get; } = new(0, 0, 0, []);
}

/// <summary>One measurement over time, as a fixed ring of samples drawn as a strip of bars.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The history is here and not in <c>Vixen.Net</c>, and that is the whole design
///         decision.</b> <c>RoundTripEstimator</c> is a running filter and <c>NetworkMetrics</c>
///         publishes gauges, both by intent: the meter's own remarks say rates are the collector's
///         job and are "deliberately not computed here", because a metric that has already been
///         differenced cannot be re-aggregated across three servers. A ring beside the estimator
///         would be a second, in-process time series competing with that pipeline, paid for by every
///         dedicated server whether or not anybody is looking. Nothing but this panel wants the
///         history, so the panel keeps it — and <c>Vixen.Net</c> gains not one line of public
///         surface, which is the claim the rest of this file already rests on.
///     </para>
///     <para>
///         ⚠ <b>A ring that is drawn as a ring, and the alternative is what makes it worth it.</b>
///         A scrolling chart shifts every sample one place left on every reading, so all
///         <see cref="Capacity" /> keys change and all <see cref="Capacity" /> regions are rebuilt,
///         four times a second, for ever. Here the slot a sample is written to is where it stays: a
///         push changes the value at exactly one slot and moves the <c>newest</c> class off one bar
///         and onto another, so three elements are rebuilt instead of a hundred and twenty. What is
///         bought with that is the sweep — the newest bar is coloured, and the chart reads like a
///         monitor trace rather than like a graph whose left edge is the past.
///     </para>
///     <para>
///         ⚠ <b>The ceiling is snapped to a 1–2–5 ladder rather than taken from the peak.</b> Two
///         reasons and the second is the one that matters: a scale that follows the peak exactly
///         changes on nearly every reading, and every bar's height is a fraction of it — so the one
///         cheap push above would become a full rebuild most of the time. It is also the difference
///         between a chart that can be read and one whose axis never holds still long enough to
///         compare a bar with the bar beside it.
///     </para>
/// </remarks>
/// <param name="heading">What is being measured.</param>
/// <param name="suffix">What its unit is, appended to the number exactly as written.</param>
sealed class NetworkTrend(string heading, string suffix) {
    /// <summary>How many samples the ring holds.</summary>
    /// <remarks>
    ///     A hundred and twenty, which at <c>NetworkView.Interval</c> is half a minute. Long enough
    ///     that a spike is still on screen when somebody looks up, short enough that the thing they
    ///     are looking at is the link as it is now.
    /// </remarks>
    public const int Capacity = 120;

    /// <summary>How tall a lane's plot is, in pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>A constant here rather than a rule in the sheet, and it is written as an inline
    ///     style.</b> A bar's height is a fraction of a scale times this, which is a length no
    ///     stylesheet can be given — the same argument <c>GpuTimelineView</c> makes, and the reason
    ///     the plot's own height is set from code too. It also means the lane draws in a test that
    ///     has loaded no stylesheet at all.
    /// </remarks>
    public const float PlotHeight = 32f;

    /// <summary>The height a zero gets, so a flat lane is a line rather than nothing.</summary>
    public const float Floor = 1f;

    readonly double[] samples = new double[Capacity];
    readonly Signal<TrendFace> face = new(TrendFace.Empty);

    int count;
    int cursor;

    /// <summary>What is being measured.</summary>
    public string Heading { get; } = heading;

    /// <summary>The bars, in slot order — which is not time order once the ring has wrapped.</summary>
    public IReadOnlyList<TrendBar> Bars => face.Value.Bars;

    /// <summary>Whether anything has been sampled yet.</summary>
    public bool Blank => face.Value.Bars.Count == 0;

    /// <summary>The newest sample, as text.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off the signal, and a plain field here would be the bug the <c>@for</c> key rule
    ///     warns about one step past where it is usually read.</b> A lane is keyed on the object, so
    ///     its region survives for the life of the panel and the effect writing this line runs again
    ///     only when something it <i>read</i> changes. Backed by a field it would read nothing that
    ///     ever changes, and the head of a lane whose bars were moving would show the first reading
    ///     for ever — beside a chart proving it wrong.
    /// </remarks>
    public string Reading => Text(face.Value.Latest);

    /// <summary>What the bars are drawn against, as text.</summary>
    public string Scale => "0–" + Text(face.Value.Ceiling);

    /// <summary>How tall the plot is, as a declaration list.</summary>
    public static string Plot { get; } = "height: " + Length(PlotHeight);

    /// <summary>Takes a sample.</summary>
    /// <param name="value">It, in whatever unit <see cref="Heading" /> is in.</param>
    public void Push(double value) {
        samples[cursor] = value;
        cursor = (cursor + 1) % Capacity;

        if (count < Capacity) {
            count++;
        }

        Realise();
    }

    /// <summary>Forgets everything sampled, for a link that is no longer there.</summary>
    /// <remarks>
    ///     ⚠ <b>Emptied rather than frozen, which is the same choice the panel makes about an absent
    ///     ledger.</b> A session that has stopped leaves a chart that is a picture of a link that no
    ///     longer exists, and there is nothing on it that says so. An empty lane is parked and the
    ///     line above the graph says why.
    /// </remarks>
    public void Clear() {
        if (count == 0 && cursor == 0) {
            return;
        }

        Array.Clear(samples);
        count = 0;
        cursor = 0;

        Realise();
    }

    /// <summary>The smallest 1–2–5 round number at or above <paramref name="peak" />.</summary>
    /// <param name="peak">The largest sample.</param>
    /// <returns>The scale.</returns>
    public static double Ladder(double peak) {
        if (peak <= 0) {
            return 1;
        }

        var power = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        var steps = peak / power;

        return power * (steps <= 1 ? 1 : steps <= 2 ? 2 : steps <= 5 ? 5 : 10);
    }

    /// <summary>Rebuilds every bar, which is what the keys then compare against the ones on screen.</summary>
    void Realise() {
        if (count == 0) {
            face.Value = TrendFace.Empty;

            return;
        }

        var peak = 0d;

        for (var slot = 0; slot < count; slot++) {
            peak = Math.Max(peak, samples[slot]);
        }

        var ceiling = Ladder(peak);
        var newest = (cursor - 1 + Capacity) % Capacity;
        var bars = new TrendBar[count];

        for (var slot = 0; slot < count; slot++) {
            var height = Math.Max(Floor, (float) (samples[slot] / ceiling) * PlotHeight);

            bars[slot] = new(slot, slot == newest ? "newest" : string.Empty, "height: " + Length(height));
        }

        face.Value = new(samples[newest], peak, ceiling, bars);
    }

    string Text(double value) => value.ToString("N1", CultureInfo.InvariantCulture) + suffix;

    static string Length(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";
}
