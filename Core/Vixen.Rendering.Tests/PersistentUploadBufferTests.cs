// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Xunit;

namespace Tests;

/// <summary>
///     The buffer that is rewritten only where it changed.
/// </summary>
/// <remarks>
///     <para>
///         Its consumer's tests — <see cref="GpuVisibilityGroupTests" /> — check the claim that
///         matters to a frame: a hundred thousand objects, one of which moves, and one object's
///         worth of bytes. What is checked here is the machinery underneath it, and in particular
///         the two states a comparison alone would get wrong: a record that has never been written
///         to a region, and a region that is several frames behind because the ring moved.
///     </para>
///     <para>
///         Both of those are invisible in a picture. A record that was skipped because its value
///         happened to equal the host's zeroed copy reads as undefined memory on the device, and a
///         change that reached one region of the ring and not the others is a scene that flickers
///         between two states at the frame rate — which looks like a race in something else.
///     </para>
/// </remarks>
public class PersistentUploadBufferTests : IDisposable {
    readonly NullDevice device = new(new() { FramesInFlight = 3 });

    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A record with a shape, so a partial write would be visible as a wrong size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Record {
        public int Value;
        public int Padding;
    }

    static readonly int Size = Marshal.SizeOf<Record>();

    PersistentUploadBuffer<Record> Buffer() => new("Test") { Device = device };

    /// <summary>Fills every region of the ring, so what follows is the steady state.</summary>
    static void Settle(PersistentUploadBuffer<Record> buffer, int count) {
        for (var frame = 0; frame <= buffer.Slots + 1; frame++) {
            buffer.Begin(count);

            for (var i = 0; i < count; i++) {
                buffer.Set(i, new() { Value = i });
            }

            buffer.Upload();
        }
    }

    /// <summary>The first frame writes everything, because the buffer holds nothing yet.</summary>
    [Fact]
    public void The_first_frame_writes_the_whole_region() {
        using var buffer = Buffer();

        buffer.Begin(16);

        for (var i = 0; i < 16; i++) {
            buffer.Set(i, new() { Value = i });
        }

        buffer.Upload();

        Assert.Equal(16 * Size, buffer.LastUploadBytes);
        Assert.Equal(1, buffer.LastUploadRegions);
    }

    /// <summary>And once every region has it, writing the same values writes nothing.</summary>
    [Fact]
    public void An_unchanged_frame_writes_nothing() {
        using var buffer = Buffer();

        Settle(buffer, 16);

        buffer.Begin(16);

        for (var i = 0; i < 16; i++) {
            buffer.Set(i, new() { Value = i });
        }

        buffer.Upload();

        Assert.Equal(0, buffer.LastUploadBytes);
        Assert.Equal(0, buffer.LastUploadRegions);
    }

    /// <summary>
    ///     One change reaches every region of the ring, once each, and then stops.
    /// </summary>
    /// <remarks>
    ///     The property the per-slot dirty sets exist for. A single set would have this change
    ///     written to whichever region happened to be current and the other two left holding the old
    ///     value — which is a scene that alternates between two states with the ring's period, and
    ///     nothing anywhere to say why.
    /// </remarks>
    [Fact]
    public void A_change_reaches_every_region_of_the_ring_exactly_once() {
        using var buffer = Buffer();

        Settle(buffer, 16);

        var written = 0;

        for (var frame = 0; frame < buffer.Slots; frame++) {
            buffer.Begin(16);

            for (var i = 0; i < 16; i++) {
                buffer.Set(i, new() { Value = i == 4 ? 99 : i });
            }

            buffer.Upload();

            Assert.Equal(Size, buffer.LastUploadBytes);
            written++;
        }

        Assert.Equal(device.FramesInFlight, written);

        // And the frame after that is settled again.
        buffer.Begin(16);

        for (var i = 0; i < 16; i++) {
            buffer.Set(i, new() { Value = i == 4 ? 99 : i });
        }

        buffer.Upload();

        Assert.Equal(0, buffer.LastUploadBytes);
    }

    /// <summary>
    ///     A record that comes into range for the first time is written even when its value is the
    ///     type's default.
    /// </summary>
    /// <remarks>
    ///     The case a comparison alone gets wrong, and the reason a region starts entirely dirty
    ///     rather than clean. The host's copy of a record it has never set is zeroed; the device's is
    ///     undefined. Comparing those two finds no difference and skips a write that was the only
    ///     thing standing between the shader and garbage.
    /// </remarks>
    [Fact]
    public void A_record_that_is_new_to_the_region_is_written_even_when_it_is_zero() {
        using var buffer = Buffer();

        Settle(buffer, 16);

        buffer.Begin(17);

        for (var i = 0; i < 16; i++) {
            buffer.Set(i, new() { Value = i });
        }

        // Default-valued, which is exactly what the host's untouched copy already held.
        buffer.Set(16, default);
        buffer.Upload();

        Assert.Equal(Size, buffer.LastUploadBytes);
    }

