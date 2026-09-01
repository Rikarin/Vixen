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
        VulkanRequirement.Available(VulkanInstance.ValidationLayerInstalled, "the validation layer is not installed");

        VulkanRequirement.Available(
            VulkanDevice.TryCreate(
                new() { PreferRenderPassObjects = renderPassObjects },
                out var device,
                out var reason
            ),
            reason ?? "no Vulkan"
        );

        using var owned = device!;

        VulkanRequirement.Available(
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
    ///     Destroying a resource the frame on the GPU is still reading says nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The contract <see cref="IGraphicsDevice.Destroy(BufferHandle)" /> states and the whole
    ///         reason a renderer may recreate a buffer mid-frame without waiting: the handle comes
    ///         back here and the object is freed once no frame that could reference it is running.
    ///         Freeing it immediately is undefined behaviour that a driver is entitled to execute
    ///         silently, so the validation layers are the only witness there is.
    ///     </para>
    ///     <para>
    ///         Written after a renderer's upload buffers were changed on the strength of a comment
    ///         claiming this, rather than on the strength of anything asserting it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void DestroyingAResourceAFrameIsUsingProducesNoValidationMessages() {
        VulkanRequirement.Available(VulkanInstance.ValidationLayerInstalled, "the validation layer is not installed");

        VulkanRequirement.Available(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason ?? "no Vulkan"
        );

        using var owned = device!;

        VulkanRequirement.Available(
            owned.ValidationEnabled,
            "the instance came up without validation, so there is nothing to assert"
        );

        VulkanDiagnostics.Reset();

        // Grow the buffer every frame and hand the old one back while the frame that copied from it
        // has been submitted and not waited on. That is exactly what an upload buffer reaching its
        // high-water mark does.
        var buffer = owned.CreateBuffer(new(256, BufferUsage.CopySource, MemoryAccess.HostUpload, "growing"));

        for (var frame = 1; frame <= owned.FramesInFlight + 2; frame++) {
            var destination = owned.CreateBuffer(
                new(256, BufferUsage.CopyDestination, MemoryAccess.DeviceLocal, "sink")
            );

            owned.BeginFrame();

            using (var commands = owned.BeginCommandList(QueueKind.Graphics, "retirement")) {
                commands.CopyBuffer(buffer, 0, destination, 0, 256);
                commands.Finish();
                owned.GraphicsQueue.Submit([commands]);
            }

            // No WaitIdle: the point is that the frame may still be running.
            owned.Destroy(buffer);
            owned.Destroy(destination);
            owned.EndFrame();

            buffer = owned.CreateBuffer(
                new(256 * (frame + 1), BufferUsage.CopySource, MemoryAccess.HostUpload, "growing")
            );
        }

        owned.WaitIdle();
        owned.Destroy(buffer);

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0 && VulkanDiagnostics.WarningCount == 0,
            $"The validation layers reported {VulkanDiagnostics.ErrorCount} error(s) and "
            + $"{VulkanDiagnostics.WarningCount} warning(s):"
            + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>
    ///     Destroying a resource <i>between</i> frames, which is the window the test above does not
    ///     cover and the one the deferral used to get wrong.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="DestroyingAResourceAFrameIsUsingProducesNoValidationMessages" />
    ///         destroys inside the <c>BeginFrame</c>/<c>EndFrame</c> pair, and that is the easy
    ///         half.</b> <see cref="VulkanDevice.Retire" /> files an action under
    ///         <c>FrameSlot</c>, and <c>EndFrame</c> is what advances <c>frame</c> — so a destroy
    ///         issued after <c>EndFrame</c> is filed under the slot the <i>next</i> <c>BeginFrame</c>
    ///         is about to drain. It was run one call later, having waited only on the fence of frame
    ///         <em>n</em> − <see cref="VulkanDevice.FramesInFlight" />; the frame that was just
    ///         submitted, and every frame between, was still on the GPU. The deferral advertised as
    ///         <c>FramesInFlight</c> frames wide was zero frames wide for every caller outside a
    ///         frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Between frames is not an exotic place to destroy from.</b> It is where
    ///         <c>EditorHost.Sync</c> retires thumbnails, and it is the moment
    ///         <c>GraphicsCompositor.Dispose</c> documents as <i>the only</i> safe one.
    ///     </para>
    ///     <para>
    ///         The witness is deterministic rather than a race: the layers hold a submission "in use"
    ///         until a fence covering it is waited on, and no frame in this loop ever waits on the
    ///         previous frame's fence. There is nothing here that can happen to come out right.
    ///     </para>
    /// </remarks>
    [Fact]
    public void DestroyingBetweenFramesProducesNoValidationMessages() {
        VulkanRequirement.Available(VulkanInstance.ValidationLayerInstalled, "the validation layer is not installed");

        VulkanRequirement.Available(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason ?? "no Vulkan"
        );

        using var owned = device!;

        VulkanRequirement.Available(
            owned.ValidationEnabled,
            "the instance came up without validation, so there is nothing to assert"
        );

        VulkanDiagnostics.Reset();

        var buffer = owned.CreateBuffer(new(256, BufferUsage.CopySource, MemoryAccess.HostUpload, "read between"));

        for (var frame = 1; frame <= owned.FramesInFlight + 2; frame++) {
            var destination = owned.CreateBuffer(
                new(256, BufferUsage.CopyDestination, MemoryAccess.DeviceLocal, "sink")
            );

            owned.BeginFrame();

            using (var commands = owned.BeginCommandList(QueueKind.Graphics, "between frames")) {
                commands.CopyBuffer(buffer, 0, destination, 0, 256);
                commands.Finish();
                owned.GraphicsQueue.Submit([commands]);
            }

            owned.EndFrame();

            // ⚠ Here, and the whole test is this line's position. The frame above has been submitted
            // and nothing has waited on it; `EditorHost.Sync` hands its thumbnails back at exactly
            // this point in exactly this loop.
            owned.Destroy(buffer);
            owned.Destroy(destination);

            buffer = owned.CreateBuffer(
                new(256 * (frame + 1), BufferUsage.CopySource, MemoryAccess.HostUpload, "read between")
            );
        }

        owned.WaitIdle();
        owned.Destroy(buffer);

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
        VulkanRequirement.Available(VulkanInstance.ValidationLayerInstalled, "the validation layer is not installed");
        VulkanDiagnostics.Reset();

        VulkanRequirement.Available(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason ?? "no Vulkan"
        );

        using (device) {
            VulkanRequirement.Available(device!.ValidationEnabled, "the instance came up without validation");
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
