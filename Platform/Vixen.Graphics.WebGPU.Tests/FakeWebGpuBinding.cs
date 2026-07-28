// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Graphics.WebGPU.Tests;

/// <summary>One call the backend made to a WebGPU implementation.</summary>
/// <param name="Name">Which call it was.</param>
/// <param name="Values">Its numeric arguments, in declaration order.</param>
/// <param name="Text">Its label or name, where it had one.</param>
public readonly record struct WebGpuCall(string Name, long[] Values, string Text = "") {
    /// <inheritdoc />
    public override string ToString() =>
        Values.Length == 0 && Text.Length == 0
            ? Name
            : $"{Name}({string.Join(", ", Values)}{(Text.Length > 0 ? $" '{Text}'" : "")})";
}

/// <summary>A WebGPU implementation that records what it was asked to do.</summary>
/// <remarks>
///     <para>
///         <b>This is what makes the WebGPU backend testable without WebGPU.</b> Everything above
///         <see cref="IWebGpuBinding" /> — translation, validation, handle lifetime, deferred
///         destruction, push-constant emulation, command replay — is the same code on a desktop and
///         in a browser, and all of it runs against this on a CI machine with no GPU, no Dawn and no
///         browser. It is the same argument that makes <c>Vixen.Graphics.Null</c> the most
///         thoroughly exercised backend in the engine, one layer lower down.
///     </para>
///     <para>
///         Handles are a counter rather than pointers, and the calls are kept in order, so a test
///         asserts on the sequence a real implementation would have seen.
///     </para>
/// </remarks>
public sealed class FakeWebGpuBinding : IWebGpuBinding {
    readonly List<WebGpuCall> calls = [];
    readonly HashSet<WgpuFeatureName> features;
    readonly HashSet<ulong> live = [];

    ulong next = 1;

    /// <summary>Creates one.</summary>
    /// <param name="limits">What it should report.</param>
    /// <param name="features">What it should claim to have.</param>
    /// <param name="hasSurface">Whether there is a surface to present to.</param>
    public FakeWebGpuBinding(
        WebGpuLimits? limits = null,
        IEnumerable<WgpuFeatureName>? features = null,
        bool hasSurface = false
    ) {
        Limits = limits ?? WebGpuLimits.Guaranteed;
        this.features = [.. features ?? []];
        HasSurface = hasSurface;
        PreferredSurfaceFormat = hasSurface ? WgpuTextureFormat.Bgra8Unorm : WgpuTextureFormat.Undefined;
    }

    /// <summary>Every call the backend made, in order.</summary>
    public IReadOnlyList<WebGpuCall> Calls => calls;

    /// <summary>Objects created and not yet released.</summary>
    public int LiveObjects => live.Count;

    /// <summary>The bytes of the last <see cref="WriteBuffer" />.</summary>
    public byte[] LastWrite { get; private set; } = [];

    /// <summary>What the next <see cref="AcquireSurfaceTexture" /> should report.</summary>
    public WgpuSurfaceStatus NextSurfaceStatus { get; set; } = WgpuSurfaceStatus.Success;

    /// <summary>Whether <see cref="WaitIdle" /> should claim it waited.</summary>
    public bool CanWait { get; set; } = true;

    /// <summary>The last WGSL or SPIR-V handed to <see cref="CreateShaderModule" />.</summary>
    public WgpuShaderSource LastShaderSource { get; private set; }

    /// <inheritdoc />
    public WebGpuAdapterInfo AdapterInfo { get; init; } = new("Fake", WgpuAdapterType.DiscreteGpu, "test");

    /// <inheritdoc />
    public WebGpuLimits Limits { get; }

    /// <inheritdoc />
    public bool HasSurface { get; }

    /// <inheritdoc />
    public WgpuTextureFormat PreferredSurfaceFormat { get; }

    /// <inheritdoc />
    public bool HasFeature(WgpuFeatureName feature) => features.Contains(feature);

