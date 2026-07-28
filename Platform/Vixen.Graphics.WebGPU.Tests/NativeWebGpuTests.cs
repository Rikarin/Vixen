// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.WebGPU.Tests;

/// <summary>The backend against a real implementation. Skipped where there is none.</summary>
/// <remarks>
///     <para>
///         <b>Everything the fake cannot check.</b> A recording binding proves the backend made the
///         calls it meant to; it cannot prove those calls are the ones a C library will accept.
///         Struct layouts, field order, enum values, which combinations an implementation validates
///         — all of that is between <c>NativeWebGpuBinding</c> and <c>webgpu.h</c>, and none of it
///         fails until something dereferences it.
///     </para>
///     <para>
///         <b>And it reads the pixels.</b> <c>docs/plan/05</c> records that lesson from the Vulkan
///         backend: every test that asserts a recorded command stream passes against a backend that
///         draws nothing at all, which is what happened for an afternoon when
///         <c>BlendState.Opaque</c> was silently zero-initialised to a write mask of <c>None</c>.
///         The draw below asserts <em>where the picture is</em> — centre covered, corners not — and
///         that cannot pass by accident.
///     </para>
/// </remarks>
public class NativeWebGpuTests {
    [Fact]
    public void ADeviceComesUpAndSaysWhatItIs() {
        using var device = WebGpuRequirement.Device();

        Assert.NotEmpty(device.Adapter.Name);
        Assert.True(device.Features.HasCompute);
        Assert.True(device.Features.HasDynamicRendering);

        // Every limit the backend reports comes through WGPULimits, whose field order is the thing a
        // mismatched header would scramble. A zero here means the struct was read wrongly, not that
        // the hardware is small.
        Assert.True(device.Features.MaxTextureSize >= 8192, $"MaxTextureSize was {device.Features.MaxTextureSize}");
        Assert.True(device.Features.MaxDescriptorSets >= 4, $"MaxBindGroups was {device.Features.MaxDescriptorSets}");
        Assert.True(device.Features.MaxColourAttachments >= 4);
        Assert.True(device.Features.MaxVertexBuffers >= 8);
        Assert.True(device.Features.MaxComputeWorkgroupSize.X >= 128);
    }

    /// <summary>
    ///     The device is asked for the adapter's limits rather than the specification's floor, so a
    ///     desktop GPU has to report more than the floor somewhere.
    /// </summary>
    [Fact]
    public void TheDeviceGotTheAdaptersLimitsAndNotTheGuaranteedFloor() {
        using var binding = WebGpuRequirement.Binding();
        var floor = WebGpuLimits.Guaranteed;

        Assert.True(
            binding.Limits.MaxTextureDimension2D > floor.MaxTextureDimension2D
            || binding.Limits.MaxBufferSize > floor.MaxBufferSize
            || binding.Limits.MaxTextureArrayLayers > floor.MaxTextureArrayLayers,
            "Every limit equals the specification's floor, which a desktop adapter does not report "
            + "unless the device was created without asking for its own."
        );
    }

