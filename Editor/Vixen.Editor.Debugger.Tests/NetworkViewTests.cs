// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net;
using Vixen.Net.Diagnostics;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Vixen.Net.Transport.Local;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Debugger.Tests;

/// <summary>
///     The network panel, over a real ledger and a real snapshot, asserted through the elements it
///     built rather than through the model it was handed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here reads elements, and that is the point of the file.</b> A markup
///         panel renders through effects: a reading writes a signal, the scheduler runs the
///         <c>@for</c> on the next flush, and "the model was assigned" and "the screen followed" are
///         two different statements. A panel whose signals were replaced with plain fields would pass
///         every test that read its properties and would draw the first reading for ever.
///     </para>
///     <para>
///         ⚠ <b>Sabotage-verified, four times.</b> Recorded because a reactivity test that would
///         pass without the reactivity is the usual way this goes wrong.
///     </para>
///     <list type="bullet">
///         <item>
///             <c>NetworkTable.entries</c> as a plain field fails
///             <see cref="A_second_reading_replaces_the_rows_on_screen" />,
///             <see cref="It_takes_a_reading_from_the_documents_clock" /> and
///             <see cref="The_box_turns_per_object_attribution_on" />, and nothing else.
///         </item>
///         <item>
///             <c>NetworkTrend.face</c> as a plain field fails the five graph tests that assert
///             something moved — the bars, the sweep, the head, the wrap and the rate — and none of
///             the ledger's.
///         </item>
///         <item>
///             <c>NetworkView.link</c> as a plain field fails exactly the three that make the graph's
///             own shape change: a session arriving, a session stopping, and a lane appearing. The
///             first two rounds of these tests did <i>not</i> catch that one, because every test held
///             its source still for its whole length — which is what a live panel never does.
///         </item>
///         <item>
///             Dropping <c>NetworkLink.Pointed</c> and reading <c>Session is null</c> in
///             <c>Quiet</c> instead fails
///             <see cref="A_source_that_is_supplied_and_empty_reads_differently_from_no_source" />
///             and nothing else — the case where the reading a host's arrival produces is
///             <i>equal</i> to the one already on the signal, so the panel is never told.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>And the traffic is real rather than a fake ledger.</b> The panel's whole claim is that
///         everything it shows was already public on <c>BandwidthLedger</c> and
///         <c>SnapshotInspector</c>; a test that hand-built entries would be a test of the panel
///         against a model nobody produces. This one replicates a moving transform through a real
///         <c>ReplicationServer</c> and inspects the bytes it wrote — the same three calls
///         <c>Samples/08</c>'s attribution report makes.
///     </para>
/// </remarks>
public sealed class NetworkViewTests : IDisposable {
    static readonly PlayerId Player = new(1);

    readonly UiTest test = UiTest.Create();
    readonly World world = new("server");
    readonly ReplicationRegistry registry = new();
    readonly NetworkIdAllocator ids = new();
    readonly ReplicationServer sender;
    readonly BandwidthLedger ledger = new();
    readonly byte[] buffer = new byte[8192];
    readonly LocalNetwork network = new();

    byte[] snapshot = [];
    bool spawned;
    uint tick = 1;

    NetworkSession? session;
    NetworkPlayer? player;
    double trip = 20;

    public NetworkViewTests() {
        ControlTheme.Install(test.Document);
        DebuggerTheme.Install(test.Document);

        registry.Register(new NetworkTransformReplicator());
        sender = new(registry) { Ledger = ledger };
    }

    public void Dispose() {
        session?.Dispose();
        world.Dispose();
        test.Dispose();
    }

    [Fact]
    public void With_nothing_attached_it_says_so_rather_than_drawing_zeroes() {
        var view = Build();

        Assert.Empty(Tagged(view.Root, "network-row"));
        Assert.NotEmpty(Descendants(view.Root).OfType<EmptyState>());
    }