    /// <summary>Every call of one name.</summary>
    /// <param name="name">The call's name.</param>
    public IReadOnlyList<WebGpuCall> OfName(string name) => [.. calls.Where(call => call.Name == name)];

    /// <summary>The names of every call, in order — what an assertion on a sequence reads.</summary>
    public string[] Names() => [.. calls.Select(call => call.Name)];

    /// <summary>Forgets everything recorded so far.</summary>
    public void Clear() => calls.Clear();

    // ── Resources ───────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject CreateBuffer(in WgpuBufferDescriptor descriptor) =>
        Create("CreateBuffer", [descriptor.Size, (long)descriptor.Usage], descriptor.Label);

    /// <inheritdoc />
    public WebGpuObject CreateTexture(in WgpuTextureDescriptor descriptor) =>
        Create(
            "CreateTexture",
            [
                (long)descriptor.Format,
                descriptor.Width,
                descriptor.Height,
                descriptor.DepthOrArrayLayers,
                descriptor.MipLevelCount,
                descriptor.SampleCount,
                (long)descriptor.Dimension,
                (long)descriptor.Usage
            ],
            descriptor.Label
        );

    /// <inheritdoc />
    public WebGpuObject CreateTextureView(WebGpuObject texture, in WgpuTextureViewDescriptor descriptor) =>
        Create(
            "CreateTextureView",
            [
                (long)texture.Value,
                (long)descriptor.Format,
                (long)descriptor.Dimension,
                descriptor.BaseMipLevel,
                descriptor.MipLevelCount,
                descriptor.BaseArrayLayer,
                descriptor.ArrayLayerCount,
                (long)descriptor.Aspect
            ],
            descriptor.Label
        );

    /// <inheritdoc />
    public WebGpuObject CreateSampler(in WgpuSamplerDescriptor descriptor) =>
        Create(
            "CreateSampler",
            [
                (long)descriptor.AddressU,
                (long)descriptor.AddressV,
                (long)descriptor.AddressW,
                (long)descriptor.MagFilter,
                (long)descriptor.MinFilter,
                (long)descriptor.MipmapFilter,
                (long)descriptor.Compare,
                descriptor.MaxAnisotropy
            ],
            descriptor.Label
        );

    /// <inheritdoc />
    public WebGpuObject CreateShaderModule(in WgpuShaderModuleDescriptor descriptor) {
        LastShaderSource = descriptor.Source;

        return Create(
            "CreateShaderModule",
            [(long)descriptor.Source, descriptor.Code.Length],
            descriptor.Label
        );
    }

    /// <inheritdoc />
    public WebGpuObject CreateBindGroupLayout(in WgpuBindGroupLayoutDescriptor descriptor) {
        var values = new List<long> { descriptor.Entries.Length };

        foreach (var entry in descriptor.Entries) {
            values.Add(entry.Binding);
            values.Add((long)entry.Visibility);
            values.Add((long)entry.BufferType);
            values.Add(entry.HasDynamicOffset ? 1 : 0);
            values.Add((long)entry.SamplerType);
            values.Add((long)entry.TextureSampleType);
        }

        return Create("CreateBindGroupLayout", [.. values], descriptor.Label);
    }

    /// <inheritdoc />
    public WebGpuObject CreatePipelineLayout(in WgpuPipelineLayoutDescriptor descriptor) =>
        Create(
            "CreatePipelineLayout",
            [descriptor.BindGroupLayouts.Length, .. descriptor.BindGroupLayouts.Select(group => (long)group.Value)],
            descriptor.Label
        );

    /// <inheritdoc />
    public WebGpuObject CreateBindGroup(in WgpuBindGroupDescriptor descriptor) =>
        Create(
            "CreateBindGroup",
            [(long)descriptor.Layout.Value, descriptor.Entries.Length],
            descriptor.Label
        );

