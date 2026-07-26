// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     The device, against whatever driver is on the machine. Skipped where there is none, so a
///     machine without a Vulkan SDK still runs the rest of the suite rather than failing it.
/// </summary>
/// <remarks>
///     Offscreen throughout — no surface, no swapchain. That is not a limitation of the tests: it is
///     the dedicated server's path and the golden-image suite's path
///     ([10](../../docs/plan/10-platforms.md)), so exercising it here means the path that has no
///     window is the one under test constantly rather than the special case that rots.
/// </remarks>
[Collection("Vulkan")]
public sealed class VulkanDeviceTests {
    static bool TryOpen(out VulkanDevice? device, out string? reason) =>
        VulkanDevice.TryCreate(new(), out device, out reason);

    static bool TryOpen(bool renderPassObjects, out VulkanDevice? device, out string? reason) =>
        VulkanDevice.TryCreate(
            new() { PreferRenderPassObjects = renderPassObjects },
            out device,
            out reason
        );

    [Fact]
    public void ADeviceIsCreated() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        Assert.NotEmpty(owned.Adapter.Name);
        Assert.Equal(2, owned.FramesInFlight);
    }

    /// <summary>
    ///     Every device Vixen will run on meets the floor, and the RHI's own contract says a
    ///     capability is absent unless proven present — so these are the invariants a translation bug
    ///     would break.
    /// </summary>
    [Fact]
    public void ReportedFeaturesAreInternallyConsistent() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;
        var features = owned.Features;

        Assert.True(features.HasCompute, "Vulkan has no device without compute.");
        Assert.True(features.MaxTextureSize >= 4096, "The floor in docs/plan/05 is 4096.");
        Assert.True(features.MaxDescriptorSets >= 4, "The engine's four-set convention is the floor.");
        Assert.True(features.MaxPushConstantSize >= 128);
        Assert.True(features.SupportsSampleCount(1), "Every device supports one sample.");
        Assert.True(features.MaxAnisotropy >= 1f);

        if (!features.HasAnisotropicFiltering) {
            Assert.Equal(1f, features.MaxAnisotropy);
        }
    }

    [Fact]
    public void TheThreeQueuesExist() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        Assert.NotNull(owned.GraphicsQueue);
        Assert.NotNull(owned.ComputeQueue);
        Assert.NotNull(owned.TransferQueue);
        Assert.Equal(QueueKind.Graphics, owned.GraphicsQueue.Kind);
    }

    [Fact]
    public void BuffersAndTexturesAreCreatedAndDestroyed() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var buffer = owned.CreateBuffer(new(1024, BufferUsage.Vertex, Name: "vertices"));
        Assert.True(buffer.IsValid);

        var texture = owned.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 64, 64, TextureUsage.Sampled | TextureUsage.CopyDestination, Name: "albedo")
        );

        Assert.True(texture.IsValid);

        var view = owned.CreateTextureView(texture);
        Assert.True(view.IsValid);

        var sampler = owned.CreateSampler(SamplerDescription.LinearClamp);
        Assert.True(sampler.IsValid);

        owned.Destroy(view);
        owned.Destroy(texture);
        owned.Destroy(buffer);
        owned.Destroy(sampler);
    }

    /// <summary>
    ///     The end-to-end test: write from the CPU, copy on the GPU, read back, and get the same
    ///     bytes. It exercises the allocator, memory-type selection, persistent mapping, command-pool
    ///     management, recording, submission, the frame fences and deferred destruction — and unlike
    ///     any of those individually, it can only pass if the whole chain is right.
    /// </summary>
    [Fact]
    public void DataSurvivesARoundTripThroughTheGpu() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        const int Size = 4096;
        var source = new byte[Size];

        for (var index = 0; index < Size; index++) {
            source[index] = (byte)(index * 31 % 251);
        }

        var upload = owned.CreateBuffer(
            new(Size, BufferUsage.CopySource, MemoryAccess.HostUpload, "upload")
        );

        var device_local = owned.CreateBuffer(
            new(Size, BufferUsage.CopySource | BufferUsage.CopyDestination, MemoryAccess.DeviceLocal, "gpu")
        );

        var readback = owned.CreateBuffer(
            new(Size, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "readback")
        );

        owned.Write(upload, 0, source);

        owned.BeginFrame();

        using (var list = owned.BeginCommandList(QueueKind.Transfer, "round trip")) {
            list.CopyBuffer(upload, 0, device_local, 0, Size);

            list.Barrier(new(
                [new(device_local, ResourceState.CopyDestination, ResourceState.CopySource)],
                []
            ));

            list.CopyBuffer(device_local, 0, readback, 0, Size);
            list.Finish();
            owned.TransferQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        var destination = new byte[Size];
        owned.Read(readback, 0, destination);

        Assert.Equal(source, destination);

        owned.Destroy(upload);
        owned.Destroy(device_local);
        owned.Destroy(readback);
    }

    /// <summary>
    ///     A device-local buffer is not writable from the CPU, and saying so is worth more than
    ///     letting the write silently go nowhere — which is what an unmapped pointer would do.
    /// </summary>
    [Fact]
    public void WritingToDeviceLocalMemoryIsRefused() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var buffer = owned.CreateBuffer(new(256, BufferUsage.Storage, Name: "device-local"));
        var thrown = Assert.Throws<InvalidOperationException>(() => owned.Write(buffer, 0, new byte[16]));

        Assert.Contains("device-local", thrown.Message);
        owned.Destroy(buffer);
    }

    /// <summary>
    ///     Reading from upload memory is legal Vulkan and, on write-combined memory, roughly an order
    ///     of magnitude slower than reading from cached memory. The RHI declines it rather than
    ///     letting a golden-image comparison quietly become the slowest thing in the suite.
    /// </summary>
    [Fact]
    public void ReadingFromUploadMemoryIsRefused() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var buffer = owned.CreateBuffer(
            new(256, BufferUsage.CopySource, MemoryAccess.HostUpload, "upload")
        );

        Assert.Throws<InvalidOperationException>(() => owned.Read(buffer, 0, new byte[16]));
        owned.Destroy(buffer);
    }

    [Fact]
    public void WritingPastTheEndOfABufferIsRefused() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var buffer = owned.CreateBuffer(
            new(64, BufferUsage.CopySource, MemoryAccess.HostUpload, "small")
        );

        Assert.Throws<ArgumentOutOfRangeException>(() => owned.Write(buffer, 32, new byte[64]));
        owned.Destroy(buffer);
    }

    /// <summary>
    ///     A destroyed handle must not resolve. This is what the generation counter in
    ///     <c>Handle&lt;T&gt;</c> exists for, and without it a stale handle silently addresses whatever
    ///     resource took the slot next.
    /// </summary>
    [Fact]
    public void AStaleHandleDoesNotResolve() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var buffer = owned.CreateBuffer(new(64, BufferUsage.Storage, Name: "doomed"));
        owned.Destroy(buffer);

        Assert.Throws<ArgumentException>(() => owned.Write(buffer, 0, new byte[8]));
    }

    /// <summary>
    ///     Drawing outside a render pass is undefined behaviour in Vulkan rather than an error, and
    ///     its diagnosis arrives as a crash inside the driver.
    /// </summary>
    [Fact]
    public void DrawingOutsideARenderPassIsRefused() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        owned.BeginFrame();
        using var list = owned.BeginCommandList(QueueKind.Graphics, "no pass");

        Assert.Throws<InvalidOperationException>(() => list.Draw(3));

        list.Finish();
        owned.EndFrame();
        owned.WaitIdle();
    }

    [Fact]
    public void RecordingAfterFinishIsRefused() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        owned.BeginFrame();
        using var list = owned.BeginCommandList(QueueKind.Graphics, "finished");
        list.Finish();

        Assert.Throws<InvalidOperationException>(() => list.SetScissor(new(0, 0, 1, 1)));

        owned.EndFrame();
        owned.WaitIdle();
    }

    /// <summary>
    ///     Submitting a list that was never finished would have the driver read a buffer that is still
    ///     being written.
    /// </summary>
    [Fact]
    public void SubmittingAnUnfinishedListIsRefused() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        owned.BeginFrame();
        using var list = owned.BeginCommandList(QueueKind.Graphics, "unfinished");

        Assert.Throws<InvalidOperationException>(() => owned.GraphicsQueue.Submit([list]));

        list.Finish();
        owned.EndFrame();
        owned.WaitIdle();
    }

    /// <summary>
    ///     Several frames in sequence, which is what exercises the fence-per-frame-slot accounting:
    ///     a fence signalled twice without a reset, or waited on before it was ever signalled, hangs
    ///     rather than fails.
    /// </summary>
    [Fact]
    public void ManyFramesRunWithoutHanging() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var buffer = owned.CreateBuffer(
            new(256, BufferUsage.CopySource | BufferUsage.CopyDestination, Name: "per-frame")
        );

        for (var index = 0; index < 8; index++) {
            owned.BeginFrame();

            using (var list = owned.BeginCommandList(QueueKind.Graphics, $"frame {index}")) {
                list.CopyBuffer(buffer, 0, buffer, 128, 128);
                list.Finish();
                owned.GraphicsQueue.Submit([list]);
            }

            owned.EndFrame();
        }

        owned.WaitIdle();
        owned.Destroy(buffer);
    }

    /// <summary>
    ///     Recording on several threads at once, which is the contract's whole point: one list per
    ///     thread, one command pool per thread and frame, no two threads touching the same pool.
    /// </summary>
    [Fact]
    public void ListsRecordOnManyThreadsAtOnce() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var buffer = owned.CreateBuffer(
            new(1024, BufferUsage.CopySource | BufferUsage.CopyDestination, Name: "shared")
        );

        owned.BeginFrame();
        var lists = new ICommandList[4];

        Parallel.For(0, lists.Length, index => {
            var list = owned.BeginCommandList(QueueKind.Graphics, $"thread {index}");
            list.CopyBuffer(buffer, 0, buffer, 512, 256);
            list.Finish();
            lists[index] = list;
        });

        owned.GraphicsQueue.Submit(lists);
        owned.EndFrame();
        owned.WaitIdle();

        foreach (var list in lists) {
            list.Dispose();
        }

        owned.Destroy(buffer);
    }

    /// <summary>
    ///     A texture rendered into offscreen and read back, through <em>both</em> pass paths: dynamic
    ///     rendering, and the <c>VkRenderPass</c> fallback that Android's Vulkan 1.1 devices need
    ///     ([10](../../docs/plan/10-platforms.md)). Running the fallback on hardware that does not
    ///     require it is the whole point — a path that only runs on hardware nobody owns is a path
    ///     that is already broken.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClearedRenderPassProducesTheClearColour(bool renderPassObjects) {
        VulkanRequirement.Available(TryOpen(renderPassObjects, out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        Assert.SkipWhen(
            renderPassObjects && owned.UsesDynamicRendering,
            "the device refused to take the render-pass-object path"
        );

        const int Side = 16;
        const int Bytes = Side * Side * 4;

        var target = owned.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            Side,
            Side,
            TextureUsage.ColourTarget | TextureUsage.CopySource,
            Name: "clear target"
        ));

        var view = owned.CreateTextureView(target);

        var readback = owned.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "clear readback")
        );

        owned.BeginFrame();

        using (var list = owned.BeginCommandList(QueueKind.Graphics, "clear")) {
            list.Barrier(new([], [new(target, ResourceState.Undefined, ResourceState.ColourTarget)]));

            list.BeginRenderPass(new(
                [new(view, LoadAction.Clear, StoreAction.Store, new(0.25f, 0.5f, 0.75f, 1f))],
                name: "clear pass"
            ));

            list.EndRenderPass();

            list.Barrier(new([], [new(target, ResourceState.ColourTarget, ResourceState.CopySource)]));
            list.CopyTextureToBuffer(new(target), new(Side, Side, 1), readback, 0);
            list.Finish();
            owned.GraphicsQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        var pixels = new byte[Bytes];
        owned.Read(readback, 0, pixels);

        // UNorm8 of 0.25, 0.5, 0.75, 1.0. A byte either side, because a driver is free to round.
        Assert.InRange(pixels[0], 62, 66);
        Assert.InRange(pixels[1], 126, 130);
        Assert.InRange(pixels[2], 189, 193);
        Assert.Equal(255, pixels[3]);

        // Every texel, not just the first: a clear that only wrote the first row would pass a
        // one-pixel check and is exactly the kind of thing a render area computed wrongly produces.
        for (var index = 0; index < Bytes; index += 4) {
            Assert.InRange(pixels[index], 62, 66);
            Assert.Equal(255, pixels[index + 3]);
        }

        owned.Destroy(view);
        owned.Destroy(target);
        owned.Destroy(readback);
    }

    /// <summary>Descriptor sets, layouts and the type check that Vulkan itself does not do.</summary>
    [Fact]
    public void DescriptorSetsAreAllocatedAndWritten() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var layout = owned.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerFrame,
            [new(0, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment)],
            "per frame"
        ));

        var set = owned.CreateDescriptorSet(layout, "per frame set");
        var uniform = owned.CreateBuffer(new(256, BufferUsage.Uniform, MemoryAccess.HostUpload, "uniforms"));

        owned.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(0, uniform)]);

        // Declared as a uniform buffer, written as a storage buffer. Vulkan does not check this and
        // the shader reads whichever it was compiled for, so the result would be silently wrong.
        Assert.Throws<ArgumentException>(
            () => owned.UpdateDescriptorSet(set, [DescriptorWrite.Storage(0, uniform)])
        );

        // A binding the layout never declared cannot be written either.
        Assert.Throws<ArgumentException>(
            () => owned.UpdateDescriptorSet(set, [DescriptorWrite.Uniform(7, uniform)])
        );

        owned.Destroy(set);
        owned.Destroy(uniform);
        owned.Destroy(layout);
    }

    /// <summary>
    ///     A great many resources, which is what the block allocator exists for: one
    ///     <c>vkAllocateMemory</c> per resource hits <c>maxMemoryAllocationCount</c> — 4096 on a great
    ///     many drivers — and a scene with more textures than that simply cannot be loaded.
    /// </summary>
    [Fact]
    public void ThousandsOfBuffersDoNotExhaustTheAllocationCount() {
        VulkanRequirement.Available(TryOpen(out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var handles = new BufferHandle[5000];

        for (var index = 0; index < handles.Length; index++) {
            handles[index] = owned.CreateBuffer(new(1024, BufferUsage.Storage, Name: ""));
        }

        Assert.True(
            owned.Allocator.LiveBlockCount < 64,
            $"{handles.Length} buffers took {owned.Allocator.LiveBlockCount} device allocations; the "
            + "whole point of suballocation is that this number stays in the dozens."
        );

        foreach (var handle in handles) {
            owned.Destroy(handle);
        }
    }
}
