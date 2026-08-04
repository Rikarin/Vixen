// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics.WebGPU;

public sealed partial class WebGpuDevice {
    /// <summary>The entry point every module the engine compiles exposes.</summary>
    /// <remarks>
    ///     WGSL entry points are named and WebGPU asks for the name; SPIR-V's are too. Raven emits
    ///     one entry point per stage per module and calls it <c>main</c>
    ///     ([07](../../docs/plan/07-raven-shader-pipeline.md)), which is the same convention
    ///     <c>Vixen.Graphics.Vulkan</c> assumes — so it is stated once here rather than threaded
    ///     through <see cref="IGraphicsDevice.CreateShader" />, which takes bytecode and a stage and
    ///     no name.
    /// </remarks>
    public const string EntryPoint = "main";

    /// <inheritdoc />
    public PipelineHandle CreateGraphicsPipeline(in GraphicsPipelineDescription description) {
        description.Validate();

        if (description.Rasterizer.Fill == FillMode.Wireframe) {
            throw new NotSupportedException(
                $"Pipeline '{description.Name}' asks for wireframe. WebGPU has no polygon fill mode — "
                + "which is what Features.HasWireframe reports — so a wireframe debug view on the web "
                + "draws line lists."
            );
        }

        if (description.Rasterizer.DepthClamp && !Features.HasDepthClamp) {
            throw new NotSupportedException(
                $"Pipeline '{description.Name}' asks for depth clamping on a device without WebGPU's "
                + "depth-clip-control feature. Ask Features.HasDepthClamp; a shadow pass without it has to "
                + "push its near plane out instead."
            );
        }

        if (!Features.SupportsSampleCount(description.SampleCount)) {
            throw new ArgumentException(
                $"Pipeline '{description.Name}' asks for {description.SampleCount} samples. WebGPU fixes "
                + "the set at one and four."
            );
        }

        var layout = ResolvePipelineLayout(description.Layout, description.Name);
        var vertex = ResolveShader(description.Vertex, description.Name, "vertex");
        var fragment = description.Fragment.IsValid
            ? ResolveShader(description.Fragment, description.Name, "fragment")
            : null;

        var targets = new WgpuColourTargetState[description.ColourTargets.Length];

        for (var index = 0; index < targets.Length; index++) {
            targets[index] = WebGpuConversions.ToWebGpu(description.ColourTargets[index]);
        }

        var buffers = BuildVertexLayouts(description);

        WgpuDepthStencilState? depth = description.DepthFormat != PixelFormat.Undefined
            ? WebGpuConversions.ToWebGpu(description.DepthStencil, description.DepthFormat, description.Rasterizer)
            : null;

        var handle = binding.CreateRenderPipeline(
            new(
                layout.Handle,
                vertex.Handle,
                EntryPoint,
                buffers,
                fragment?.Handle ?? WebGpuObject.Null,
                EntryPoint,
                targets,
                WebGpuConversions.ToWebGpu(description.Topology),

                // WebGPU requires a strip index format on a strip pipeline and forbids one otherwise.
                // The RHI states the index format at bind time, so a strip pipeline has to pick: 16-bit
                // is the RHI's own default and is what a strip that needs restart indices wants least
                // to be wrong about.
                WebGpuConversions.IsStrip(description.Topology)
                    ? WgpuIndexFormat.Uint16
                    : WgpuIndexFormat.Undefined,
                WebGpuConversions.ToWebGpu(description.Rasterizer.FrontFace),
                WebGpuConversions.ToWebGpu(description.Rasterizer.Cull),
                description.Rasterizer.DepthClamp,
                depth,
                description.SampleCount,
                description.Name
            )
        );

        lock (gate) {
            return new(
                pipelines.Add(new WebGpuPipeline(handle, false, layout.PushConstantGroup, description.Name))
            );
        }
    }

    /// <inheritdoc />
    public PipelineHandle CreateComputePipeline(in ComputePipelineDescription description) {
        description.Validate();

        var layout = ResolvePipelineLayout(description.Layout, description.Name);
        var compute = ResolveShader(description.Compute, description.Name, "compute");

        var handle = binding.CreateComputePipeline(
            new(layout.Handle, compute.Handle, EntryPoint, description.Name)
        );

        lock (gate) {
            return new(
                pipelines.Add(new WebGpuPipeline(handle, true, layout.PushConstantGroup, description.Name))
            );
        }
    }

