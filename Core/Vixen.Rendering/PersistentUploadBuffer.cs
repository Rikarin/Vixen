// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Graphics;

namespace Vixen.Rendering;

/// <summary>
///     A run of unmanaged records that lives across frames, and is rewritten only where it changed.
/// </summary>
/// <typeparam name="T">The element. Blittable, because an upload is a blit.</typeparam>
/// <remarks>
///     <para>
///         The sibling of <see cref="UploadBuffer{T}" />, and the difference between them is the
///         whole reason this exists. That one is refilled from scratch every frame, which is right
///         for a skeleton's matrices or a frame's light list — data the host recomputes anyway, so
///         retaining it would buy nothing. This one is for data that is <em>mostly the same as last
///         frame's</em>: a hundred thousand object bounds, of which a frame typically moves a
///         handful. Uploading all of them costs three megabytes a frame to say that 99.99% of them
///         did not move.
///     </para>
///     <para>
///         <strong>Dirtiness is decided by comparison, not by cooperation.</strong>
///         <see cref="Set" /> compares the record it is given against the one it already holds and
///         marks the slot only when the bytes differ. That is more work than being told — a linear
///         pass over records the caller had to produce anyway — and it is what makes the tracking
///         <em>sound</em>: there is no way for a caller to change something and forget to say so,
///         which is the failure mode a dirty flag has and the one that shows up as geometry culled
///         against last week's bounds. The alternative needs every writer of the source data to
///         cooperate, and a writer that does not is silently wrong rather than slow.
///     </para>
///     <para>
///         <strong>A dirty set per frame in flight, not one.</strong> The ring is
///         <see cref="UploadBuffer{T}" />'s, and for the same reason: writing bytes the device may
///         still be reading is a race the API cannot report. But a persistent buffer cannot simply
///         rewrite this frame's region — the region is <see cref="Slots" /> frames stale, and what it
///         is missing is every change since it was last written. So a change marks the record dirty
///         in <em>every</em> slot, and each slot flushes its own set when its turn comes. One moved
///         object costs one record written per frame for <see cref="Slots" /> frames, rather than the
///         whole buffer once.
///     </para>
///     <para>
///         <strong>Runs are coalesced, because a driver call costs more than the bytes do.</strong>
///         Two dirty records with three clean ones between them are one write of five, not two
///         writes of one — see <see cref="MergeGap" />. What that trades is bytes for calls, in the
///         direction every measurement of this has ever pointed.
///     </para>
/// </remarks>
/// <param name="name">A debug name for the buffer.</param>
/// <param name="usage">What the buffer is for; storage by default, as the object scene is.</param>
public sealed class PersistentUploadBuffer<T>(string name, BufferUsage usage = BufferUsage.Storage)
    : IDisposable where T : unmanaged {
    /// <summary>
    ///     How many clean records two dirty ones may be separated by and still be written together.
    /// </summary>
    /// <remarks>
    ///     Sixteen, which for a thirty-two byte record is half a kilobyte of slack to save one call
    ///     into the driver. The number is a trade rather than a rule: zero would be the fewest bytes
    ///     and the most calls, and a scene whose changes are spatially clustered — which is what a
    ///     moving camera and a streamed world both produce — pays almost nothing for it.
    /// </remarks>
    public const int MergeGap = 16;

    /// <summary>The host's copy of what the device holds, which is what a change is compared against.</summary>
    T[] records = [];

    /// <summary>
    ///     One bitset per frame in flight: which records that slot's region is missing.
    /// </summary>
    /// <remarks>
    ///     Jagged rather than one flat array, because the two dimensions grow for different reasons —
    ///     the record count with the scene, the slot count with the device's frames in flight — and a
    ///     flat one would have to be re-laid-out whenever either did.
    /// </remarks>
    ulong[][] dirty = [];

    int count;
    int capacity;
    int slot;
    int slots = 1;
    BufferHandle buffer;
    bool disposed;

    /// <summary>The device the buffer lives on. Set before the first upload.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>The buffer, valid once something has been uploaded.</summary>
    public BufferHandle Buffer => buffer;

    /// <summary>How many records are live this frame.</summary>
    public int Count => count;

    /// <summary>How many the buffer has room for, per frame in flight.</summary>
    public int Capacity => capacity;

    /// <summary>Where this frame's region starts, in bytes. Bind the buffer at this offset.</summary>
    public long Offset => (long)slot * Stride;

    /// <summary>How many bytes one frame's region occupies, including its alignment padding.</summary>
    public long Stride { get; private set; }

    /// <summary>How many frames the ring is deep.</summary>
    public int Slots => slots;

    /// <summary>What a buffer binding's offset must be a multiple of.</summary>
    /// <remarks>
    ///     Two hundred and fifty-six, the largest <c>minStorageBufferOffsetAlignment</c> any target
    ///     reports — the same number and the same argument as <see cref="UploadBuffer{T}.Alignment" />.
    /// </remarks>
    public int Alignment { get; set; } = 256;

    /// <summary>
    ///     How many bytes the last <see cref="Upload" /> handed to the device.
    /// </summary>
    /// <remarks>
    ///     Exposed because it is the number this class exists to make small, and a number nothing
    ///     else can observe: the RHI has no counter, and a frame that quietly went back to uploading
    ///     everything is a frame that still draws correctly. It is what the incremental-upload test
    ///     asserts on, which is what makes deleting the comparison in <see cref="Set" /> a failing
    ///     test rather than a slower frame.
    /// </remarks>
    public long LastUploadBytes { get; private set; }

    /// <summary>How many separate writes the last <see cref="Upload" /> made.</summary>
    /// <remarks>The other half of the trade <see cref="MergeGap" /> makes, so a test can see both.</remarks>
    public int LastUploadRegions { get; private set; }

    /// <summary>What the host holds, for a test or an inspector.</summary>
    public ReadOnlySpan<T> Items => records.AsSpan(0, count);

    /// <summary>
    ///     Starts a frame: moves to the next region and states how many records are live.
    /// </summary>
    /// <remarks>
    ///     The region this moves on to is the one used <see cref="Slots" /> frames ago, which the
    ///     device has finished with. Unlike <see cref="UploadBuffer{T}.Begin" /> it does not forget
    ///     the contents — that is the point of the type — so what it does instead is make room, which
    ///     may invalidate every slot if the buffer had to be rebuilt.
    /// </remarks>
    /// <param name="required">How many records this frame will address.</param>
    public void Begin(int required) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(required);

        slot = slots == 0 ? 0 : (slot + 1) % slots;
        count = required;

        Reserve(required);
    }

    /// <summary>
    ///     Records one entry, marking it for upload if — and only if — it differs from what the
    ///     device already has.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Compared as bytes rather than through <see cref="IEquatable{T}" />, which the element
    ///         is not required to implement: a record here is a GPU struct with a fixed layout, so
    ///         its bytes <em>are</em> its value, and the padding a caller leaves is part of what gets
    ///         uploaded either way.
    ///     </para>
    ///     <para>
    ///         Marked in every slot, not this one. A change has to reach each region before it can be
    ///         forgotten, and each of them is written on a different frame.
    ///     </para>
    /// </remarks>
    /// <param name="index">Which record.</param>
    /// <param name="value">Its new contents.</param>
    public void Set(int index, in T value) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        if (Same(records[index], value)) {
            return;
        }

        records[index] = value;

        for (var i = 0; i < dirty.Length; i++) {
            Mark(dirty[i], index);
        }
    }

    /// <summary>Writes the regions this frame's slot is missing, and nothing else.</summary>
    public void Upload() {
        ObjectDisposedException.ThrowIf(disposed, this);

        LastUploadBytes = 0;
        LastUploadRegions = 0;

        if (Device is null || count == 0) {
            return;
        }

        EnsureBuffer();

        if (!buffer.IsValid || slot >= dirty.Length) {
            return;
        }

        var bits = dirty[slot];
        var size = Unsafe.SizeOf<T>();
        var index = 0;

        while (index < count) {
            if (!IsDirty(bits, index)) {
                index++;
                continue;
            }

            var start = index;
            var end = index + 1;

            // Extend while the next dirty record is close enough that writing the clean ones
            // between them is cheaper than a second call.
            while (end < count) {
                var next = NextDirty(bits, end, Math.Min(count, end + MergeGap + 1));

                if (next < 0) {
                    break;
                }

                end = next + 1;
            }

            var bytes = MemoryMarshal.AsBytes(records.AsSpan(start, end - start));
            Device.Write(buffer, Offset + ((long)start * size), bytes);

            LastUploadBytes += bytes.Length;
            LastUploadRegions++;

            Clear(bits, start, end);
            index = end;
        }
    }

    /// <summary>The first dirty index in <c>[from, limit)</c>, or -1.</summary>
    static int NextDirty(ulong[] bits, int from, int limit) {
        for (var i = from; i < limit; i++) {
            if (IsDirty(bits, i)) {
                return i;
            }
        }

        return -1;
    }

    static bool IsDirty(ulong[] bits, int index) {
        var word = index >> 6;
        return word < bits.Length && (bits[word] & (1UL << (index & 63))) != 0;
    }

    static void Mark(ulong[] bits, int index) {
        var word = index >> 6;

        if (word < bits.Length) {
            bits[word] |= 1UL << (index & 63);
        }
    }

    static void Clear(ulong[] bits, int start, int end) {
        for (var i = start; i < end; i++) {
            var word = i >> 6;

            if (word < bits.Length) {
                bits[word] &= ~(1UL << (i & 63));
            }
        }
    }

    /// <summary>Whether two records hold the same bytes.</summary>
    static bool Same(in T left, in T right) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in left, 1))
            .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in right, 1)));

    void Reserve(int required) {
        if (required <= records.Length) {
            return;
        }

        Array.Resize(ref records, Math.Max(required, Math.Max(records.Length * 2, 256)));
    }

    /// <summary>
    ///     Builds or rebuilds the device buffer, and the per-slot dirty sets that go with it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every slot starts entirely dirty, and that is not conservatism: a freshly created
    ///         buffer holds nothing, so <em>no</em> record in it matches the host's copy — including
    ///         the ones the host has never written, which is what makes a record coming into range
    ///         for the first time upload even when its value happens to be the type's default.
    ///     </para>
    ///     <para>
    ///         Bits beyond <see cref="Count" /> stay set until the record they name comes into range,
    ///         because <see cref="Upload" /> only walks the live prefix. That is what makes a scene
    ///         that grows correct without a second flag saying which records have ever been written.
    ///     </para>
    /// </remarks>
    void EnsureBuffer() {
        var wanted = Device is null ? slots : Math.Max(1, Device.FramesInFlight);

        if (Device is null || (records.Length <= capacity && buffer.IsValid && slots == wanted)) {
            return;
        }

        // Destroyed rather than retired, which is the same hazard UploadBuffer carries and the same
        // account of it: growing frees a buffer an unfinished frame may be reading, at the
        // high-water mark and then never again, and retiring it needs a device-level queue that does
        // not exist yet.
        if (buffer.IsValid) {
            Device.Destroy(buffer);
        }

        capacity = records.Length;
        slots = wanted;
        slot = Math.Min(slot, slots - 1);

        var bytes = (long)capacity * Unsafe.SizeOf<T>();
        var alignment = Math.Max(1, Alignment);
        Stride = (bytes + alignment - 1) / alignment * alignment;

        buffer = Device.CreateBuffer(new(Stride * slots, usage, MemoryAccess.HostUpload, name));

        dirty = new ulong[slots][];

        for (var i = 0; i < slots; i++) {
            dirty[i] = new ulong[(capacity + 63) / 64];
            Array.Fill(dirty[i], ulong.MaxValue);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (buffer.IsValid) {
            Device?.Destroy(buffer);
            buffer = default;
        }

        records = [];
        dirty = [];
        count = 0;
        capacity = 0;
    }
}
