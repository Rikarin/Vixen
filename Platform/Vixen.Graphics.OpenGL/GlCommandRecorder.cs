// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.OpenGL;

/// <summary>What one recorded command is.</summary>
enum GlCommandKind : byte {
    /// <summary>Bind the pass's framebuffer, set the draw buffers, and do the load actions.</summary>
    BeginRenderPass,

    /// <summary>Do the store actions.</summary>
    EndRenderPass,

    /// <summary>Set the viewport and depth range.</summary>
    SetViewport,

    /// <summary>Set the scissor rectangle.</summary>
    SetScissor,

    /// <summary>Set the blend constant.</summary>
    SetBlendConstant,

    /// <summary>Set the stencil reference.</summary>
    SetStencilReference,

    /// <summary>Use the program and apply the state block.</summary>
    BindPipeline,

    /// <summary>Remember a set for the next draw to resolve.</summary>
    BindDescriptorSet,

    /// <summary>Upload push constants.</summary>
    PushConstants,

    /// <summary>Remember a vertex buffer for the next draw to attach.</summary>
    BindVertexBuffer,

    /// <summary>Bind the index buffer.</summary>
    BindIndexBuffer,

    /// <summary>A non-indexed draw.</summary>
    Draw,

    /// <summary>An indexed draw.</summary>
    DrawIndexed,

    /// <summary>An indexed draw with GPU-written arguments.</summary>
    DrawIndexedIndirect,

    /// <summary>A compute dispatch.</summary>
    Dispatch,

    /// <summary>A compute dispatch with a GPU-written group count.</summary>
    DispatchIndirect,

    /// <summary>A barrier group — mostly nothing, occasionally a memory barrier.</summary>
    Barrier,

    /// <summary>A buffer-to-buffer copy.</summary>
    CopyBuffer,

    /// <summary>A buffer-to-texture copy.</summary>
    CopyBufferToTexture,

    /// <summary>A texture-to-buffer copy.</summary>
    CopyTextureToBuffer,

    /// <summary>A texture-to-texture copy.</summary>
    CopyTexture,

    /// <summary>Open a debug group.</summary>
    PushDebugGroup,

    /// <summary>Close the innermost debug group.</summary>
    PopDebugGroup,

    /// <summary>Insert a debug marker.</summary>
    InsertDebugMarker
}

/// <summary>One recorded command.</summary>
/// <remarks>
///     <para>
///         A flat struct with generic slots rather than a class per command. The whole point of this
///         buffer is that recording is cheap enough to do on a worker thread that would otherwise be
///         idle, and a command hierarchy would allocate per call and put the GC in the middle of the
///         render loop.
///     </para>
///     <para>
///         What each slot means depends on the kind, and the only place that mapping is written down
///         is the pair of <c>GlCommandRecorder.Record*</c> and <c>GlDevice.Replay</c> — which is why
///         they are the only two things allowed to touch the fields.
///     </para>
/// </remarks>
struct GlCommand {
    /// <summary>What the command is.</summary>
    public GlCommandKind Kind;

    /// <summary>Integer arguments.</summary>
    public int Int0, Int1, Int2, Int3;

    /// <summary>Byte offsets and sizes.</summary>
    public long Long0, Long1, Long2;

    /// <summary>Float arguments — a viewport, a clear colour, a blend constant.</summary>
    public float Float0, Float1, Float2, Float3, Float4, Float5;

    /// <summary>An unsigned argument — a stencil reference, a dispatch dimension.</summary>
    public uint Uint0;

    /// <summary>Where this command's variable-length data starts in its arena.</summary>
    public int PayloadIndex;

    /// <summary>How much of it there is.</summary>
    public int PayloadCount;

    /// <summary>A buffer argument.</summary>
    public BufferHandle Buffer0, Buffer1;

    /// <summary>A texture argument.</summary>
    public TextureHandle Texture0, Texture1;

    /// <summary>A pipeline argument.</summary>
    public PipelineHandle Pipeline;

    /// <summary>A descriptor-set argument.</summary>
    public DescriptorSetHandle Descriptors;
}

/// <summary>One attachment as a render pass recorded it.</summary>
readonly record struct GlAttachment(
    TextureViewHandle View,
    LoadAction Load,
    StoreAction Store,
    Color4 ClearColour,
    float ClearDepth,
    byte ClearStencil,
    bool IsDepth,
    bool IsReadOnly
);

