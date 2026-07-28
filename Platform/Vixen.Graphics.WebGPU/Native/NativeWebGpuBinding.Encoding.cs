// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
// See NativeWebGpuBinding.cs for why every clashing name is aliased rather than left to resolve.
using NativeBlendFactor = Silk.NET.WebGPU.BlendFactor;
using NativeBlendOperation = Silk.NET.WebGPU.BlendOperation;
using NativeBlendState = Silk.NET.WebGPU.BlendState;
using NativeCompareFunction = Silk.NET.WebGPU.CompareFunction;
using NativeCullMode = Silk.NET.WebGPU.CullMode;
using NativeDepthStencilState = Silk.NET.WebGPU.DepthStencilState;
using NativeFrontFace = Silk.NET.WebGPU.FrontFace;
using NativeIndexFormat = Silk.NET.WebGPU.IndexFormat;
using NativePrimitiveTopology = Silk.NET.WebGPU.PrimitiveTopology;
using NativeStencilFaceState = Silk.NET.WebGPU.StencilFaceState;
using NativeStencilOperation = Silk.NET.WebGPU.StencilOperation;
using NativeVertexBufferLayout = Silk.NET.WebGPU.VertexBufferLayout;
using NativeVertexFormat = Silk.NET.WebGPU.VertexFormat;
using NativeVertexStepMode = Silk.NET.WebGPU.VertexStepMode;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;
using WgpuTexture = Silk.NET.WebGPU.Texture;

namespace Vixen.Graphics.WebGPU.Native;

public sealed unsafe partial class NativeWebGpuBinding {
    // ── Pipelines ───────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject CreateRenderPipeline(in WgpuRenderPipelineDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);
        var vertexEntry = SilkMarshal.StringToPtr(descriptor.VertexEntryPoint, NativeStringEncoding.UTF8);
        var fragmentEntry = SilkMarshal.StringToPtr(descriptor.FragmentEntryPoint, NativeStringEncoding.UTF8);

        // Every attribute of every buffer, flattened into one block: WebGPU wants a pointer per
        // buffer into an array of attributes, and one allocation is easier to reason about than one
        // per buffer.
        var attributeCount = 0;

        foreach (var buffer in descriptor.VertexBuffers) {
            attributeCount += buffer.Attributes?.Length ?? 0;
        }

        var attributes = stackalloc VertexAttribute[Math.Max(1, attributeCount)];
        var buffers = stackalloc NativeVertexBufferLayout[Math.Max(1, descriptor.VertexBuffers.Length)];
        var targets = stackalloc ColorTargetState[Math.Max(1, descriptor.ColourTargets.Length)];
        var blends = stackalloc NativeBlendState[Math.Max(1, descriptor.ColourTargets.Length)];

