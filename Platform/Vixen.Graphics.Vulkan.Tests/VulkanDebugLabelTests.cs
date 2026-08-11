// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     The debug label stack, which is the one thing this backend records that nothing in a frame
///     ever reads back.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The symptom of getting this wrong is not a crash.</b> An unbalanced stack is a
///         RenderDoc or Xcode capture whose grouping is silently wrong from that point on, with later
///         passes nested inside labels they have nothing to do with. That is worse than no labels,
///         because the tree looks authoritative — and there is no picture, no counter and no
///         exception that reports it. A test is the only reader there is.
///     </para>
///     <para>
///         Asserted twice over: on <c>VulkanCommandList.DebugGroupDepth</c>, which counts what was
///         actually emitted, and on the validation layers, which know
///         <c>VUID-vkCmdEndDebugUtilsLabelEXT-commandBuffer-01912</c> — an end with no matching begin
///         on the queue the buffer was submitted to.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class VulkanDebugLabelTests {
    const int Side = 16;

    /// <summary>
    ///     A render pass with no name is still a render pass, and the backend brackets every one of
    ///     them with a debug group.
    /// </summary>
    /// <remarks>
    ///     Nothing prevents an empty name: <c>RenderPassDescription.Name</c> defaults to one, and a
    ///     frame document that omits a pass's title produces one. The backend used to decline to open
    ///     a group it could not name while ending one regardless, so this pass closed a label opened
    ///     before it and left the rest of the frame one level too shallow.
    /// </remarks>
    [Fact]
    public void AnUnnamedRenderPassLeavesTheLabelStackBalanced() {
        VulkanRequirement.Available(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason ?? "no Vulkan"
        );

        using var owned = device!;
        VulkanRequirement.Available(owned.DebugUtils is not null, "VK_EXT_debug_utils is not loaded");
        VulkanDiagnostics.Reset();

        var target = owned.CreateTexture(
            new(PixelFormat.Rgba8UNorm, Side, Side, TextureUsage.ColourTarget, Name: "unnamed pass target")
        );

        var view = owned.CreateTextureView(target);

        owned.BeginFrame();

        using (var list = (VulkanCommandList)owned.BeginCommandList(QueueKind.Graphics, "labels")) {
            list.Barrier(new([], [new(target, ResourceState.Undefined, ResourceState.ColourTarget)]));

            list.BeginRenderPass(
                new([new(view, LoadAction.Clear, StoreAction.Store, new(0f, 0f, 0f, 1f))], name: "")
            );

            list.EndRenderPass();

            Assert.Equal(0, list.DebugGroupDepth);

            list.Finish();
            owned.GraphicsQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        owned.Destroy(view);
        owned.Destroy(target);

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "An unnamed render pass produced validation errors: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>
    ///     A pop with nothing under it closes a label this list did not open — one belonging to
    ///     whatever was submitted before it — so it is dropped rather than emitted.
    /// </summary>
    [Fact]
    public void APopWithNothingOpenIsNotEmitted() {
        VulkanRequirement.Available(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason ?? "no Vulkan"
        );

        using var owned = device!;
        VulkanRequirement.Available(owned.DebugUtils is not null, "VK_EXT_debug_utils is not loaded");
        VulkanDiagnostics.Reset();

        owned.BeginFrame();

        using (var list = (VulkanCommandList)owned.BeginCommandList(QueueKind.Graphics, "stray pop")) {
            list.PopDebugGroup();

            Assert.Equal(0, list.DebugGroupDepth);

            list.Finish();
            owned.GraphicsQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "A stray pop produced validation errors: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>
    ///     An empty name opens a group all the same, so a pop that follows closes <em>it</em> rather
    ///     than the group around it.
    /// </summary>
    /// <remarks>
    ///     The nesting half of the guarantee, and the reason a placeholder is not interchangeable with
    ///     a depth counter. Skipping the unnamed push and letting the counter absorb the extra pop
    ///     keeps the arithmetic balanced while closing the wrong label: the outer group would end at
    ///     the inner pop, and everything after it would sit one level too shallow.
    /// </remarks>
    [Fact]
    public void AnUnnamedGroupNestsInsideANamedOne() {
        VulkanRequirement.Available(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason ?? "no Vulkan"
        );

        using var owned = device!;
        VulkanRequirement.Available(owned.DebugUtils is not null, "VK_EXT_debug_utils is not loaded");
        VulkanDiagnostics.Reset();

        owned.BeginFrame();

        using (var list = (VulkanCommandList)owned.BeginCommandList(QueueKind.Graphics, "nesting")) {
            list.PushDebugGroup("outer");
            Assert.Equal(1, list.DebugGroupDepth);

            list.PushDebugGroup("");
            Assert.Equal(2, list.DebugGroupDepth);

            list.PopDebugGroup();
            Assert.Equal(1, list.DebugGroupDepth);

            list.PopDebugGroup();
            Assert.Equal(0, list.DebugGroupDepth);

            list.Finish();
            owned.GraphicsQueue.Submit([list]);
        }

        owned.EndFrame();
        owned.WaitIdle();

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "A nested unnamed group produced validation errors: "
            + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }
}
