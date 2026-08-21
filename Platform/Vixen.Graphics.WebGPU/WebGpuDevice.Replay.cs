// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Graphics.WebGPU;

public sealed partial class WebGpuDevice {
    // Grown once and reused. A render pass descriptor is built once per pass per frame, and an
    // array allocated there is an allocation in the frame loop; the same argument as the one that
    // put a count beside the array in WgpuRenderPassDescriptor.
    WgpuColourAttachment[] replayAttachments = new WgpuColourAttachment[8];
    readonly WebGpuObject[] replaySubmission = new WebGpuObject[1];
    readonly uint[] replayPushOffset = new uint[1];

    /// <summary>Turns recorded streams back into WebGPU calls and submits the result.</summary>
    /// <param name="lists">The lists, in submission order.</param>
    /// <remarks>
    ///     One encoder for the whole batch rather than one per list. A submission is the expensive
    ///     part on every API and a browser turns each into an interop crossing, so four lists
    ///     together cost roughly what one does — which is exactly why
    ///     <see cref="ICommandSubmitter.Submit(ReadOnlySpan{ICommandList})" /> takes a span in the first
    ///     place.
    /// </remarks>
    internal void Replay(ReadOnlySpan<ICommandList> lists) {
        lock (gate) {
            ThrowIfDisposed();

            foreach (var list in lists) {
                if (list is not WebGpuCommandList) {
                    throw new ArgumentException(
                        $"A {list.GetType().Name} was submitted to a WebGPU queue. A command list belongs "
                        + "to the device that began it.",
                        nameof(lists)
                    );
                }
            }

            var encoder = binding.CreateCommandEncoder(Label(lists));
            var state = new ReplayState(encoder);

            foreach (var list in lists) {
                var typed = (WebGpuCommandList)list;
                typed.MarkSubmitted();
                Replay(typed, ref state);
            }

            // A pass left open at the end of a batch is not a caller error — a compute pass is
            // opened by this replay, not by the caller — so it is closed here rather than complained
            // about.
            CloseComputePass(ref state);

            var buffer = binding.FinishCommandEncoder(encoder, "Vixen");
            replaySubmission[0] = buffer;
            binding.Submit(replaySubmission);
            binding.Release(WebGpuObjectKind.CommandBuffer, buffer);
        }
    }