        try {
            var written = 0;

            for (var index = 0; index < descriptor.VertexBuffers.Length; index++) {
                var source = descriptor.VertexBuffers[index];
                var declared = source.Attributes ?? [];
                var first = written;

                foreach (var attribute in declared) {
                    attributes[written++] = new() {
                        Format = (NativeVertexFormat)attribute.Format,
                        Offset = (ulong)attribute.Offset,
                        ShaderLocation = attribute.ShaderLocation
                    };
                }

                buffers[index] = new() {
                    ArrayStride = (ulong)source.ArrayStride,
                    StepMode = (NativeVertexStepMode)source.StepMode,
                    AttributeCount = (nuint)declared.Length,
                    Attributes = declared.Length == 0 ? null : attributes + first
                };
            }

            for (var index = 0; index < descriptor.ColourTargets.Length; index++) {
                var source = descriptor.ColourTargets[index];

                blends[index] = new() {
                    Color = new() {
                        Operation = (NativeBlendOperation)source.Colour.Operation,
                        SrcFactor = (NativeBlendFactor)source.Colour.SourceFactor,
                        DstFactor = (NativeBlendFactor)source.Colour.DestinationFactor
                    },
                    Alpha = new() {
                        Operation = (NativeBlendOperation)source.Alpha.Operation,
                        SrcFactor = (NativeBlendFactor)source.Alpha.SourceFactor,
                        DstFactor = (NativeBlendFactor)source.Alpha.DestinationFactor
                    }
                };

                targets[index] = new() {
                    Format = (TextureFormat)source.Format,

                    // A null blend means "no blending" in WebGPU, which is not the same as a blend
                    // state that happens to be one-times-source-plus-zero: passing the latter makes
                    // some implementations take a slower path for a pipeline that does not blend.
                    Blend = source.BlendEnabled ? blends + index : null,
                    WriteMask = (ColorWriteMask)source.WriteMask
                };
            }

            var fragment = new FragmentState {
                Module = (ShaderModule*)descriptor.FragmentModule.Value,
                EntryPoint = (byte*)fragmentEntry,
                TargetCount = (nuint)descriptor.ColourTargets.Length,
                Targets = descriptor.ColourTargets.Length == 0 ? null : targets
            };

            var depthClip = new PrimitiveDepthClipControl {
                Chain = new() { SType = SType.PrimitiveDepthClipControl },
                UnclippedDepth = true
            };

            var primitive = new PrimitiveState {
                NextInChain = descriptor.UnclippedDepth ? &depthClip.Chain : null,
                Topology = (NativePrimitiveTopology)descriptor.Topology,
                StripIndexFormat = (NativeIndexFormat)descriptor.StripIndexFormat,
                FrontFace = (NativeFrontFace)descriptor.FrontFace,
                CullMode = (NativeCullMode)descriptor.CullMode
            };

            NativeDepthStencilState depth = default;

            if (descriptor.DepthStencil is { } state) {
                depth = new() {
                    Format = (TextureFormat)state.Format,
                    DepthWriteEnabled = state.DepthWriteEnabled,
                    DepthCompare = (NativeCompareFunction)state.DepthCompare,
                    StencilFront = Face(state.Front),
                    StencilBack = Face(state.Back),
                    StencilReadMask = state.StencilReadMask,
                    StencilWriteMask = state.StencilWriteMask,
                    DepthBias = state.DepthBias,
                    DepthBiasSlopeScale = state.DepthBiasSlopeScale,
                    DepthBiasClamp = state.DepthBiasClamp
                };
            }

            var native = new RenderPipelineDescriptor {
                Label = (byte*)label,
                Layout = (PipelineLayout*)descriptor.Layout.Value,
                Vertex = new() {
                    Module = (ShaderModule*)descriptor.VertexModule.Value,
                    EntryPoint = (byte*)vertexEntry,
                    BufferCount = (nuint)descriptor.VertexBuffers.Length,
                    Buffers = descriptor.VertexBuffers.Length == 0 ? null : buffers
                },
                Primitive = primitive,
                DepthStencil = descriptor.DepthStencil is null ? null : &depth,
                Multisample = new() {
                    Count = (uint)Math.Max(1, descriptor.SampleCount),
                    Mask = uint.MaxValue,
                    AlphaToCoverageEnabled = false
                },

                // A depth-only pipeline has no fragment state at all, rather than one with no
                // targets: WebGPU rejects a fragment stage that writes nothing.
                Fragment = descriptor.FragmentModule.IsValid ? &fragment : null
            };

            return Wrap(api.DeviceCreateRenderPipeline(device, &native));
        } finally {
            SilkMarshal.Free(label);
            SilkMarshal.Free(vertexEntry);
            SilkMarshal.Free(fragmentEntry);
        }
    }

    /// <inheritdoc />
    public WebGpuObject CreateComputePipeline(in WgpuComputePipelineDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);
        var entry = SilkMarshal.StringToPtr(descriptor.EntryPoint, NativeStringEncoding.UTF8);

        try {
            var native = new ComputePipelineDescriptor {
                Label = (byte*)label,
                Layout = (PipelineLayout*)descriptor.Layout.Value,
                Compute = new() {
                    Module = (ShaderModule*)descriptor.Module.Value,
                    EntryPoint = (byte*)entry
                }
            };

            return Wrap(api.DeviceCreateComputePipeline(device, &native));
        } finally {
            SilkMarshal.Free(label);
            SilkMarshal.Free(entry);
        }
    }

    // ── Encoding ────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject CreateCommandEncoder(string label) {
        var text = SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);

        try {
            var native = new CommandEncoderDescriptor { Label = (byte*)text };
            return Wrap(api.DeviceCreateCommandEncoder(device, &native));
        } finally {
            SilkMarshal.Free(text);
        }
    }

    /// <inheritdoc />
    public WebGpuObject FinishCommandEncoder(WebGpuObject encoder, string label) {
        var text = SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);

        try {
            var native = new CommandBufferDescriptor { Label = (byte*)text };
            var buffer = api.CommandEncoderFinish((CommandEncoder*)encoder.Value, &native);
            api.CommandEncoderRelease((CommandEncoder*)encoder.Value);

            return Wrap(buffer);
        } finally {
            SilkMarshal.Free(text);
        }
    }

    /// <inheritdoc />
    public void CopyBufferToBuffer(
        WebGpuObject encoder,
        WebGpuObject source,
        long sourceOffset,
        WebGpuObject destination,
        long destinationOffset,
        long size
    ) =>
        api.CommandEncoderCopyBufferToBuffer(
            (CommandEncoder*)encoder.Value,
            (WgpuBuffer*)source.Value,
            (ulong)sourceOffset,
            (WgpuBuffer*)destination.Value,
            (ulong)destinationOffset,
            (ulong)size
        );

    /// <inheritdoc />
    public void CopyBufferToTexture(
        WebGpuObject encoder,
        in WgpuImageCopyBuffer source,
        in WgpuImageCopyTexture destination,
        int width,
        int height,
        int depthOrLayers
    ) {
        var from = Linear(source);
        var to = Region(destination);
        var extent = Extent(width, height, depthOrLayers);

        api.CommandEncoderCopyBufferToTexture((CommandEncoder*)encoder.Value, &from, &to, &extent);
    }

    /// <inheritdoc />
    public void CopyTextureToBuffer(
        WebGpuObject encoder,
        in WgpuImageCopyTexture source,
        in WgpuImageCopyBuffer destination,
        int width,
        int height,
        int depthOrLayers
    ) {
        var from = Region(source);
        var to = Linear(destination);
        var extent = Extent(width, height, depthOrLayers);

        api.CommandEncoderCopyTextureToBuffer((CommandEncoder*)encoder.Value, &from, &to, &extent);
    }

    /// <inheritdoc />
    public void CopyTextureToTexture(
        WebGpuObject encoder,
        in WgpuImageCopyTexture source,
        in WgpuImageCopyTexture destination,
        int width,
        int height,
        int depthOrLayers
    ) {
        var from = Region(source);
        var to = Region(destination);
        var extent = Extent(width, height, depthOrLayers);

        api.CommandEncoderCopyTextureToTexture((CommandEncoder*)encoder.Value, &from, &to, &extent);
    }

    /// <inheritdoc />
    public void EncoderPushDebugGroup(WebGpuObject encoder, string name) =>
        api.CommandEncoderPushDebugGroup((CommandEncoder*)encoder.Value, name);

    /// <inheritdoc />
    public void EncoderPopDebugGroup(WebGpuObject encoder) =>
        api.CommandEncoderPopDebugGroup((CommandEncoder*)encoder.Value);

    /// <inheritdoc />
    public void EncoderInsertDebugMarker(WebGpuObject encoder, string name) =>
        api.CommandEncoderInsertDebugMarker((CommandEncoder*)encoder.Value, name);

    // ── Render passes ───────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject BeginRenderPass(WebGpuObject encoder, in WgpuRenderPassDescriptor descriptor) {
        var label = SilkMarshal.StringToPtr(descriptor.Label, NativeStringEncoding.UTF8);
        var count = Math.Max(1, descriptor.ColourAttachmentCount);

        // Two layouts, because the binding and the implementation disagree about one field. See
        // WgpuColourAttachment below: this is the whole of the accommodation, and it is here rather
        // than spread through the backend because it is the only struct the RHI reaches that differs.
        var wide = stackalloc RenderPassColorAttachment[count];
        var compact = stackalloc WgpuColourAttachment[count];

        try {
            for (var index = 0; index < descriptor.ColourAttachmentCount; index++) {
                var source = descriptor.ColourAttachments[index];

                if (WebGpuLoader.IsWgpuNative) {
                    compact[index] = new() {
                        View = (TextureView*)source.View.Value,
                        ResolveTarget = (TextureView*)source.ResolveTarget.Value,
                        LoadOp = (LoadOp)source.LoadOp,
                        StoreOp = (StoreOp)source.StoreOp,
                        ClearValue = new(source.ClearR, source.ClearG, source.ClearB, source.ClearA)
                    };

                    continue;
                }

                wide[index] = new() {
                    View = (TextureView*)source.View.Value,
                    ResolveTarget = (TextureView*)source.ResolveTarget.Value,
                    LoadOp = (LoadOp)source.LoadOp,
                    StoreOp = (StoreOp)source.StoreOp,
                    ClearValue = new(source.ClearR, source.ClearG, source.ClearB, source.ClearA),

                    // Only meaningful for a 3D attachment, and WebGPU rejects a non-zero value on
                    // anything else. The RHI has no way to render into a slice of a volume, so it is
                    // always the first one.
                    DepthSlice = WholeTexture
                };
            }

            var colours = WebGpuLoader.IsWgpuNative ? (RenderPassColorAttachment*)compact : wide;

            RenderPassDepthStencilAttachment depth = default;

            if (descriptor.DepthStencil is { } attachment) {
                depth = new() {
                    View = (TextureView*)attachment.View.Value,
                    DepthLoadOp = (LoadOp)attachment.DepthLoadOp,
                    DepthStoreOp = (StoreOp)attachment.DepthStoreOp,
                    DepthClearValue = attachment.DepthClearValue,
                    DepthReadOnly = attachment.DepthReadOnly,
                    StencilLoadOp = (LoadOp)attachment.StencilLoadOp,
                    StencilStoreOp = (StoreOp)attachment.StencilStoreOp,
                    StencilClearValue = attachment.StencilClearValue,
                    StencilReadOnly = attachment.StencilReadOnly
                };

                // A read-only aspect may not carry a load or store operation, and WebGPU rejects the
                // combination rather than ignoring it.
                if (attachment.DepthReadOnly) {
                    depth.DepthLoadOp = LoadOp.Undefined;
                    depth.DepthStoreOp = StoreOp.Undefined;
                    depth.StencilLoadOp = LoadOp.Undefined;
                    depth.StencilStoreOp = StoreOp.Undefined;
                }
            }

            var native = new RenderPassDescriptor {
                Label = (byte*)label,
                ColorAttachmentCount = (nuint)descriptor.ColourAttachmentCount,
                ColorAttachments = descriptor.ColourAttachmentCount == 0 ? null : colours,
                DepthStencilAttachment = descriptor.DepthStencil is null ? null : &depth
            };

            return Wrap(api.CommandEncoderBeginRenderPass((CommandEncoder*)encoder.Value, &native));
        } finally {
            SilkMarshal.Free(label);
        }
    }

    /// <inheritdoc />
    public void EndRenderPass(WebGpuObject pass) {
        api.RenderPassEncoderEnd((RenderPassEncoder*)pass.Value);
        api.RenderPassEncoderRelease((RenderPassEncoder*)pass.Value);
    }

    /// <inheritdoc />
    public void RenderPassSetPipeline(WebGpuObject pass, WebGpuObject pipeline) =>
        api.RenderPassEncoderSetPipeline((RenderPassEncoder*)pass.Value, (RenderPipeline*)pipeline.Value);

    /// <inheritdoc />
    public void RenderPassSetBindGroup(
        WebGpuObject pass,
        uint group,
        WebGpuObject bindGroup,
        ReadOnlySpan<uint> dynamicOffsets
    ) {
        fixed (uint* offsets = dynamicOffsets) {
            api.RenderPassEncoderSetBindGroup(
                (RenderPassEncoder*)pass.Value,
                group,
                (BindGroup*)bindGroup.Value,
                (nuint)dynamicOffsets.Length,
                offsets
            );
        }
    }

    /// <inheritdoc />
    public void RenderPassSetVertexBuffer(WebGpuObject pass, uint slot, WebGpuObject buffer, long offset, long size) =>
        api.RenderPassEncoderSetVertexBuffer(
            (RenderPassEncoder*)pass.Value,
            slot,
            (WgpuBuffer*)buffer.Value,
            (ulong)offset,
            (ulong)size
        );

    /// <inheritdoc />
    public void RenderPassSetIndexBuffer(
        WebGpuObject pass,
        WebGpuObject buffer,
        WgpuIndexFormat format,
        long offset,
        long size
    ) =>
        api.RenderPassEncoderSetIndexBuffer(
            (RenderPassEncoder*)pass.Value,
            (WgpuBuffer*)buffer.Value,
            (NativeIndexFormat)format,
            (ulong)offset,
            (ulong)size
        );

    /// <inheritdoc />
    public void RenderPassSetViewport(
        WebGpuObject pass,
        float x,
        float y,
        float width,
        float height,
        float minDepth,
        float maxDepth
    ) =>
        api.RenderPassEncoderSetViewport((RenderPassEncoder*)pass.Value, x, y, width, height, minDepth, maxDepth);

    /// <inheritdoc />
    public void RenderPassSetScissorRect(WebGpuObject pass, uint x, uint y, uint width, uint height) =>
        api.RenderPassEncoderSetScissorRect((RenderPassEncoder*)pass.Value, x, y, width, height);

    /// <inheritdoc />
    public void RenderPassSetBlendConstant(WebGpuObject pass, double r, double g, double b, double a) {
        var colour = new Color(r, g, b, a);
        api.RenderPassEncoderSetBlendConstant((RenderPassEncoder*)pass.Value, &colour);
    }

    /// <inheritdoc />
    public void RenderPassSetStencilReference(WebGpuObject pass, uint reference) =>
        api.RenderPassEncoderSetStencilReference((RenderPassEncoder*)pass.Value, reference);

    /// <inheritdoc />
    public void RenderPassDraw(
        WebGpuObject pass,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance
    ) =>
        api.RenderPassEncoderDraw(
            (RenderPassEncoder*)pass.Value,
            vertexCount,
            instanceCount,
            firstVertex,
            firstInstance
        );

    /// <inheritdoc />
    public void RenderPassDrawIndexed(
        WebGpuObject pass,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int baseVertex,
        uint firstInstance
    ) =>
        api.RenderPassEncoderDrawIndexed(
            (RenderPassEncoder*)pass.Value,
            indexCount,
            instanceCount,
            firstIndex,
            baseVertex,
            firstInstance
        );

    /// <inheritdoc />
    public void RenderPassDrawIndexedIndirect(WebGpuObject pass, WebGpuObject arguments, long offset) =>
        api.RenderPassEncoderDrawIndexedIndirect(
            (RenderPassEncoder*)pass.Value,
            (WgpuBuffer*)arguments.Value,
            (ulong)offset
        );

    /// <inheritdoc />
    public void RenderPassPushDebugGroup(WebGpuObject pass, string name) =>
        api.RenderPassEncoderPushDebugGroup((RenderPassEncoder*)pass.Value, name);

    /// <inheritdoc />
    public void RenderPassPopDebugGroup(WebGpuObject pass) =>
        api.RenderPassEncoderPopDebugGroup((RenderPassEncoder*)pass.Value);

    /// <inheritdoc />
    public void RenderPassInsertDebugMarker(WebGpuObject pass, string name) =>
        api.RenderPassEncoderInsertDebugMarker((RenderPassEncoder*)pass.Value, name);

    // ── Compute passes ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject BeginComputePass(WebGpuObject encoder, string label) {
        var text = SilkMarshal.StringToPtr(label, NativeStringEncoding.UTF8);

        try {
            var native = new ComputePassDescriptor { Label = (byte*)text };
            return Wrap(api.CommandEncoderBeginComputePass((CommandEncoder*)encoder.Value, &native));
        } finally {
            SilkMarshal.Free(text);
        }
    }

    /// <inheritdoc />
    public void EndComputePass(WebGpuObject pass) {
        api.ComputePassEncoderEnd((ComputePassEncoder*)pass.Value);
        api.ComputePassEncoderRelease((ComputePassEncoder*)pass.Value);
    }

    /// <inheritdoc />
    public void ComputePassSetPipeline(WebGpuObject pass, WebGpuObject pipeline) =>
        api.ComputePassEncoderSetPipeline((ComputePassEncoder*)pass.Value, (ComputePipeline*)pipeline.Value);

    /// <inheritdoc />
    public void ComputePassSetBindGroup(
        WebGpuObject pass,
        uint group,
        WebGpuObject bindGroup,
        ReadOnlySpan<uint> dynamicOffsets
    ) {
        fixed (uint* offsets = dynamicOffsets) {
            api.ComputePassEncoderSetBindGroup(
                (ComputePassEncoder*)pass.Value,
                group,
                (BindGroup*)bindGroup.Value,
                (nuint)dynamicOffsets.Length,
                offsets
            );
        }
    }

    /// <inheritdoc />
    public void ComputePassDispatch(WebGpuObject pass, uint groupsX, uint groupsY, uint groupsZ) =>
        api.ComputePassEncoderDispatchWorkgroups((ComputePassEncoder*)pass.Value, groupsX, groupsY, groupsZ);

    /// <inheritdoc />
    public void ComputePassDispatchIndirect(WebGpuObject pass, WebGpuObject arguments, long offset) =>
        api.ComputePassEncoderDispatchWorkgroupsIndirect(
            (ComputePassEncoder*)pass.Value,
            (WgpuBuffer*)arguments.Value,
            (ulong)offset
        );

    /// <inheritdoc />
    public void ComputePassPushDebugGroup(WebGpuObject pass, string name) =>
        api.ComputePassEncoderPushDebugGroup((ComputePassEncoder*)pass.Value, name);

    /// <inheritdoc />
    public void ComputePassPopDebugGroup(WebGpuObject pass) =>
        api.ComputePassEncoderPopDebugGroup((ComputePassEncoder*)pass.Value);

    /// <inheritdoc />
    public void ComputePassInsertDebugMarker(WebGpuObject pass, string name) =>
        api.ComputePassEncoderInsertDebugMarker((ComputePassEncoder*)pass.Value, name);

    /// <summary>The whole of a texture, where WebGPU wants a slice index it does not use.</summary>
    const uint WholeTexture = 0;

    /// <summary>
    ///     A colour attachment as wgpu-native 0.19 lays it out — without <c>depthSlice</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one place the binding and the pinned implementation disagree.</b>
    ///         <c>Silk.NET.WebGPU</c> 2.23.0's <c>RenderPassColorAttachment</c> carries a
    ///         <c>depthSlice</c> between <c>view</c> and <c>resolveTarget</c>; wgpu-native did not
    ///         add that field until v22.1.0.1, which is also the release that removed three entry
    ///         points Silk still declares. There is no wgpu-native release that agrees with this
    ///         binding on both, and <see cref="WebGpuLoader" /> refuses the ones that fail the
    ///         second test — so a loaded wgpu-native is a 0.19-era one and wants this shape.
    ///     </para>
    ///     <para>
    ///         Passing Silk's struct to it puts <c>resolveTarget</c>'s low half where
    ///         <c>loadOp</c> belongs, and wgpu panics with <c>invalid load op for render pass color
    ///         attachment: 0</c> — which is what it did, and which is worth writing down because the
    ///         message names neither the struct nor the version.
    ///     </para>
    ///     <para>
    ///         Dawn of the same vintage has the field, so it takes Silk's struct unchanged. Nothing
    ///         else in <c>webgpu.h</c> differs between the two that the RHI reaches: the other two
    ///         changed structs are <c>WGPUDeviceDescriptor</c>, which only gained a trailing field,
    ///         and <c>WGPUSurfaceCapabilities</c>, which nothing here reads.
    ///     </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    struct WgpuColourAttachment {
        public ChainedStruct* NextInChain;

        public TextureView* View;

        public TextureView* ResolveTarget;

        public LoadOp LoadOp;

        public StoreOp StoreOp;

        public Color ClearValue;
    }

    static NativeStencilFaceState Face(in WgpuStencilFaceState face) => new() {
        Compare = (NativeCompareFunction)face.Compare,
        FailOp = (NativeStencilOperation)face.FailOp,
        DepthFailOp = (NativeStencilOperation)face.DepthFailOp,
        PassOp = (NativeStencilOperation)face.PassOp
    };

    static Extent3D Extent(int width, int height, int depthOrLayers) => new(
        (uint)Math.Max(1, width),
        (uint)Math.Max(1, height),
        (uint)Math.Max(1, depthOrLayers)
    );

    static ImageCopyTexture Region(in WgpuImageCopyTexture region) => new() {
        Texture = (WgpuTexture*)region.Texture.Value,
        MipLevel = (uint)region.MipLevel,
        Origin = new((uint)region.OriginX, (uint)region.OriginY, (uint)region.OriginZ),
        Aspect = (TextureAspect)region.Aspect
    };

    static ImageCopyBuffer Linear(in WgpuImageCopyBuffer buffer) => new() {
        Buffer = (WgpuBuffer*)buffer.Buffer.Value,
        Layout = new() {
            Offset = (ulong)buffer.Offset,
            BytesPerRow = (uint)buffer.BytesPerRow,
            RowsPerImage = (uint)Math.Max(1, buffer.RowsPerImage)
        }
    };

}
