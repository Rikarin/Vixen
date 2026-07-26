// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Pooling;
using Xunit;

namespace Vixen.Core.Tests;

/// <summary>
///     The rentals the frame loop leans on. Two properties matter throughout: a rental hands back
///     exactly what was asked for regardless of what the pool actually allocated, and disposing one
///     leaves nothing behind that the next renter can observe.
/// </summary>
public class PoolingTests {
    sealed class Box {
        public int Value { get; set; }
    }

    [Fact]
    public void An_object_pool_builds_an_instance_when_it_is_empty() {
        var built = 0;
        var pool = new ObjectPool<Box>(() => {
                built++;
                return new();
            }
        );

        pool.Rent();
        pool.Rent();

        Assert.Equal(2, built);
    }

    [Fact]
    public void A_returned_instance_is_handed_out_again() {
        var pool = new ObjectPool<Box>(static () => new());
        var first = pool.Rent();

        pool.Return(first);

        Assert.Same(first, pool.Rent());
    }

    [Fact]
    public void Reset_runs_on_return_so_the_pool_does_not_hold_on_to_the_last_use() {
        var pool = new ObjectPool<Box>(static () => new(), static box => box.Value = 0);
        var box = pool.Rent();
        box.Value = 42;

        pool.Return(box);

        Assert.Equal(0, box.Value);
    }

    [Fact]
    public void A_full_pool_drops_the_surplus_instead_of_growing() {
        var pool = new ObjectPool<Box>(static () => new(), capacity: 2);
        var retained = new[] { pool.Rent(), pool.Rent(), pool.Rent() };

        foreach (var box in retained) {
            pool.Return(box);
        }

        // Two came back from the pool; the third had to be built.
        var again = new[] { pool.Rent(), pool.Rent(), pool.Rent() };
        Assert.Equal(2, again.Count(retained.Contains));
        Assert.Equal(2, pool.Capacity);
    }

    [Fact]
    public void A_scoped_rental_returns_itself() {
        var pool = new ObjectPool<Box>(static () => new());
        Box borrowed;

        using (var scope = pool.RentScoped()) {
            borrowed = scope.Value;
        }

        Assert.Same(borrowed, pool.Rent());
    }

    [Fact]
    public void Clearing_a_pool_releases_what_it_held() {
        var pool = new ObjectPool<Box>(static () => new());
        var box = pool.Rent();
        pool.Return(box);

        pool.Clear();

        Assert.NotSame(box, pool.Rent());
    }

    [Fact]
    public void A_rented_array_is_exactly_as_long_as_it_was_asked_to_be() {
        // ArrayPool hands out a power-of-two array; the rental must not leak that.
        using var rental = PooledArray.Rent<int>(100);

        Assert.Equal(100, rental.Length);
        Assert.Equal(100, rental.Span.Length);
        Assert.True(rental.Array.Length >= 100);
    }

    [Fact]
    public void A_cleared_rental_is_zeroed_even_though_the_pool_recycles() {
        using (var dirty = PooledArray.Rent<int>(64)) {
            dirty.Span.Fill(0x5eeded);
        }

        using var clean = PooledArray.RentCleared<int>(64);

        foreach (var value in clean) {
            Assert.Equal(0, value);
        }
    }

    [Fact]
    public void The_clearing_policy_follows_whether_the_element_type_holds_references() {
        // Not tidiness: an uncleared object[] in the pool roots everything it last held.
        Assert.True(PooledArray.ClearsOnReturn<string>());
        Assert.True(PooledArray.ClearsOnReturn<KeyValuePair<int, object>>());
        Assert.False(PooledArray.ClearsOnReturn<int>());
        Assert.False(PooledArray.ClearsOnReturn<AssetId>());
    }

