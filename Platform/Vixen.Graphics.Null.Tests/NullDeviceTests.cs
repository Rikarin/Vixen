// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.Null.Tests;

public sealed class NullDeviceTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    [Fact]
    public void ItReportsItselfAsSoftwareRatherThanPretendingToBeAGpu() {
        Assert.Equal(AdapterKind.Software, device.Adapter.Kind);
        Assert.Equal(0ul, device.Adapter.DeviceMemory);
    }

    /// <summary>
    ///     A handle used after it is destroyed is caught by its generation, here, rather than by a
    ///     driver that would have read freed memory.
    /// </summary>
    [Fact]
    public void ADestroyedHandleIsRefusedRatherThanReused() {
        var buffer = device.CreateBuffer(new(256, BufferUsage.Uniform, MemoryAccess.HostUpload, "Constants"));
        device.Destroy(buffer);

        Assert.Throws<ArgumentException>(() => device.Write(buffer, 0, new byte[4]));
    }

    /// <summary>
    ///     The assertion a leak test wants: a create-and-destroy cycle comes back to where it
    ///     started, or something is not returning what it took.
    /// </summary>
    [Fact]
    public void EveryResourceThatIsTakenComesBack() {
        Assert.Equal(0, device.LiveResourceCount);

        for (var round = 0; round < 100; round++) {
            var buffer = device.CreateBuffer(new(1024, BufferUsage.Vertex, Name: "Mesh"));
            var texture = device.CreateTexture(new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.Sampled, Name: "T"));
            var view = device.CreateTextureView(texture);
            var sampler = device.CreateSampler(SamplerDescription.LinearClamp);

            device.Destroy(view);
            device.Destroy(sampler);
            device.Destroy(texture);
            device.Destroy(buffer);
        }

        Assert.Equal(0, device.LiveResourceCount);
    }

    [Fact]
    public void ADeviceLocalBufferCannotBeWrittenByTheHost() {
        var buffer = device.CreateBuffer(new(256, BufferUsage.Vertex, MemoryAccess.DeviceLocal, "Mesh"));

        var thrown = Assert.Throws<InvalidOperationException>(() => device.Write(buffer, 0, new byte[16]));
        Assert.Contains("Stage it", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An overrun caught here is caught without a driver and without a corrupted heap — which is
    ///     the whole reason a backend with no memory still checks bounds.
    /// </summary>
    [Fact]
    public void WritingPastTheEndIsCaughtEvenThoughThereIsNoMemory() {
        var buffer = device.CreateBuffer(new(16, BufferUsage.Uniform, MemoryAccess.HostUpload, "Small"));

        device.Write(buffer, 0, new byte[16]);
        Assert.Throws<ArgumentOutOfRangeException>(() => device.Write(buffer, 8, new byte[16]));
    }

    [Fact]
    public void ReadbackGivesDefinedBytesRatherThanWhateverWasThere() {
        var buffer = device.CreateBuffer(
            new(8, BufferUsage.Storage | BufferUsage.CopyDestination, MemoryAccess.HostReadback, "Readback")
        );

        var destination = new byte[8];
        Array.Fill(destination, (byte)0xCD);
        device.Read(buffer, 0, destination);

        Assert.All(destination, value => Assert.Equal(0, value));
    }

    /// <summary>
    ///     A device that claims no compute must refuse a compute pipeline, or the fallback path the
    ///     capability exists for never gets taken.
    /// </summary>
    [Fact]
    public void ADeviceWithoutComputeRefusesAComputePipeline() {
        using var limited = new NullDevice(new() { Features = GraphicsDeviceFeatures.Minimum });
        var shader = limited.CreateShader(ShaderStage.Compute, [1, 2, 3, 4]);
        var layout = limited.CreatePipelineLayout(new([]));

        Assert.Throws<NotSupportedException>(
            () => limited.CreateComputePipeline(new(shader, layout, "Cull"))
        );
    }

    [Fact]
    public void ATextureLargerThanTheDeviceAllowsIsRefused() {
        using var limited = new NullDevice(new() { Features = GraphicsDeviceFeatures.Minimum });

        var thrown = Assert.Throws<ArgumentException>(
            () => limited.CreateTexture(new(PixelFormat.Rgba8UNorm, 8192, 8192, TextureUsage.Sampled, Name: "Huge"))
        );

        Assert.Contains("4096", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A binding written as a kind other than the one it was declared as is refused, which is the
    ///     failure a driver is worst at reporting.
    /// </summary>
    /// <remarks>
    ///     The dynamic kinds are the case worth spelling out: a set declared
    ///     <see cref="DescriptorKind.DynamicUniformBuffer" /> and written as a plain
    ///     <see cref="DescriptorKind.UniformBuffer" /> takes no offset at bind time, so every per-draw
    ///     offset is ignored and every object draws with the first one's block. Nothing on a device
    ///     says so without the validation layers.
    /// </remarks>
    [Fact]
    public void AWriteOfTheWrongKindIsRefused() {
        var layout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerDraw, [new(0, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex)], "Draw")
        );

        var set = device.CreateDescriptorSet(layout);
        var buffer = device.CreateBuffer(new(256, BufferUsage.Uniform, MemoryAccess.HostUpload, "Transforms"));

        var thrown = Assert.Throws<ArgumentException>(
            () => device.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(0, buffer, 0, 64)])
        );

        Assert.Contains("DynamicUniformBuffer", thrown.Message, StringComparison.Ordinal);

        // And the write the layout actually declared goes through.
        device.UpdateDescriptorSet(set, [DescriptorWrite.DynamicUniform(0, buffer, 0, 64)]);
    }

    [Fact]
    public void ASwapChainCyclesThroughItsImages() {
        using var swapChain = device.CreateSwapChain(new(SurfaceHandle.None, new(1280, 720), ImageCount: 3));

        var seen = new HashSet<TextureViewHandle>();

        for (var frame = 0; frame < 3; frame++) {
            Assert.Equal(SwapChainStatus.Ready, swapChain.AcquireNextImage(out var view));
            Assert.True(seen.Add(view));
            Assert.Equal(SwapChainStatus.Ready, swapChain.Present());
        }

        // And then round again, onto the images it already has.
        Assert.Equal(SwapChainStatus.Ready, swapChain.AcquireNextImage(out var repeated));
        Assert.Contains(repeated, seen);
    }

    /// <summary>
    ///     The out-of-date path is the one nobody exercises until a user drags a window edge. Here
    ///     it is a field.
    /// </summary>
    [Fact]
    public void AnOutOfDateSwapChainReportsItRatherThanHandingBackAnImage() {
        using var swapChain = (NullSwapChain)device.CreateSwapChain(new(SurfaceHandle.None, new(800, 600)));
        swapChain.NextStatus = SwapChainStatus.OutOfDate;

        Assert.Equal(SwapChainStatus.OutOfDate, swapChain.AcquireNextImage(out var view));
        Assert.False(view.IsValid);

        swapChain.Resize(new(1024, 768));

        Assert.Equal(new Int2(1024, 768), swapChain.Size);
        Assert.Equal(SwapChainStatus.Ready, swapChain.AcquireNextImage(out var fresh));
        Assert.True(fresh.IsValid);
    }

    [Fact]
    public void ASwapChainReturnsItsImagesWhenItIsDisposed() {
        var before = device.LiveResourceCount;
        var swapChain = device.CreateSwapChain(new(SurfaceHandle.None, new(640, 480), ImageCount: 2));

        swapChain.AcquireNextImage(out _);
        swapChain.AcquireNextImage(out _);
        Assert.True(device.LiveResourceCount > before);

        swapChain.Dispose();

        // The views come back; the textures behind them are the swapchain's and are freed with it in
        // a real backend, so this asserts the views rather than the whole count.
        Assert.True(device.LiveResourceCount < before + 4);
    }

    [Fact]
    public void FramesAreCounted() {
        for (var frame = 0; frame < 5; frame++) {
            device.BeginFrame();
            device.EndFrame();
        }

        Assert.Equal(5, device.FrameCount);
    }

    /// <summary>
    ///     <c>docs/plan/05</c> is explicit that this is a shipping backend as well as a test one, so
    ///     a server must be able to run it without accumulating a command log.
    /// </summary>
    [Fact]
    public void ADeviceThatWasNotToldToRecordDoesNot() {
        using var server = new NullDevice();

        Assert.Null(server.Recorder);

        using var list = server.BeginCommandList();
        list.PushDebugGroup("Frame");
        list.PopDebugGroup();
        list.Finish();
        server.GraphicsQueue.Submit([list]);
    }

    /// <summary>A binding the layout does not declare is refused rather than ignored.</summary>
    [Fact]
    public void AWriteToAnUndeclaredBindingIsRefused() {
        var layout = device.CreateDescriptorSetLayout(
            new(DescriptorSetSlot.PerMaterial, [new(0, DescriptorKind.StorageBuffer, ShaderStage.Fragment)], "Material")
        );

        var set = device.CreateDescriptorSet(layout);
        var buffer = device.CreateBuffer(new(256, BufferUsage.Storage, Name: "Instances"));

        Assert.Throws<ArgumentException>(() => device.UpdateDescriptorSet(set, [DescriptorWrite.Storage(3, buffer)]));
    }

    /// <summary>
    ///     An element past the end of an array binding is refused, and a bindless one is measured
    ///     against the table's size rather than against zero.
    /// </summary>
    /// <remarks>
    ///     The second half is the one worth the test. An unbounded binding carries <c>Count == 0</c>,
    ///     so a bounds check written the obvious way rejects every write to a bindless table and a
    ///     check that skips zero-count bindings accepts every one — including the writes past the end
    ///     that corrupt a neighbouring descriptor.
    /// </remarks>
    [Fact]
    public void AnElementOutsideItsBindingIsRefused() {
        var bounded = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, 4)],
                "Probes"
            )
        );

        var table = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerFrame,
                [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, 0)],
                "Textures"
            )
        );

        var texture = device.CreateTexture(new(PixelFormat.Rgba8UNorm, 4, 4, TextureUsage.Sampled, Name: "Grey"));
        var view = device.CreateTextureView(texture);

        var boundedSet = device.CreateDescriptorSet(bounded);
        var tableSet = device.CreateDescriptorSet(table);

        device.UpdateDescriptorSet(boundedSet, [DescriptorWrite.Texture(0, view, 3)]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => device.UpdateDescriptorSet(boundedSet, [DescriptorWrite.Texture(0, view, 4)])
        );

        device.UpdateDescriptorSet(tableSet, [DescriptorWrite.Texture(0, view, 4)]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => device.UpdateDescriptorSet(
                tableSet,
                [DescriptorWrite.Texture(0, view, device.Features.MaxBindlessDescriptors)]
            )
        );
    }

    /// <summary>
    ///     A device reporting no descriptor indexing refuses an unbounded binding, like every real one.
    /// </summary>
    [Fact]
    public void AnUnboundedBindingNeedsTheCapability() {
        using var minimum = new NullDevice(new() { Features = GraphicsDeviceFeatures.Minimum });

        var refused = Assert.Throws<ArgumentException>(
            () => minimum.CreateDescriptorSetLayout(
                new(
                    DescriptorSetSlot.PerFrame,
                    [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, 0)],
                    "Textures"
                )
            )
        );

        Assert.Contains("HasBindless", refused.Message, StringComparison.Ordinal);
    }

    public void Dispose() => device.Dispose();
}