    /// <summary>The five columns the ledger answers with, drawn from real traffic.</summary>
    [Fact]
    public void A_ledger_with_traffic_in_it_fills_the_columns() {
        Replicate(20);

        var view = Attached();

        // by component, by field, by connection. The call and object columns are empty — nothing
        // called anything and per-object attribution is off — and they are parked rather than gone.
        Assert.NotEmpty(Rows(view, "by component"));
        Assert.NotEmpty(Rows(view, "by field"));
        Assert.NotEmpty(Rows(view, "by connection"));

        Assert.Empty(Rows(view, "by remote call"));
        Assert.True(Table(view, "by remote call").HasClass("parked"));
        Assert.False(Table(view, "by component").HasClass("parked"));
    }

    /// <summary>
    ///     ⚠ The assertion the whole design rests on: a second reading replaces the rows on screen.
    ///     Replacing either signal write in <c>NetworkView.vxml</c> or <c>NetworkTable.Show</c> with a
    ///     plain field assignment fails this and nothing else.
    /// </summary>
    [Fact]
    public void A_second_reading_replaces_the_rows_on_screen() {
        Replicate(4);

        var view = Attached();
        var first = Cost(view, "by component");

        Replicate(20);
        view.Take();
        test.Frames(2);

        Assert.NotEqual(first, Cost(view, "by component"));
    }

    /// <summary>The panel drives itself off the document's clock, and not faster than its interval.</summary>
    [Fact]
    public void It_takes_a_reading_from_the_documents_clock() {
        Replicate(4);

        var view = Attached();
        var first = Cost(view, "by component");

        // No `Take` — only time passing, which is the whole difference between this panel and the
        // statistics one.
        Replicate(20);
        test.Advance(NetworkView.Interval + NetworkView.Interval);

        Assert.NotEqual(first, Cost(view, "by component"));
    }

    /// <summary>
    ///     ⚠ A panel that kept reading after it left the tree is the hazard a pulled source exists to
    ///     avoid, and the one a subscription to a document-wide event puts back if it is not undone.
    /// </summary>
    [Fact]
    public void A_removed_panel_stops_reading() {
        Replicate(4);

        var reads = 0;
        var view = BuildContext.Build<NetworkView>(test.Document, test.Document.Root);

        view.Source = () => {
            reads++;

            return ledger;
        };

        test.Advance(NetworkView.Interval + NetworkView.Interval);
        Assert.True(reads > 0, "the panel never read its source");

        view.Root.Remove();

        var after = reads;
        test.Advance(NetworkView.Interval + NetworkView.Interval);

        Assert.Equal(after, reads);
        Assert.Equal(0, test.Document.Effects.PendingCount);
    }

