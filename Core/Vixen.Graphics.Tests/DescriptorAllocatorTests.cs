// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.Tests;

/// <summary>
///     The per-frame descriptor allocator — the lifetime a frame graph needs and the RHI does not have.
/// </summary>
/// <remarks>
///     Two properties carry the whole class, and they pull in opposite directions. Sets must be
///     reused, or a frame allocates hundreds of them; and a set must not be reused before the GPU has
///     finished reading it, which no API here will report and no driver will complain about. The ring
///     is what reconciles them, so most of what follows is about the ring's depth.
/// </remarks>
public class DescriptorAllocatorTests : IDisposable {
    readonly NullDevice device = new(new() { FramesInFlight = 3 });
    readonly DescriptorSetLayoutHandle layout;
    readonly DescriptorSetLayoutHandle other;
    readonly BufferHandle first;
    readonly BufferHandle second;

    public DescriptorAllocatorTests() {
        layout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerView, [new(0, DescriptorKind.StorageBuffer, ShaderStage.Fragment)], "View")
        );

        // The same shape as `layout` in a different set, which is what
        // A_set_belongs_to_its_layout needs: identical writes, so that the only thing that can
        // separate the two sets is the layout. A different *kind* here would make that test pass for
        // the wrong reason — and now that the Null backend holds a write against what the layout
        // declared, it would not pass at all.
        other = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerMaterial, [new(0, DescriptorKind.StorageBuffer, ShaderStage.Fragment)], "Material")
        );

        first = device.CreateBuffer(new(1024, BufferUsage.Storage, Name: "First"));
        second = device.CreateBuffer(new(1024, BufferUsage.Storage, Name: "Second"));
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Two passes asking for the same bindings get one set between them.
    /// </summary>
    /// <remarks>
    ///     The difference between a set per pass and a set per distinct combination. Every pass in a
    ///     shading chain reads the same shadow atlas and the same cluster list, so in a real frame this
    ///     is two orders of magnitude rather than a rounding error.
    /// </remarks>
    [Fact]
    public void Identical_writes_within_a_frame_are_one_set() {
        using var allocator = new DescriptorAllocator(device);
        allocator.BeginFrame();

        var a = allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);
        var b = allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);

        Assert.Equal(a, b);
        Assert.Equal(1, allocator.WriteCount);
        Assert.Equal(1, allocator.ReuseCount);
        Assert.Equal(1, allocator.SetCount);
    }

    /// <summary>Different bindings are different sets, even at the same binding index.</summary>
    [Fact]
    public void Different_writes_are_different_sets() {
        using var allocator = new DescriptorAllocator(device);
        allocator.BeginFrame();

        var a = allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);
        var b = allocator.Allocate(layout, [DescriptorWrite.Storage(0, second)]);

        Assert.NotEqual(a, b);
        Assert.Equal(2, allocator.WriteCount);
        Assert.Equal(0, allocator.ReuseCount);
    }

    /// <summary>Two layouts never share a set, however identical the writes are.</summary>
    /// <remarks>
    ///     A set is only bindable to a pipeline whose layout it was allocated from, so a cache keyed
    ///     on the writes alone would hand back something the validation layers reject and a release
    ///     driver silently mis-binds.
    /// </remarks>
    [Fact]
    public void A_set_belongs_to_its_layout() {
        using var allocator = new DescriptorAllocator(device);
        allocator.BeginFrame();

        var a = allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);
        var b = allocator.Allocate(other, [DescriptorWrite.Storage(0, first)]);

        Assert.NotEqual(a, b);
    }

    /// <summary>
    ///     A set is never handed out again while the GPU could still be reading it.
    /// </summary>
    /// <remarks>
    ///     The load-bearing one. The same request every frame is answered with a <em>different</em> set
    ///     until the frame that first used it has retired — because rewriting it earlier points a
    ///     descriptor the GPU is reading at something else, which is a use-after-free that executes
    ///     without a word on most drivers.
    /// </remarks>
    [Fact]
    public void A_set_in_flight_is_never_rewritten() {
        using var allocator = new DescriptorAllocator(device);
        var seen = new List<DescriptorSetHandle>();

        for (var frame = 0; frame < device.FramesInFlight; frame++) {
            allocator.BeginFrame();
            seen.Add(allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]));
        }

        Assert.Equal(device.FramesInFlight, seen.Distinct().Count());
    }

    /// <summary>
    ///     Once the ring has come round, the same workload allocates nothing new forever.
    /// </summary>
    /// <remarks>
    ///     What a leak test asserts: a steady frame settles at exactly <c>FramesInFlight</c> sets. A
    ///     pool that kept growing here would be one that never returns anything, which is the failure
    ///     mode that only shows up after an hour of play.
    /// </remarks>
    [Fact]
    public void A_steady_frame_settles_at_one_set_per_frame_in_flight() {
        using var allocator = new DescriptorAllocator(device);

        for (var frame = 0; frame < 100; frame++) {
            allocator.BeginFrame();
            allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);
        }

        Assert.Equal(device.FramesInFlight, allocator.SetCount);
    }

    /// <summary>A recycled set goes back to its own layout's pool, not to a shared one.</summary>
    [Fact]
    public void A_recycled_set_is_reused_for_the_layout_it_was_made_for() {
        using var allocator = new DescriptorAllocator(device);

        for (var frame = 0; frame < 100; frame++) {
            allocator.BeginFrame();
            allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);
            allocator.Allocate(other, [DescriptorWrite.Storage(0, second)]);
        }

        // Two layouts, one set each per frame in flight. A shared pool would hand a PerView set to a
        // PerMaterial request and settle at half this.
        Assert.Equal(device.FramesInFlight * 2, allocator.SetCount);
    }

    /// <summary>The cache does not survive the frame.</summary>
    /// <remarks>
    ///     Deliberate, not an oversight. The handles in it name transient graph memory that the next
    ///     frame is free to give to something else, so a cache that persisted would be correct exactly
    ///     until two frames' graphs differed — the hardest kind of bug to find, because the first
    ///     thousand frames work.
    /// </remarks>
    [Fact]
    public void The_cache_is_cleared_at_the_frame_boundary() {
        using var allocator = new DescriptorAllocator(device);

        allocator.BeginFrame();
        allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);
        allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);
        Assert.Equal(1, allocator.ReuseCount);

        allocator.BeginFrame();
        Assert.Equal(0, allocator.ReuseCount);
        Assert.Equal(0, allocator.WriteCount);
    }

    /// <summary>Reset takes everything back at once, for after the caller has waited.</summary>
    [Fact]
    public void Reset_returns_every_ring_slot() {
        using var allocator = new DescriptorAllocator(device);

        for (var frame = 0; frame < 10; frame++) {
            allocator.BeginFrame();
            allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);
            allocator.Reset();
        }

        // Reset makes the previous frame's set available immediately, so every frame reuses the first.
        Assert.Equal(1, allocator.SetCount);
    }

    /// <summary>Disposing returns every set it ever created.</summary>
    [Fact]
    public void Dispose_destroys_every_set_it_created() {
        var before = device.LiveResourceCount;
        var allocator = new DescriptorAllocator(device);

        for (var frame = 0; frame < 20; frame++) {
            allocator.BeginFrame();
            allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);
            allocator.Allocate(layout, [DescriptorWrite.Storage(0, second)]);
        }

        Assert.True(device.LiveResourceCount > before, "nothing was created, so nothing is being tested");

        allocator.Dispose();

        Assert.Equal(before, device.LiveResourceCount);
    }

    /// <summary>Disposing twice is not an error, and does not destroy anything twice.</summary>
    [Fact]
    public void Dispose_is_idempotent() {
        var before = device.LiveResourceCount;
        var allocator = new DescriptorAllocator(device);

        allocator.BeginFrame();
        allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)]);

        allocator.Dispose();
        allocator.Dispose();

        Assert.Equal(before, device.LiveResourceCount);
    }

    /// <summary>A set with no layout is refused rather than guessed at.</summary>
    [Fact]
    public void Allocating_without_a_layout_is_refused() {
        using var allocator = new DescriptorAllocator(device);
        allocator.BeginFrame();

        Assert.Throws<ArgumentException>(
            () => allocator.Allocate(DescriptorSetLayoutHandle.Null, [DescriptorWrite.Storage(0, first)])
        );
    }

    /// <summary>A device claiming no frames in flight still gets a ring one deep.</summary>
    /// <remarks>
    ///     A ring of zero would divide by zero on the first frame. Clamping is not politeness — a
    ///     headless device that renders nothing legitimately reports no frames.
    /// </remarks>
    [Fact]
    public void A_device_with_no_frames_in_flight_still_works() {
        using var headless = new NullDevice(new() { FramesInFlight = 0 });
        using var allocator = new DescriptorAllocator(headless);

        var target = headless.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerFrame, [new(0, DescriptorKind.StorageBuffer, ShaderStage.Compute)], "Frame")
        );

        var buffer = headless.CreateBuffer(new(256, BufferUsage.Storage, Name: "Only"));

        allocator.BeginFrame();
        var a = allocator.Allocate(target, [DescriptorWrite.Storage(0, buffer)]);
        allocator.BeginFrame();
        var b = allocator.Allocate(target, [DescriptorWrite.Storage(0, buffer)]);

        Assert.Equal(1, allocator.FramesInFlight);
        Assert.Equal(a, b);
    }

    /// <summary>Using one after disposing it says so, rather than handing out a destroyed set.</summary>
    [Fact]
    public void Using_a_disposed_allocator_is_refused() {
        var allocator = new DescriptorAllocator(device);
        allocator.Dispose();

        Assert.Throws<ObjectDisposedException>(() => allocator.BeginFrame());
        Assert.Throws<ObjectDisposedException>(
            () => allocator.Allocate(layout, [DescriptorWrite.Storage(0, first)])
        );
    }
}