/// <summary>A command list's recorded work, replayed on the GL thread at submit.</summary>
/// <remarks>
///     <para>
///         <b>The concession <c>docs/plan/05</c> declares up front.</b> A GL context is current on
///         one thread and its state is that thread's; there is no equivalent of recording four
///         command buffers on four cores. The RHI's contract says a command list may be recorded on
///         any thread, and that contract is worth more than the cost of keeping it — so recording
///         writes into managed memory here and the calls happen on the GL thread when the list is
///         submitted.
///     </para>
///     <para>
///         The cost is one struct write and, for the few commands with variable-length data, a copy
///         into an arena. The benefit is that <c>Vixen.Rendering</c> does not have a GL-shaped branch
///         anywhere in it, which is the thing that would actually be expensive.
///     </para>
///     <para>
///         Arenas rather than an array per command: a frame records thousands of these, and a
///         four-byte <c>uint[]</c> of dynamic offsets allocated per bind is a thousand allocations a
///         frame for sixteen bytes each.
///     </para>
/// </remarks>
sealed class GlCommandRecorder {
    readonly List<GlCommand> commands = [];
    readonly List<byte> bytes = [];
    readonly List<uint> uints = [];
    readonly List<GlAttachment> attachments = [];
    readonly List<TextureBarrier> textureBarriers = [];
    readonly List<BufferBarrier> bufferBarriers = [];
    readonly List<string> names = [];

    /// <summary>The recorded commands, in order.</summary>
    public IReadOnlyList<GlCommand> Commands => commands;

    /// <summary>How many commands have been recorded.</summary>
    public int Count => commands.Count;

    /// <summary>Throws away everything, keeping the arenas.</summary>
    /// <remarks>
    ///     Capacity is deliberately kept. A pooled list is reused every frame and a renderer's frame
    ///     is roughly the same size as the last one, so after a few frames no recording allocates at
    ///     all.
    /// </remarks>
    public void Reset() {
        commands.Clear();
        bytes.Clear();
        uints.Clear();
        attachments.Clear();
        textureBarriers.Clear();
        bufferBarriers.Clear();
        names.Clear();
    }

    /// <summary>Records a command with no variable-length data.</summary>
    public void Add(in GlCommand command) => commands.Add(command);

    /// <summary>Records a render pass's attachments and returns where they went.</summary>
    public (int Index, int Count) AddAttachments(ReadOnlySpan<GlAttachment> values) {
        var index = attachments.Count;

        foreach (var value in values) {
            attachments.Add(value);
        }

        return (index, attachments.Count - index);
    }

    /// <summary>Copies bytes into the arena and returns where they went.</summary>
    public (int Index, int Count) AddBytes(ReadOnlySpan<byte> values) {
        var index = bytes.Count;
        bytes.AddRange(values);
        return (index, values.Length);
    }

    /// <summary>Copies unsigned integers into the arena and returns where they went.</summary>
    public (int Index, int Count) AddUInts(ReadOnlySpan<uint> values) {
        var index = uints.Count;

        foreach (var value in values) {
            uints.Add(value);
        }

        return (index, values.Length);
    }

    /// <summary>Copies a barrier group's textures into the arena.</summary>
    public (int Index, int Count) AddTextureBarriers(ReadOnlySpan<TextureBarrier> values) {
        var index = textureBarriers.Count;

        foreach (var value in values) {
            textureBarriers.Add(value);
        }

        return (index, values.Length);
    }

    /// <summary>Copies a barrier group's buffers into the arena.</summary>
    public (int Index, int Count) AddBufferBarriers(ReadOnlySpan<BufferBarrier> values) {
        var index = bufferBarriers.Count;

        foreach (var value in values) {
            bufferBarriers.Add(value);
        }

        return (index, values.Length);
    }

    /// <summary>Keeps a name and returns its index.</summary>
    public int AddName(string name) {
        names.Add(name);
        return names.Count - 1;
    }

    /// <summary>Reads back a run of attachments.</summary>
    public ReadOnlySpan<GlAttachment> Attachments(int index, int count) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(attachments).Slice(index, count);

    /// <summary>Reads back a run of bytes.</summary>
    public ReadOnlySpan<byte> Bytes(int index, int count) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes).Slice(index, count);

    /// <summary>Reads back a run of unsigned integers.</summary>
    public ReadOnlySpan<uint> UInts(int index, int count) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(uints).Slice(index, count);

    /// <summary>Reads back a run of texture barriers.</summary>
    public ReadOnlySpan<TextureBarrier> TextureBarriers(int index, int count) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(textureBarriers).Slice(index, count);

    /// <summary>Reads back a run of buffer barriers.</summary>
    public ReadOnlySpan<BufferBarrier> BufferBarriers(int index, int count) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bufferBarriers).Slice(index, count);

    /// <summary>Reads back a name.</summary>
    public string Name(int index) => names[index];
}
