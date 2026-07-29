// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Host bytes into a buffer the frame declared.
/// </summary>
/// <remarks>
///     <para>
///         The half of a compute pass nobody could author. A dispatch could declare what it read and
///         what it wrote, and everything it read had to have been filled by <em>another dispatch</em>
///         or by a host that imported a buffer it filled itself — because a graph buffer has no handle
///         until the graph compiles, and a device-local one cannot be written by the host at any point.
///         A histogram that starts at zero, a table of coefficients, a list of emitters the CPU
///         decided on: each is a value the frame needs and no pass produces.
///     </para>
///     <para>
///         <strong>Staged, and the staging is a ring.</strong> The bytes go into a host-upload buffer
///         and reach the destination through a copy the graph records, which is the only route to
///         device-local memory. One region per frame in flight, for the reason
///         <see cref="UploadBuffer{T}" /> has one: writing the same host-visible bytes every frame is
///         a memcpy into memory an unfinished frame may still be copying out of, and the symptom is a
///         buffer that is briefly a blend of two frames.
///     </para>
///     <para>
///         <strong>What it declares is a write, and that is the whole point.</strong> A node that
///         copied into the buffer without saying so would run wherever it was recorded and be read by
///         a dispatch the driver was free to start first. Declaring the write is what orders this
///         ahead of every pass that reads the buffer and puts the barrier between them — the same edge
///         <see cref="ComputeRenderer" /> exists for, from the other end.
///     </para>
/// </remarks>
public sealed class BufferUploadRenderer : SceneRenderer, IDisposable {
    byte[] pending = [];
    int length;
    UploadBuffer<byte>? staging;

    /// <summary>The name of the buffer to fill.</summary>
    /// <remarks>
    ///     Declared or imported, either way — but it has to have been declared with
    ///     <see cref="BufferUsage.CopyDestination" />, which the build says rather than the driver.
    /// </remarks>
    public required string Buffer { get; init; }

    /// <summary>Where in that buffer the bytes land.</summary>
    public long Offset { get; set; }

    /// <summary>
    ///     What refreshes the bytes, run at the start of every build.
    /// </summary>
    /// <remarks>
    ///     For the values that are recomputed per frame — a camera's exposure target, a count of what
    ///     the CPU emitted — where a host calling <see cref="Set{T}(in T)" /> from its own update would be
    ///     one more thing to keep in step with the compositor's order. A node whose bytes never change
    ///     sets them once and leaves this null.
    /// </remarks>
    public Action<BufferUploadRenderer>? OnUpload { get; init; }

    /// <summary>What will be uploaded, for a test or an inspector.</summary>
    public ReadOnlySpan<byte> Data => pending.AsSpan(0, length);

    /// <summary>How many bytes are staged.</summary>
    public int Length => length;

    /// <summary>How many times the bytes have actually gone to the device.</summary>
    public int UploadCount { get; private set; }

    /// <summary>Stages raw bytes, replacing whatever was there.</summary>
    public void Set(ReadOnlySpan<byte> bytes) {
        bytes.CopyTo(Reserve(bytes.Length));
    }

    /// <summary>Stages a run of records, replacing whatever was there.</summary>
    /// <typeparam name="T">The element. Blittable, because an upload is a blit.</typeparam>
    public void Set<T>(ReadOnlySpan<T> items) where T : unmanaged {
        Set(MemoryMarshal.AsBytes(items));
    }

    /// <summary>Stages one record, replacing whatever was there.</summary>
    /// <typeparam name="T">The record. Blittable, because an upload is a blit.</typeparam>
    public void Set<T>(in T item) where T : unmanaged {
        MemoryMarshal.Write(Reserve(Unsafe.SizeOf<T>()), in item);
    }

    /// <summary>
    ///     Room for a given number of bytes, to be filled in place.
    /// </summary>
    /// <param name="bytes">How many.</param>
    /// <remarks>
    ///     For a caller that builds its bytes rather than having them: writing into this is one copy
    ///     where <see cref="Set(ReadOnlySpan{byte})" /> from a scratch array is two. The contents are
    ///     whatever the last frame left, so a caller that fills part of it is uploading the rest of a
    ///     previous frame — which is the caller's business, and is what a partial update wants.
    /// </remarks>
    public Span<byte> Reserve(int bytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        if (bytes > pending.Length) {
            Array.Resize(ref pending, Math.Max(bytes, Math.Max(pending.Length * 2, 256)));
        }

        length = bytes;
        return pending.AsSpan(0, bytes);
    }

    /// <summary>Nothing to stage means no pass, which is what a node with no data should cost.</summary>
    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        OnUpload?.Invoke(this);

        if (frame.Device is not { } device || length == 0) {
            return;
        }

        var target = frame.Buffer(ToString(), Buffer);
        var description = frame.DescribeBuffer(ToString(), Buffer);

        if ((description.Usage & BufferUsage.CopyDestination) == 0) {
            throw new CompositorBindingException(
                ToString(),
                "buffer",
                Buffer,
                "was not declared as a copy destination, so nothing can be uploaded into it. Add "
                + "CopyDestination to its usage — without it the copy is a validation error on a debug "
                + "driver and silently nothing on a release one"
            );
        }

        if (Offset < 0 || Offset + length > description.Size) {
            throw new CompositorBindingException(
                ToString(),
                "buffer",
                Buffer,
                $"is {description.Size} bytes, and {length} bytes at offset {Offset} runs off the end "
                + "of it"
            );
        }

        // Named after the node rather than after the buffer, because a capture full of "upload" is a
        // capture nobody can read and two nodes may well fill the same buffer.
        staging ??= new($"{this}.Staging", BufferUsage.CopySource);
        staging.Device = device;

        // Written here rather than in the pass body, and this is the ordinary reason: a host-visible
        // write inside a command list is a memcpy that happens when the list is *recorded*, not where
        // it sits in the stream, so it would race whatever the earlier half of the list is doing.
        staging.Begin();
        staging.Add(pending.AsSpan(0, length));
        staging.Upload();

        var source = staging.Buffer;
        var sourceOffset = staging.Offset;
        var destinationOffset = Offset;
        var size = (long)length;
        UploadCount++;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                pass.Kind = PassKind.Transfer;
                pass.Writes(target, ResourceState.CopyDestination);

                pass.Execute(
                    context => context.CommandList.CopyBuffer(
                        source,
                        sourceOffset,
                        context.Buffer(target),
                        destinationOffset,
                        size
                    )
                );
            }
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        staging?.Dispose();
        staging = null;
    }
}