    /// <inheritdoc />
    public ISwapChain CreateSwapChain(in SwapChainDescription description) {
        ThrowIfDisposed();

        if (!binding.HasSurface) {
            throw new InvalidOperationException(
                "A swapchain was asked for on a device with no surface. WebGPU's surface is chosen when "
                + "the adapter is requested, because which adapter can present is a property of the "
                + "surface — so a device meant for a window has to be created knowing about the window."
            );
        }

        return new WebGpuSwapChain(this, description);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Not yet, and the reason is that the feature is not free to ask for.</b> WebGPU has
    ///     <c>timestamp-query</c>, and it is an <i>optional</i> feature a device must be created
    ///     with — browsers gate it behind a flag for the same fingerprinting and timing-attack
    ///     reasons they blur <c>performance.now</c>, so a device that asked for it unconditionally
    ///     would fail to be created on the majority of the configurations this backend targets. It
    ///     belongs with a device-creation option that says whether the caller wants a profileable
    ///     device, which is a decision the editor's remote-inspector work has not needed yet.
    /// </remarks>
    public QueryPoolHandle CreateQueryPool(in QueryPoolDescription description) =>
        throw new NotSupportedException(
            $"Query pool '{description.Name}' was asked for on the WebGPU backend. `timestamp-query` is "
            + "an optional device feature this backend does not request. Ask "
            + "Features.HasTimestampQueries first."
        );

    /// <inheritdoc />
    public void Destroy(QueryPoolHandle handle) {
        // Nothing was created, so nothing is freed — and a Destroy that threw would turn a clean-up
        // path into a second failure.
    }

    /// <inheritdoc />
    public bool TryResolveQueries(QueryPoolHandle pool, int first, Span<ulong> results) =>
        throw new NotSupportedException(
            "The WebGPU backend does not request `timestamp-query`, so no pool exists to resolve."
        );

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Not yet, and not soon.</b> Ray tracing is not in the WebGPU specification —
    ///     <c>chromium-experimental-ray-tracing</c> exists behind flags, but a capability the
    ///     specification has not admitted is not one a backend can report — so
    ///     <see cref="GraphicsDeviceFeatures.HasRayTracing" /> is false here and the distance-field
    ///     tracer is the path, exactly as on GL.
    /// </remarks>
    public AccelerationStructureSizes GetAccelerationStructureSizes(in AccelerationStructureBuildInput input) =>
        throw new NotSupportedException(
            "Acceleration-structure sizes were asked for on the WebGPU backend. Ray tracing is not "
            + "in the WebGPU specification — ask Features.HasRayTracing and take the distance-field "
            + "tracer."
        );

    /// <inheritdoc />
    public AccelerationStructureHandle CreateAccelerationStructure(in AccelerationStructureDescription description) =>
        throw new NotSupportedException(
            $"Acceleration structure '{description.Name}' was asked for on the WebGPU backend, and "
            + "ray tracing is not in the WebGPU specification. Ask Features.HasRayTracing and take "
            + "the distance-field tracer."
        );

    /// <inheritdoc />
    public ulong GetAccelerationStructureAddress(AccelerationStructureHandle handle) =>
        throw new NotSupportedException(
            "The WebGPU backend has no ray tracing, so no acceleration structure exists to address."
        );

    /// <inheritdoc />
    public void Destroy(AccelerationStructureHandle handle) {
        // Nothing was created, so nothing is freed — and a Destroy that threw would turn a clean-up
        // path into a second failure.
    }

    static WgpuVertexBufferLayout[] BuildVertexLayouts(in GraphicsPipelineDescription description) {
        if (description.VertexBuffers is not { Length: > 0 } declared) {
            return [];
        }

        var buffers = new WgpuVertexBufferLayout[declared.Length];

        for (var index = 0; index < buffers.Length; index++) {
            var source = declared[index];
            var attributes = new WgpuVertexElement[source.Attributes?.Length ?? 0];

            for (var slot = 0; slot < attributes.Length; slot++) {
                var attribute = source.Attributes![slot];
                var format = attribute.Format.ToWebGpu();

                if (format == WgpuVertexFormat.Undefined) {
                    throw new NotSupportedException(
                        $"Pipeline '{description.Name}' uses vertex format {attribute.Format} at location "
                        + $"{attribute.Location}, which WebGPU does not have."
                    );
                }

                attributes[slot] = new(format, attribute.Offset, attribute.Location);
            }

            buffers[index] = new(
                source.Stride,
                source.StepMode == VertexStepMode.Instance
                    ? WgpuVertexStepMode.Instance
                    : WgpuVertexStepMode.Vertex,
                attributes
            );
        }

        return buffers;
    }

    WebGpuPipelineLayout ResolvePipelineLayout(PipelineLayoutHandle handle, string pipeline) {
        lock (gate) {
            if (!pipelineLayouts.TryGet(handle.Value, out var found)) {
                throw new ArgumentException(
                    $"The layout of pipeline '{pipeline}' does not exist, or has been destroyed.",
                    nameof(handle)
                );
            }

            return (WebGpuPipelineLayout)found;
        }
    }

    WebGpuShader ResolveShader(ShaderHandle handle, string pipeline, string stage) {
        lock (gate) {
            if (!shaders.TryGet(handle.Value, out var found)) {
                throw new ArgumentException(
                    $"The {stage} shader of pipeline '{pipeline}' does not exist, or has been destroyed.",
                    nameof(handle)
                );
            }

            return (WebGpuShader)found;
        }
    }
}
