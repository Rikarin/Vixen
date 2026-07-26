// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Collections.Tests;

/// <summary>
///     Handles and the pool behind them. The generation check is the whole reason this type exists,
///     so most of what is asserted here is that a stale handle is caught rather than silently
///     addressing whatever moved into the slot.
/// </summary>
public class HandleTests {
    sealed class Resource(string name) {
        public string Name => name;
    }

    [Fact]
    public void A_zeroed_handle_refers_to_nothing() {
        Assert.True(Handle<Resource>.Null.IsNull);
        Assert.True(default(Handle<Resource>).IsNull);
        Assert.Equal(Handle<Resource>.Null, default);
        Assert.False(new HandlePool<Resource>().Contains(Handle<Resource>.Null));
    }

    [Fact]
    public void A_handle_round_trips_through_its_packed_form() {
        var handle = new Handle<Resource>(7, 3);

        Assert.Equal(handle, Handle<Resource>.FromPacked(handle.Packed));
        Assert.Equal(handle.GetHashCode(), Handle<Resource>.FromPacked(handle.Packed).GetHashCode());
    }

    [Fact]
    public void An_added_item_comes_back_through_its_handle() {
        var pool = new HandlePool<Resource>();
        var resource = new Resource("buffer");

        var handle = pool.Add(resource);

        Assert.True(pool.Contains(handle));
        Assert.Same(resource, pool.Get(handle));
        Assert.True(pool.TryGet(handle, out var found));
        Assert.Same(resource, found);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void A_handle_to_a_removed_item_is_detected_rather_than_followed() {
        // The entire point. With a raw index this read would return whatever now lives in the slot.
        var pool = new HandlePool<Resource>();
        var stale = pool.Add(new("first"));

        Assert.True(pool.Remove(stale));

        Assert.False(pool.Contains(stale));
        Assert.False(pool.TryGet(stale, out _));
        Assert.Throws<InvalidOperationException>(() => pool.Get(stale));
        Assert.False(pool.Remove(stale));
    }

    [Fact]
    public void A_reused_slot_hands_out_a_different_handle() {
        var pool = new HandlePool<Resource>();
        var first = pool.Add(new("first"));
        pool.Remove(first);

        var second = pool.Add(new("second"));

        // Same slot, so the index matches — and the generation is what tells them apart.
        Assert.Equal(first.Index, second.Index);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.NotEqual(first, second);
        Assert.False(pool.Contains(first));
        Assert.True(pool.Contains(second));
        Assert.Equal("second", pool.Get(second).Name);
    }

    [Fact]
    public void A_forged_handle_carrying_a_freed_slots_generation_is_rejected() {
        // Handle is a public struct, so anything can construct one. A freed slot's generation is
        // even and no live handle ever carries an even generation, which is what makes this safe
        // rather than merely unlikely.
        var pool = new HandlePool<Resource>();
        var handle = pool.Add(new("resource"));
        pool.Remove(handle);

        Assert.False(pool.Contains(new(handle.Index, handle.Generation + 1)));
        Assert.False(pool.Contains(new(handle.Index, 0)));
        Assert.False(pool.Contains(new(9999, 1)));
    }

    [Fact]
    public void A_reference_into_the_pool_can_be_mutated_in_place() {
        var pool = new HandlePool<int>();
        var handle = pool.Add(5);

        pool.GetReference(handle) = 42;

        Assert.Equal(42, pool.Get(handle));
        pool.Remove(handle);
        Assert.Throws<InvalidOperationException>(() => pool.GetReference(handle));
    }

    [Fact]
    public void The_pool_enumerates_only_its_live_slots() {
        var pool = new HandlePool<Resource>();
        var first = pool.Add(new("a"));
        var second = pool.Add(new("b"));
        var third = pool.Add(new("c"));

        pool.Remove(second);

        var seen = new List<string>();
        foreach (var (handle, item) in pool) {
            Assert.True(pool.Contains(handle));
            seen.Add(item.Name);
        }

        Assert.Equal(new[] { "a", "c" }, seen);
        Assert.Equal(2, pool.Count);
        Assert.True(pool.Contains(first));
        Assert.True(pool.Contains(third));
    }

    [Fact]
    public void Clearing_invalidates_every_outstanding_handle() {
        var pool = new HandlePool<Resource>();
        var handles = new[] { pool.Add(new("a")), pool.Add(new("b")), pool.Add(new("c")) };

        pool.Clear();

        Assert.Equal(0, pool.Count);
        foreach (var handle in handles) {
            Assert.False(pool.Contains(handle));
        }
    }

    [Fact]
    public void The_pool_grows_and_keeps_every_handle_valid_across_the_growth() {
        var pool = new HandlePool<int>(capacity: 2);
        var handles = new List<Handle<int>>();

        for (var i = 0; i < 500; i++) {
            handles.Add(pool.Add(i));
        }

        Assert.Equal(500, pool.Count);
        for (var i = 0; i < 500; i++) {
            Assert.Equal(i, pool.Get(handles[i]));
        }
    }

    [Fact]
    public void Slots_are_recycled_rather_than_the_table_growing_without_bound() {
        var pool = new HandlePool<int>();

        for (var i = 0; i < 1000; i++) {
            var handle = pool.Add(i);
            pool.Remove(handle);
        }

        Assert.Equal(0, pool.Count);
        Assert.Equal(1, pool.Capacity);
    }

    [Fact]
    public void Handles_sort_and_render_readably() {
        Assert.True(new Handle<Resource>(1, 1) < new Handle<Resource>(2, 1));
        Assert.True(new Handle<Resource>(1, 1) < new Handle<Resource>(1, 3));
        Assert.Equal("Resource#7:3", new Handle<Resource>(7, 3).ToString());
        Assert.Equal("Resource#null", Handle<Resource>.Null.ToString());
    }
}
