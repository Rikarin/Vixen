// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

/// <summary>What one recorded command is.</summary>
public enum WebGpuCommandKind : byte {
    /// <summary>Begins a render pass.</summary>
    BeginRenderPass = 0,

    /// <summary>Ends the current render pass.</summary>
    EndRenderPass = 1,

    /// <summary>Sets the viewport.</summary>
    SetViewport = 2,

    /// <summary>Sets the scissor rectangle.</summary>
    SetScissor = 3,

    /// <summary>Sets the blend constant.</summary>
    SetBlendConstant = 4,

    /// <summary>Sets the stencil reference value.</summary>
    SetStencilReference = 5,

    /// <summary>Binds a pipeline.</summary>
    BindPipeline = 6,

    /// <summary>Binds a bind group.</summary>
    BindDescriptorSet = 7,

    /// <summary>Writes the emulated push-constant block.</summary>
    PushConstants = 8,

    /// <summary>Binds a vertex buffer.</summary>
    BindVertexBuffer = 9,

    /// <summary>Binds the index buffer.</summary>
    BindIndexBuffer = 10,

    /// <summary>Draws without indices.</summary>
    Draw = 11,

    /// <summary>Draws with indices.</summary>
    DrawIndexed = 12,

    /// <summary>Draws with arguments the GPU wrote.</summary>
    DrawIndexedIndirect = 13,

    /// <summary>Runs a compute shader.</summary>
    Dispatch = 14,

    /// <summary>Runs a compute shader with a group count the GPU wrote.</summary>
    DispatchIndirect = 15,

    /// <summary>Copies between buffers.</summary>
    CopyBuffer = 16,

    /// <summary>Copies from a buffer into a texture.</summary>
    CopyBufferToTexture = 17,

    /// <summary>Copies from a texture into a buffer.</summary>
    CopyTextureToBuffer = 18,

    /// <summary>Copies between textures.</summary>
    CopyTexture = 19,

    /// <summary>Opens a named group in a capture.</summary>
    PushDebugGroup = 20,

    /// <summary>Closes the innermost debug group.</summary>
    PopDebugGroup = 21,

    /// <summary>Marks a point in a capture.</summary>
    InsertDebugMarker = 22
}

/// <summary>One recorded command, as flat as it can be made.</summary>
/// <remarks>
///     <para>
///         <b>Why a deferred stream at all, when WebGPU has command encoders?</b> Because an encoder
///         belongs to one thread and there is only one thread in a browser tab, and the RHI promises
///         that lists record on any thread — one per thread per frame. So a list records into
///         managed memory and the queue replays it at submit, which is exactly what the GL backend
///         does and for exactly the same reason (<c>docs/plan/05</c>).
///     </para>
///     <para>
///         It buys two other things. The replay is shared between the native and browser surfaces,
///         so neither writes it. And the stream is a flat array of blittable values with its
///         variable-length parts in side buffers — so a browser surface that later wants to hand the
///         whole frame across the interop boundary in one call has something to hand across.
///     </para>
///     <para>
///         Fields are named by position rather than by meaning because the meaning differs per kind;
///         <see cref="WebGpuCommandList" /> is the only writer and <c>WebGpuQueue</c> the only
///         reader, and each command's layout is stated where it is recorded.
///     </para>
/// </remarks>
/// <param name="Kind">Which command it is.</param>
/// <param name="A">The first small integer operand.</param>
/// <param name="B">The second.</param>
/// <param name="C">The third.</param>
/// <param name="D">The fourth.</param>
/// <param name="E">The first wide operand — an offset or a size in bytes.</param>
/// <param name="F">The second.</param>
/// <param name="G">The third.</param>
/// <param name="Object0">The first WebGPU object it names.</param>
/// <param name="Object1">The second.</param>
public readonly record struct WebGpuCommand(
    WebGpuCommandKind Kind,
    int A = 0,
    int B = 0,
    int C = 0,
    int D = 0,
    long E = 0,
    long F = 0,
    long G = 0,
    WebGpuObject Object0 = default,
    WebGpuObject Object1 = default
);
