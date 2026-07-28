// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;

namespace Vixen.Graphics.WebGPU.Browser;

/// <summary>What to create a browser WebGPU binding as.</summary>
public readonly record struct BrowserWebGpuOptions() {
    /// <summary>A CSS selector for the canvas to present to, or empty for offscreen.</summary>
    /// <remarks>
    ///     A selector rather than a <see cref="Vixen.Core.SurfaceHandle" />: a canvas has no pointer
    ///     to hand out, and <see cref="Vixen.Core.SurfaceKind.Web" /> carries an element id that
    ///     means nothing to WebAssembly's address space. Passing the selector keeps this project
    ///     independent of <c>Vixen.Platform.Web</c>, which owns the canvas and is free to name it
    ///     however it likes.
    /// </remarks>
    public string CanvasSelector { get; init; } = "";

    /// <summary>Which adapter to prefer.</summary>
    public WgpuPowerPreference PowerPreference { get; init; } = WgpuPowerPreference.HighPerformance;

    /// <summary>Where <c>vixen-webgpu.js</c> is fetched from.</summary>
    public string ModuleUrl { get; init; } = WebGpuInterop.DefaultModuleUrl;
}

/// <summary>WebGPU through <c>navigator.gpu</c>.</summary>
/// <remarks>
///     <para>
///         The other of the two surface implementations <c>docs/plan/05</c> asks for.
///         <c>Silk.NET.WebGPU</c> binds <c>webgpu.h</c>, which a browser does not expose at all — so
///         everything here goes through <c>[JSImport]</c> to <c>vixen-webgpu.js</c>, and nothing
///         above <see cref="IWebGpuBinding" /> knows the difference.
///     </para>
///     <para>
///         <b>Two things a browser cannot do, and they are reported rather than faked.</b>
///         <see cref="WaitIdle" /> returns <see langword="false" />: a tab has one thread and it is
///         the one that would have to run the completion callback, so blocking on the queue is a
///         deadlock and not a wait. <see cref="ReadBuffer" /> returns <see langword="false" /> for
///         the same reason — WebGPU's map is a promise, and there is no thread to resolve it on.
///         A frame that needs a value back on the web has to ask a frame early and collect it later.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class BrowserWebGpuBinding : IWebGpuBinding {
    readonly WebGpuPacker packer = new();
    readonly HashSet<WgpuFeatureName> features;

    bool disposed;

    BrowserWebGpuBinding(WebGpuLimits limits, HashSet<WgpuFeatureName> features, WebGpuAdapterInfo info) {
        Limits = limits;
        this.features = features;
        AdapterInfo = info;
        HasSurface = WebGpuInterop.HasSurface();

        PreferredSurfaceFormat = HasSurface
            ? (WgpuTextureFormat)(uint)WebGpuInterop.PreferredFormat()
            : WgpuTextureFormat.Undefined;
    }

    /// <inheritdoc />
    public WebGpuAdapterInfo AdapterInfo { get; }

    /// <inheritdoc />
    public WebGpuLimits Limits { get; }

    /// <inheritdoc />
    public bool HasSurface { get; }

    /// <inheritdoc />
    public WgpuTextureFormat PreferredSurfaceFormat { get; }

    /// <inheritdoc />
    public bool HasFeature(WgpuFeatureName feature) => features.Contains(feature);

    /// <summary>Loads the module, asks for an adapter and a device, and configures the canvas.</summary>
    /// <param name="options">What to create.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="PlatformNotSupportedException">This browser has no WebGPU, or refused a
    /// device.</exception>
    /// <remarks>
    ///     Asynchronous because <c>navigator.gpu.requestAdapter</c> is, and no amount of wanting
    ///     changes that: there is nothing to spin on. An application head awaits this once at boot,
    ///     the same way it awaits <c>WebAudioBackend.CreateAsync</c>.
    /// </remarks>
    public static async Task<BrowserWebGpuBinding> CreateAsync(BrowserWebGpuOptions options) {
        await WebGpuInterop.ImportAsync(options.ModuleUrl).ConfigureAwait(false);

        if (!WebGpuInterop.IsSupported()) {
            throw new PlatformNotSupportedException(
                "This browser has no navigator.gpu. WebGPU is unavailable — take the WebGL2 path, "
                + "which is what Vixen.Graphics.OpenGL's WebGL2 profile is for."
            );
        }

        var failure = await WebGpuInterop
            .InitialiseAsync(options.CanvasSelector, PreferenceName(options.PowerPreference))
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(failure)) {
            throw new PlatformNotSupportedException($"WebGPU could not start: {failure}");
        }

        return new(ReadLimits(), ReadFeatures(), new(WebGpuInterop.AdapterName(), WgpuAdapterType.Unknown, "browser"));
    }

    // ── Resources ───────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject CreateBuffer(in WgpuBufferDescriptor descriptor) =>
        Wrap(WebGpuInterop.CreateBuffer(descriptor.Size, (int)descriptor.Usage, descriptor.Label));

    /// <inheritdoc />
    /// <remarks>Layout: format, width, height, depthOrArrayLayers, mipLevelCount, sampleCount,
    /// dimension, usage — eight 32-bit integers.</remarks>
    public WebGpuObject CreateTexture(in WgpuTextureDescriptor descriptor) {
        var packed = packer.Reset()
            .Enum((uint)descriptor.Format)
            .Int(descriptor.Width)
            .Int(descriptor.Height)
            .Int(Math.Max(1, descriptor.DepthOrArrayLayers))
            .Int(Math.Max(1, descriptor.MipLevelCount))
            .Int(Math.Max(1, descriptor.SampleCount))
            .Enum((uint)descriptor.Dimension)
            .Enum((uint)descriptor.Usage);

        return Wrap(WebGpuInterop.CreateTexture(packed.Written, descriptor.Label));
    }

    /// <inheritdoc />
    /// <remarks>Layout: format, dimension, baseMipLevel, mipLevelCount, baseArrayLayer,
    /// arrayLayerCount, aspect — seven 32-bit integers.</remarks>
    public WebGpuObject CreateTextureView(WebGpuObject texture, in WgpuTextureViewDescriptor descriptor) {
        var packed = packer.Reset()
            .Enum((uint)descriptor.Format)
            .Enum((uint)descriptor.Dimension)
            .Int(descriptor.BaseMipLevel)
            .Int(Math.Max(1, descriptor.MipLevelCount))
            .Int(descriptor.BaseArrayLayer)
            .Int(Math.Max(1, descriptor.ArrayLayerCount))
            .Enum((uint)descriptor.Aspect);

        return Wrap(WebGpuInterop.CreateTextureView((int)texture.Value, packed.Written, descriptor.Label));
    }

    /// <inheritdoc />
    /// <remarks>Layout: addressU, addressV, addressW, magFilter, minFilter, mipmapFilter, compare,
    /// maxAnisotropy — eight 32-bit integers — then lodMinClamp and lodMaxClamp as 32-bit
    /// floats.</remarks>
    public WebGpuObject CreateSampler(in WgpuSamplerDescriptor descriptor) {
        var packed = packer.Reset()
            .Enum((uint)descriptor.AddressU)
            .Enum((uint)descriptor.AddressV)
            .Enum((uint)descriptor.AddressW)
            .Enum((uint)descriptor.MagFilter)
            .Enum((uint)descriptor.MinFilter)
            .Enum((uint)descriptor.MipmapFilter)
            .Enum((uint)descriptor.Compare)
            .Int(Math.Max(1, (int)descriptor.MaxAnisotropy))
            .Float(descriptor.LodMinClamp)
            .Float(descriptor.LodMaxClamp);

        return Wrap(WebGpuInterop.CreateSampler(packed.Written, descriptor.Label));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     WGSL only. A browser has no SPIR-V path at all — the specification removed it long before
    ///     shipping — so a SPIR-V module is refused here rather than handed over to fail as a syntax
    ///     error in a console nobody is reading.
    /// </remarks>
    public WebGpuObject CreateShaderModule(in WgpuShaderModuleDescriptor descriptor) {
        if (descriptor.Source == WgpuShaderSource.SpirV) {
            throw new NotSupportedException(
                $"Shader '{descriptor.Label}' is SPIR-V. A browser accepts WGSL and nothing else. Raven "
                + "cross-compiles to WGSL through SPIRV-Cross (docs/plan/07 § ADR-012); the web build "
                + "has to ship that output rather than the SPIR-V it came from."
            );
        }

        return Wrap(
            WebGpuInterop.CreateShaderModule(Encoding.UTF8.GetString(descriptor.Code), descriptor.Label)
        );
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Layout: an entry count, then ten 32-bit integers per entry — binding, visibility,
    ///     bufferType, hasDynamicOffset, samplerType, textureSampleType, textureViewDimension,
    ///     multisampled, storageAccess, storageFormat.
    /// </remarks>
    public WebGpuObject CreateBindGroupLayout(in WgpuBindGroupLayoutDescriptor descriptor) {
        packer.Reset().Int(descriptor.Entries.Length);

        foreach (var entry in descriptor.Entries) {
            packer.Int((int)entry.Binding)
                .Enum((uint)entry.Visibility)
                .Enum((uint)entry.BufferType)
                .Bool(entry.HasDynamicOffset)
                .Enum((uint)entry.SamplerType)
                .Enum((uint)entry.TextureSampleType)
                .Enum((uint)entry.TextureViewDimension)
                .Bool(entry.Multisampled)
                .Enum((uint)entry.StorageAccess)
                .Enum((uint)entry.StorageFormat);
        }

        return Wrap(WebGpuInterop.CreateBindGroupLayout(packer.Written, descriptor.Label));
    }

    /// <inheritdoc />
    /// <remarks>Layout: a count, then one handle per bind group layout.</remarks>
    public WebGpuObject CreatePipelineLayout(in WgpuPipelineLayoutDescriptor descriptor) {
        packer.Reset().Int(descriptor.BindGroupLayouts.Length);

        foreach (var group in descriptor.BindGroupLayouts) {
            packer.Object(group);
        }

        return Wrap(WebGpuInterop.CreatePipelineLayout(packer.Written, descriptor.Label));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Layout: an entry count, then per entry binding, buffer, sampler and textureView as 32-bit
    ///     integers, followed by offset and size as 64-bit floats.
    /// </remarks>
    public WebGpuObject CreateBindGroup(in WgpuBindGroupDescriptor descriptor) {
        packer.Reset().Int(descriptor.Entries.Length);

        foreach (var entry in descriptor.Entries) {
            packer.Int((int)entry.Binding)
                .Object(entry.Buffer)
                .Object(entry.Sampler)
                .Object(entry.TextureView)
                .Long(entry.Offset)
                .Long(entry.Size);
        }

        return Wrap(WebGpuInterop.CreateBindGroup((int)descriptor.Layout.Value, packer.Written, descriptor.Label));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>The one big one. Layout, in order:</para>
    ///     <list type="number">
    ///         <item><description>
    ///             twelve 32-bit integers — layout, vertexModule, fragmentModule, topology,
    ///             stripIndexFormat, frontFace, cullMode, unclippedDepth, sampleCount,
    ///             vertexBufferCount, colourTargetCount, hasDepthStencil;
    ///         </description></item>
    ///         <item><description>
    ///             per vertex buffer — arrayStride as a 64-bit float, then stepMode and an attribute
    ///             count as integers, then per attribute format and shaderLocation as integers and
    ///             offset as a 64-bit float;
    ///         </description></item>
    ///         <item><description>
    ///             per colour target — format, blendEnabled, writeMask, colour operation, source and
    ///             destination, alpha operation, source and destination: nine integers;
    ///         </description></item>
    ///         <item><description>
    ///             the depth-stencil state, when present — format, depthWriteEnabled, depthCompare,
    ///             stencilReadMask, stencilWriteMask, depthBias, then four integers for each stencil
    ///             face, then depthBiasSlopeScale and depthBiasClamp as 32-bit floats.
    ///         </description></item>
    ///     </list>
    /// </remarks>
    public WebGpuObject CreateRenderPipeline(in WgpuRenderPipelineDescriptor descriptor) {
        packer.Reset()
            .Object(descriptor.Layout)
            .Object(descriptor.VertexModule)
            .Object(descriptor.FragmentModule)
            .Enum((uint)descriptor.Topology)
            .Enum((uint)descriptor.StripIndexFormat)
            .Enum((uint)descriptor.FrontFace)
            .Enum((uint)descriptor.CullMode)
            .Bool(descriptor.UnclippedDepth)
            .Int(Math.Max(1, descriptor.SampleCount))
            .Int(descriptor.VertexBuffers.Length)
            .Int(descriptor.ColourTargets.Length)
            .Bool(descriptor.DepthStencil is not null);

        foreach (var buffer in descriptor.VertexBuffers) {
            var attributes = buffer.Attributes ?? [];

            packer.Long(buffer.ArrayStride).Enum((uint)buffer.StepMode).Int(attributes.Length);

            foreach (var attribute in attributes) {
                packer.Enum((uint)attribute.Format).Int((int)attribute.ShaderLocation).Long(attribute.Offset);
            }
        }

        foreach (var target in descriptor.ColourTargets) {
            packer.Enum((uint)target.Format)
                .Bool(target.BlendEnabled)
                .Enum((uint)target.WriteMask)
                .Enum((uint)target.Colour.Operation)
                .Enum((uint)target.Colour.SourceFactor)
                .Enum((uint)target.Colour.DestinationFactor)
                .Enum((uint)target.Alpha.Operation)
                .Enum((uint)target.Alpha.SourceFactor)
                .Enum((uint)target.Alpha.DestinationFactor);
        }

        if (descriptor.DepthStencil is { } depth) {
            packer.Enum((uint)depth.Format)
                .Bool(depth.DepthWriteEnabled)
                .Enum((uint)depth.DepthCompare)
                .Int((int)depth.StencilReadMask)
                .Int((int)depth.StencilWriteMask)
                .Int(depth.DepthBias);

            Face(depth.Front);
            Face(depth.Back);

            packer.Float(depth.DepthBiasSlopeScale).Float(depth.DepthBiasClamp);
        }

        return Wrap(
            WebGpuInterop.CreateRenderPipeline(
                packer.Written,
                descriptor.VertexEntryPoint,
                descriptor.FragmentEntryPoint,
                descriptor.Label
            )
        );

        void Face(in WgpuStencilFaceState face) =>
            packer.Enum((uint)face.Compare)
                .Enum((uint)face.FailOp)
                .Enum((uint)face.DepthFailOp)
                .Enum((uint)face.PassOp);
    }

    /// <inheritdoc />
    public WebGpuObject CreateComputePipeline(in WgpuComputePipelineDescriptor descriptor) =>
        Wrap(
            WebGpuInterop.CreateComputePipeline(
                (int)descriptor.Layout.Value,
                (int)descriptor.Module.Value,
                descriptor.EntryPoint,
                descriptor.Label
            )
        );

    /// <inheritdoc />
    /// <remarks>
    ///     The kind is ignored: JavaScript has no separate release per type, and dropping the table
    ///     entry is the whole of it. The garbage collector reclaims the WebGPU object once nothing —
    ///     including the implementation's own pending work — still refers to it.
    /// </remarks>
    public void Release(WebGpuObjectKind kind, WebGpuObject handle) {
        if (handle.IsValid) {
            WebGpuInterop.Release((int)handle.Value);
        }
    }

    // ── Queue ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void WriteBuffer(WebGpuObject buffer, long offset, ReadOnlySpan<byte> data) {
        // The marshaller needs a writable view; the data is only read on the other side. Copying
        // would be a per-write allocation on the upload path, so the span is un-consted here rather
        // than duplicated.
        var writable = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(data),
            data.Length
        );

        WebGpuInterop.WriteBuffer((int)buffer.Value, offset, writable);
    }

    /// <inheritdoc />
    /// <remarks>Always false; see the type's remarks.</remarks>
    public bool ReadBuffer(WebGpuObject buffer, long offset, Span<byte> destination) => false;

    /// <inheritdoc />
    public void Submit(ReadOnlySpan<WebGpuObject> commands) {
        foreach (var command in commands) {
            WebGpuInterop.Submit((int)command.Value);
        }
    }

    /// <inheritdoc />
    /// <remarks>Always false; see the type's remarks.</remarks>
    public bool WaitIdle() => false;

    /// <inheritdoc />
    /// <remarks>A browser has an event loop of its own and nothing to pump.</remarks>
    public void Tick() { }

    // ── Encoding ────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject CreateCommandEncoder(string label) => Wrap(WebGpuInterop.CreateCommandEncoder(label));

    /// <inheritdoc />
    public WebGpuObject FinishCommandEncoder(WebGpuObject encoder, string label) =>
        Wrap(WebGpuInterop.FinishCommandEncoder((int)encoder.Value, label));

    /// <inheritdoc />
    public void CopyBufferToBuffer(
        WebGpuObject encoder,
        WebGpuObject source,
        long sourceOffset,
        WebGpuObject destination,
        long destinationOffset,
        long size
    ) =>
        WebGpuInterop.CopyBufferToBuffer(
            (int)encoder.Value,
            (int)source.Value,
            sourceOffset,
            (int)destination.Value,
            destinationOffset,
            size
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
        packer.Reset();
        Linear(source);
        Region(destination);
        Extent(width, height, depthOrLayers);

        WebGpuInterop.CopyTexture((int)encoder.Value, 0, packer.Written);
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
        packer.Reset();
        Region(source);
        Linear(destination);
        Extent(width, height, depthOrLayers);

        WebGpuInterop.CopyTexture((int)encoder.Value, 1, packer.Written);
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
        packer.Reset();
        Region(source);
        Region(destination);
        Extent(width, height, depthOrLayers);

        WebGpuInterop.CopyTexture((int)encoder.Value, 2, packer.Written);
    }

    /// <inheritdoc />
    public void EncoderPushDebugGroup(WebGpuObject encoder, string name) =>
        WebGpuInterop.DebugGroup((int)encoder.Value, 0, name);

    /// <inheritdoc />
    public void EncoderPopDebugGroup(WebGpuObject encoder) => WebGpuInterop.DebugGroup((int)encoder.Value, 1, "");

    /// <inheritdoc />
    public void EncoderInsertDebugMarker(WebGpuObject encoder, string name) =>
        WebGpuInterop.DebugGroup((int)encoder.Value, 2, name);

    // ── Render passes ───────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    ///     Layout: a colour attachment count and a depth flag, then per colour attachment view,
    ///     resolveTarget, loadOp and storeOp as integers followed by four 64-bit floats of clear
    ///     colour; then, when present, view, depthLoadOp, depthStoreOp, stencilLoadOp,
    ///     stencilStoreOp, stencilClearValue, depthReadOnly and stencilReadOnly as integers and
    ///     depthClearValue as a 32-bit float.
    /// </remarks>
    public WebGpuObject BeginRenderPass(WebGpuObject encoder, in WgpuRenderPassDescriptor descriptor) {
        packer.Reset().Int(descriptor.ColourAttachmentCount).Bool(descriptor.DepthStencil is not null);

        for (var index = 0; index < descriptor.ColourAttachmentCount; index++) {
            var attachment = descriptor.ColourAttachments[index];

            packer.Object(attachment.View)
                .Object(attachment.ResolveTarget)
                .Enum((uint)attachment.LoadOp)
                .Enum((uint)attachment.StoreOp)
                .Double(attachment.ClearR)
                .Double(attachment.ClearG)
                .Double(attachment.ClearB)
                .Double(attachment.ClearA);
        }

        if (descriptor.DepthStencil is { } depth) {
            packer.Object(depth.View)
                .Enum((uint)depth.DepthLoadOp)
                .Enum((uint)depth.DepthStoreOp)
                .Enum((uint)depth.StencilLoadOp)
                .Enum((uint)depth.StencilStoreOp)
                .Int((int)depth.StencilClearValue)
                .Bool(depth.DepthReadOnly)
                .Bool(depth.StencilReadOnly)
                .Float(depth.DepthClearValue);
        }

        return Wrap(WebGpuInterop.BeginRenderPass((int)encoder.Value, packer.Written, descriptor.Label));
    }

    /// <inheritdoc />
    public void EndRenderPass(WebGpuObject pass) => WebGpuInterop.EndPass((int)pass.Value);

    /// <inheritdoc />
    public void RenderPassSetPipeline(WebGpuObject pass, WebGpuObject pipeline) =>
        WebGpuInterop.SetPipeline((int)pass.Value, (int)pipeline.Value);

    /// <inheritdoc />
    public void RenderPassSetBindGroup(
        WebGpuObject pass,
        uint group,
        WebGpuObject bindGroup,
        ReadOnlySpan<uint> dynamicOffsets
    ) =>
        SetBindGroup(pass, group, bindGroup, dynamicOffsets);

    /// <inheritdoc />
    public void RenderPassSetVertexBuffer(WebGpuObject pass, uint slot, WebGpuObject buffer, long offset, long size) =>
        WebGpuInterop.SetVertexBuffer((int)pass.Value, (int)slot, (int)buffer.Value, offset, size);

    /// <inheritdoc />
    public void RenderPassSetIndexBuffer(
        WebGpuObject pass,
        WebGpuObject buffer,
        WgpuIndexFormat format,
        long offset,
        long size
    ) =>
        WebGpuInterop.SetIndexBuffer((int)pass.Value, (int)buffer.Value, (int)format, offset, size);

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
        WebGpuInterop.SetViewport((int)pass.Value, x, y, width, height, minDepth, maxDepth);

    /// <inheritdoc />
    public void RenderPassSetScissorRect(WebGpuObject pass, uint x, uint y, uint width, uint height) =>
        WebGpuInterop.SetScissorRect((int)pass.Value, (int)x, (int)y, (int)width, (int)height);

    /// <inheritdoc />
    public void RenderPassSetBlendConstant(WebGpuObject pass, double r, double g, double b, double a) =>
        WebGpuInterop.SetBlendConstant((int)pass.Value, r, g, b, a);

    /// <inheritdoc />
    public void RenderPassSetStencilReference(WebGpuObject pass, uint reference) =>
        WebGpuInterop.SetStencilReference((int)pass.Value, reference);

    /// <inheritdoc />
    public void RenderPassDraw(
        WebGpuObject pass,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance
    ) =>
        WebGpuInterop.Draw((int)pass.Value, vertexCount, instanceCount, firstVertex, firstInstance);

    /// <inheritdoc />
    public void RenderPassDrawIndexed(
        WebGpuObject pass,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int baseVertex,
        uint firstInstance
    ) =>
        WebGpuInterop.DrawIndexed((int)pass.Value, indexCount, instanceCount, firstIndex, baseVertex, firstInstance);

    /// <inheritdoc />
    public void RenderPassDrawIndexedIndirect(WebGpuObject pass, WebGpuObject arguments, long offset) =>
        WebGpuInterop.DrawIndexedIndirect((int)pass.Value, (int)arguments.Value, offset);

    /// <inheritdoc />
    public void RenderPassPushDebugGroup(WebGpuObject pass, string name) =>
        WebGpuInterop.DebugGroup((int)pass.Value, 0, name);

    /// <inheritdoc />
    public void RenderPassPopDebugGroup(WebGpuObject pass) => WebGpuInterop.DebugGroup((int)pass.Value, 1, "");

    /// <inheritdoc />
    public void RenderPassInsertDebugMarker(WebGpuObject pass, string name) =>
        WebGpuInterop.DebugGroup((int)pass.Value, 2, name);

    // ── Compute passes ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject BeginComputePass(WebGpuObject encoder, string label) =>
        Wrap(WebGpuInterop.BeginComputePass((int)encoder.Value, label));

    /// <inheritdoc />
    public void EndComputePass(WebGpuObject pass) => WebGpuInterop.EndPass((int)pass.Value);

    /// <inheritdoc />
    public void ComputePassSetPipeline(WebGpuObject pass, WebGpuObject pipeline) =>
        WebGpuInterop.SetPipeline((int)pass.Value, (int)pipeline.Value);

    /// <inheritdoc />
    public void ComputePassSetBindGroup(
        WebGpuObject pass,
        uint group,
        WebGpuObject bindGroup,
        ReadOnlySpan<uint> dynamicOffsets
    ) =>
        SetBindGroup(pass, group, bindGroup, dynamicOffsets);

    /// <inheritdoc />
    public void ComputePassDispatch(WebGpuObject pass, uint groupsX, uint groupsY, uint groupsZ) =>
        WebGpuInterop.Dispatch((int)pass.Value, groupsX, groupsY, groupsZ);

    /// <inheritdoc />
    public void ComputePassDispatchIndirect(WebGpuObject pass, WebGpuObject arguments, long offset) =>
        WebGpuInterop.DispatchIndirect((int)pass.Value, (int)arguments.Value, offset);

    /// <inheritdoc />
    public void ComputePassPushDebugGroup(WebGpuObject pass, string name) =>
        WebGpuInterop.DebugGroup((int)pass.Value, 0, name);

    /// <inheritdoc />
    public void ComputePassPopDebugGroup(WebGpuObject pass) => WebGpuInterop.DebugGroup((int)pass.Value, 1, "");

    /// <inheritdoc />
    public void ComputePassInsertDebugMarker(WebGpuObject pass, string name) =>
        WebGpuInterop.DebugGroup((int)pass.Value, 2, name);

    // ── Surface ─────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void ConfigureSurface(in WgpuSurfaceConfiguration configuration) =>
        WebGpuInterop.ConfigureSurface(
            (int)configuration.Format,
            (int)configuration.Usage,
            Math.Max(1, configuration.Width),
            Math.Max(1, configuration.Height),
            (int)configuration.AlphaMode
        );

    /// <inheritdoc />
    /// <remarks>
    ///     A canvas context never reports out of date: it hands out a texture sized to the canvas
    ///     whenever asked, and a resize is something the page did rather than something the
    ///     presentation engine noticed. So the only failure is having no context at all.
    /// </remarks>
    public WgpuSurfaceStatus AcquireSurfaceTexture(out WebGpuObject texture) {
        var handle = WebGpuInterop.AcquireSurfaceTexture();
        texture = new((ulong)handle);

        return handle == 0 ? WgpuSurfaceStatus.Lost : WgpuSurfaceStatus.Success;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing at all, and that is correct rather than unfinished. A canvas is presented by the
    ///     compositor when the task that drew into it yields; there is no present call in WebGPU's
    ///     browser API, which is why <c>GPUCanvasContext</c> has none.
    /// </remarks>
    public void PresentSurface() { }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        WebGpuInterop.Shutdown();
    }

    void SetBindGroup(WebGpuObject pass, uint group, WebGpuObject bindGroup, ReadOnlySpan<uint> dynamicOffsets) {
        packer.Reset();

        foreach (var offset in dynamicOffsets) {
            packer.Int(unchecked((int)offset));
        }

        WebGpuInterop.SetBindGroup((int)pass.Value, (int)group, (int)bindGroup.Value, packer.Written);
    }

    void Region(in WgpuImageCopyTexture region) =>
        packer.Object(region.Texture)
            .Int(region.MipLevel)
            .Int(region.OriginX)
            .Int(region.OriginY)
            .Int(region.OriginZ)
            .Enum((uint)region.Aspect);

    void Linear(in WgpuImageCopyBuffer buffer) =>
        packer.Object(buffer.Buffer)
            .Int(buffer.BytesPerRow)
            .Int(Math.Max(1, buffer.RowsPerImage))
            .Long(buffer.Offset);

    void Extent(int width, int height, int depthOrLayers) =>
        packer.Int(Math.Max(1, width)).Int(Math.Max(1, height)).Int(Math.Max(1, depthOrLayers));

    static WebGpuObject Wrap(int handle) {
        if (handle == 0) {
            throw new InvalidOperationException(
                "A WebGPU object could not be created. The browser reports why on the console and "
                + "through the device's uncapturederror event; vixen-webgpu.js forwards both."
            );
        }

        return new((ulong)handle);
    }

    static string PreferenceName(WgpuPowerPreference preference) => preference switch {
        WgpuPowerPreference.LowPower => "low-power",
        WgpuPowerPreference.HighPerformance => "high-performance",
        _ => ""
    };

    /// <summary>The device's limits, in <see cref="WebGpuLimits" />'s declaration order.</summary>
    /// <remarks>
    ///     Read raw and normalised once, rather than defaulted field by field: a browser reports
    ///     zero for a limit it has not implemented, and <see cref="WebGpuLimits.OrGuaranteed" /> is
    ///     where that is turned into the specification's floor — for both surfaces, in one place.
    /// </remarks>
    static WebGpuLimits ReadLimits() {
        Span<byte> buffer = stackalloc byte[14 * sizeof(double)];
        WebGpuInterop.ReadLimits(buffer);

        return new WebGpuLimits {
            MaxTextureDimension2D = At(buffer, 0),
            MaxTextureDimension3D = At(buffer, 1),
            MaxTextureArrayLayers = At(buffer, 2),
            MaxBindGroups = At(buffer, 3),
            MaxUniformBufferBindingSize = At(buffer, 4),
            MinUniformBufferOffsetAlignment = At(buffer, 5),
            MaxVertexBuffers = At(buffer, 6),
            MaxBufferSize = At(buffer, 7),
            MaxVertexAttributes = At(buffer, 8),
            MaxColorAttachments = At(buffer, 9),
            MaxDynamicUniformBuffersPerPipelineLayout = At(buffer, 10),
            MaxComputeWorkgroupSizeX = At(buffer, 11),
            MaxComputeWorkgroupSizeY = At(buffer, 12),
            MaxComputeWorkgroupSizeZ = At(buffer, 13)
        }.OrGuaranteed();
    }

    /// <summary>One limit, as the double it crossed as.</summary>
    static int At(ReadOnlySpan<byte> buffer, int index) {
        var value = BinaryPrimitives.ReadDoubleLittleEndian(buffer[(index * sizeof(double))..]);
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    static HashSet<WgpuFeatureName> ReadFeatures() {
        var found = new HashSet<WgpuFeatureName>();

        foreach (var name in WebGpuInterop.ReadFeatures()) {
            if (Named(name) is { } feature) {
                found.Add(feature);
            }
        }

        return found;
    }

    /// <summary>A WebGPU feature name as the enum <c>webgpu.h</c> gives it a number.</summary>
    /// <remarks>
    ///     A browser names features with strings and the C header numbers them, so the two vocabularies
    ///     meet here. Only the ones <see cref="WebGpuCapabilities.Wanted" /> asks for are listed:
    ///     anything else the browser offers is something nothing above reads.
    /// </remarks>
    static WgpuFeatureName? Named(string name) => name switch {
        "depth-clip-control" => WgpuFeatureName.DepthClipControl,
        "depth32float-stencil8" => WgpuFeatureName.Depth32FloatStencil8,
        "timestamp-query" => WgpuFeatureName.TimestampQuery,
        "texture-compression-bc" => WgpuFeatureName.TextureCompressionBc,
        "texture-compression-etc2" => WgpuFeatureName.TextureCompressionEtc2,
        "texture-compression-astc" => WgpuFeatureName.TextureCompressionAstc,
        "indirect-first-instance" => WgpuFeatureName.IndirectFirstInstance,
        "shader-f16" => WgpuFeatureName.ShaderF16,
        "rg11b10ufloat-renderable" => WgpuFeatureName.Rg11B10UfloatRenderable,
        "bgra8unorm-storage" => WgpuFeatureName.Bgra8UnormStorage,
        "float32-filterable" => WgpuFeatureName.Float32Filterable,
        _ => null
    };
}
