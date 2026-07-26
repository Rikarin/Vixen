// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     Compute, and with it the only end-to-end proof that a descriptor set the RHI built is the one
///     a shader actually reads.
/// </summary>
/// <remarks>
///     A descriptor written to the wrong binding, a set bound to the wrong slot, a pipeline layout
///     whose sets are in the wrong order — none of these fail, and none of them are visible from the
///     API. Running a shader that reads through the descriptor and checking its arithmetic is what
///     makes them visible.
/// </remarks>
[Collection("Vulkan")]
public sealed class VulkanComputeTests {
    const int Elements = 256;
    const int Bytes = Elements * sizeof(uint);

    [Fact]
    public void AComputeShaderReadsAndWritesThroughItsDescriptorSet() {
        Assert.SkipUnless(VulkanDevice.TryCreate(new(), out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;
        VulkanDiagnostics.Reset();

        var shader = owned.CreateShader(ShaderStage.Compute, TestShaders.Compute, "multiply");

        var setLayout = owned.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerFrame,
            [new(0, DescriptorKind.StorageBuffer, ShaderStage.Compute)],
            "data"
        ));

        var layout = owned.CreatePipelineLayout(new(
            [setLayout],
            [new(ShaderStage.Compute, 0, sizeof(uint))],
            "multiply layout"
        ));

        var pipeline = owned.CreateComputePipeline(new(shader, layout, "multiply"));

        // Host-visible storage, so the same buffer is both the input and the readback. Legal, and it
        // keeps the test about compute rather than about staging — which the round-trip test already
        // covers.
        var data = owned.CreateBuffer(new(
            Bytes,
            BufferUsage.Storage | BufferUsage.CopySource | BufferUsage.CopyDestination,
            MemoryAccess.HostUpload,
            "data"
        ));

        var readback = owned.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "readback")
        );

        var set = owned.CreateDescriptorSet(setLayout, "data set");
        owned.UpdateDescriptorSet(set, [DescriptorWrite.Storage(0, data)]);

        var source = new uint[Elements];

        for (var index = 0; index < Elements; index++) {
            source[index] = (uint)(index + 1);
        }

        owned.Write(data, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(source.AsSpan()));

        const uint Multiplier = 7;
        owned.BeginFrame();

        using (var list = owned.BeginCommandList(QueueKind.Compute, "multiply")) {
            list.Barrier(new([new(data, ResourceState.HostAccess, ResourceState.ShaderWrite)], []));
            list.BindPipeline(pipeline);
            list.BindDescriptorSet(DescriptorSetSlot.PerFrame, set);
            list.PushConstants(ShaderStage.Compute, 0, BitConverter.GetBytes(Multiplier));
            list.Dispatch(Elements / 64);

            list.Barrier(new([new(data, ResourceState.ShaderWrite, ResourceState.CopySource)], []));
            list.CopyBuffer(data, 0, readback, 0, Bytes);
            list.Finish();
            owned.ComputeQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        var raw = new byte[Bytes];
        owned.Read(readback, 0, raw);
        var result = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(raw);

        for (var index = 0; index < Elements; index++) {
            Assert.Equal((source[index] * Multiplier) + (uint)index, result[index]);
        }

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "Compute produced validation errors: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );

        owned.Destroy(set);
        owned.Destroy(readback);
        owned.Destroy(data);
        owned.Destroy(pipeline);
        owned.Destroy(layout);
        owned.Destroy(setLayout);
        owned.Destroy(shader);
    }

    /// <summary>Compute does not run inside a render pass on any API, and Vulkan does not say so.</summary>
    [Fact]
    public void DispatchingInsideARenderPassIsRefused() {
        Assert.SkipUnless(VulkanDevice.TryCreate(new(), out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var target = owned.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.ColourTarget, Name: "target")
        );

        var view = owned.CreateTextureView(target);

        owned.BeginFrame();

        using (var list = owned.BeginCommandList(QueueKind.Graphics, "dispatch in pass")) {
            list.Barrier(new([], [new(target, ResourceState.Undefined, ResourceState.ColourTarget)]));
            list.BeginRenderPass(new([new(view)], name: "pass"));

            Assert.Throws<InvalidOperationException>(() => list.Dispatch(1));

            list.EndRenderPass();
            list.Finish();
            owned.GraphicsQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        owned.Destroy(view);
        owned.Destroy(target);
    }

    /// <summary>
    ///     Vulkan permits only self-dependency barriers inside a render pass, which the RHI does not
    ///     expose — and a barrier recorded there is undefined rather than an error.
    /// </summary>
    [Fact]
    public void ABarrierInsideARenderPassIsRefused() {
        Assert.SkipUnless(VulkanDevice.TryCreate(new(), out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var target = owned.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.ColourTarget, Name: "target")
        );

        var view = owned.CreateTextureView(target);

        owned.BeginFrame();

        using (var list = owned.BeginCommandList(QueueKind.Graphics, "barrier in pass")) {
            list.Barrier(new([], [new(target, ResourceState.Undefined, ResourceState.ColourTarget)]));
            list.BeginRenderPass(new([new(view)], name: "pass"));

            Assert.Throws<InvalidOperationException>(
                () => list.Barrier(new([], [new(target, ResourceState.ColourTarget, ResourceState.ShaderRead)]))
            );

            list.EndRenderPass();
            list.Finish();
            owned.GraphicsQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        owned.Destroy(view);
        owned.Destroy(target);
    }
}