    [Fact]
    public void A_rented_array_bounds_checks_against_the_requested_length() {
        var rental = PooledArray.Rent<int>(4);
        try {
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = rental[4]);
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = rental[-1]);
        } finally {
            rental.Dispose();
        }
    }

    [Fact]
    public void A_pooled_list_grows_past_its_initial_capacity_without_losing_anything() {
        using var list = new PooledList<int>(4);

        for (var i = 0; i < 1000; i++) {
            list.Add(i);
        }

        Assert.Equal(1000, list.Count);
        for (var i = 0; i < 1000; i++) {
            Assert.Equal(i, list[i]);
        }
    }

    [Fact]
    public void A_pooled_list_appends_runs_and_reserves_space() {
        using var list = new PooledList<int>(0);

        list.AddRange([1, 2, 3]);
        var reserved = list.AppendSpan(2);
        reserved[0] = 4;
        reserved[1] = 5;
        list.Add(6);

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, list.Span.ToArray());
    }

    [Fact]
    public void Removing_from_a_pooled_list_either_keeps_order_or_is_cheap() {
        using var ordered = new PooledList<int>(8);
        ordered.AddRange([1, 2, 3, 4]);
        ordered.RemoveAt(1);
        Assert.Equal(new[] { 1, 3, 4 }, ordered.Span.ToArray());

        using var swapped = new PooledList<int>(8);
        swapped.AddRange([1, 2, 3, 4]);
        swapped.RemoveAtSwapBack(1);
        Assert.Equal(new[] { 1, 4, 3 }, swapped.Span.ToArray());
    }

    [Fact]
    public void A_pooled_list_bounds_checks_against_its_count_not_its_capacity() {
        var list = new PooledList<int>(64);
        try {
            list.Add(1);

            Assert.Equal(1, list.Count);
            Assert.True(list.Capacity >= 64);
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = list[1]);
        } finally {
            list.Dispose();
        }
    }

    [Fact]
    public void Clearing_a_pooled_list_keeps_the_buffer_and_disposing_gives_it_back() {
        var list = new PooledList<int>(16);
        list.AddRange([1, 2, 3]);

        list.Clear();
        Assert.Equal(0, list.Count);
        Assert.True(list.Capacity >= 16);

        list.Dispose();
        Assert.Equal(0, list.Count);
        Assert.Equal(0, list.Capacity);

        // Disposing twice must not return the same buffer to the pool twice.
        list.Dispose();
    }

    [Fact]
    public void A_default_pooled_list_behaves_as_an_empty_one() {
        // Nothing stops a caller writing `default`, so it had better not throw.
        var list = default(PooledList<int>);

        Assert.Equal(0, list.Count);
        Assert.True(list.IsEmpty);
        Assert.Equal(0, list.Span.Length);

        list.Add(1);
        Assert.Equal(new[] { 1 }, list.Span.ToArray());
        list.Dispose();
    }

    [Fact]
    public void A_pooled_list_enumerates_and_copies_out() {
        using var list = new PooledList<int>(4);
        list.AddRange([1, 2, 3]);

        var sum = 0;
        foreach (var value in list) {
            sum += value;
        }

        Assert.Equal(6, sum);
        Assert.Equal(new[] { 1, 2, 3 }, list.ToArray());
    }

    [Fact]
    public void A_pooled_dictionary_behaves_as_a_dictionary() {
        var map = new PooledDictionary<string, int>();
        try {
            map.Add("a", 1);
            map["b"] = 2;

            Assert.Equal(new[] { "a", "b" }, map.Keys.Order(StringComparer.Ordinal));
            Assert.True(map.TryGetValue("a", out var a));
            Assert.Equal(1, a);
            Assert.True(map.ContainsKey("b"));
            Assert.False(map.TryAdd("a", 9));
            Assert.True(map.Remove("a"));
            Assert.Equal(new[] { "b" }, map.Keys.ToArray());
        } finally {
            map.Dispose();
        }
    }

    [Fact]
    public void Disposing_a_pooled_dictionary_clears_it_before_the_pool_takes_it_back() {
        var map = new PooledDictionary<string, int>();
        map.Add("stale", 1);

        // The instance the pool will hand to the next renter.
        var underlying = map.AsDictionary();
        Assert.Single(underlying);

        map.Dispose();

        Assert.Empty(underlying);
        Assert.Empty(map.Keys);
        Assert.False(map.ContainsKey("stale"));
    }

    [Fact]
    public void A_default_pooled_dictionary_reads_as_empty_and_rents_on_first_write() {
        var map = default(PooledDictionary<int, int>);

        Assert.Empty(map.Keys);
        Assert.False(map.ContainsKey(1));
        Assert.False(map.TryGetValue(1, out _));

        map[1] = 5;

        Assert.Equal(5, map[1]);
        map.Dispose();
    }

    [Fact]
    public void A_pooled_dictionary_enumerates_its_entries() {
        var map = new PooledDictionary<int, int>(4);
        try {
            map[1] = 10;
            map[2] = 20;

            var sum = 0;
            foreach (var entry in map) {
                sum += entry.Key + entry.Value;
            }

            Assert.Equal(33, sum);
        } finally {
            map.Dispose();
        }
    }
}
