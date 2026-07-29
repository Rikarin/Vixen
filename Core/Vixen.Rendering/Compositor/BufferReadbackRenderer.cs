// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     A buffer the frame produced, back on the host.
/// </summary>
/// <remarks>
///     <para>
///         The other half of the seam <see cref="BufferUploadRenderer" /> opens, and the one five
///         other things were waiting on: a numeric gate that compares a shader against its C# port
///         needs the shader's answer, an auto-exposure chain needs the histogram's, and a particle
///         system that reaps on the device needs to know how many survived. None of them can get it
///         from a device-local buffer — the host cannot map one — so the value comes out through a
///         copy into host-readable memory, which is a pass, which is an edge the graph has to know
///         about.
///     </para>
///     <para>
///         <strong>Declaring the read is what makes the answer mean anything.</strong> A copy recorded
///         without it runs wherever it was written and reads whatever the buffer held before the
///         dispatch — which is zeroes on a fresh allocation and last frame's contents on a recycled
///         one. Both look like a plausible result. The declared read is what puts the barrier between
///         the producer and the copy, and it is exactly the thing a hand-written readback gets wrong.
///     </para>
///     <para>
///         <strong>The pass has a side effect, because its whole point is outside the graph.</strong>
///         Nothing in the frame reads what this writes, so culling would remove it — see
///         <see cref="RenderGraphPassBuilder.SideEffect" />.
///     </para>
///     <para>
///         <strong>When the bytes are valid is <see cref="Latency" />'s question, and it is the only
///         hard one here.</strong> There is no implicit wait anywhere in this — see
///         <see cref="IGraphicsDevice.Read" /> for why an RHI that inserted one would be hiding a stall
///         the caller cannot see.
///     </para>
/// </remarks>
public sealed class BufferReadbackRenderer : SceneRenderer, IDisposable {
    byte[] bytes = [];
    BufferHandle readback;
    IGraphicsDevice? device;
    long stride;
    long frames;
    int slots = 1;

    /// <summary>The name of the buffer to read.</summary>
    /// <remarks>
    ///     It has to have been declared with <see cref="BufferUsage.CopySource" />, which the build
    ///     says rather than the driver.
    /// </remarks>
    public required string Buffer { get; init; }

    /// <summary>Where in that buffer to start reading.</summary>
    public long Offset { get; set; }

    /// <summary>How many bytes, or zero for the rest of the buffer from <see cref="Offset" />.</summary>
    public long Size { get; set; }

    /// <summary>
    ///     How many frames sit between the copy and the read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Zero is the stall, and choosing to pay it is a decision.</strong>
    ///         <see cref="Fetch" /> then reads the region this frame's copy went into, which is only
    ///         meaningful once the caller has submitted the frame and waited for it — submit,
    ///         <see cref="IGraphicsDevice.WaitIdle" />, fetch. That is what a test wants, and what a
    ///         one-off query wants, and it is a full pipeline bubble.
    ///     </para>
    ///     <para>
    ///         <strong>Anything at or above <see cref="IGraphicsDevice.FramesInFlight" /> is free.</strong>
    ///         The region read is from a frame the host's own loop has already waited on before
    ///         reusing its resources, so there is nothing left to wait for — and the build fetches it
    ///         itself, so a host that only wants the values writes no frame-loop code at all. What it
    ///         costs instead is staleness: an exposure or a survivor count that is two frames old,
    ///         which is invisible, against a stall that is not.
    ///     </para>
    ///     <para>
    ///         Between one and that is neither. The region may belong to a frame still running, and
    ///         this cannot know — what a loop waits on is the loop's business — so it does not refuse,
    ///         but there is nothing a value in that range buys that the full depth does not.
    ///     </para>
    /// </remarks>
    public int Latency { get; set; }

    /// <summary>What came back, valid once <see cref="ReadCount" /> is above zero.</summary>
    public ReadOnlySpan<byte> Data => bytes.AsSpan(0, (int)Length);

    /// <summary>How many bytes each read produces.</summary>
    /// <remarks>Zero until the first build has resolved <see cref="Size" /> against the buffer.</remarks>
    public long Length { get; private set; }

    /// <summary>How many times bytes have come back.</summary>
    public int ReadCount { get; private set; }

    /// <summary>How many frames have been copied.</summary>
    public int CopyCount { get; private set; }

    /// <summary>What to do with the bytes, run by every <see cref="Fetch" /> that produces some.</summary>
    /// <remarks>
    ///     The seam an auto-exposure chain or a survivor count hangs off: with <see cref="Latency" />
    ///     set, the build fetches and this runs, and nothing in the host's frame loop has to know a
    ///     readback exists.
    /// </remarks>
    public Action<BufferReadbackRenderer>? OnRead { get; init; }

