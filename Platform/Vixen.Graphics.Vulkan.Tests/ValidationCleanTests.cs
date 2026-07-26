// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     Everything the backend does, with the validation layers watching, asserting that they said
///     nothing.
/// </summary>
/// <remarks>
///     <para>
///         Validation-clean-in-debug is a stated non-negotiable
///         ([00](../../docs/plan/00-vision-and-principles.md)) and until this existed it was not
///         enforced by anything. The first run of this backend against a real driver produced
///         twenty-three validation errors while every other test passed, because the messages went to
///         the console and the console is not a gate.
///     </para>
///     <para>
///         One test rather than a check inside each: the layers report on whatever thread hit the
///         problem and the recorder is process-wide, so attributing a message to a test needs the
///         tests not to overlap. This collection is serialised with the rest of the driver tests for
///         the same reason.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class ValidationCleanTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFullFrameProducesNoValidationMessages(bool renderPassObjects) {
        Assert.SkipUnless(VulkanInstance.ValidationLayerInstalled, "the validation layer is not installed");

        Assert.SkipUnless(
            VulkanDevice.TryCreate(
                new() { PreferRenderPassObjects = renderPassObjects },
                out var device,
                out var reason
            ),
            reason ?? "no Vulkan"
        );

        using var owned = device!;

        Assert.SkipUnless(
            owned.ValidationEnabled,
            "the instance came up without validation, so there is nothing to assert"
        );

        // After creation, because device creation is itself under test and its messages belong to it
        // — but the reset has to be here rather than before, or a message from a previous test's
        // teardown would be attributed to this one.
        VulkanDiagnostics.Reset();

        Exercise(owned);

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0 && VulkanDiagnostics.WarningCount == 0,
            $"The validation layers reported {VulkanDiagnostics.ErrorCount} error(s) and "
            + $"{VulkanDiagnostics.WarningCount} warning(s):"
            + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>
    ///     Device creation itself, which is where the dynamic-rendering dependency bug lived — after
    ///     the reset, so nothing else can be blamed for it.
    /// </summary>
    [Fact]
    public void DeviceCreationProducesNoValidationMessages() {
        Assert.SkipUnless(VulkanInstance.ValidationLayerInstalled, "the validation layer is not installed");
        VulkanDiagnostics.Reset();

        Assert.SkipUnless(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason ?? "no Vulkan"
        );

        using (device) {
            Assert.SkipUnless(device!.ValidationEnabled, "the instance came up without validation");
        }

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            $"Creating and destroying a device produced {VulkanDiagnostics.ErrorCount} validation "
            + "error(s):"
            + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>A frame that touches every part of the backend a headless device can reach.</summary>
    static void Exercise(VulkanDevice device) {
        const int Side = 8;
        const int Bytes = Side * Side * 4;

        var upload = device.CreateBuffer(new(Bytes, BufferUsage.CopySource, MemoryAccess.HostUpload, "upload"));
        var readback = device.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "readback")
        );

        var target = device.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            Side,
            Side,
            TextureUsage.ColourTarget | TextureUsage.CopySource | TextureUsage.CopyDestination,
            Name: "target"
        ));

        var depth = device.CreateTexture(new(
            PixelFormat.Depth32Float,
            Side,
            Side,
            TextureUsage.DepthStencilTarget,
            Name: "depth"
        ));

        var view = device.CreateTextureView(target);
        var depthView = device.CreateTextureView(depth);
        var sampler = device.CreateSampler(SamplerDescription.LinearClamp);

        var setLayout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerFrame,
            [
                new(0, DescriptorKind.UniformBuffer, ShaderStage.AllGraphics),
                new(1, DescriptorKind.Sampler, ShaderStage.Fragment)
            ],
            "per frame"
        ));

        var layout = device.CreatePipelineLayout(new(
            [setLayout],
            [new(ShaderStage.Vertex, 0, 64)],
            "layout"
        ));

        var uniform = device.CreateBuffer(new(256, BufferUsage.Uniform, MemoryAccess.HostUpload, "uniform"));
        var set = device.CreateDescriptorSet(setLayout, "set");

        device.UpdateDescriptorSet(set, [
            DescriptorWrite.Uniform(0, uniform),
            DescriptorWrite.SamplerAt(1, sampler)
        ]);

        device.Write(upload, 0, new byte[Bytes]);

        for (var frame = 0; frame < 3; frame++) {
            device.BeginFrame();

            using (var list = device.BeginCommandList(QueueKind.Graphics, $"frame {frame}")) {
                list.Barrier(new([], [
                    new(target, ResourceState.Undefined, ResourceState.CopyDestination),
                    new(depth, ResourceState.Undefined, ResourceState.DepthStencilWrite)
                ]));

                list.CopyBufferToTexture(upload, 0, new(target), new(Side, Side, 1));

                list.Barrier(new([], [
                    new(target, ResourceState.CopyDestination, ResourceState.ColourTarget)
                ]));

                list.BeginRenderPass(new(
                    [new(view, LoadAction.Clear, StoreAction.Store, new(0.1f, 0.2f, 0.3f, 1f))],
                    new(depthView),
                    "pass"
                ));

                list.SetViewport(new(0, 0, Side, Side));
                list.SetScissor(ScissorRect.Full(new(Side, Side)));
                list.EndRenderPass();

                list.Barrier(new([], [
                    new(target, ResourceState.ColourTarget, ResourceState.CopySource)
                ]));

                list.CopyTextureToBuffer(new(target), new(Side, Side, 1), readback, 0);
                list.Finish();
                device.GraphicsQueue.Submit([list]);
            }

            device.EndFrame();
        }

        device.WaitIdle();

        device.Destroy(set);
        device.Destroy(layout);
        device.Destroy(setLayout);
        device.Destroy(uniform);
        device.Destroy(sampler);
        device.Destroy(depthView);
        device.Destroy(view);
        device.Destroy(depth);
        device.Destroy(target);
        device.Destroy(readback);
        device.Destroy(upload);
    }
}
