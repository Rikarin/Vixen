// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     The flat object array and its stable ids — docs/plan/06 § Stride's model, idea 1.
/// </summary>
public class RenderObjectStoreTests {
    static RenderObject At(float x, float radius = 1f) =>
        new() { Bounds = new(new Vector3(x, 0f, 0f), radius), Stages = RenderStageMask.Of(0) };

    [Fact]
    public void An_added_object_comes_back_by_its_id() {
        using var store = new RenderObjectStore();

        var id = store.Add(At(5f));

        Assert.True(id.IsValid);
        Assert.True(store[id].IsAlive);
        Assert.Equal(5f, store[id].Bounds.Center.X);
    }

    /// <summary>
    ///     Ids stay put. Removal frees a slot rather than compacting, because every feature's
    ///     parallel array is indexed by the id — compacting would invalidate all of them at once.
    /// </summary>
    [Fact]
    public void Removing_one_object_does_not_move_another() {
        using var store = new RenderObjectStore();

        var first = store.Add(At(1f));
        var middle = store.Add(At(2f));
        var last = store.Add(At(3f));

        store.Remove(middle);

        Assert.Equal(1f, store[first].Bounds.Center.X);
        Assert.Equal(3f, store[last].Bounds.Center.X);
        Assert.False(store[middle].IsAlive);
    }

    [Fact]
    public void A_freed_slot_is_reused_before_the_array_grows() {
        using var store = new RenderObjectStore();

        store.Add(At(1f));
        var freed = store.Add(At(2f));
        store.Remove(freed);

        var reused = store.Add(At(3f));

        Assert.Equal(freed.Index, reused.Index);
        Assert.Equal(2, store.Count);
    }

    /// <summary>Reusing a slot clears every feature's data for it, so nothing is inherited.</summary>
    /// <remarks>
    ///     Cleared on reuse rather than on removal, which is the same work at a better time: the
    ///     frame that removed the object may still be in flight, and a slot nothing reads does not
    ///     need to be tidy.
    /// </remarks>
    [Fact]
    public void A_reused_slot_starts_with_cleared_feature_data() {
        using var store = new RenderObjectStore();

        var data = store.Data.Register<int>();

        var first = store.Add(At(1f));
        store.Data.Data(data)[first.Index] = 99;
        store.Remove(first);

        var reused = store.Add(At(2f));

        Assert.Equal(first.Index, reused.Index);
        Assert.Equal(0, store.Data.Data(data)[reused.Index]);
    }

    [Fact]
    public void Feature_arrays_are_grown_by_adding_objects() {
        using var store = new RenderObjectStore();

        var data = store.Data.Register<int>();

        for (var i = 0; i < 500; i++) {
            store.Add(At(i));
        }

        Assert.True(store.Data.Data(data).Length >= store.Count);
    }

    [Fact]
    public void Count_is_the_high_water_mark_and_LiveCount_is_what_is_alive() {
        using var store = new RenderObjectStore();

        var a = store.Add(At(1f));
        store.Add(At(2f));
        store.Remove(a);

        Assert.Equal(2, store.Count);
        Assert.Equal(1, store.LiveCount);
    }

    /// <summary>Removing the same id twice does not corrupt the free list.</summary>
    /// <remarks>
    ///     Without the liveness test, the slot would be pushed twice and handed out to two different
    ///     objects, which is the sort of aliasing that presents as one object rendering with
    ///     another's transform.
    /// </remarks>
    [Fact]
    public void Removing_twice_does_not_free_the_slot_twice() {
        using var store = new RenderObjectStore();

        var id = store.Add(At(1f));
        store.Remove(id);
        store.Remove(id);

        var first = store.Add(At(2f));
        var second = store.Add(At(3f));

        Assert.NotEqual(first.Index, second.Index);
    }

    [Fact]
    public void An_out_of_range_id_is_refused_rather_than_read() {
        using var store = new RenderObjectStore();
        store.Add(At(1f));

        Assert.Throws<ArgumentOutOfRangeException>(() => store[new(7)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => store[RenderObjectId.Invalid]);
    }

    [Fact]
    public void Clearing_keeps_the_memory_for_the_next_scene() {
        using var store = new RenderObjectStore();

        for (var i = 0; i < 100; i++) {
            store.Add(At(i));
        }

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.LiveCount);
        Assert.Equal(new RenderObjectId(0), store.Add(At(1f)));
    }
}