    /// <summary>What came back, as records.</summary>
    /// <typeparam name="T">The element. Blittable, because a readback is a blit.</typeparam>
    public ReadOnlySpan<T> As<T>() where T : unmanaged => MemoryMarshal.Cast<byte, T>(Data);

    /// <summary>
    ///     Reads the region copied <see cref="Latency" /> frames before the most recent one.
    /// </summary>
    /// <returns>Whether there was a region that far back to read.</returns>
    /// <remarks>
    ///     The caller is responsible for having waited on the frame that filled it — see
    ///     <see cref="Latency" /> for which frame that is. Reading one that is still running produces
    ///     bytes rather than an error, which is why the two cases are a documented choice rather than
    ///     something this could check.
    /// </remarks>
    public bool Fetch() {
        var index = frames - 1 - Latency;

        if (device is null || !readback.IsValid || Length <= 0 || index < 0) {
            return false;
        }

        device.Read(readback, (int)(index % slots) * stride, bytes.AsSpan(0, (int)Length));
        ReadCount++;
        OnRead?.Invoke(this);

        return true;
    }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Device is not { } owner) {
            return;
        }

        var source = frame.Buffer(ToString(), Buffer);
        var description = frame.DescribeBuffer(ToString(), Buffer);

        if ((description.Usage & BufferUsage.CopySource) == 0) {
            throw new CompositorBindingException(
                ToString(),
                "buffer",
                Buffer,
                "was not declared as a copy source, so nothing can be read out of it. Add CopySource "
                + "to its usage — without it the copy is a validation error on a debug driver and "
                + "silently nothing on a release one"
            );
        }

        var size = Size > 0 ? Size : description.Size - Offset;

        if (Offset < 0 || size <= 0 || Offset + size > description.Size) {
            throw new CompositorBindingException(
                ToString(),
                "buffer",
                Buffer,
                $"is {description.Size} bytes, and {size} bytes at offset {Offset} is not a range "
                + "inside it"
            );
        }

        if (!Ensure(owner, size)) {
            return;
        }

        var destinationOffset = (int)(frames % slots) * stride;
        var sourceOffset = Offset;
        var destination = readback;
        frames++;
        CopyCount++;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Transfer;
                pass.Reads(source, ResourceState.CopySource);

                // Nothing in the frame reads what this produces — the host does, next frame or after
                // a wait — so as far as culling can see the pass is dead. Saying so is what a readback
                // costs, and it is the honest way round: a graph that guessed would keep every pass.
                pass.SideEffect();

                pass.Execute(
                    context => context.CommandList.CopyBuffer(
                        context.Buffer(source),
                        sourceOffset,
                        destination,
                        destinationOffset,
                        size
                    )
                );
            }
        );

        // After the copy is declared, so "the most recent frame" in Fetch means the one just recorded
        // — which makes a Latency of one the previous frame from either side of the call.
        if (Latency > 0) {
            Fetch();
        }
    }

    /// <summary>
    ///     The readback buffer, one region per frame the ring is deep.
    /// </summary>
    /// <remarks>
    ///     Deep enough for both things at once: <see cref="IGraphicsDevice.FramesInFlight" />, so a
    ///     copy never lands in a region an unfinished frame is still filling, and
    ///     <see cref="Latency" /> plus one, so the region being read is not the region being written.
    /// </remarks>
    bool Ensure(IGraphicsDevice owner, long size) {
        var wanted = Math.Max(Latency + 1, Math.Max(1, owner.FramesInFlight));

        // Two hundred and fifty-six, for the reason UploadBuffer aligns to it: the largest offset
        // alignment any target reports, and a handful of regions' padding costs nothing.
        var aligned = (size + 255) / 256 * 256;

        // Against the size and not the stride: two sizes an alignment apart round to the same stride,
        // and taking that as "nothing changed" would leave Length describing the previous one — a
        // readback that came back short with the buffer looking correct.
        if (ReferenceEquals(device, owner) && readback.IsValid && Length == size && slots == wanted) {
            return true;
        }

        Release();

        device = owner;
        stride = aligned;
        slots = wanted;
        Length = size;
        frames = 0;

        if (bytes.Length < size) {
            bytes = new byte[size];
        }

        readback = owner.CreateBuffer(
            new(stride * slots, BufferUsage.CopyDestination, MemoryAccess.HostReadback, $"{this}.Readback")
        );

        return readback.IsValid;
    }

    void Release() {
        if (readback.IsValid) {
            device?.Destroy(readback);
            readback = default;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        Release();
        device = null;
        Length = 0;
        frames = 0;
    }
}
