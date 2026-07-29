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