    void Replay(WebGpuCommandList list, ref ReplayState state) {
        foreach (var command in list.Commands) {
            switch (command.Kind) {
                case WebGpuCommandKind.BeginRenderPass: {
                    CloseComputePass(ref state);

                    if (replayAttachments.Length < command.B) {
                        replayAttachments = new WgpuColourAttachment[command.B];
                    }

                    for (var index = 0; index < command.B; index++) {
                        replayAttachments[index] = list.ColourAttachments[command.A + index];
                    }

                    var depth = command.C >= 0 ? list.DepthAttachments[command.C] : (WgpuDepthStencilAttachment?)null;

                    state.RenderPass = binding.BeginRenderPass(
                        state.Encoder,
                        new(replayAttachments, command.B, depth, list.Labels[command.D])
                    );

                    break;
                }

                case WebGpuCommandKind.EndRenderPass:
                    binding.EndRenderPass(state.RenderPass);
                    state.RenderPass = WebGpuObject.Null;
                    break;

                case WebGpuCommandKind.SetViewport:
                    binding.RenderPassSetViewport(
                        state.RenderPass,
                        list.Floats[command.A],
                        list.Floats[command.A + 1],
                        list.Floats[command.A + 2],
                        list.Floats[command.A + 3],
                        list.Floats[command.A + 4],
                        list.Floats[command.A + 5]
                    );

                    break;

                case WebGpuCommandKind.SetScissor:
                    binding.RenderPassSetScissorRect(
                        state.RenderPass,
                        (uint)command.A,
                        (uint)command.B,
                        (uint)command.C,
                        (uint)command.D
                    );

                    break;

                case WebGpuCommandKind.SetBlendConstant:
                    binding.RenderPassSetBlendConstant(
                        state.RenderPass,
                        list.Floats[command.A],
                        list.Floats[command.A + 1],
                        list.Floats[command.A + 2],
                        list.Floats[command.A + 3]
                    );

                    break;

                case WebGpuCommandKind.SetStencilReference:
                    binding.RenderPassSetStencilReference(state.RenderPass, unchecked((uint)command.A));
                    break;

                case WebGpuCommandKind.BindPipeline:
                    state.PushConstantGroup = command.B;

                    if (command.A == 1) {
                        EnsureComputePass(ref state);
                        binding.ComputePassSetPipeline(state.ComputePass, command.Object0);
                    } else {
                        binding.RenderPassSetPipeline(state.RenderPass, command.Object0);
                    }

                    break;

                case WebGpuCommandKind.BindDescriptorSet: {
                    var offsets = Offsets(list, command.B, command.C);

                    if (state.RenderPass.IsValid) {
                        binding.RenderPassSetBindGroup(state.RenderPass, (uint)command.A, command.Object0, offsets);
                    } else {
                        EnsureComputePass(ref state);
                        binding.ComputePassSetBindGroup(state.ComputePass, (uint)command.A, command.Object0, offsets);
                    }

                    break;
                }

                case WebGpuCommandKind.PushConstants: {
                    if (state.PushConstantGroup < 0) {
                        throw new InvalidOperationException(
                            $"PushConstants was recorded in '{list.Name}' with no pipeline bound, or with one "
                            + "whose layout declares no push-constant range. WebGPU emulates them through a "
                            + "bind group whose index comes from the pipeline layout, so there is nowhere to "
                            + "put the block until a pipeline says where."
                        );
                    }

                    var ring = EnsurePushConstantRing();
                    replayPushOffset[0] = ring.Allocate(PushConstantBlock(list, command.A));

                    if (state.RenderPass.IsValid) {
                        binding.RenderPassSetBindGroup(
                            state.RenderPass,
                            (uint)state.PushConstantGroup,
                            ring.BindGroup,
                            replayPushOffset
                        );
                    } else {
                        EnsureComputePass(ref state);

                        binding.ComputePassSetBindGroup(
                            state.ComputePass,
                            (uint)state.PushConstantGroup,
                            ring.BindGroup,
                            replayPushOffset
                        );
                    }

                    break;
                }

                case WebGpuCommandKind.BindVertexBuffer:
                    binding.RenderPassSetVertexBuffer(
                        state.RenderPass,
                        (uint)command.A,
                        command.Object0,
                        command.E,
                        command.F
                    );

                    break;

                case WebGpuCommandKind.BindIndexBuffer:
                    binding.RenderPassSetIndexBuffer(
                        state.RenderPass,
                        command.Object0,
                        (WgpuIndexFormat)command.A,
                        command.E,
                        command.F
                    );

                    break;

                case WebGpuCommandKind.Draw:
                    binding.RenderPassDraw(
                        state.RenderPass,
                        (uint)command.A,
                        (uint)command.B,
                        (uint)command.C,
                        (uint)command.D
                    );

                    break;

                case WebGpuCommandKind.DrawIndexed:
                    binding.RenderPassDrawIndexed(
                        state.RenderPass,
                        (uint)command.A,
                        (uint)command.B,
                        (uint)command.C,
                        (int)command.E,
                        (uint)command.D
                    );

                    break;

                case WebGpuCommandKind.DrawIndexedIndirect:
                    binding.RenderPassDrawIndexedIndirect(state.RenderPass, command.Object0, command.E);
                    break;

                case WebGpuCommandKind.Dispatch:
                    EnsureComputePass(ref state);

                    binding.ComputePassDispatch(
                        state.ComputePass,
                        (uint)command.A,
                        (uint)command.B,
                        (uint)command.C
                    );

                    break;

                case WebGpuCommandKind.DispatchIndirect:
                    EnsureComputePass(ref state);
                    binding.ComputePassDispatchIndirect(state.ComputePass, command.Object0, command.E);
                    break;

                case WebGpuCommandKind.CopyBuffer:
                    CloseComputePass(ref state);

                    binding.CopyBufferToBuffer(
                        state.Encoder,
                        command.Object0,
                        command.E,
                        command.Object1,
                        command.F,
                        command.G
                    );

                    break;

                case WebGpuCommandKind.CopyBufferToTexture:
                    CloseComputePass(ref state);

                    binding.CopyBufferToTexture(
                        state.Encoder,
                        list.BufferCopies[command.A],
                        list.TextureCopies[command.B],
                        (int)command.E,
                        (int)command.F,
                        (int)command.G
                    );

                    break;

                case WebGpuCommandKind.CopyTextureToBuffer:
                    CloseComputePass(ref state);

                    binding.CopyTextureToBuffer(
                        state.Encoder,
                        list.TextureCopies[command.A],
                        list.BufferCopies[command.B],
                        (int)command.E,
                        (int)command.F,
                        (int)command.G
                    );

                    break;

                case WebGpuCommandKind.CopyTexture:
                    CloseComputePass(ref state);

                    binding.CopyTextureToTexture(
                        state.Encoder,
                        list.TextureCopies[command.A],
                        list.TextureCopies[command.B],
                        (int)command.E,
                        (int)command.F,
                        (int)command.G
                    );

                    break;

                case WebGpuCommandKind.PushDebugGroup: {
                    var label = list.Labels[command.A];

                    if (state.RenderPass.IsValid) {
                        binding.RenderPassPushDebugGroup(state.RenderPass, label);
                    } else if (state.ComputePass.IsValid) {
                        binding.ComputePassPushDebugGroup(state.ComputePass, label);
                    } else {
                        binding.EncoderPushDebugGroup(state.Encoder, label);
                    }

                    break;
                }

                case WebGpuCommandKind.PopDebugGroup:
                    if (state.RenderPass.IsValid) {
                        binding.RenderPassPopDebugGroup(state.RenderPass);
                    } else if (state.ComputePass.IsValid) {
                        binding.ComputePassPopDebugGroup(state.ComputePass);
                    } else {
                        binding.EncoderPopDebugGroup(state.Encoder);
                    }

                    break;

                case WebGpuCommandKind.InsertDebugMarker: {
                    var label = list.Labels[command.A];

                    if (state.RenderPass.IsValid) {
                        binding.RenderPassInsertDebugMarker(state.RenderPass, label);
                    } else if (state.ComputePass.IsValid) {
                        binding.ComputePassInsertDebugMarker(state.ComputePass, label);
                    } else {
                        binding.EncoderInsertDebugMarker(state.Encoder, label);
                    }

                    break;
                }

                default:
                    throw new InvalidOperationException($"Unrecorded command kind {command.Kind}.");
            }
        }
    }

