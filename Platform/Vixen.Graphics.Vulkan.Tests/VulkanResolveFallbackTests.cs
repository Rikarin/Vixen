// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>What a multisample resolve does on each of the backend's two render-pass paths.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>On the <c>VkRenderPass</c> path it did nothing, and said nothing.</b>
///         <c>AttachmentKey</c> has no notion of a resolve — <see cref="StoreAction.Resolve" />
///         translates to <c>VK_ATTACHMENT_STORE_OP_STORE</c> like any other store — so a pass that
///         asked for one stored the multisampled image, never wrote the resolve target, and drew no
///         validation message. Measured here on MoltenVK with a 4× red clear: the dynamic-rendering
///         path reads (255, 0, 0, 255) out of the resolve target and the render-pass path read
///         (255, 0, 255, 255) with <c>ErrorCount = 0</c>.
///     </para>
///     <para>
///         ⚠ <b>Which makes it the worst kind of gap: the path it is on is the mandatory one.</b>
///         [10](../../docs/plan/10-platforms.md) § Android keeps a large slice of the platform on
///         Vulkan 1.1, where there is no dynamic rendering to fall back from. And neither
///         <c>MsaaResolveImageTests</c> nor <c>DepthResolveImageTests</c> could see it: both run on
///         whichever path their device chooses, which on every machine here is the other one.
///     </para>
///     <para>
///         The backend now refuses instead. Filling it in means <c>vkCreateRenderPass2</c> — resolve
///         attachments in the description, <c>pResolveAttachments</c> in the subpass, their views in
///         the framebuffer, and <c>VkSubpassDescriptionDepthStencilResolve</c> chained for the depth
///         half, which <c>VkRenderPassCreateInfo</c> cannot carry at all — so it is filed rather
///         than smuggled in beside a refusal.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class VulkanResolveFallbackTests {
    const int Side = 16;
    const int Bytes = Side * Side * 4;

    static bool TryOpen(bool renderPassObjects, out VulkanDevice? device, out string? reason) =>
        VulkanDevice.TryCreate(
            new() { PreferRenderPassObjects = renderPassObjects },
            out device,
            out reason
        );

    /// <summary>
    ///     With dynamic rendering, a 4× red clear stored as a resolve arrives in the single-sample
    ///     target.
    /// </summary>
    /// <remarks>
    ///     A clear rather than a draw, deliberately: every sample of every texel is the same colour,
    ///     so the average is the clear colour exactly and the assertion does not depend on where a
    ///     driver puts its sample points. What is under test is whether the resolve <em>ran</em>.
    /// </remarks>
    [Fact]
    public void WithDynamicRenderingAResolveReachesItsTarget() {
        VulkanRequirement.Available(TryOpen(false, out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        VulkanRequirement.Available(
            owned.UsesDynamicRendering,
            "this device has no VK_KHR_dynamic_rendering, so there is no path here to compare against"
        );

        var pixels = Resolve(owned);

        Assert.True(
            pixels[0] > 200 && pixels[1] < 40 && pixels[2] < 40 && pixels[3] > 200,
            $"the resolve target reads ({pixels[0]}, {pixels[1]}, {pixels[2]}, {pixels[3]}) rather "
            + "than the red the multisampled attachment was cleared to."
        );
    }

    /// <summary>
    ///     ⚠ And on the <c>VkRenderPass</c> path it is refused, where it used to be dropped.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The refusal is the fix, not a placeholder for one.</b> A wrong picture with a clean
    ///         validation log and exit code 0 is the failure shape this repository keeps finding; a
    ///         stated refusal is the one thing that cannot be mistaken for a frame that worked, and
    ///         it names both causes — a device without <c>VK_KHR_dynamic_rendering</c>, and
    ///         <c>PreferRenderPassObjects</c>.
    ///     </para>
    ///     <para>
    ///         Asserted on the message rather than only on the type: the audience is somebody
    ///         watching an Android build render its multisampled frame into nothing, and "not
    ///         supported" without the sentence after it sends them into the render graph.
    ///     </para>
    /// </remarks>
    [Fact]
    public void WithRenderPassObjectsAResolveIsRefusedRatherThanDropped() {
        VulkanRequirement.Available(TryOpen(true, out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        Assert.False(owned.UsesDynamicRendering);

        var refusal = Assert.Throws<NotSupportedException>(() => Resolve(owned));

        Assert.Contains(nameof(StoreAction.Resolve), refusal.Message, StringComparison.Ordinal);
        Assert.Contains("VK_KHR_dynamic_rendering", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("PreferRenderPassObjects", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>And the depth half is refused by its own check rather than by the colour one.</summary>
    /// <remarks>
    ///     ⚠ A pass may resolve depth and not colour — a depth prepass feeding a resolved buffer is
    ///     exactly that — so a guard that only walked the colour attachments would leave the depth
    ///     resolve silently dropped for the shape most likely to ask for it.
    /// </remarks>
    [Fact]
    public void ADepthOnlyResolveIsRefusedToo() {
        VulkanRequirement.Available(TryOpen(true, out var device, out var reason), reason ?? "no Vulkan");
        using var owned = device!;

        var multisampled = owned.CreateTexture(new(
            PixelFormat.Depth32Float,
            Side,
            Side,
            TextureUsage.DepthStencilTarget,
            SampleCount: 4,
            Name: "depth samples"
        ));

        var resolved = owned.CreateTexture(new(
            PixelFormat.Depth32Float,
            Side,
            Side,
            TextureUsage.DepthStencilTarget | TextureUsage.CopySource,
            Name: "depth resolved"
        ));

        var samplesView = owned.CreateTextureView(multisampled);
        var resolvedView = owned.CreateTextureView(resolved);

        owned.BeginFrame();

        try {
            using var list = owned.BeginCommandList(QueueKind.Graphics, "depth resolve");

            var refusal = Assert.Throws<NotSupportedException>(
                () => list.BeginRenderPass(new(
                    [],
                    new DepthStencilAttachment(
                        samplesView,
                        LoadAction.Clear,
                        StoreAction.Resolve,
                        ResolveView: resolvedView
                    ),
                    name: "depth only"
                ))
            );

            Assert.Contains("depth attachment", refusal.Message, StringComparison.Ordinal);
        } finally {
            owned.EndFrame();
            owned.WaitIdle();
            owned.Destroy(resolvedView);
            owned.Destroy(samplesView);
            owned.Destroy(resolved);
            owned.Destroy(multisampled);
        }
    }

    /// <summary>Clears a 4× attachment to red, resolves it, and reads the resolve target back.</summary>
    static byte[] Resolve(VulkanDevice device) {
        var multisampled = device.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            Side,
            Side,
            TextureUsage.ColourTarget,
            SampleCount: 4,
            Name: "samples"
        ));

        var resolved = device.CreateTexture(new(
            PixelFormat.Rgba8UNorm,
            Side,
            Side,
            TextureUsage.ColourTarget | TextureUsage.CopySource,
            Name: "resolved"
        ));

        var samplesView = device.CreateTextureView(multisampled);
        var resolvedView = device.CreateTextureView(resolved);

        var readback = device.CreateBuffer(
            new(Bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "resolve readback")
        );

        device.BeginFrame();

        try {
            using var list = device.BeginCommandList(QueueKind.Graphics, "resolve");

            list.Barrier(new([], [
                new(multisampled, ResourceState.Undefined, ResourceState.ColourTarget),
                new(resolved, ResourceState.Undefined, ResourceState.ColourTarget)
            ]));

            list.BeginRenderPass(new(
                [
                    new(
                        samplesView,
                        LoadAction.Clear,
                        StoreAction.Resolve,
                        new(1f, 0f, 0f, 1f),
                        resolvedView
                    )
                ],
                name: "msaa clear"
            ));

            list.EndRenderPass();

            list.Barrier(new([], [
                new(resolved, ResourceState.ColourTarget, ResourceState.CopySource)
            ]));

            list.CopyTextureToBuffer(new(resolved), new(Side, Side, 1), readback, 0);
            list.Finish();
            device.GraphicsQueue.Submit([list]);
        } finally {
            device.EndFrame();
            device.WaitIdle();
        }

        var pixels = new byte[Bytes];
        device.Read(readback, 0, pixels);

        device.Destroy(readback);
        device.Destroy(resolvedView);
        device.Destroy(samplesView);
        device.Destroy(resolved);
        device.Destroy(multisampled);

        return pixels;
    }
}
