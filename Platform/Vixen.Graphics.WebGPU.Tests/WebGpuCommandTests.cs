// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.WebGPU.Tests;

/// <summary>Recording, its validation, and what replay makes of it.</summary>
/// <remarks>
///     Two questions, and both are worth asking without a GPU. Did the list refuse what WebGPU would
///     have refused — a draw outside a pass, a copy inside one? And did the replay make the calls a
///     WebGPU implementation would have seen, in the order it would have seen them?
/// </remarks>
public sealed class WebGpuCommandTests : IDisposable {
    readonly FakeWebGpuBinding binding = new();

    /// <summary>Disposes the fake, which a device would otherwise do at the end of each test.</summary>
    public void Dispose() => binding.Dispose();

    [Fact]
    public void ADrawOutsideAPassIsRefused() {
        using var device = new WebGpuDevice(binding);
        using var list = device.BeginCommandList();

        var thrown = Assert.Throws<InvalidOperationException>(() => list.Draw(3));
        Assert.Contains("outside a render pass", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PassesDoNotNest() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);
        using var list = device.BeginCommandList();

        list.BeginRenderPass(Pass(view, "First"));
        Assert.Throws<InvalidOperationException>(() => list.BeginRenderPass(Pass(view, "Second")));
    }

    [Fact]
    public void ACopyInsideAPassIsRefused() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);
        var source = device.CreateBuffer(new(64, BufferUsage.CopySource, Name: "From"));
        var destination = device.CreateBuffer(new(64, BufferUsage.CopyDestination, Name: "To"));

        using var list = device.BeginCommandList();
        list.BeginRenderPass(Pass(view));

        Assert.Throws<InvalidOperationException>(() => list.CopyBuffer(source, 0, destination, 0, 64));
    }

    [Fact]
    public void ADispatchInsideAPassIsRefused() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);

        using var list = device.BeginCommandList();
        list.BeginRenderPass(Pass(view));

        Assert.Throws<InvalidOperationException>(() => list.Dispatch(1));
    }

    [Fact]
    public void AListFinishedInsideAPassIsRefused() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);

        using var list = device.BeginCommandList(QueueKind.Graphics, "Main");
        list.BeginRenderPass(Pass(view));

        var thrown = Assert.Throws<InvalidOperationException>(list.Finish);
        Assert.Contains("Main", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A debug group may not straddle a pass boundary: WebGPU pushes onto a pass encoder while a
    ///     pass is open and onto the command encoder otherwise, and the two are separate stacks.
    /// </summary>
    [Fact]
    public void ADebugGroupMayNotStraddleAPassBoundary() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);

        using var list = device.BeginCommandList(QueueKind.Graphics, "Main");
        list.BeginRenderPass(Pass(view));
        list.PushDebugGroup("Inside");

        var thrown = Assert.Throws<InvalidOperationException>(list.EndRenderPass);
        Assert.Contains("closed in the pass that opened it", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListSubmittedTwiceIsRefused() {
        using var device = new WebGpuDevice(binding);
        using var list = device.BeginCommandList(QueueKind.Graphics, "Main");
        list.Finish();

        device.GraphicsQueue.Submit([list]);
        Assert.Throws<InvalidOperationException>(() => device.GraphicsQueue.Submit([list]));
    }

    [Fact]
    public void AListSubmittedBeforeFinishIsRefused() {
        using var device = new WebGpuDevice(binding);
        using var list = device.BeginCommandList();

        Assert.Throws<InvalidOperationException>(() => device.GraphicsQueue.Submit([list]));
    }

    [Fact]
    public void ABufferCopiedOntoItselfIsRefused() {
        using var device = new WebGpuDevice(binding);
        var buffer = device.CreateBuffer(new(64, BufferUsage.CopySource | BufferUsage.CopyDestination, Name: "Self"));

        using var list = device.BeginCommandList();
        Assert.Throws<ArgumentException>(() => list.CopyBuffer(buffer, 0, buffer, 0, 8));
    }

    [Fact]
    public void ACopyPastTheEndOfABufferIsRefused() {
        using var device = new WebGpuDevice(binding);
        var source = device.CreateBuffer(new(16, BufferUsage.CopySource, Name: "From"));
        var destination = device.CreateBuffer(new(64, BufferUsage.CopyDestination, Name: "To"));

        using var list = device.BeginCommandList();
        Assert.Throws<ArgumentOutOfRangeException>(() => list.CopyBuffer(source, 0, destination, 0, 32));
    }

    /// <summary>
    ///     WebGPU has no multi-draw indirect. The refusal names the capability rather than looping,
    ///     so the cost is visible at the call site.
    /// </summary>
    [Fact]
    public void MultiDrawIndirectIsRefusedByName() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);
        var arguments = device.CreateBuffer(new(256, BufferUsage.Indirect, Name: "Args"));

        using var list = device.BeginCommandList();
        list.BeginRenderPass(Pass(view));

        var thrown = Assert.Throws<NotSupportedException>(() => list.DrawIndexedIndirect(arguments, 0, 8));
        Assert.Contains("HasMultiDrawIndirect", thrown.Message, StringComparison.Ordinal);

        list.DrawIndexedIndirect(arguments, 0);
    }

    /// <summary>
    ///     A handle destroyed before the list is submitted is still the object that was drawn with,
    ///     because a recorded command names the object rather than the handle.
    /// </summary>
    [Fact]
    public void HandlesAreResolvedWhenTheyAreRecorded() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);
        var buffer = device.CreateBuffer(new(64, BufferUsage.Vertex, Name: "Mesh"));

        using var list = device.BeginCommandList();
        list.BeginRenderPass(Pass(view));
        list.BindVertexBuffer(0, buffer);
        list.Draw(3);
        list.EndRenderPass();
        list.Finish();

        device.Destroy(buffer);
        device.GraphicsQueue.Submit([list]);

        Assert.Single(binding.OfName("RenderPassSetVertexBuffer"));
    }

    [Fact]
    public void AStaleHandleIsCaughtWhereItWasUsed() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);
        var buffer = device.CreateBuffer(new(64, BufferUsage.Vertex, Name: "Mesh"));
        device.Destroy(buffer);

        using var list = device.BeginCommandList();
        list.BeginRenderPass(Pass(view));

        Assert.Throws<ArgumentException>(() => list.BindVertexBuffer(0, buffer));
    }

    // ── Replay ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A frame's calls, in the order WebGPU would have seen them.</summary>
    [Fact]
    public void AFrameReplaysInOrder() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);
        var buffer = device.CreateBuffer(new(64, BufferUsage.Vertex, Name: "Mesh"));
        binding.Clear();

        using var list = device.BeginCommandList(QueueKind.Graphics, "Main");
        list.BeginRenderPass(Pass(view, "Opaque"));
        list.SetViewport(new Viewport(0, 0, 64, 64));
        list.SetScissor(new(0, 0, 64, 64));
        list.BindVertexBuffer(0, buffer);
        list.Draw(3);
        list.EndRenderPass();
        list.Finish();

        device.GraphicsQueue.Submit([list]);

        Assert.Equal(
            [
                "CreateCommandEncoder",
                "BeginRenderPass",
                "RenderPassSetViewport",
                "RenderPassSetScissorRect",
                "RenderPassSetVertexBuffer",
                "RenderPassDraw",
                "EndRenderPass",
                "FinishCommandEncoder",
                "Submit",
                "Release"
            ],
            binding.Names()
        );
    }

    /// <summary>
    ///     The RHI has no compute pass — a dispatch simply happens between render passes — so replay
    ///     opens one and closes it the moment something that cannot be inside one arrives.
    /// </summary>
    [Fact]
    public void ReplayOpensAComputePassAroundDispatches() {
        using var device = new WebGpuDevice(binding);
        var source = device.CreateBuffer(new(64, BufferUsage.CopySource, Name: "From"));
        var destination = device.CreateBuffer(new(64, BufferUsage.CopyDestination, Name: "To"));
        binding.Clear();

        using var list = device.BeginCommandList(QueueKind.Compute);
        list.Dispatch(4, 2, 1);
        list.Dispatch(1);
        list.CopyBuffer(source, 0, destination, 0, 16);
        list.Finish();

        device.ComputeQueue.Submit([list]);

        Assert.Equal(
            [
                "CreateCommandEncoder",
                "BeginComputePass",
                "ComputePassDispatch",
                "ComputePassDispatch",
                "EndComputePass",
                "CopyBufferToBuffer",
                "FinishCommandEncoder",
                "Submit",
                "Release"
            ],
            binding.Names()
        );
    }

    /// <summary>
    ///     A compute pass replay opened is one replay closes, even when the list never says so.
    /// </summary>
    [Fact]
    public void AComputePassIsClosedAtTheEndOfTheBatch() {
        using var device = new WebGpuDevice(binding);
        binding.Clear();

        using var list = device.BeginCommandList(QueueKind.Compute);
        list.Dispatch(1);
        list.Finish();

        device.ComputeQueue.Submit([list]);

        Assert.Contains("EndComputePass", binding.Names());
    }

    /// <summary>One encoder for the whole batch, because a submission is the expensive part.</summary>
    [Fact]
    public void OneBatchIsOneEncoderAndOneSubmit() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);
        binding.Clear();

        using var first = device.BeginCommandList(QueueKind.Graphics, "Shadow");
        first.BeginRenderPass(Pass(view, "Shadow"));
        first.EndRenderPass();
        first.Finish();

        using var second = device.BeginCommandList(QueueKind.Graphics, "Opaque");
        second.BeginRenderPass(Pass(view, "Opaque"));
        second.EndRenderPass();
        second.Finish();

        device.GraphicsQueue.Submit([first, second]);

        Assert.Single(binding.OfName("CreateCommandEncoder"));
        Assert.Single(binding.OfName("Submit"));
        Assert.Equal(2, binding.OfName("BeginRenderPass").Count);
    }

    /// <summary>
    ///     Barriers are validated for shape and dropped: WebGPU tracks resource state itself, so
    ///     there is no call to make.
    /// </summary>
    [Fact]
    public void BarriersAreDroppedRatherThanTranslated() {
        using var device = new WebGpuDevice(binding);
        var buffer = device.CreateBuffer(new(64, BufferUsage.Storage, Name: "Particles"));
        binding.Clear();

        using var list = device.BeginCommandList();

        list.Barrier(
            new([new(buffer, ResourceState.ShaderWrite, ResourceState.VertexInput)], [])
        );

        list.Finish();
        device.GraphicsQueue.Submit([list]);

        Assert.Equal(["CreateCommandEncoder", "FinishCommandEncoder", "Submit", "Release"], binding.Names());
    }

    // ── Push constants ──────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Each PushConstants takes its own slot of the ring and binds it, so two draws in one frame
    ///     read their own values rather than the last one's.
    /// </summary>
    [Fact]
    public void EachPushConstantWriteTakesItsOwnSlot() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);
        var pipeline = PipelineWithPushConstants(device);
        binding.Clear();

        using var list = device.BeginCommandList();
        list.BeginRenderPass(Pass(view));
        list.BindPipeline(pipeline);
        list.PushConstants(ShaderStage.Vertex, 0, [1, 0, 0, 0]);
        list.Draw(3);
        list.PushConstants(ShaderStage.Vertex, 0, [2, 0, 0, 0]);
        list.Draw(3);
        list.EndRenderPass();
        list.Finish();

        device.GraphicsQueue.Submit([list]);

        var binds = binding.OfName("RenderPassSetBindGroup");
        Assert.Equal(2, binds.Count);

        // Group 1: one caller set, then the emulated push-constant group after it.
        Assert.Equal(1, binds[0].Values[0]);
        Assert.Equal(1, binds[1].Values[0]);

        // Different slots of the ring, which is the whole point.
        Assert.NotEqual(binds[0].Values[2], binds[1].Values[2]);
        Assert.Equal(0, binds[0].Values[2]);
        Assert.Equal(WebGpuLimits.Guaranteed.MinUniformBufferOffsetAlignment, binds[1].Values[2]);
    }

    /// <summary>
    ///     A partial write keeps the rest of the block, which is what push constants do on a real API
    ///     and is not what a uniform buffer would do on its own.
    /// </summary>
    [Fact]
    public void APartialPushConstantWriteKeepsTheRestOfTheBlock() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);
        var pipeline = PipelineWithPushConstants(device);

        using var list = device.BeginCommandList();
        list.BeginRenderPass(Pass(view));
        list.BindPipeline(pipeline);
        list.PushConstants(ShaderStage.Vertex, 0, [1, 2, 3, 4]);
        list.PushConstants(ShaderStage.Vertex, 4, [5, 6, 7, 8]);
        list.Draw(3);
        list.EndRenderPass();
        list.Finish();

        binding.Clear();
        device.GraphicsQueue.Submit([list]);

        Assert.Equal(WebGpuCapabilities.PushConstantSize, binding.LastWrite.Length);
        Assert.Equal<byte[]>([1, 2, 3, 4, 5, 6, 7, 8], binding.LastWrite[..8]);
    }

    [Fact]
    public void PushConstantsWithNoPipelineBoundSayWhy() {
        using var device = new WebGpuDevice(binding);
        var view = View(device);

        using var list = device.BeginCommandList(QueueKind.Graphics, "Main");
        list.BeginRenderPass(Pass(view));
        list.PushConstants(ShaderStage.Vertex, 0, [1, 2, 3, 4]);
        list.EndRenderPass();
        list.Finish();

        var thrown = Assert.Throws<InvalidOperationException>(() => device.GraphicsQueue.Submit([list]));
        Assert.Contains("no pipeline bound", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APushConstantWritePastTheBlockIsRefused() {
        using var device = new WebGpuDevice(binding);

        using var list = device.BeginCommandList();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => list.PushConstants(ShaderStage.Vertex, 120, new byte[16])
        );
    }

    // ── Copies ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     bytesPerRow is a multiple of 256 on every implementation, and callers do not know that —
    ///     getting it wrong shears the image diagonally.
    /// </summary>
    [Fact]
    public void ATextureUploadPadsItsRowsTo256() {
        using var device = new WebGpuDevice(binding);
        var source = device.CreateBuffer(new(65536, BufferUsage.CopySource, MemoryAccess.HostUpload, "Staging"));
        var texture = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 17, 5, TextureUsage.CopyDestination, Name: "Odd")
        );

        binding.Clear();

        using var list = device.BeginCommandList();
        list.CopyBufferToTexture(source, 0, new(texture), new(17, 5, 1));
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        var call = Assert.Single(binding.OfName("CopyBufferToTexture"));

        // 17 pixels × 4 bytes is 68, rounded up to 256.
        Assert.Equal(256, call.Values[2]);
        Assert.Equal(5, call.Values[3]);
    }

    [Fact]
    public void ACompressedTextureCountsBlocksAndNotPixels() {
        using var device = new WebGpuDevice(binding);
        var source = device.CreateBuffer(new(65536, BufferUsage.CopySource, MemoryAccess.HostUpload, "Staging"));
        var texture = device.CreateTexture(
            new(PixelFormat.Bc7RgbaUNorm, 64, 64, TextureUsage.CopyDestination, Name: "Albedo")
        );

        binding.Clear();

        using var list = device.BeginCommandList();
        list.CopyBufferToTexture(source, 0, new(texture), new(64, 64, 1));
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        var call = Assert.Single(binding.OfName("CopyBufferToTexture"));

        // 16 blocks of 16 bytes is 256, which needs no padding — and 16 rows of blocks, not 64.
        Assert.Equal(256, call.Values[2]);
        Assert.Equal(16, call.Values[3]);
    }

    /// <summary>
    ///     depth24plus has no defined byte layout, so a copy of one is refused with the reason.
    /// </summary>
    [Fact]
    public void CopyingDepth24PlusIsRefused() {
        using var device = new WebGpuDevice(binding);
        var destination = device.CreateBuffer(new(65536, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "Readback"));
        var texture = device.CreateTexture(
            new(PixelFormat.Depth24UNormStencil8, 64, 64, TextureUsage.CopySource, Name: "Depth")
        );

        using var list = device.BeginCommandList();

        var thrown = Assert.Throws<NotSupportedException>(
            () => list.CopyTextureToBuffer(new(texture), new(64, 64, 1), destination, 0)
        );

        Assert.Contains("Depth32Float", thrown.Message, StringComparison.Ordinal);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────

    static TextureViewHandle View(WebGpuDevice device) {
        var texture = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.ColourTarget, Name: "Target")
        );

        return device.CreateTextureView(texture);
    }

    /// <summary>
    ///     A one-attachment pass. The attachments are an array rather than a collection expression:
    ///     RenderPassDescription is a ref struct over a span, and a stack-allocated span cannot
    ///     outlive the method that made it.
    /// </summary>
    static RenderPassDescription Pass(TextureViewHandle view, string name = "Pass") =>
        new(new ColourAttachment[] { new(view) }, null, name);

    /// <summary>A pipeline whose layout declares push constants, so replay has a group to bind.</summary>
    static PipelineHandle PipelineWithPushConstants(WebGpuDevice device) {
        var set = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerFrame, [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex)], "Frame")
        );

        var layout = device.CreatePipelineLayout(new([set], [new(ShaderStage.Vertex, 0, 64)], "Layout"));
        var vertex = device.CreateShader(ShaderStage.Vertex, "@vertex fn main() {}"u8, "Vertex");
        var fragment = device.CreateShader(ShaderStage.Fragment, "@fragment fn main() {}"u8, "Fragment");

        return device.CreateGraphicsPipeline(
            new(
                vertex,
                fragment,
                layout,
                [new(PixelFormat.Rgba8UNorm)],
                DepthStencil: DepthStencilState.Disabled,
                Name: "Pipeline"
            )
        );
    }
}
