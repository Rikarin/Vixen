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
///         ⚠ <b>Sabotage-verified.</b> Replacing <c>NetworkTable</c>'s <c>entries</c> signal with a
///         plain field fails <see cref="A_second_reading_replaces_the_rows_on_screen" />,
///         <see cref="It_takes_a_reading_from_the_documents_clock" /> and
///         <see cref="The_box_turns_per_object_attribution_on" /> and nothing else. Recorded because
///         a reactivity test that would pass without the reactivity is the usual way this goes wrong.
///     </para>
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

    byte[] snapshot = [];
    bool spawned;
    uint tick = 1;

    public NetworkViewTests() {
        ControlTheme.Install(test.Document);
        DebuggerTheme.Install(test.Document);

        registry.Register(new NetworkTransformReplicator());
        sender = new(registry) { Ledger = ledger };
    }

    public void Dispose() {
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
