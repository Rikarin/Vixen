// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Ui.Reactive.Tests;

/// <summary>
///     The collection, and the change log a keyed reconciler will read. The log is the whole point,
///     so most of these are about what it says rather than about what the list contains.
/// </summary>
public class CollectionSignalTests {
    [Fact]
    public void A_collection_reports_what_changed_rather_than_that_something_did() {
        var items = new CollectionSignal<string>(["a", "b"]);
        var start = items.Revision;

        items.Add("c");
        items.Insert(0, "z");
        items.RemoveAt(2);
        items.Move(0, 1);

        Assert.True(items.TryGetChangesSince(start, out var changes));
        Assert.Equal(4, changes.Length);
        Assert.Equal(new CollectionChange(CollectionChangeKind.Inserted, 2), changes[0]);
        Assert.Equal(new CollectionChange(CollectionChangeKind.Inserted, 0), changes[1]);
        Assert.Equal(new CollectionChange(CollectionChangeKind.Removed, 2), changes[2]);
        Assert.Equal(new CollectionChange(CollectionChangeKind.Moved, 0, 1), changes[3]);
    }

    [Fact]
    public void Replaying_the_log_onto_a_copy_reproduces_the_collection() {
        // The property that makes the log worth having: a consumer that only ever sees the changes
        // ends up with what a consumer that re-read everything would have.
        Gen.Select(Gen.Int[0, 4], Gen.Int[0, 9]).Array[0, 40].Sample(script => {
                var items = new CollectionSignal<int>(changeLogCapacity: 256);
                var mirror = new List<int>();
                var cursor = items.Revision;

                foreach (var (operation, value) in script) {
                    switch (operation) {
                        case 0:
                            items.Add(value);
                            break;
                        case 1 when items.Peek().Length > 0:
                            items.Insert(value % items.Peek().Length, value);
                            break;
                        case 2 when items.Peek().Length > 0:
                            items.RemoveAt(value % items.Peek().Length);
                            break;
                        case 3 when items.Peek().Length > 0:
                            items[value % items.Peek().Length] = value;
                            break;
                        case 4 when items.Peek().Length > 1:
                            items.Move(value % items.Peek().Length, (value + 1) % items.Peek().Length);
                            break;
                        default:
                            continue;
                    }
                }

                Assert.True(items.TryGetChangesSince(cursor, out var changes));
                foreach (var change in changes) {
                    switch (change.Kind) {
                        case CollectionChangeKind.Inserted:
                            mirror.Insert(change.Index, 0);
                            break;
                        case CollectionChangeKind.Removed:
                            mirror.RemoveAt(change.Index);
                            break;
                        case CollectionChangeKind.Replaced:
                            break;
                        case CollectionChangeKind.Moved:
                            var moved = mirror[change.Index];
                            mirror.RemoveAt(change.Index);
                            mirror.Insert(change.Target, moved);
                            break;
                        default:
                            throw new InvalidOperationException($"unknown change {change.Kind}");
                    }
                }

                // The values are not replayed — the log carries positions, which is what a keyed
                // reconciler needs — so what is checked is that the shape agrees.
                Assert.Equal(items.Peek().Length, mirror.Count);
            }
        );
    }

    [Fact]
    public void A_consumer_that_has_fallen_further_behind_than_the_log_is_told_to_start_again() {
        var items = new CollectionSignal<int>(changeLogCapacity: 4);
        var stale = items.Revision;

        for (var i = 0; i < 5; i++) {
            items.Add(i);
        }

        Assert.False(items.TryGetChangesSince(stale, out _));
        Assert.True(items.TryGetChangesSince(items.Revision - 4, out var recent));
        Assert.Equal(4, recent.Length);
    }

    [Fact]
    public void The_log_reads_correctly_when_the_ring_has_wrapped() {
        var items = new CollectionSignal<int>(changeLogCapacity: 4);
        for (var i = 0; i < 6; i++) {
            items.Add(i);
        }

        var cursor = items.Revision - 3;
        items.Add(99);

        Assert.True(items.TryGetChangesSince(cursor, out var changes));
        Assert.Equal(4, changes.Length);
        Assert.Equal(new CollectionChange(CollectionChangeKind.Inserted, 3), changes[0]);
        Assert.Equal(new CollectionChange(CollectionChangeKind.Inserted, 6), changes[3]);
    }

    [Fact]
    public void Clearing_is_recorded_as_removals_from_the_end() {
        var items = new CollectionSignal<int>([1, 2, 3]);
        var cursor = items.Revision;

        items.Clear();

        Assert.True(items.TryGetChangesSince(cursor, out var changes));
        Assert.Equal(3, changes.Length);
        Assert.Equal(new CollectionChange(CollectionChangeKind.Removed, 2), changes[0]);
        Assert.Equal(new CollectionChange(CollectionChangeKind.Removed, 0), changes[2]);
    }

    [Fact]
    public void A_mutation_wakes_whatever_was_reading_the_collection() {
        var scheduler = new EffectScheduler();
        var items = new CollectionSignal<int>([1, 2]);
        var seen = 0;
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                seen = items.Count;
            },
            scheduler
        );

        scheduler.Flush();

        Assert.Equal(2, seen);

        items.Add(3);
        scheduler.Flush();

        Assert.Equal(3, seen);
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Peeking_the_contents_does_not_subscribe_to_them() {
        var scheduler = new EffectScheduler();
        var items = new CollectionSignal<int>([1]);
        var runs = 0;

        using var effect = new Effect(() => {
                runs++;
                _ = items.Peek().Length;
            },
            scheduler
        );

        scheduler.Flush();
        items.Add(2);
        scheduler.Flush();

        Assert.Equal(1, runs);
    }
}