    /// <inheritdoc />
    public WebGpuObject CreateRenderPipeline(in WgpuRenderPipelineDescriptor descriptor) =>
        Create(
            "CreateRenderPipeline",
            [
                (long)descriptor.Layout.Value,
                (long)descriptor.VertexModule.Value,
                (long)descriptor.FragmentModule.Value,
                (long)descriptor.Topology,
                (long)descriptor.StripIndexFormat,
                (long)descriptor.FrontFace,
                (long)descriptor.CullMode,
                descriptor.UnclippedDepth ? 1 : 0,
                descriptor.SampleCount,
                descriptor.VertexBuffers.Length,
                descriptor.ColourTargets.Length,
                descriptor.DepthStencil is null ? 0 : 1
            ],
            descriptor.Label
        );

    /// <inheritdoc />
    public WebGpuObject CreateComputePipeline(in WgpuComputePipelineDescriptor descriptor) =>
        Create(
            "CreateComputePipeline",
            [(long)descriptor.Layout.Value, (long)descriptor.Module.Value],
            descriptor.Label
        );

    /// <inheritdoc />
    public void Release(WebGpuObjectKind kind, WebGpuObject handle) {
        live.Remove(handle.Value);
        Record("Release", [(long)kind, (long)handle.Value]);
    }

