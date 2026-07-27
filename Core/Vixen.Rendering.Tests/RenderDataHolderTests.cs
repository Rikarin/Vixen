// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     Per-feature data arrays — docs/plan/06 § Stride's model, idea 2.
/// </summary>
/// <remarks>
///     The mechanism doc 06 calls "the reason Stride's renderer is extensible where most are not":
///     a feature that needs per-object state registers an array of its own rather than adding a
///     field to a shared type. These tests are about the properties that makes possible — arrays
///     staying in lockstep, ids staying stable — because those are what break silently if the
///     growth or reuse rules are wrong.
/// </remarks>
public class RenderDataHolderTests {
    [Fact]
    public void A_registered_array_is_addressed_by_object_id() {
        using var holder = new RenderDataHolder();

        var transforms = holder.Register<Matrix4x4>();
        holder.EnsureCapacity(8);

        holder.Data(transforms)[3] = Matrix4x4.Identity;

        Assert.Equal(Matrix4x4.Identity, holder.Data(transforms)[3]);
    }

    /// <summary>
    ///     Every array grows together, because a short one is an out-of-range read inside a job.
    /// </summary>
    /// <remarks>
    ///     The failure this prevents is the least debuggable shape the renderer has: a feature array
    ///     one element shorter than the object array, read on a worker thread, at an index that only
    ///     occurs once the scene passes some size.
    /// </remarks>
    [Fact]
    public void Every_array_grows_in_lockstep_however_late_it_was_registered() {
        using var holder = new RenderDataHolder();

        var first = holder.Register<int>();
        holder.EnsureCapacity(100);

        var late = holder.Register<float>();

        Assert.Equal(holder.Data(first).Length, holder.Data(late).Length);
        Assert.True(holder.Data(late).Length >= 100);
    }

    [Fact]
    public void Growth_keeps_what_was_already_written() {
        using var holder = new RenderDataHolder();

        var values = holder.Register<int>();
        holder.EnsureCapacity(4);
        holder.Data(values)[2] = 42;

        holder.EnsureCapacity(1000);

        Assert.Equal(42, holder.Data(values)[2]);
    }

    /// <summary>New storage is zeroed, so an unfilled slot is a default rather than old memory.</summary>
    /// <remarks>
    ///     On a transform array, whatever the allocator handed back is a NaN matrix and an object
    ///     that vanishes for a frame — which reads as a culling bug and is not one.
    /// </remarks>
    [Fact]
    public void New_storage_is_zeroed() {
        using var holder = new RenderDataHolder();

        var values = holder.Register<float>();
        holder.EnsureCapacity(256);

        Assert.All(holder.Data(values).ToArray(), value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Clearing_a_slot_clears_it_in_every_array() {
        using var holder = new RenderDataHolder();

        var ints = holder.Register<int>();
        var floats = holder.Register<float>();
        holder.EnsureCapacity(8);

        holder.Data(ints)[5] = 7;
        holder.Data(floats)[5] = 7f;

        holder.ClearSlot(new(5));

        Assert.Equal(0, holder.Data(ints)[5]);
        Assert.Equal(0f, holder.Data(floats)[5]);
    }

    [Fact]
    public void Growth_never_shrinks() {
        using var holder = new RenderDataHolder();

        holder.Register<int>();
        holder.EnsureCapacity(500);
        var grown = holder.Capacity;

        holder.EnsureCapacity(1);

        Assert.Equal(grown, holder.Capacity);
    }

    [Fact]
    public void Using_a_disposed_holder_is_refused_rather_than_reading_freed_memory() {
        var holder = new RenderDataHolder();
        var values = holder.Register<int>();
        holder.EnsureCapacity(4);
        holder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => holder.Data(values));
        Assert.Throws<ObjectDisposedException>(() => holder.EnsureCapacity(8));
    }

    [Fact]
    public void Disposing_twice_is_harmless() {
        var holder = new RenderDataHolder();
        holder.Register<int>();
        holder.Dispose();
        holder.Dispose();
    }
}