    /// <summary>Opens a compute pass if one is not already open.</summary>
    /// <remarks>
    ///     The RHI has render passes and no compute passes: a dispatch simply happens between them.
    ///     WebGPU has no dispatch outside a compute pass, so one is opened on demand and closed the
    ///     moment anything that cannot be inside one arrives — a render pass, or a copy. That keeps
    ///     the RHI's model intact and costs one pass object per run of dispatches.
    /// </remarks>
    void EnsureComputePass(ref ReplayState state) {
        if (!state.ComputePass.IsValid) {
            state.ComputePass = binding.BeginComputePass(state.Encoder, "Compute");
        }
    }

    void CloseComputePass(ref ReplayState state) {
        if (state.ComputePass.IsValid) {
            binding.EndComputePass(state.ComputePass);
            state.ComputePass = WebGpuObject.Null;
        }
    }

    PushConstantRing EnsurePushConstantRing() =>
        pushConstants ??= new(binding, pushConstantSlotsPerFrame, FramesInFlight);

    static ReadOnlySpan<uint> Offsets(WebGpuCommandList list, int start, int count) =>
        count == 0 ? default : CollectionsMarshal.AsSpan(list.DynamicOffsets).Slice(start, count);

    static ReadOnlySpan<byte> PushConstantBlock(WebGpuCommandList list, int index) =>
        CollectionsMarshal.AsSpan(list.PushConstantBlocks)
            .Slice(index * WebGpuCapabilities.PushConstantSize, WebGpuCapabilities.PushConstantSize);

    static string Label(ReadOnlySpan<ICommandList> lists) =>
        lists.Length == 1 && lists[0] is WebGpuCommandList single && single.Name.Length > 0
            ? single.Name
            : "Vixen";

    /// <summary>What replay is in the middle of.</summary>
    /// <remarks>
    ///     A struct passed by reference rather than fields on the device, because a replay is not
    ///     device state — two threads may not replay at once and the lock says so, but a leftover
    ///     pass handle from a failed replay would outlive its encoder and be used by the next one.
    /// </remarks>
    struct ReplayState(WebGpuObject encoder) {
        public readonly WebGpuObject Encoder = encoder;

        public WebGpuObject RenderPass;

        public WebGpuObject ComputePass;

        public int PushConstantGroup = -1;
    }
}