    // ── Queue ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void WriteBuffer(WebGpuObject buffer, long offset, ReadOnlySpan<byte> data) {
        LastWrite = data.ToArray();
        Record("WriteBuffer", [(long)buffer.Value, offset, data.Length]);
    }

    /// <inheritdoc />
    public bool ReadBuffer(WebGpuObject buffer, long offset, Span<byte> destination) {
        Record("ReadBuffer", [(long)buffer.Value, offset, destination.Length]);
        destination.Clear();

        return CanWait;
    }

    /// <inheritdoc />
    public void Submit(ReadOnlySpan<WebGpuObject> commands) => Record("Submit", [commands.Length]);

    /// <inheritdoc />
    public bool WaitIdle() {
        Record("WaitIdle", []);
        return CanWait;
    }

    /// <inheritdoc />
    public void Tick() => Record("Tick", []);

    // ── Encoding ────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject CreateCommandEncoder(string label) => Create("CreateCommandEncoder", [], label);

    /// <inheritdoc />
    public WebGpuObject FinishCommandEncoder(WebGpuObject encoder, string label) {
        live.Remove(encoder.Value);
        return Create("FinishCommandEncoder", [(long)encoder.Value], label);
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
        Record(
            "CopyBufferToBuffer",
            [(long)source.Value, sourceOffset, (long)destination.Value, destinationOffset, size]
        );

    /// <inheritdoc />
    public void CopyBufferToTexture(
        WebGpuObject encoder,
        in WgpuImageCopyBuffer source,
        in WgpuImageCopyTexture destination,
        int width,
        int height,
        int depthOrLayers
    ) =>
        Record(
            "CopyBufferToTexture",
            [
                (long)source.Buffer.Value,
                source.Offset,
                source.BytesPerRow,
                source.RowsPerImage,
                (long)destination.Texture.Value,
                destination.MipLevel,
                destination.OriginZ,
                width,
                height,
                depthOrLayers
            ]
        );

    /// <inheritdoc />
    public void CopyTextureToBuffer(
        WebGpuObject encoder,
        in WgpuImageCopyTexture source,
        in WgpuImageCopyBuffer destination,
        int width,
        int height,
        int depthOrLayers
    ) =>
        Record(
            "CopyTextureToBuffer",
            [
                (long)source.Texture.Value,
                source.MipLevel,
                (long)destination.Buffer.Value,
                destination.Offset,
                destination.BytesPerRow,
                width,
                height,
                depthOrLayers
            ]
        );

    /// <inheritdoc />
    public void CopyTextureToTexture(
        WebGpuObject encoder,
        in WgpuImageCopyTexture source,
        in WgpuImageCopyTexture destination,
        int width,
        int height,
        int depthOrLayers
    ) =>
        Record(
            "CopyTextureToTexture",
            [
                (long)source.Texture.Value,
                source.MipLevel,
                (long)destination.Texture.Value,
                destination.MipLevel,
                width,
                height,
                depthOrLayers
            ]
        );

    /// <inheritdoc />
    public void EncoderPushDebugGroup(WebGpuObject encoder, string name) =>
        Record("EncoderPushDebugGroup", [], name);

    /// <inheritdoc />
    public void EncoderPopDebugGroup(WebGpuObject encoder) => Record("EncoderPopDebugGroup", []);

    /// <inheritdoc />
    public void EncoderInsertDebugMarker(WebGpuObject encoder, string name) =>
        Record("EncoderInsertDebugMarker", [], name);

    // ── Render passes ───────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject BeginRenderPass(WebGpuObject encoder, in WgpuRenderPassDescriptor descriptor) {
        var values = new List<long> {
            descriptor.ColourAttachmentCount,
            descriptor.DepthStencil is null ? 0 : 1
        };

        for (var index = 0; index < descriptor.ColourAttachmentCount; index++) {
            var attachment = descriptor.ColourAttachments[index];
            values.Add((long)attachment.View.Value);
            values.Add((long)attachment.LoadOp);
            values.Add((long)attachment.StoreOp);
            values.Add((long)attachment.ResolveTarget.Value);
        }

        return Create("BeginRenderPass", [.. values], descriptor.Label);
    }

    /// <inheritdoc />
    public void EndRenderPass(WebGpuObject pass) {
        live.Remove(pass.Value);
        Record("EndRenderPass", [(long)pass.Value]);
    }

    /// <inheritdoc />
    public void RenderPassSetPipeline(WebGpuObject pass, WebGpuObject pipeline) =>
        Record("RenderPassSetPipeline", [(long)pipeline.Value]);

    /// <inheritdoc />
    public void RenderPassSetBindGroup(
        WebGpuObject pass,
        uint group,
        WebGpuObject bindGroup,
        ReadOnlySpan<uint> dynamicOffsets
    ) =>
        Record(
            "RenderPassSetBindGroup",
            [group, (long)bindGroup.Value, .. dynamicOffsets.ToArray().Select(offset => (long)offset)]
        );

    /// <inheritdoc />
    public void RenderPassSetVertexBuffer(WebGpuObject pass, uint slot, WebGpuObject buffer, long offset, long size) =>
        Record("RenderPassSetVertexBuffer", [slot, (long)buffer.Value, offset, size]);

    /// <inheritdoc />
    public void RenderPassSetIndexBuffer(
        WebGpuObject pass,
        WebGpuObject buffer,
        WgpuIndexFormat format,
        long offset,
        long size
    ) =>
        Record("RenderPassSetIndexBuffer", [(long)buffer.Value, (long)format, offset, size]);

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
        Record("RenderPassSetViewport", [(long)x, (long)y, (long)width, (long)height]);

    /// <inheritdoc />
    public void RenderPassSetScissorRect(WebGpuObject pass, uint x, uint y, uint width, uint height) =>
        Record("RenderPassSetScissorRect", [x, y, width, height]);

    /// <inheritdoc />
    public void RenderPassSetBlendConstant(WebGpuObject pass, double r, double g, double b, double a) =>
        Record("RenderPassSetBlendConstant", []);

    /// <inheritdoc />
    public void RenderPassSetStencilReference(WebGpuObject pass, uint reference) =>
        Record("RenderPassSetStencilReference", [reference]);

    /// <inheritdoc />
    public void RenderPassDraw(
        WebGpuObject pass,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance
    ) =>
        Record("RenderPassDraw", [vertexCount, instanceCount, firstVertex, firstInstance]);

    /// <inheritdoc />
    public void RenderPassDrawIndexed(
        WebGpuObject pass,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int baseVertex,
        uint firstInstance
    ) =>
        Record("RenderPassDrawIndexed", [indexCount, instanceCount, firstIndex, baseVertex, firstInstance]);

    /// <inheritdoc />
    public void RenderPassDrawIndexedIndirect(WebGpuObject pass, WebGpuObject arguments, long offset) =>
        Record("RenderPassDrawIndexedIndirect", [(long)arguments.Value, offset]);

    /// <inheritdoc />
    public void RenderPassPushDebugGroup(WebGpuObject pass, string name) =>
        Record("RenderPassPushDebugGroup", [], name);

    /// <inheritdoc />
    public void RenderPassPopDebugGroup(WebGpuObject pass) => Record("RenderPassPopDebugGroup", []);

    /// <inheritdoc />
    public void RenderPassInsertDebugMarker(WebGpuObject pass, string name) =>
        Record("RenderPassInsertDebugMarker", [], name);

    // ── Compute passes ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public WebGpuObject BeginComputePass(WebGpuObject encoder, string label) =>
        Create("BeginComputePass", [], label);

    /// <inheritdoc />
    public void EndComputePass(WebGpuObject pass) {
        live.Remove(pass.Value);
        Record("EndComputePass", [(long)pass.Value]);
    }

    /// <inheritdoc />
    public void ComputePassSetPipeline(WebGpuObject pass, WebGpuObject pipeline) =>
        Record("ComputePassSetPipeline", [(long)pipeline.Value]);

    /// <inheritdoc />
    public void ComputePassSetBindGroup(
        WebGpuObject pass,
        uint group,
        WebGpuObject bindGroup,
        ReadOnlySpan<uint> dynamicOffsets
    ) =>
        Record(
            "ComputePassSetBindGroup",
            [group, (long)bindGroup.Value, .. dynamicOffsets.ToArray().Select(offset => (long)offset)]
        );

    /// <inheritdoc />
    public void ComputePassDispatch(WebGpuObject pass, uint groupsX, uint groupsY, uint groupsZ) =>
        Record("ComputePassDispatch", [groupsX, groupsY, groupsZ]);

    /// <inheritdoc />
    public void ComputePassDispatchIndirect(WebGpuObject pass, WebGpuObject arguments, long offset) =>
        Record("ComputePassDispatchIndirect", [(long)arguments.Value, offset]);

    /// <inheritdoc />
    public void ComputePassPushDebugGroup(WebGpuObject pass, string name) =>
        Record("ComputePassPushDebugGroup", [], name);

    /// <inheritdoc />
    public void ComputePassPopDebugGroup(WebGpuObject pass) => Record("ComputePassPopDebugGroup", []);

    /// <inheritdoc />
    public void ComputePassInsertDebugMarker(WebGpuObject pass, string name) =>
        Record("ComputePassInsertDebugMarker", [], name);

    // ── Surface ─────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void ConfigureSurface(in WgpuSurfaceConfiguration configuration) =>
        Record(
            "ConfigureSurface",
            [
                (long)configuration.Format,
                (long)configuration.Usage,
                configuration.Width,
                configuration.Height,
                (long)configuration.PresentMode,
                (long)configuration.AlphaMode
            ]
        );

    /// <inheritdoc />
    public WgpuSurfaceStatus AcquireSurfaceTexture(out WebGpuObject texture) {
        if (NextSurfaceStatus != WgpuSurfaceStatus.Success) {
            texture = WebGpuObject.Null;
            Record("AcquireSurfaceTexture", [(long)NextSurfaceStatus]);

            return NextSurfaceStatus;
        }

        texture = Create("AcquireSurfaceTexture", [], "");
        return WgpuSurfaceStatus.Success;
    }

    /// <inheritdoc />
    public void PresentSurface() => Record("PresentSurface", []);

    /// <inheritdoc />
    public void Dispose() => Record("Dispose", []);

    /// <summary>The recorded stream, one call per line, for a failing assertion to print.</summary>
    public string Dump() {
        var text = new StringBuilder();

        foreach (var call in calls) {
            text.AppendLine(call.ToString());
        }

        return text.ToString();
    }

    WebGpuObject Create(string name, long[] values, string label) {
        var handle = next++;
        live.Add(handle);
        calls.Add(new(name, values, label));

        return new(handle);
    }

    void Record(string name, long[] values, string text = "") => calls.Add(new(name, values, text));
}
