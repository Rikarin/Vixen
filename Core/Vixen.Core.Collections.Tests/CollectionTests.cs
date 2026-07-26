// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Core.Collections.Tests;

/// <summary>
///     The rest of the collections. Where a BCL type has the same semantics it is used as the
///     oracle: a sparse set should behave exactly like a dictionary that happens to iterate densely,
///     and a bit set exactly like a hash set of integers. Those comparisons find the edge cases
///     nobody enumerates by hand.
/// </summary>
public class CollectionTests {
    [Fact]
    public void A_free_list_recycles_the_slots_it_is_given_back() {
        var list = new FreeList<string>();

        var first = list.Add("a");
        var second = list.Add("b");
        list.Release(first);

        Assert.Equal(first, list.Add("c"));
        Assert.Equal(2, list.Count);
        Assert.Equal("c", list[first]);
        Assert.Equal("b", list[second]);
    }

    [Fact]
    public void Releasing_a_slot_twice_is_caught_rather_than_corrupting_the_list() {
        // Without the check the slot lands on the free list twice and two callers get the same
        // index — a corruption that surfaces arbitrarily far from its cause.
        var list = new FreeList<string>();
        var index = list.Add("a");

        list.Release(index);

        Assert.Throws<InvalidOperationException>(() => list.Release(index));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Release(99));
    }

    [Fact]
    public void A_free_list_enumerates_only_its_live_slots() {
        var list = new FreeList<int>();
        list.Add(10);
        var middle = list.Add(20);
        list.Add(30);
        list.Release(middle);

        var seen = new List<int>();
        foreach (var (_, item) in list) {
            seen.Add(item);
        }

        Assert.Equal(new[] { 10, 30 }, seen);
        Assert.False(list.IsLive(middle));
        Assert.True(list.IsLive(0));
    }

    [Fact]
    public void A_sparse_set_behaves_like_a_dictionary_that_iterates_densely() {
        // The oracle. Random operations against a Dictionary, checking the two agree throughout.
        var operations = Gen.Select(Gen.Int[0, 3], Gen.Int[0, 40], Gen.Int[0, 100]).Array[0, 200];

        operations.Sample(script => {
                var set = new SparseSet<int>(keyCapacity: 8, capacity: 2);
                var reference = new Dictionary<int, int>();

                foreach (var (operation, key, value) in script) {
                    switch (operation) {
                        case 0:
                        case 1:
                            set.Set(key, value);
                            reference[key] = value;
                            break;
                        case 2:
                            Assert.Equal(reference.Remove(key), set.Remove(key));
                            break;
                        default:
                            Assert.Equal(reference.ContainsKey(key), set.Contains(key));
                            break;
                    }

                    Assert.Equal(reference.Count, set.Count);
                }

                // Dense side holds exactly the entries, in some order.
                Assert.Equal(reference.Count, set.Keys.Length);
                foreach (var key in set.Keys) {
                    Assert.True(set.TryGetValue(key, out var value));
                    Assert.Equal(reference[key], value);
                }
            }
        );
    }

    [Fact]
    public void Removing_from_a_sparse_set_keeps_the_dense_side_packed() {
        var set = new SparseSet<string>();
        set.Set(1, "a");
        set.Set(5, "b");
        set.Set(9, "c");

        set.Remove(1);

        // The last entry moved into the hole, so the order changed and there is no gap.
        Assert.Equal(2, set.Count);
        Assert.Equal(2, set.Values.Length);
        Assert.Contains("b", set.Values.ToArray());
        Assert.Contains("c", set.Values.ToArray());
        Assert.False(set.Contains(1));
    }

    [Fact]
    public void A_sparse_set_grows_its_key_range_on_demand() {
        var set = new SparseSet<int>(keyCapacity: 4);
        set.Set(1000, 7);

        Assert.True(set.KeyCapacity > 1000);
        Assert.True(set.TryGetValue(1000, out var value));
        Assert.Equal(7, value);
        Assert.False(set.Contains(999));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.Set(-1, 0));
    }

    [Fact]
    public void A_sparse_set_value_can_be_mutated_in_place() {
        var set = new SparseSet<int>();
        set.Set(3, 10);

        set.GetReference(3) += 5;

        Assert.Equal(15, set.GetReference(3));
        Assert.Throws<KeyNotFoundException>(() => set.GetReference(4));
    }

    [Fact]
    public void A_bit_set_behaves_like_a_hash_set_of_integers() =>
        Gen.Int[0, 500].Array[0, 100].Sample(indices => {
                var bits = new BitSet(8);
                var reference = new HashSet<int>();

                foreach (var index in indices) {
                    bits.Set(index);
                    reference.Add(index);
                }

                Assert.Equal(reference.Count, bits.PopCount());

                var enumerated = new List<int>();
                foreach (var index in bits) {
                    enumerated.Add(index);
                }

                // Ascending, and exactly the set that went in.
                Assert.Equal(reference.OrderBy(static i => i), enumerated);
            }
        );

    [Fact]
    public void A_bit_set_reads_false_past_its_end_without_growing() {
        var bits = new BitSet(64);
        var capacity = bits.Capacity;

        Assert.False(bits[10_000]);
        bits.Clear(10_000);

        Assert.Equal(capacity, bits.Capacity);
        Assert.True(bits.IsEmpty());

        // Setting is what grows it.
        bits.Set(10_000);
        Assert.True(bits[10_000]);
        Assert.True(bits.Capacity > 10_000);
    }

    [Fact]
    public void Bit_set_operations_match_their_set_theoretic_meanings() {
        var a = new BitSet();
        var b = new BitSet();
        foreach (var index in new[] { 1, 5, 70 }) {
            a.Set(index);
        }

        foreach (var index in new[] { 5, 70, 200 }) {
            b.Set(index);
        }

        Assert.False(a.Contains(b));
        Assert.True(a.Intersects(b));

        var union = new BitSet();
        union.UnionWith(a);
        union.UnionWith(b);
        Assert.True(union.Contains(a));
        Assert.True(union.Contains(b));
        Assert.Equal(4, union.PopCount());

        var intersection = new BitSet();
        intersection.UnionWith(a);
        intersection.IntersectWith(b);
        Assert.Equal(2, intersection.PopCount());
        Assert.True(intersection[5]);
        Assert.False(intersection[1]);

        var difference = new BitSet();
        difference.UnionWith(a);
        difference.ExceptWith(b);
        Assert.Equal(1, difference.PopCount());
        Assert.True(difference[1]);
    }

    [Fact]
    public void An_empty_bit_set_is_contained_by_everything() {
        // The archetype query that asks for nothing matches every archetype, which is the identity
        // that makes "no filter" need no special case.
        var empty = new BitSet();
        var populated = new BitSet();
        populated.Set(3);

        Assert.True(populated.Contains(empty));
        Assert.True(empty.Contains(empty));
        Assert.False(empty.Contains(populated));
    }

    [Fact]
    public void A_small_list_stays_inline_until_it_outgrows_its_buffer() {
        using var list = new SmallList<int, Buffer4<int>>();

        Assert.Equal(4, SmallList<int, Buffer4<int>>.InlineCapacity);

        list.AddRange([1, 2, 3, 4]);
        Assert.False(list.HasSpilled);
        Assert.Equal(new[] { 1, 2, 3, 4 }, list.Span.ToArray());

        list.Add(5);
        Assert.True(list.HasSpilled);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, list.Span.ToArray());
    }

    [Fact]
    public void A_small_list_survives_growing_well_past_its_inline_buffer() {
        var list = new SmallList<int, Buffer8<int>>();
        try {
            for (var i = 0; i < 1000; i++) {
                list.Add(i);
            }

            Assert.Equal(1000, list.Count);
            for (var i = 0; i < 1000; i++) {
                Assert.Equal(i, list[i]);
            }
        } finally {
            list.Dispose();
        }
    }

    [Fact]
    public void Removing_from_a_small_list_either_keeps_order_or_is_cheap() {
        using var ordered = new SmallList<int, Buffer8<int>>();
        ordered.AddRange([1, 2, 3, 4]);
        ordered.RemoveAt(1);
        Assert.Equal(new[] { 1, 3, 4 }, ordered.Span.ToArray());

        using var swapped = new SmallList<int, Buffer8<int>>();
        swapped.AddRange([1, 2, 3, 4]);
        swapped.RemoveAtSwapBack(1);
        Assert.Equal(new[] { 1, 4, 3 }, swapped.Span.ToArray());
    }

    [Fact]
    public void A_chunked_array_keeps_references_valid_while_it_grows() {
        // The property a List<T> cannot offer, and the entire reason this type exists.
        var array = new ChunkedArray<int>(chunkSize: 4);
        array.Add(42);

        ref var first = ref array[0];

        for (var i = 1; i < 10_000; i++) {
            array.Add(i);
        }

        // Still pointing at the same element after thousands of chunks were added.
        first = 7;
        Assert.Equal(7, array[0]);
        Assert.Equal(10_000, array.Count);
    }

    [Fact]
    public void A_chunked_array_rounds_its_chunk_size_to_a_power_of_two() {
        var array = new ChunkedArray<int>(chunkSize: 100);

        Assert.Equal(128, array.ChunkSize);

        array.Grow(300);
        Assert.Equal(300, array.Count);
        Assert.Equal(3, array.ChunkCount);

        // The last chunk is short, because it only reports the live elements.
        Assert.Equal(128, array.GetChunk(0).Length);
        Assert.Equal(300 - 256, array.GetChunk(2).Length);
    }

    [Fact]
    public void A_ring_buffer_drops_its_oldest_entry_rather_than_growing() {
        var ring = new RingBuffer<int>(3);
        foreach (var value in new[] { 1, 2, 3, 4, 5 }) {
            ring.Enqueue(value);
        }

        Assert.Equal(3, ring.Count);
        Assert.Equal(2, ring.OverwrittenCount);
        Assert.Equal(new[] { 3, 4, 5 }, ring.ToArrayInOrder());

        // And says how much it threw away, so "missing the beginning" is distinguishable from
        // "nothing was logged".
        Assert.True(ring.IsFull);
        Assert.False(ring.TryEnqueue(6));
        Assert.Equal(new[] { 3, 4, 5 }, ring.ToArrayInOrder());
    }

    [Fact]
    public void A_ring_buffer_dequeues_oldest_first_across_the_wrap() {
        var ring = new RingBuffer<int>(3);
        ring.Enqueue(1);
        ring.Enqueue(2);
        ring.Enqueue(3);
        ring.Enqueue(4);

        Assert.True(ring.TryPeek(out var oldest));
        Assert.Equal(2, oldest);

        Assert.True(ring.TryDequeue(out var first));
        Assert.True(ring.TryDequeue(out var second));
        Assert.Equal(2, first);
        Assert.Equal(3, second);

        ring.Enqueue(5);
        Assert.Equal(new[] { 4, 5 }, ring.ToArrayInOrder());

        ring.Clear();
        Assert.True(ring.IsEmpty);
        Assert.False(ring.TryDequeue(out _));
    }

    [Fact]
    public void An_indexed_priority_queue_comes_out_in_priority_order() =>
        Gen.Select(Gen.Int[0, 60], Gen.Int[-100, 100]).Array[1, 60].Sample(entries => {
                var queue = new IndexedPriorityQueue<int>(idCapacity: 4);
                var reference = new Dictionary<int, int>();

                foreach (var (id, priority) in entries) {
                    queue.SetPriority(id, priority);
                    reference[id] = priority;
                }

                Assert.Equal(reference.Count, queue.Count);

                var previous = int.MinValue;
                while (queue.TryDequeue(out var id, out var priority)) {
                    Assert.True(priority >= previous);
                    Assert.Equal(reference[id], priority);
                    Assert.True(reference.Remove(id));
                    previous = priority;
                }

                Assert.Empty(reference);
            }
        );

    [Fact]
    public void Decreasing_a_priority_moves_an_entry_forward_and_never_backward() {
        var queue = new IndexedPriorityQueue<int>();
        queue.Enqueue(1, 10);
        queue.Enqueue(2, 20);
        queue.Enqueue(3, 30);

        // Improving works.
        Assert.True(queue.TryDecreasePriority(3, 5));
        Assert.True(queue.TryPeek(out var id, out var priority));
        Assert.Equal(3, id);
        Assert.Equal(5, priority);

        // Making it worse is refused, which is what keeps two relaxations reaching the same entry
        // in an unlucky order from undoing each other.
        Assert.False(queue.TryDecreasePriority(3, 100));
        Assert.True(queue.TryPeek(out _, out var unchanged));
        Assert.Equal(5, unchanged);

        Assert.False(queue.TryDecreasePriority(99, 0));
    }

    [Fact]
    public void An_indexed_priority_queue_can_reach_an_entry_it_has_already_queued() {
        var queue = new IndexedPriorityQueue<int>();
        queue.Enqueue(1, 10);
        queue.Enqueue(2, 20);

        // Raising a priority is the direction the BCL's queue cannot do at all.
        queue.SetPriority(1, 30);
        Assert.True(queue.TryPeek(out var id, out _));
        Assert.Equal(2, id);

        Assert.True(queue.Contains(1));
        Assert.True(queue.Remove(1));
        Assert.False(queue.Contains(1));
        Assert.False(queue.Remove(1));
        Assert.Equal(1, queue.Count);

        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(2, 5));
    }

    [Fact]
    public void An_empty_priority_queue_reports_rather_than_throws() {
        var queue = new IndexedPriorityQueue<int>();

        Assert.True(queue.IsEmpty);
        Assert.False(queue.TryPeek(out _, out _));
        Assert.False(queue.TryDequeue(out _, out _));

        queue.Enqueue(1, 1);
        queue.Clear();
        Assert.True(queue.IsEmpty);
        Assert.False(queue.Contains(1));
    }
}

static class RingBufferExtensions {
    public static T[] ToArrayInOrder<T>(this RingBuffer<T> ring) {
        var items = new T[ring.Count];
        ring.CopyTo(items);
        return items;
    }
}
