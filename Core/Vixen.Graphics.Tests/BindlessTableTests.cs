// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.Tests;

/// <summary>
///     The global texture table — one set a shader indexes rather than a set a draw binds.
/// </summary>
/// <remarks>
///     Three properties carry it, and they are the ones a bindless renderer is wrong without. An
///     index that was handed out is written into data nobody can find again, so it must not move. The
///     same view asked for twice must be one descriptor, or a table is an array of duplicates. And a
///     released index must not come back while a frame that named it is still in flight, which is the
///     hazard nothing reports and no driver complains about.
/// </remarks>
public class BindlessTableTests : IDisposable {
    readonly NullDevice device = new(new() { FramesInFlight = 3 });
    readonly TextureViewHandle albedo;
    readonly TextureViewHandle normal;
    readonly TextureViewHandle missing;

    public BindlessTableTests() {
        albedo = View("Albedo");
        normal = View("Normal");
        missing = View("Missing");
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A table is one set of one unbounded binding, and it exists.</summary>
    [Fact]
    public void A_table_is_one_set_and_one_layout() {
        using var table = new BindlessTable(device);

        Assert.True(table.Layout.IsValid);
        Assert.True(table.Set.IsValid);
        Assert.Equal(device.Features.MaxBindlessDescriptors, table.Capacity);
        Assert.Equal(0, table.Count);
    }

    /// <summary>Indices start at zero and count up, and each one is written once.</summary>
    [Fact]
    public void Each_new_view_takes_the_next_slot() {
        using var table = new BindlessTable(device);

        Assert.Equal(0u, table.Add(albedo));
        Assert.Equal(1u, table.Add(normal));
        Assert.Equal(2, table.Count);
        Assert.Equal(2, table.WriteCount);
        Assert.Equal(2, device.DescriptorWrites);
    }

    /// <summary>
    ///     The same view twice is the same index and one descriptor.
    /// </summary>
    /// <remarks>
    ///     The economy the table exists for. Forty materials over one atlas is one descriptor, and a
    ///     settled scene that keeps asking costs nothing — which is what the write count asserts.
    /// </remarks>
    [Fact]
    public void The_same_view_is_the_same_slot() {
        using var table = new BindlessTable(device);

        var first = table.Add(albedo);
        var second = table.Add(albedo);

        Assert.Equal(first, second);
        Assert.Equal(1, table.Count);
        Assert.Equal(1, table.WriteCount);
    }

    /// <summary>
    ///     A view released once while two callers hold it keeps its slot.
    /// </summary>
    /// <remarks>
    ///     The failure this prevents is not a wasted slot. A table that deduplicated without counting
    ///     would hand the index back on the first release, and the thirty-nine materials still
    ///     holding it would sample whatever arrived there next — a wrong image, with nothing anywhere
    ///     that could report it.
    /// </remarks>
    [Fact]
    public void A_slot_survives_until_the_last_reference_goes() {
        using var table = new BindlessTable(device);

        var index = table.Add(albedo);
        table.Add(albedo);

        Assert.False(table.Remove(albedo));
        Assert.True(table.TryGetIndex(albedo, out var still));
        Assert.Equal(index, still);

        Assert.True(table.Remove(albedo));
        Assert.False(table.TryGetIndex(albedo, out _));
    }

    /// <summary>
    ///     A released index is not handed out again until the frames that could name it have retired.
    /// </summary>
    /// <remarks>
    ///     <see cref="DescriptorAllocator" />'s hazard, one level up. The index is in a material
    ///     record the GPU reads while the CPU records the next frame, so reusing it immediately points
    ///     an in-flight draw at a texture that arrived after it. Asserted by count rather than by
    ///     identity: a fresh slot is what a too-eager table would <em>not</em> give.
    /// </remarks>
    [Fact]
    public void A_released_slot_waits_out_the_frames_in_flight() {
        using var table = new BindlessTable(device);

        table.Add(albedo);
        table.Remove(albedo);

        // Two of the three frames gone by, and the slot is still not available.
        table.BeginFrame();
        table.BeginFrame();

        Assert.Equal(1u, table.Add(normal));

        table.BeginFrame();

        // The third — now the ring is back on the slot the release went into.
        Assert.Equal(0u, table.Add(missing));
        Assert.Equal(2, table.HighWaterMark);
    }

    /// <summary>A table that is never told about frames never reuses anything.</summary>
    /// <remarks>
    ///     Stated as a test because it is the failure mode of the missed call: not corruption, which
    ///     would be found, but a high-water mark that walks to <see cref="BindlessTable.Capacity" />
    ///     and a table that then refuses a texture on a machine with descriptors to spare.
    /// </remarks>
    [Fact]
    public void Without_a_frame_boundary_no_slot_comes_back() {
        using var table = new BindlessTable(device, capacity: 4);

        for (var index = 0; index < 4; index++) {
            var view = View($"Streamed{index}");
            table.Add(view);
            table.Remove(view);
        }

        Assert.Equal(0, table.Count);
        Assert.Equal(4, table.HighWaterMark);
        Assert.Throws<InvalidOperationException>(() => table.Add(albedo));
    }

    /// <summary>The high-water mark is what the free list works below, and it is not the count.</summary>
    [Fact]
    public void The_high_water_mark_does_not_fall_with_the_count() {
        using var table = new BindlessTable(device);

        table.Add(albedo);
        table.Add(normal);
        table.Remove(normal);

        Assert.Equal(1, table.Count);
        Assert.Equal(2, table.HighWaterMark);
    }

    /// <summary>A full table says so rather than overwriting a slot somebody holds.</summary>
    [Fact]
    public void A_full_table_refuses() {
        using var table = new BindlessTable(device, capacity: 2);

        table.Add(albedo);
        table.Add(normal);

        var full = Assert.Throws<InvalidOperationException>(() => table.Add(missing));
        Assert.Contains("MaxBindlessDescriptors", full.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The capacity reaches the layout, so a write past it is refused by the device.
    /// </summary>
    /// <remarks>
    ///     The half that is easy to leave out and impossible to see. A table can enforce its own
    ///     ceiling in <see cref="BindlessTable.Add" /> and still declare a layout sized at the
    ///     device's maximum — everything passes, every test agrees, and the descriptor pool behind a
    ///     thousand-slot table reserves a million descriptors. Asked of the device rather than of the
    ///     table, because the table's own answer is the one that was already right.
    /// </remarks>
    [Fact]
    public void The_capacity_is_the_layouts_and_not_only_the_tables() {
        using var table = new BindlessTable(device, capacity: 8);

        device.UpdateDescriptorSet(table.Set, [DescriptorWrite.Texture(0, albedo, 7)]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => device.UpdateDescriptorSet(table.Set, [DescriptorWrite.Texture(0, albedo, 8)])
        );
    }

    /// <summary>A table that asked for nothing gets the device's ceiling.</summary>
    [Fact]
    public void No_capacity_means_the_devices_own() {
        using var table = new BindlessTable(device);

        Assert.Equal(device.Features.MaxBindlessDescriptors, table.Capacity);

        device.UpdateDescriptorSet(
            table.Set,
            [DescriptorWrite.Texture(0, albedo, device.Features.MaxBindlessDescriptors - 1)]
        );
    }

    /// <summary>A capacity the device cannot reach is refused where the number is, not where it is used.</summary>
    [Fact]
    public void A_capacity_past_the_devices_limit_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BindlessTable(device, capacity: device.Features.MaxBindlessDescriptors + 1)
        );

    /// <summary>
    ///     A device with no descriptor indexing has no table, and says which capability is missing.
    /// </summary>
    /// <remarks>
    ///     There is deliberately no emulated path. A table faked as a bounded array of the largest
    ///     size the device allows is a different shader with a different limit, so the fork belongs in
    ///     the host — which is what an exception naming the capability makes it write.
    /// </remarks>
    [Fact]
    public void A_device_without_the_capability_has_no_table() {
        using var minimum = new NullDevice(new() { Features = GraphicsDeviceFeatures.Minimum });

        Assert.False(BindlessTable.IsSupportedBy(minimum));

        var refused = Assert.Throws<NotSupportedException>(() => new BindlessTable(minimum));
        Assert.Contains("HasBindless", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A device that claims the capability and reports no descriptors has no table either.
    /// </summary>
    /// <remarks>
    ///     The pair is the point. <c>HasBindless</c> alone is what a backend sets from an extension
    ///     string, and a table sized from a zero limit is one that refuses its first texture — so the
    ///     support question asks both and the failure lands at construction.
    /// </remarks>
    [Fact]
    public void The_capability_and_the_limit_are_one_question() {
        using var inconsistent = new NullDevice(
            new() { Features = GraphicsDeviceFeatures.Minimum with { HasBindless = true } }
        );

        Assert.False(BindlessTable.IsSupportedBy(inconsistent));
        Assert.Throws<NotSupportedException>(() => new BindlessTable(inconsistent));
    }

    /// <summary>A table holds texture views, so it is not made of buffers.</summary>
    [Fact]
    public void A_table_is_of_textures() =>
        Assert.Throws<ArgumentException>(() => new BindlessTable(device, kind: DescriptorKind.StorageBuffer));

    /// <summary>A null view has no slot, because an index that samples nothing is not a fallback.</summary>
    [Fact]
    public void A_null_view_takes_no_slot() {
        using var table = new BindlessTable(device);
        Assert.Throws<ArgumentException>(() => table.Add(TextureViewHandle.Null));
    }

    /// <summary>
    ///     A freed slot is rewritten to the fallback, where there is one.
    /// </summary>
    /// <remarks>
    ///     An index outliving the texture it named is a bug either way. The difference is whether it
    ///     samples a magenta 1×1 somebody can see or whatever the driver left in a descriptor pointing
    ///     at a destroyed image, which on most drivers is the old texture and on some is a hang.
    /// </remarks>
    [Fact]
    public void A_freed_slot_falls_back_where_one_was_given() {
        using var table = new BindlessTable(device, fallback: missing);

        table.Add(albedo);
        Assert.Equal(1, table.WriteCount);

        table.Remove(albedo);
        Assert.Equal(2, table.WriteCount);
    }

    /// <summary>Without a fallback, a freed slot costs no write at all.</summary>
    [Fact]
    public void A_freed_slot_costs_nothing_without_one() {
        using var table = new BindlessTable(device);

        table.Add(albedo);
        table.Remove(albedo);

        Assert.Equal(1, table.WriteCount);
    }

    /// <summary>A reset empties the high-water mark too, which nothing else does.</summary>
    [Fact]
    public void A_reset_takes_the_high_water_mark_with_it() {
        using var table = new BindlessTable(device);

        table.Add(albedo);
        table.Add(normal);
        table.Reset();

        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.HighWaterMark);
        Assert.Equal(0u, table.Add(missing));
    }

    /// <summary>The set and the layout go back to the device.</summary>
    [Fact]
    public void Disposing_gives_the_set_back() {
        var table = new BindlessTable(device);
        var set = table.Set;

        table.Dispose();

        Assert.Throws<ArgumentException>(() => device.UpdateDescriptorSet(set, [DescriptorWrite.Texture(0, albedo)]));
    }

    /// <summary>Disposing twice is not an error, because a table is often owned by two things.</summary>
    [Fact]
    public void Disposing_twice_is_allowed() {
        var table = new BindlessTable(device);

        table.Dispose();
        table.Dispose();
    }

    TextureViewHandle View(string name) =>
        device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: name))
        );
}