    /// <summary>Nearby changes coalesce into one write; distant ones do not.</summary>
    [Fact]
    public void Runs_are_coalesced_up_to_the_merge_gap() {
        using var buffer = Buffer();

        Settle(buffer, 256);

        buffer.Begin(256);

        // Exactly MergeGap clean records between them, which is the most that still merges.
        for (var i = 0; i < 256; i++) {
            var changed = i is 10 or 10 + PersistentUploadBuffer<Record>.MergeGap + 1;
            buffer.Set(i, new() { Value = changed ? -1 : i });
        }

        buffer.Upload();

        Assert.Equal(1, buffer.LastUploadRegions);
        Assert.Equal((PersistentUploadBuffer<Record>.MergeGap + 2) * Size, buffer.LastUploadBytes);

        Settle(buffer, 256);
        buffer.Begin(256);

        for (var i = 0; i < 256; i++) {
            var changed = i is 10 or 10 + PersistentUploadBuffer<Record>.MergeGap + 2;
            buffer.Set(i, new() { Value = changed ? -2 : i });
        }

        buffer.Upload();

        // One further apart, and the merge stops — which is what says it has a limit.
        Assert.Equal(2, buffer.LastUploadRegions);
        Assert.Equal(2 * Size, buffer.LastUploadBytes);
    }

    /// <summary>
    ///     Growing past the buffer's capacity rebuilds it, and everything is written again.
    /// </summary>
    /// <remarks>
    ///     Not an optimization that was missed: the new buffer is different memory holding nothing,
    ///     so no record in it matches the host's copy. Stated as a test because "the upload count
    ///     jumped" is otherwise indistinguishable from the tracking having broken.
    /// </remarks>
    [Fact]
    public void Growing_past_capacity_writes_the_whole_region_again() {
        using var buffer = Buffer();

        Settle(buffer, 16);

        var grown = buffer.Capacity + 1;

        buffer.Begin(grown);

        for (var i = 0; i < grown; i++) {
            buffer.Set(i, new() { Value = i });
        }

        buffer.Upload();

        Assert.Equal((long)grown * Size, buffer.LastUploadBytes);
    }

    /// <summary>
    ///     A shrunken frame binds fewer records and leaves the rest alone, and growing back writes
    ///     nothing it does not have to.
    /// </summary>
    /// <remarks>
    ///     The host's copy and the device's region stay in step whatever the live count does,
    ///     because the copy is only changed by a write that also reaches the device. So a scene that
    ///     shrinks and grows back to the same values is settled, not re-uploaded.
    /// </remarks>
    [Fact]
    public void Shrinking_and_growing_back_does_not_re_upload() {
        using var buffer = Buffer();

        Settle(buffer, 16);

        buffer.Begin(8);

        for (var i = 0; i < 8; i++) {
            buffer.Set(i, new() { Value = i });
        }

        buffer.Upload();
        Assert.Equal(0, buffer.LastUploadBytes);

        buffer.Begin(16);

        for (var i = 0; i < 16; i++) {
            buffer.Set(i, new() { Value = i });
        }

        buffer.Upload();
        Assert.Equal(0, buffer.LastUploadBytes);
    }

    /// <summary>Each region is its own bytes, so the offset moves with the ring.</summary>
    [Fact]
    public void Each_frame_binds_its_own_region() {
        using var buffer = Buffer();

        Settle(buffer, 16);

        var offsets = new HashSet<long>();

        for (var frame = 0; frame < buffer.Slots; frame++) {
            buffer.Begin(16);
            buffer.Upload();
            offsets.Add(buffer.Offset);
        }

        Assert.Equal(buffer.Slots, offsets.Count);
        Assert.All(offsets, offset => Assert.Equal(0, offset % buffer.Alignment));
    }

    /// <summary>Without a device there is nothing to write to, and nothing throws saying so.</summary>
    [Fact]
    public void A_buffer_with_no_device_uploads_nothing() {
        using var buffer = new PersistentUploadBuffer<Record>("Test");

        buffer.Begin(4);
        buffer.Set(0, new() { Value = 1 });
        buffer.Upload();

        Assert.Equal(0, buffer.LastUploadBytes);
        Assert.False(buffer.Buffer.IsValid);
    }

    [Fact]
    public void Setting_outside_the_live_count_is_refused() {
        using var buffer = Buffer();

        buffer.Begin(4);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Set(4, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Set(-1, default));
    }
}