    /// <summary>The packet pane is the inspector, and it draws one line per record.</summary>
    [Fact]
    public void A_captured_snapshot_is_taken_apart_into_rows() {
        Spawn();
        Spawn();
        Replicate(1);

        var view = Attached();

        var records = Tagged(view.Root, "network-record");

        Assert.Equal(2, records.Length);
        Assert.Contains(records, row => TextOf(row).Contains("NetworkTransform", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ A truncated decode still returns what it found, and the pane has to say the rest was
    ///     unread — a pane that only showed the records would under-report the packet it exists to
    ///     explain.
    /// </summary>
    [Fact]
    public void A_truncated_snapshot_is_marked_rather_than_hidden() {
        Spawn();
        Replicate(1);

        snapshot = snapshot[..3];

        var view = Attached();
        var status = Assert.Single(Tagged(view.Root, "network-status"), part => part.HasClass("truncated"));

        Assert.Contains("TRUNCATED", TextOf(status), StringComparison.Ordinal);
    }

    /// <summary>Nothing is stripped that carries information, and the shared namespace is not that.</summary>
    [Fact]
    public void The_namespace_every_row_shares_is_taken_off_the_column() {
        Replicate(20);

        var view = Attached();
        var name = Assert.Single(Rows(view, "by component").Select(row => TextOf(Tagged(row, "network-name").Single())));

        Assert.Equal("NetworkTransform", name);
    }

    /// <summary>Per-object attribution is the ledger's own opt-in, reachable from the panel.</summary>
    [Fact]
    public void The_box_turns_per_object_attribution_on() {
        Replicate(4);

        var view = Attached();

        Assert.Empty(Rows(view, "by object"));

        var box = Descendants(view.Root).OfType<CheckBox>().Single();
        test.Get("checkbox").Click();
        test.Frames(2);

        Assert.True(box.IsChecked);
        Assert.True(ledger.TrackObjects);

        Replicate(4);
        view.Take();
        test.Frames(2);

        Assert.NotEmpty(Rows(view, "by object"));
    }

    // ============================================================ The graph

    /// <summary>
    ///     ⚠ An estimator with no samples reports a round trip of zero, so a graph drawn from one is
    ///     a flat line along the bottom — which is a picture of a perfect link and is the opposite of
    ///     "nothing has measured it".
    /// </summary>
    [Fact]
    public void With_no_session_the_graph_says_why_rather_than_drawing_a_flat_line() {
        var view = Attached();
        test.Advance(NetworkView.Interval + NetworkView.Interval);

        Assert.Empty(Tagged(view.Root, "network-sample"));
        Assert.Empty(Tagged(view.Root, "network-lane"));
        Assert.Contains(Statuses(view), line => line.Contains("no NetworkSession", StringComparison.Ordinal));
    }

    /// <summary>The graph's clock is the document's, and a reading is a bar.</summary>
    [Fact]
    public void Every_reading_of_the_clock_puts_a_bar_on_the_graph() {
        var view = Graphed();
        var first = Samples(view, "round trip").Length;

        Assert.True(first > 0, "the panel took no reading at all");

        test.Advance(NetworkView.Interval * 3);

        var bars = Samples(view, "round trip");

        Assert.True(bars.Length > first, "the clock ran and the graph did not grow");
        Assert.True(bars[^1].HasClass("newest"), "nothing on the strip says which bar is now");
    }

    /// <summary>
    ///     ⚠ The assertion the ring exists for. A scrolling chart shifts every sample one place left
    ///     on every reading, so every key changes and every region is rebuilt; a ring writes one slot
    ///     and moves the sweep, so every other element on the strip is the element it already was.
    ///     Asserted as instance identity, because that is the only thing that tells the two apart.
    /// </summary>
    [Fact]
    public void A_reading_moves_the_sweep_and_leaves_every_other_bar_alone() {
        var view = Graphed();
        test.Advance(NetworkView.Interval * 4);

        var before = Samples(view, "round trip");
        Assert.True(before.Length > 2, "not enough of a strip to say anything");

        test.Advance(NetworkView.Interval);

        var after = Samples(view, "round trip");
        Assert.True(after.Length > before.Length, "the strip did not grow");

        // Every bar except the one that was the sweep — it loses the class, so its value changes and
        // its region goes with it.
        for (var slot = 0; slot < before.Length - 1; slot++) {
            Assert.Same(before[slot], after[slot]);
        }
    }

    /// <summary>
    ///     ⚠ The head of a lane is inside a region keyed on an object that never changes, so the only
    ///     thing that can make it follow the bars is that what it reads is a signal. A plain field
    ///     leaves this line showing the first reading for ever, beside a chart proving it wrong.
    /// </summary>
    [Fact]
    public void The_number_above_a_lane_follows_the_bars_under_it() {
        var view = Graphed();
        var first = Reading(view, "round trip");

        test.Advance(NetworkView.Interval * 6);

        Assert.NotEqual(first, Reading(view, "round trip"));
    }

    /// <summary>The ring is a ring: it fills, and then it writes over the oldest.</summary>
    [Fact]
    public void The_ring_fills_and_then_writes_over_the_oldest() {
        var view = Graphed();

        // Twice round, so the strip is certainly full however the frame delta divides the interval.
        test.Advance(NetworkView.Interval * NetworkTrend.Capacity * 2);

        var before = Samples(view, "round trip");

        Assert.Equal(NetworkTrend.Capacity, before.Length);

        var head = Array.FindIndex(before, bar => bar.HasClass("newest"));
        Assert.True(head >= 0, "nothing on the strip says which bar is now");

        test.Advance(NetworkView.Interval);

        var after = Samples(view, "round trip");

        // Still exactly full. A strip that grew would be a list rather than a ring, and a strip that
        // scrolled would have replaced every element on it.
        Assert.Equal(NetworkTrend.Capacity, after.Length);

        var moved = Array.FindIndex(after, bar => bar.HasClass("newest"));

        Assert.NotEqual(head, moved);

        // Half the ring away from either end of the sweep, so it is a slot nothing touched.
        var untouched = (moved + (NetworkTrend.Capacity / 2)) % NetworkTrend.Capacity;
        Assert.Same(before[untouched], after[untouched]);
    }

    /// <summary>
    ///     ⚠ Nothing in the engine measures packet loss — no transport reports it, no meter publishes
    ///     it — so the lane is absent and says so rather than being drawn flat, which would read as a
    ///     clean link.
    /// </summary>
    [Fact]
    public void Nothing_measures_loss_so_the_third_lane_is_absent_and_says_why() {
        var view = Graphed();
        test.Advance(NetworkView.Interval * 2);

        Assert.Equal(2, Tagged(view.Root, "network-lane").Length);
        Assert.Contains(Statuses(view), line => line.Contains("No loss lane", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A host that can count retransmissions gets the third lane — as a rate, which is the thing
    ///     a running total cannot be turned into without a second reading and the time between them.
    /// </summary>
    [Fact]
    public void A_retransmit_counter_is_charted_as_a_rate() {
        var sent = 0L;
        var view = Graphed(() => sent += 10);

        test.Advance(NetworkView.Interval * 4);

        Assert.Equal(3, Tagged(view.Root, "network-lane").Length);
        Assert.NotEmpty(Samples(view, "retransmits"));
        Assert.DoesNotContain(Statuses(view), line => line.Contains("No loss lane", StringComparison.Ordinal));

        // Ten more every reading and a reading every quarter second is about forty a second. The
        // counter is a total and never says that; the ring is what makes the division possible.
        var reading = Reading(view, "retransmits");

        Assert.EndsWith("/s", reading, StringComparison.Ordinal);
        Assert.NotEqual("0.0/s", reading);
    }

    /// <summary>
    ///     The scale is a round number at or above the peak, which is what keeps a reading from
    ///     re-drawing every bar — and what makes two bars in a lane comparable at all.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]          // nothing measured is not a divide by zero
    [InlineData(0.4, 0.5)]      // and the ladder goes below one
    [InlineData(1, 1)]          // a rung is its own ceiling rather than the next one up
    [InlineData(1.1, 2)]
    [InlineData(3, 5)]
    [InlineData(6, 10)]
    [InlineData(84, 100)]
    [InlineData(230, 500)]
    public void The_scale_is_the_next_round_number_above_the_peak(double peak, double scale) =>
        Assert.Equal(scale, NetworkTrend.Ladder(peak), 9);

    /// <summary>
    ///     ⚠ "This host supplies no session" and "the session is not running" are different answers,
    ///     and the second one is reached by a reading that is <i>equal</i> to the first — which is a
    ///     reading the signal refuses. So whether a source was supplied has to be part of the reading
    ///     rather than read off the panel's own property, or this line stays on the first answer for
    ///     ever. The host assigns after <c>Build</c> returns, which is what makes that reachable.
    /// </summary>
    [Fact]
    public void A_source_that_is_supplied_and_empty_reads_differently_from_no_source() {
        var view = BuildContext.Build<NetworkView>(test.Document, test.Document.Root);

        test.Advance(NetworkView.Interval * 2);

        Assert.Contains(Statuses(view), line => line.Contains("no NetworkSession", StringComparison.Ordinal));

        view.Session = () => null;
        test.Advance(NetworkView.Interval * 2);

        Assert.Contains(Statuses(view), line => line.Contains("No session running", StringComparison.Ordinal));
        Assert.DoesNotContain(Statuses(view), line => line.Contains("no NetworkSession", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ A session arrives after the panel is open — play mode being started — and the graph has
    ///     to appear. The first thing every binding under the graph reads is <c>Link</c>, so this is
    ///     the assertion that says that store is a signal: on a plain field the panel would draw the
    ///     empty state it was built with for as long as it was open.
    /// </summary>
    [Fact]
    public void A_session_that_arrives_after_the_panel_is_open_is_picked_up() {
        var host = Host();
        var live = false;
        var view = BuildContext.Build<NetworkView>(test.Document, test.Document.Root);

        view.Session = () => {
            if (!live) {
                return null;
            }

            player!.RoundTrip.Add(TimeSpan.FromMilliseconds(trip += 5));

            return host;
        };

        test.Advance(NetworkView.Interval * 2);

        Assert.Empty(Tagged(view.Root, "network-lane"));

        live = true;
        test.Advance(NetworkView.Interval * 2);

        Assert.NotEmpty(Tagged(view.Root, "network-lane"));
        Assert.NotEmpty(Samples(view, "round trip"));
    }

    /// <summary>
    ///     ⚠ And a session that stops takes the graph with it. Left standing it would be a picture of
    ///     a link that no longer exists with nothing on it saying so — which is the same mistake as
    ///     drawing a flat line for a link nothing has measured, made from the other end.
    /// </summary>
    [Fact]
    public void A_session_that_stops_takes_the_graph_with_it() {
        var host = Host();
        var live = true;
        var view = BuildContext.Build<NetworkView>(test.Document, test.Document.Root);

        view.Session = () => {
            if (!live) {
                return null;
            }

            player!.RoundTrip.Add(TimeSpan.FromMilliseconds(trip += 5));

            return host;
        };

        test.Advance(NetworkView.Interval * 3);

        Assert.NotEmpty(Samples(view, "round trip"));

        live = false;
        test.Advance(NetworkView.Interval * 2);

        Assert.Empty(Tagged(view.Root, "network-lane"));
        Assert.Contains(Statuses(view), line => line.Contains("No session running", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ The <c>@for</c> source itself changes, which is the key rule read at the loop rather than
    ///     at the row: <c>Lanes</c> is two lanes or three depending on what the host can count, so it
    ///     has to read a signal or the loop reconciles once and never again.
    /// </summary>
    [Fact]
    public void A_retransmit_counter_that_arrives_brings_its_lane_with_it() {
        var counting = false;
        var sent = 0L;
        var view = Graphed(() => counting ? sent += 10 : (long?) null);

        test.Advance(NetworkView.Interval * 2);

        Assert.Equal(2, Tagged(view.Root, "network-lane").Length);

        counting = true;
        test.Advance(NetworkView.Interval * 3);

        Assert.Equal(3, Tagged(view.Root, "network-lane").Length);
        Assert.NotEmpty(Samples(view, "retransmits"));
    }

    // ============================================================ Harness

    /// <summary>A panel with nothing wired, which is what an editor running no session shows.</summary>
    NetworkView Build() {
        var built = BuildContext.Build<NetworkView>(test.Document, test.Document.Root);
        test.Frames(2);

        return built;
    }

    /// <summary>A panel pointed at the ledger, the registry and the last snapshot.</summary>
    NetworkView Attached() {
        var built = BuildContext.Build<NetworkView>(test.Document, test.Document.Root);

        built.Source = () => ledger;
        built.Registry = () => registry;
        built.Capture = () => snapshot;

        // ⚠ The host's own first reading, and the module does the same thing for the same reason: a
        // component's `OnComposed` runs inside the build, before `Build` has returned and therefore
        // before the three lines above.
        built.Take();
        test.Frames(2);

        return built;
    }

    /// <summary>A panel pointed at a running host session, and optionally at a retransmit counter.</summary>
    /// <remarks>
    ///     ⚠ <b>The session delegate feeds the estimator, which is a test double doing what a real
    ///     session's ping loop does.</b> <c>NetworkView.Sample</c> pulls its source exactly once per
    ///     reading, so one ping comes back per reading — and it is a <i>rising</i> one, because a
    ///     lane whose numbers never moved would be a lane that could freeze without a test noticing.
    /// </remarks>
    NetworkView Graphed(Func<long?>? retransmits = null) {
        var host = Host();
        var built = BuildContext.Build<NetworkView>(test.Document, test.Document.Root);

        built.Session = () => {
            player!.RoundTrip.Add(TimeSpan.FromMilliseconds(trip += 5));

            return host;
        };

        built.Retransmits = retransmits;

        // One reading: the panel's clock has never ticked, so the first tick takes one whatever the
        // interval says, and the second frame is inside it.
        test.Frames(2);

        return built;
    }

    /// <summary>A host session — one process, both halves — with its own player connected.</summary>
    NetworkSession Host() {
        var made = new NetworkSession(new LocalTransport(network), ownsTransport: true);
        made.StartHost();

        for (var round = 0; round < 32 && made.Players.Count == 0; round++) {
            made.Update(TimeSpan.FromMilliseconds(16));
        }

        session = made;
        player = Assert.Single(made.Players);

        return made;
    }

    Entity Spawn() {
        spawned = true;

        return world.Create(ids.Next(), new NetworkTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity });
    }

    /// <summary>Moves everything and sends a snapshot, keeping the last one's bytes.</summary>
    void Replicate(int steps) {
        if (!spawned) {
            Spawn();
        }

        for (var step = 0; step < steps; step++) {
            foreach (var chunk in world.Chunks(new QueryDescription().WithAll<NetworkTransform>())) {
                var transforms = chunk.Values<NetworkTransform>();

                for (var index = 0; index < chunk.Count; index++) {
                    transforms[index].Position = new(step * 0.4f, index, step * 0.2f);
                }
            }

            var at = new Tick(tick);

            sender.Capture(world, at);
            ledger.Advance(TimeSpan.FromMilliseconds(33));

            if (sender.TryWriteSnapshot(world, Player, at, buffer, out var written)) {
                snapshot = written.ToArray();
                sender.Acknowledge(Player, at);
            }

            world.AdvanceVersion();
            tick++;
        }
    }

    static UiElement Table(NetworkView view, string heading) =>
        Assert.Single(
            Tagged(view.Root, "network-table"),
            table => Tagged(table, "network-heading").Any(part => TextOf(part) == heading)
        );

    static UiElement[] Rows(NetworkView view, string heading) => Tagged(Table(view, heading), "network-row");

    /// <summary>What the dearest row of a column costs, which is what a second reading moves.</summary>
    static string Cost(NetworkView view, string heading) => TextOf(Tagged(Rows(view, heading)[0], "network-cost").Single());

    /// <summary>One lane of the graph, by what it is a measurement of.</summary>
    static UiElement Lane(NetworkView view, string heading) =>
        Assert.Single(
            Tagged(view.Root, "network-lane"),
            lane => Tagged(lane, "network-name").Any(part => TextOf(part) == heading)
        );

    /// <summary>A lane's bars, left to right — which is slot order, not time order.</summary>
    static UiElement[] Samples(NetworkView view, string heading) => Tagged(Lane(view, heading), "network-sample");

    /// <summary>The number written above a lane.</summary>
    static string Reading(NetworkView view, string heading) =>
        TextOf(Tagged(Lane(view, heading), "network-cost").Single());

    /// <summary>Every line of prose the panel is showing.</summary>
    static string[] Statuses(NetworkView view) => [.. Tagged(view.Root, "network-status").Select(TextOf)];

    static UiElement[] Tagged(UiElement root, string tag) =>
        [.. Descendants(root).Where(element => element.Tag == tag)];

    /// <summary>
    ///     A walk rather than a read, because markup text is its own element: an interpolation emits
    ///     a <c>text</c> child rather than setting the parent's own string.
    /// </summary>
    static string TextOf(UiElement element) {
        var text = element.Text ?? string.Empty;

        foreach (var child in Descendants(element)) {
            text += child.Text ?? string.Empty;
        }

        return text;
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var nested in Descendants(child)) {
                yield return nested;
            }
        }
    }
}
