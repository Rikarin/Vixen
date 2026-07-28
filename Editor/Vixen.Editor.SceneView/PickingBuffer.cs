// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Editor.SceneView;

/// <summary>A pick somebody asked for and has not been answered yet.</summary>
/// <param name="X">Where, in render pixels from the viewport's top-left.</param>
/// <param name="Y">Ditto.</param>
/// <param name="Additive">Whether the answer extends the selection rather than replacing it.</param>
/// <param name="Sequence">Which pick this is, so a late answer to an abandoned one is recognisable.</param>
public readonly record struct PickRequest(int X, int Y, bool Additive, long Sequence);

/// <summary>A pick that has come back.</summary>
/// <param name="Id">What was under the pointer, or zero for nothing.</param>
/// <param name="Additive">Whether the answer extends the selection.</param>
/// <param name="Sequence">Which pick this answers.</param>
public readonly record struct PickResult(uint Id, bool Additive, long Sequence) {
    /// <summary>Whether anything was hit.</summary>
    /// <remarks>
    ///     Zero is nothing, so an object's id is its index plus one. Using zero as a valid id would
    ///     make "the sky" and "the first object in the scene" the same answer, and a cleared target
    ///     reads as zeroes.
    /// </remarks>
    public bool IsHit => Id != 0;
}

/// <summary>The ring of readback buffers a picking stage copies into.</summary>
/// <remarks>
///     <para>
///         <b>Read back asynchronously, several frames later.</b> Mapping a buffer the GPU may still
///         be writing means waiting for the GPU, and doing that in the frame the click happened turns
///         a click into a stall of however long the frame had left to run. The ring is
///         <see cref="Latency" /> deep and a pick is resolved when its slot comes round again, by
///         which time the copy has certainly landed.
///     </para>
///     <para>
///         ⚠ <b>One pixel is copied, not the target.</b> A full-resolution R32 readback is sixteen
///         megabytes a frame for a 4K viewport and answers a question about one pixel. The rectangle
///         copied is the one the pointer is in — which is also why the picking pass can be skipped
///         entirely on every frame nobody clicked.
///     </para>
///     <para>
///         <b>The buffers are made once and kept.</b> They are four bytes each; allocating one per
///         click would put a device allocation on the click path, which is the one path where a
///         hitch is felt as the tool being unresponsive.
///     </para>
/// </remarks>
public sealed class PickingBuffer : IDisposable {
    readonly IGraphicsDevice device;
    readonly BufferHandle[] slots;
    readonly PickRequest?[] pending;

    long sequence;
    int frame;
    bool disposed;

    /// <summary>How many frames a pick takes to come back.</summary>
    public int Latency => slots.Length;

    /// <summary>The pick waiting to be drawn this frame, if any.</summary>
    public PickRequest? Requested { get; private set; }

    /// <summary>Makes a ring of readback buffers.</summary>
    /// <param name="device">The device.</param>
    /// <param name="latency">How many frames deep, which is how many frames in flight there are.</param>
    public PickingBuffer(IGraphicsDevice device, int latency = 3) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThan(latency, 2);

        this.device = device;
        slots = new BufferHandle[latency];
        pending = new PickRequest?[latency];

        for (var index = 0; index < latency; index++) {
            slots[index] = device.CreateBuffer(
                new BufferDescription(
                    sizeof(uint),
                    BufferUsage.CopyDestination,
                    MemoryAccess.HostReadback,
                    $"PickingReadback{index}"
                )
            );
        }
    }

    /// <summary>Asks what is under a point.</summary>
    /// <param name="x">Where, in render pixels from the viewport's top-left.</param>
    /// <param name="y">Ditto.</param>
    /// <param name="additive">Whether the answer extends the selection.</param>
    /// <returns>The sequence number the answer will carry.</returns>
    /// <remarks>
    ///     ⚠ <b>One pick per frame, and a second replaces the first.</b> Two clicks inside one frame
    ///     is a double-click, which is one selection and one action; queueing both would select the
    ///     first thing and then the second, which is what makes a double-click on a hierarchy look
    ///     like it selected the wrong object.
    /// </remarks>
    public long Request(int x, int y, bool additive = false) {
        Requested = new(x, y, additive, ++sequence);
        return sequence;
    }

    /// <summary>The buffer this frame's copy should go into, if there is a copy to make.</summary>
    /// <param name="destination">The buffer.</param>
    /// <param name="request">What was asked.</param>
    /// <returns>Whether anything was asked.</returns>
    public bool TryTakeRequest(out BufferHandle destination, out PickRequest request) {
        if (Requested is not { } asked) {
            destination = default;
            request = default;

            return false;
        }

        destination = slots[frame];
        request = asked;
        pending[frame] = asked;
        Requested = null;

        return true;
    }

    /// <summary>Moves to the next slot and answers whatever the one it lands on was asked.</summary>
    /// <param name="result">The answer.</param>
    /// <returns>Whether there was one.</returns>
    /// <remarks>
    ///     Called once a frame, after the frame has been submitted. The slot it lands on is the one
    ///     written <see cref="Latency" /> frames ago, so the copy into it has completed — which is
    ///     the whole reason the ring is as deep as the number of frames in flight.
    /// </remarks>
    public bool Advance(out PickResult result) {
        ObjectDisposedException.ThrowIf(disposed, this);

        frame = (frame + 1) % slots.Length;

        if (pending[frame] is not { } asked) {
            result = default;
            return false;
        }

        pending[frame] = null;

        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        device.Read(slots[frame], 0, bytes);

        result = new(BitConverter.ToUInt32(bytes), asked.Additive, asked.Sequence);
        return true;
    }

    /// <summary>Forgets every outstanding pick.</summary>
    /// <remarks>
    ///     What a viewport being resized or a scene being closed calls: the answers in flight are
    ///     about a picture that no longer exists, and delivering them would select whatever object
    ///     happens to have that id in the new scene.
    /// </remarks>
    public void Abandon() {
        Requested = null;
        Array.Clear(pending);
    }

    /// <summary>The size of the region copied out of the id target.</summary>
    /// <remarks>One pixel. Named rather than written as <c>new(1, 1, 1)</c> at the call site.</remarks>
    public static Int3 CopySize => new(1, 1, 1);

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var slot in slots) {
            device.Destroy(slot);
        }
    }
}