    [Fact]
    public void ResourcesAreCreatedAndReleased() {
        using var device = WebGpuRequirement.Device();

        var buffer = device.CreateBuffer(new(1024, BufferUsage.Vertex | BufferUsage.CopyDestination, Name: "Mesh"));
        var texture = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.Sampled | TextureUsage.CopyDestination, MipLevels: 4, Name: "Albedo")
        );

        var view = device.CreateTextureView(texture);
        var sampler = device.CreateSampler(SamplerDescription.LinearRepeat);

        Assert.True(buffer.IsValid);
        Assert.True(view.IsValid);
        Assert.True(sampler.IsValid);
        Assert.Equal(4, device.LiveResourceCount);

        device.Destroy(sampler);
        device.Destroy(view);
        device.Destroy(texture);
        device.Destroy(buffer);

        Assert.Equal(0, device.LiveResourceCount);
    }

    /// <summary>
    ///     A cube view is a 2D texture read six layers at a time, and getting the view dimension
    ///     wrong is a validation error rather than a wrong picture — so it is worth asking a real
    ///     implementation.
    /// </summary>
    [Fact]
    public void ACubeViewIsAccepted() {
        using var device = WebGpuRequirement.Device();

        var texture = device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                32,
                32,
                TextureUsage.Sampled,
                ArrayLayers: 6,
                Dimension: TextureDimension.TextureCube,
                Name: "Environment"
            )
        );

        Assert.True(device.CreateTextureView(texture).IsValid);
    }

    /// <summary>
    ///     A host write goes through <c>queue.writeBuffer</c>, which validates its alignment — so
    ///     this is where the widening in <c>Write</c> is checked against the rule it exists for
    ///     rather than against a recorded call.
    /// </summary>
    [Fact]
    public void AnUnalignedHostWriteIsAccepted() {
        using var device = WebGpuRequirement.Device();
        var buffer = device.CreateBuffer(new(256, BufferUsage.Uniform, MemoryAccess.HostUpload, "Constants"));

        device.Write(buffer, 5, [1, 2, 3]);
        device.Write(buffer, 0, [1, 2, 3, 4, 5, 6, 7, 8]);
        device.WaitIdle();
    }

    [Fact]
    public void AShaderCompilesAndAPipelineIsBuiltFromIt() {
        using var device = WebGpuRequirement.Device();

        var vertex = device.CreateShader(ShaderStage.Vertex, TestShaders.Vertex, "Vertex");
        var fragment = device.CreateShader(ShaderStage.Fragment, TestShaders.Fragment, "Fragment");
        var layout = device.CreatePipelineLayout(new([], Name: "Empty"));

        var pipeline = device.CreateGraphicsPipeline(
            new(
                vertex,
                fragment,
                layout,
                [new(PixelFormat.Rgba8UNorm)],
                DepthStencil: DepthStencilState.Disabled,
                Name: "Triangle"
            )
        );

        Assert.True(pipeline.IsValid);
    }

    /// <summary>
    ///     Blending is the state most likely to be accepted and wrong: every factor and operation
    ///     crosses as an enum value, and a shifted one still compiles.
    /// </summary>
    [Fact]
    public void EveryBlendStateTheEngineShipsCompiles() {
        using var device = WebGpuRequirement.Device();

        var vertex = device.CreateShader(ShaderStage.Vertex, TestShaders.Vertex, "Vertex");
        var fragment = device.CreateShader(ShaderStage.Fragment, TestShaders.Fragment, "Fragment");
        var layout = device.CreatePipelineLayout(new([], Name: "Empty"));

        BlendState[] blends = [
            BlendState.Opaque,
            BlendState.AlphaBlend,
            BlendState.PremultipliedAlpha,
            BlendState.Additive
        ];

        foreach (var blend in blends) {
            var pipeline = device.CreateGraphicsPipeline(
                new(
                    vertex,
                    fragment,
                    layout,
                    [new(PixelFormat.Rgba8UNorm, blend)],
                    DepthStencil: DepthStencilState.Disabled,
                    Name: "Blend"
                )
            );

            Assert.True(pipeline.IsValid);
        }
    }

    /// <summary>
    ///     A depth pipeline under the engine's reversed-Z convention, which is
    ///     <see cref="CompareFunction.Greater" /> — the mapping most likely to be silently wrong,
    ///     because every comparison compiles.
    /// </summary>
    [Fact]
    public void ADepthPipelineCompilesWithTheEnginesReversedComparison() {
        using var device = WebGpuRequirement.Device();

        var vertex = device.CreateShader(ShaderStage.Vertex, TestShaders.Vertex, "Vertex");
        var fragment = device.CreateShader(ShaderStage.Fragment, TestShaders.Fragment, "Fragment");
        var layout = device.CreatePipelineLayout(new([], Name: "Empty"));

        var pipeline = device.CreateGraphicsPipeline(
            new(
                vertex,
                fragment,
                layout,
                [new(PixelFormat.Rgba8UNorm)],
                DepthStencil: DepthStencilState.Default,
                DepthFormat: PixelFormat.Depth32Float,
                Name: "Depth"
            )
        );

        Assert.True(pipeline.IsValid);
    }

    /// <summary>
    ///     A vertex layout, which is where a wrong <see cref="VertexFormat" /> mapping would land:
    ///     the implementation validates the stride against the attribute offsets and sizes, so a
    ///     format that decodes to the wrong width is refused rather than mis-read.
    /// </summary>
    [Fact]
    public void AVertexLayoutIsValidatedAgainstItsStride() {
        using var device = WebGpuRequirement.Device();

        var vertex = device.CreateShader(ShaderStage.Vertex, TestShaders.VertexWithAttributes, "Vertex");
        var fragment = device.CreateShader(ShaderStage.Fragment, TestShaders.Fragment, "Fragment");
        var layout = device.CreatePipelineLayout(new([], Name: "Empty"));

        var pipeline = device.CreateGraphicsPipeline(
            new(
                vertex,
                fragment,
                layout,
                [new(PixelFormat.Rgba8UNorm)],
                [
                    new(
                        32,
                        [
                            new(0, VertexFormat.Float32X3, 0),
                            new(1, VertexFormat.Float32X2, 12),
                            new(2, VertexFormat.UNorm8X4, 20),
                            new(3, VertexFormat.SNorm16X4, 24)
                        ]
                    )
                ],
                DepthStencil: DepthStencilState.Disabled,
                Name: "Mesh"
            )
        );

        Assert.True(pipeline.IsValid);
    }

    /// <summary>
    ///     A compute pipeline with a bind group, which is the whole descriptor path end to end: a
    ///     layout, a set, a buffer written into it, and a bind group the implementation accepts.
    /// </summary>
    [Fact]
    public void AComputePipelineAndItsBindGroupAreAccepted() {
        using var device = WebGpuRequirement.Device();

        var setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerFrame,
                [
                    new(0, DescriptorKind.UniformBuffer, ShaderStage.Compute),
                    new(1, DescriptorKind.StorageBuffer, ShaderStage.Compute)
                ],
                "Compute"
            )
        );

        var layout = device.CreatePipelineLayout(new([setLayout], Name: "Compute"));
        var shader = device.CreateShader(ShaderStage.Compute, TestShaders.Compute, "Compute");
        var pipeline = device.CreateComputePipeline(new(shader, layout, "Compute"));

        var uniform = device.CreateBuffer(new(16, BufferUsage.Uniform, MemoryAccess.HostUpload, "Constants"));
        var storage = device.CreateBuffer(new(64, BufferUsage.Storage | BufferUsage.CopySource, Name: "Output"));
        var set = device.CreateDescriptorSet(setLayout, "Compute");

        device.UpdateDescriptorSet(
            set,
            [DescriptorWrite.Uniform(0, uniform), DescriptorWrite.Storage(1, storage)]
        );

        Assert.True(pipeline.IsValid);
        Assert.True(set.IsValid);
    }

    /// <summary>
    ///     A dispatch, replayed — which is where the compute pass the RHI does not have gets opened
    ///     and closed, and where an unbalanced one would be a validation error.
    /// </summary>
    [Fact]
    public void AComputeShaderRunsAndItsOutputComesBack() {
        using var device = WebGpuRequirement.Device();

        var setLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerFrame,
                [
                    new(0, DescriptorKind.UniformBuffer, ShaderStage.Compute),
                    new(1, DescriptorKind.StorageBuffer, ShaderStage.Compute)
                ],
                "Compute"
            )
        );

        var layout = device.CreatePipelineLayout(new([setLayout], Name: "Compute"));
        var shader = device.CreateShader(ShaderStage.Compute, TestShaders.Compute, "Compute");
        var pipeline = device.CreateComputePipeline(new(shader, layout, "Compute"));

        var uniform = device.CreateBuffer(new(16, BufferUsage.Uniform, MemoryAccess.HostUpload, "Constants"));
        var storage = device.CreateBuffer(
            new(64, BufferUsage.Storage | BufferUsage.CopySource, Name: "Output")
        );

        var readback = device.CreateBuffer(
            new(64, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "Readback")
        );

        var set = device.CreateDescriptorSet(setLayout, "Compute");
        device.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(0, uniform), DescriptorWrite.Storage(1, storage)]);

        // The multiplier the shader applies, so the answer is not something a zeroed buffer could
        // produce by accident.
        device.Write(uniform, 0, [7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        using (var list = device.BeginCommandList(QueueKind.Compute, "Compute")) {
            list.BindPipeline(pipeline);
            list.BindDescriptorSet(DescriptorSetSlot.PerFrame, set);
            list.Dispatch(16);
            list.CopyBuffer(storage, 0, readback, 0, 64);
            list.Finish();

            device.ComputeQueue.Submit([list]);
        }

        device.WaitIdle();

        var bytes = new byte[64];
        device.Read(readback, 0, bytes);

        for (var index = 0; index < 16; index++) {
            Assert.Equal((uint)(index * 7), BitConverter.ToUInt32(bytes, index * 4));
        }
    }

    /// <summary>
    ///     A triangle, drawn offscreen, read back, and asserted on by position.
    /// </summary>
    /// <remarks>
    ///     Centre covered and corners not. That pins several things at once that nothing else does:
    ///     the pipeline draws at all, the clear colour reaches the attachment, the viewport covers
    ///     the target, and the write mask is not <see cref="ColourWriteMask.None" /> — which is
    ///     precisely the bug <c>ColourTargetState.EffectiveBlend</c> exists to prevent and which no
    ///     recorded stream would have caught.
    /// </remarks>
    [Fact]
    public void ATriangleIsDrawnWhereItWasAimed() {
        using var device = WebGpuRequirement.Device();

        // Two-sided, so this test is about *where* the picture is and the winding is asserted on its
        // own below. A draw test that also depended on the cull mode would fail for two unrelated
        // reasons and say neither.
        var picture = Render(device, Pipeline(device, TestShaders.Vertex, RasterizerState.TwoSided));

        Assert.Equal(Green, picture.At(32, 40));
        Assert.Equal(Red, picture.At(1, 1));
        Assert.Equal(Red, picture.At(62, 1));
        Assert.Equal(Red, picture.At(1, 62));
        Assert.Equal(Red, picture.At(62, 62));
    }

    /// <summary>
    ///     The same triangle with culling, once each way round.
    /// </summary>
    /// <remarks>
    ///     <b>This is what pins the winding and viewport conventions together</b>, and it is the same
    ///     assertion <c>Vixen.Graphics.Vulkan</c>'s suite makes — deliberately, because two backends
    ///     that disagree here draw different pictures from the same scene. The shader emits its
    ///     vertices in one order; with the engine's counter-clockwise front face, exactly one of
    ///     culling front faces and culling back faces may produce a picture. If the front-face
    ///     convention and the viewport's Y flip disagreed, both would cull or neither would — and
    ///     every other test in this file would still pass.
    ///
    ///     Which of the two survives is deliberately not asserted: that is a fact about WebGPU's
    ///     framebuffer orientation, and pinning it here would be writing down whatever the
    ///     implementation happened to do.
    /// </remarks>
    [Fact]
    public void CullingRemovesExactlyOneWinding() {
        using var device = WebGpuRequirement.Device();

        var front = Render(device, Pipeline(device, TestShaders.Vertex, new(CullMode.Front)));
        var back = Render(device, Pipeline(device, TestShaders.Vertex, new(CullMode.Back)));

        var drawnWithFrontCulled = front.At(32, 40) == Green;
        var drawnWithBackCulled = back.At(32, 40) == Green;

        Assert.True(
            drawnWithFrontCulled != drawnWithBackCulled,
            "Culling front faces and culling back faces produced the same picture, so the front-face "
            + "convention and the viewport's Y flip are not agreeing with each other."
        );
    }

    /// <summary>
    ///     Push constants, emulated — the one place where what the shader reads and what the backend
    ///     writes have to agree about a bind group index nothing in WebGPU declares.
    /// </summary>
    /// <remarks>
    ///     The shader offsets the triangle along X by what it reads out of
    ///     <c>@group(0) @binding(0)</c>, and the test moves it from one side of the picture to the
    ///     other. A backend that bound the block at the wrong group, wrote it at the wrong offset, or
    ///     handed the wrong dynamic offset would draw the triangle in its unmoved position — and a
    ///     recorded stream would have shown a bind either way.
    ///
    ///     X rather than Y: clip-space X reaches the framebuffer without a flip on any API, so this
    ///     asserts the constant arrived rather than which way up the viewport is.
    /// </remarks>
    [Fact]
    public void APushConstantMovesTheTriangle() {
        using var device = WebGpuRequirement.Device();

        var vertex = device.CreateShader(ShaderStage.Vertex, TestShaders.VertexPushed, "Vertex");
        var fragment = device.CreateShader(ShaderStage.Fragment, TestShaders.Fragment, "Fragment");
        var layout = device.CreatePipelineLayout(new([], [new(ShaderStage.Vertex, 0, 16)], "Pushed"));

        var pipeline = device.CreateGraphicsPipeline(
            new(
                vertex,
                fragment,
                layout,
                [new(PixelFormat.Rgba8UNorm)],
                Rasterizer: RasterizerState.TwoSided,
                DepthStencil: DepthStencilState.Disabled,
                Name: "Pushed"
            )
        );

        var left = Render(device, pipeline, push: -0.5f);
        var right = Render(device, pipeline, push: 0.5f);

        Assert.Equal(Green, left.At(16, 36));
        Assert.Equal(Red, left.At(48, 36));

        Assert.Equal(Green, right.At(48, 36));
        Assert.Equal(Red, right.At(16, 36));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────

    const uint Green = 0xFF00FF00;
    const uint Red = 0xFF0000FF;

    static PipelineHandle Pipeline(WebGpuDevice device, ReadOnlySpan<byte> vertexShader, RasterizerState rasterizer) {
        var vertex = device.CreateShader(ShaderStage.Vertex, vertexShader, "Vertex");
        var fragment = device.CreateShader(ShaderStage.Fragment, TestShaders.Fragment, "Fragment");
        var layout = device.CreatePipelineLayout(new([], Name: "Empty"));

        return device.CreateGraphicsPipeline(
            new(
                vertex,
                fragment,
                layout,
                [new(PixelFormat.Rgba8UNorm)],
                Rasterizer: rasterizer,
                DepthStencil: DepthStencilState.Disabled,
                Name: "Triangle"
            )
        );
    }

    /// <summary>Renders one pass into a 64×64 target and copies it back.</summary>
    /// <remarks>
    ///     64 wide because a row of <see cref="PixelFormat.Rgba8UNorm" /> is then exactly 256 bytes,
    ///     which is the row alignment WebGPU requires — so the readback has no padding to step over
    ///     and a failure is a failure of the picture rather than of the arithmetic.
    /// </remarks>
    static Picture Render(WebGpuDevice device, PipelineHandle pipeline, float? push = null, int vertices = 3) {
        const int size = 64;

        var target = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, size, size, TextureUsage.ColourTarget | TextureUsage.CopySource, Name: "Target")
        );

        var view = device.CreateTextureView(target);
        var readback = device.CreateBuffer(
            new(size * size * 4, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "Readback")
        );

        using (var list = device.BeginCommandList(QueueKind.Graphics, "Offscreen")) {
            ColourAttachment[] attachments = [new(view, LoadAction.Clear, StoreAction.Store, new(1f, 0f, 0f, 1f))];

            list.BeginRenderPass(new(attachments, null, "Offscreen"));
            list.SetViewport(new Viewport(0, 0, size, size));
            list.SetScissor(new(0, 0, size, size));
            list.BindPipeline(pipeline);

            if (push is { } offset) {
                list.PushConstants(ShaderStage.Vertex, 0, BitConverter.GetBytes(offset));
            }

            list.Draw(vertices);
            list.EndRenderPass();

            list.CopyTextureToBuffer(new(target), new(size, size, 1), readback, 0);
            list.Finish();

            device.GraphicsQueue.Submit([list]);
        }

        device.WaitIdle();

        var bytes = new byte[size * size * 4];
        device.Read(readback, 0, bytes);

        return new(bytes, size);
    }

    /// <summary>What came back, addressed by pixel.</summary>
    /// <param name="Bytes">The rows, tightly packed.</param>
    /// <param name="Size">The edge length in pixels.</param>
    readonly record struct Picture(byte[] Bytes, int Size) {
        /// <summary>The pixel at a position, as <c>0xAABBGGRR</c>.</summary>
        /// <param name="x">The column.</param>
        /// <param name="y">The row, from the top.</param>
        public uint At(int x, int y) => BitConverter.ToUInt32(Bytes, ((y * Size) + x) * 4);
    }
}
