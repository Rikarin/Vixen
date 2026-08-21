// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     Barrier translation. Too narrow a stage mask is a race that appears on one vendor's driver and
///     not another's; too wide a one is a stall nobody can find because nothing is wrong with the
///     picture. Neither is visible without asserting it.
/// </summary>
public sealed class VulkanBarrierTests {
    /// <summary>
    ///     ⚠ <b>What <c>Undefined</c> means as a <em>use</em>, which is not what it means as a
    ///     barrier's source half.</b> No stage of this queue is using the resource and no access is
    ///     being performed on it — see
    ///     <see cref="AnUndefinedSourceOrdersAfterEverythingBecauseTheMemoryHadATenant" /> for the
    ///     question this one is not the answer to.
    /// </summary>
    [Fact]
    public void UndefinedNamesNoUseOfItsOwn() {
        Assert.Equal(PipelineStageFlags.TopOfPipeBit, VulkanBarriers.ToStage(ResourceState.Undefined));
        Assert.Equal(AccessFlags.None, VulkanBarriers.ToAccess(ResourceState.Undefined));
        Assert.Equal(ImageLayout.Undefined, VulkanBarriers.ToLayout(ResourceState.Undefined));
    }

    /// <summary>
    ///     ⚠ <b>An <c>Undefined</c> source waits for everything, because the memory has a previous
    ///     tenant.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         "The contents may be discarded" and "there is nothing to wait for" are not the same
    ///         statement, and reading the first as the second is the single-queue aliasing bug.
    ///         <c>TransientResourcePool</c> hands one physical texture to two virtual resources whose
    ///         lifetimes do not overlap, and the second one's first transition says it comes from
    ///         <see cref="ResourceState.Undefined" /> — see
    ///         <c>RenderGraphTests.AResourceTakingOverAliasedMemoryDiscardsWhatWasThere</c>. The
    ///         hazard is not the contents: it is the <em>previous</em> resource's last write to that
    ///         same image, which is still in flight. <c>TopOfPipe</c> as a source orders against
    ///         nothing at all, so the take-over's layout transition and the new tenant's first write
    ///         may both begin while the old tenant's colour writes are still landing.
    ///     </para>
    ///     <para>
    ///         The pool outlives the frame, so this is not only about aliasing within one frame:
    ///         every transient in every frame after the first is a physical resource the previous
    ///         frame was still using, and with two frames in flight that write is genuinely
    ///         outstanding.
    ///     </para>
    ///     <para>
    ///         <b>The layout stays <see cref="ImageLayout.Undefined" />.</b> Discarding the contents
    ///         is right and is what avoids a decompress for garbage; what changes is only which work
    ///         the barrier waits for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnUndefinedSourceOrdersAfterEverythingBecauseTheMemoryHadATenant() {
        Assert.Equal(PipelineStageFlags.AllCommandsBit, VulkanBarriers.SourceStage(ResourceState.Undefined));
        Assert.Equal(AccessFlags.MemoryWriteBit, VulkanBarriers.SourceAccess(ResourceState.Undefined));

        // Still a discard. Waiting for the previous tenant is not the same as preserving what it left.
        Assert.Equal(ImageLayout.Undefined, VulkanBarriers.ToLayout(ResourceState.Undefined));
    }

    /// <summary>
    ///     ⚠ Every other state's source half is its ordinary translation. The widening is for
    ///     <c>Undefined</c> alone — a barrier whose source is a real state names that state's stages,
    ///     and widening those would be the stall nobody can find.
    /// </summary>
    [Fact]
    public void OnlyUndefinedWidensItsSourceHalf() {
        foreach (var state in Enum.GetValues<ResourceState>()) {
            if (state == ResourceState.Undefined) {
                continue;
            }

            Assert.Equal(VulkanBarriers.ToStage(state), VulkanBarriers.SourceStage(state));
            Assert.Equal(VulkanBarriers.ToAccess(state), VulkanBarriers.SourceAccess(state));
        }
    }

    /// <summary>
    ///     ⚠ The widened source has to survive the queue clamp, or a transfer or compute list would
    ///     record an aliasing barrier whose source mask clamped away to nothing — which
    ///     <c>VulkanCommandList</c> then substitutes <c>TopOfPipe</c> for, restoring the very bug.
    /// </summary>
    [Theory]
    [InlineData(QueueKind.Graphics)]
    [InlineData(QueueKind.Compute)]
    [InlineData(QueueKind.Transfer)]
    public void TheWidenedUndefinedSourceSurvivesEveryQueueClamp(QueueKind kind) {
        var stages = VulkanBarriers.SourceStage(ResourceState.Undefined) & VulkanBarriers.SupportedStages(kind);
        var access = VulkanBarriers.SourceAccess(ResourceState.Undefined) & VulkanBarriers.SupportedAccess(kind);

        Assert.Equal(PipelineStageFlags.AllCommandsBit, stages);
        Assert.Equal(AccessFlags.MemoryWriteBit, access);
    }

    /// <summary>
    ///     Every state has to name at least one stage. A barrier with an empty source or destination
    ///     mask is a validation error, and the states most likely to produce one are the ones nobody
    ///     writes by hand.
    /// </summary>
    [Fact]
    public void EveryStateNamesAtLeastOneStage() {
        foreach (var state in Enum.GetValues<ResourceState>()) {
            Assert.NotEqual(PipelineStageFlags.None, VulkanBarriers.ToStage(state));
        }
    }

    /// <summary>
    ///     A uniform or sampled texture can be read by any stage that runs shaders and the RHI does
    ///     not say which. Guessing fragment-only is a race on the first vertex shader that reads a
    ///     uniform — which is every vertex shader.
    /// </summary>
    [Fact]
    public void ShaderReadsNameEveryShaderStage() {
        var stages = VulkanBarriers.ToStage(ResourceState.ShaderRead);

        Assert.True((stages & PipelineStageFlags.VertexShaderBit) != 0);
        Assert.True((stages & PipelineStageFlags.FragmentShaderBit) != 0);
        Assert.True((stages & PipelineStageFlags.ComputeShaderBit) != 0);
    }

    /// <summary>
    ///     Depth is written by the late fragment test and read by the early one. Naming only one lets
    ///     the other overlap the transition.
    /// </summary>
    [Fact]
    public void DepthNamesBothFragmentTestStages() {
        foreach (var state in (ResourceState[]) [
                     ResourceState.DepthStencilWrite,
                     ResourceState.DepthStencilRead
                 ]) {
            var stages = VulkanBarriers.ToStage(state);

            Assert.True((stages & PipelineStageFlags.EarlyFragmentTestsBit) != 0);
            Assert.True((stages & PipelineStageFlags.LateFragmentTestsBit) != 0);
        }
    }

    /// <summary>
    ///     The specification is explicit that the presentation engine's access is not covered by an
    ///     access flag, and naming one is a validation error.
    /// </summary>
    [Fact]
    public void PresentTakesNoAccessMask() {
        Assert.Equal(AccessFlags.None, VulkanBarriers.ToAccess(ResourceState.Present));
        Assert.Equal(ImageLayout.PresentSrcKhr, VulkanBarriers.ToLayout(ResourceState.Present));
    }

    [Theory]
    [InlineData(ResourceState.ColourTarget, ImageLayout.ColorAttachmentOptimal)]
    [InlineData(ResourceState.DepthStencilWrite, ImageLayout.DepthStencilAttachmentOptimal)]
    [InlineData(ResourceState.DepthStencilRead, ImageLayout.DepthStencilReadOnlyOptimal)]
    [InlineData(ResourceState.ShaderRead, ImageLayout.ShaderReadOnlyOptimal)]
    [InlineData(ResourceState.ShaderWrite, ImageLayout.General)]
    [InlineData(ResourceState.CopySource, ImageLayout.TransferSrcOptimal)]
    [InlineData(ResourceState.CopyDestination, ImageLayout.TransferDstOptimal)]
    [InlineData(ResourceState.Present, ImageLayout.PresentSrcKhr)]
    public void EachSingleStateHasItsOwnLayout(ResourceState state, ImageLayout expected) =>
        Assert.Equal(expected, VulkanBarriers.ToLayout(state));

    /// <summary>
    ///     Depth-tested while sampled: a shadow map, or a depth-prepass buffer re-read. Both uses are
    ///     reads and one layout serves both, so widening to <c>General</c> here would give up the
    ///     hardware's depth compression for nothing.
    /// </summary>
    [Fact]
    public void DepthReadAndShaderReadShareOneOptimalLayout() =>
        Assert.Equal(
            ImageLayout.DepthStencilReadOnlyOptimal,
            VulkanBarriers.ToLayout(ResourceState.DepthStencilRead | ResourceState.ShaderRead)
        );

    /// <summary>
    ///     Two incompatible uses have to widen. An image has exactly one layout, and picking either
    ///     one of a conflicting pair would be wrong half the time.
    /// </summary>
    [Fact]
    public void IncompatibleStatesWidenToGeneral() {
        Assert.Equal(
            ImageLayout.General,
            VulkanBarriers.ToLayout(ResourceState.ColourTarget | ResourceState.ShaderRead)
        );

        Assert.Equal(
            ImageLayout.General,
            VulkanBarriers.ToLayout(ResourceState.CopySource | ResourceState.CopyDestination)
        );
    }

    /// <summary>
    ///     A state with no layout-bearing bit still has to name a layout an image can be in — a buffer
    ///     state applied to an image is a caller error, and <c>General</c> is the answer that is never
    ///     invalid.
    /// </summary>
    [Fact]
    public void ABufferOnlyStateStillNamesALegalLayout() =>
        Assert.Equal(ImageLayout.General, VulkanBarriers.ToLayout(ResourceState.VertexInput));

    [Fact]
    public void VertexInputCoversBothIndexAndAttributeReads() {
        var access = VulkanBarriers.ToAccess(ResourceState.VertexInput);

        Assert.True((access & AccessFlags.VertexAttributeReadBit) != 0);
        Assert.True((access & AccessFlags.IndexReadBit) != 0);
    }

    /// <summary>
    ///     Depth-stencil <em>write</em> implies read too — the test reads the buffer it then writes,
    ///     and an access mask naming only the write is a race the hardware will find.
    /// </summary>
    [Fact]
    public void DepthWriteImpliesDepthRead() {
        var access = VulkanBarriers.ToAccess(ResourceState.DepthStencilWrite);

        Assert.True((access & AccessFlags.DepthStencilAttachmentWriteBit) != 0);
        Assert.True((access & AccessFlags.DepthStencilAttachmentReadBit) != 0);
    }

    [Fact]
    public void CombinedStatesUnionTheirAccessMasks() {
        var access = VulkanBarriers.ToAccess(ResourceState.ShaderRead | ResourceState.CopyDestination);

        Assert.True((access & AccessFlags.ShaderReadBit) != 0);
        Assert.True((access & AccessFlags.TransferWriteBit) != 0);
    }

    /// <summary>
    ///     ⚠ The bug the async scheduler could not have run without. A compute pass reading a texture
    ///     produces a barrier whose state is <see cref="ResourceState.ShaderRead" />, which names the
    ///     vertex and fragment stages as well as the compute one — and a compute-only queue family
    ///     has neither, so the barrier is invalid usage rather than merely wide.
    /// </summary>
    [Fact]
    public void AComputeQueueMayNotNameTheVertexOrFragmentStage() {
        var stages = VulkanBarriers.ToStage(ResourceState.ShaderRead)
            & VulkanBarriers.SupportedStages(QueueKind.Compute);

        Assert.Equal(PipelineStageFlags.ComputeShaderBit, stages);
    }

    /// <summary>
    ///     The other half of the same fix. Dropping the attachment stage without dropping the
    ///     attachment access trades one validation error for another, because an access flag has to
    ///     be one the stages in the mask can perform.
    /// </summary>
    [Fact]
    public void AComputeQueueMayNotNameAttachmentStagesOrAccesses() {
        var supported = VulkanBarriers.SupportedStages(QueueKind.Compute);

        Assert.Equal(
            PipelineStageFlags.None,
            VulkanBarriers.ToStage(ResourceState.ColourTarget) & supported
        );

        Assert.Equal(
            PipelineStageFlags.None,
            VulkanBarriers.ToStage(ResourceState.DepthStencilWrite) & supported
        );

        Assert.Equal(
            AccessFlags.None,
            VulkanBarriers.ToAccess(ResourceState.ColourTarget) & VulkanBarriers.SupportedAccess(QueueKind.Compute)
        );
    }

    /// <summary>
    ///     A transfer family accepts copies and nothing else, which is what makes
    ///     <see cref="PassKind.Transfer" /> the one kind whose queue can do <em>less</em> than the
    ///     graphics queue rather than differently.
    /// </summary>
    [Fact]
    public void ATransferQueueMayNameNoShaderStageAtAll() {
        var supported = VulkanBarriers.SupportedStages(QueueKind.Transfer);

        Assert.Equal(PipelineStageFlags.None, VulkanBarriers.ToStage(ResourceState.ShaderRead) & supported);
        Assert.Equal(PipelineStageFlags.None, VulkanBarriers.ToStage(ResourceState.ShaderWrite) & supported);
        Assert.Equal(PipelineStageFlags.TransferBit, VulkanBarriers.ToStage(ResourceState.CopySource) & supported);
    }

    /// <summary>
    ///     ⚠ Nothing is dropped on the graphics queue, and that is the property that keeps a scheduled
    ///     frame identical to an unscheduled one on a device with one universal family — which is
    ///     every device this engine has been developed on, so it is also the only leg CI can check.
    /// </summary>
    [Fact]
    public void TheGraphicsQueueClampsNothing() {
        foreach (var state in Enum.GetValues<ResourceState>()) {
            Assert.Equal(
                VulkanBarriers.ToStage(state),
                VulkanBarriers.ToStage(state) & VulkanBarriers.SupportedStages(QueueKind.Graphics)
            );

            Assert.Equal(
                VulkanBarriers.ToAccess(state),
                VulkanBarriers.ToAccess(state) & VulkanBarriers.SupportedAccess(QueueKind.Graphics)
            );
        }
    }

    /// <summary>
    ///     Every state a queue can genuinely take part in still names a stage after the clamp. A state
    ///     that clamped away to nothing is one the caller has to substitute a top- or bottom-of-pipe
    ///     for, and the set that needs substituting should be exactly the set that queue cannot do.
    /// </summary>
    [Theory]
    [InlineData(QueueKind.Compute, ResourceState.ShaderWrite)]
    [InlineData(QueueKind.Compute, ResourceState.UniformRead)]
    [InlineData(QueueKind.Compute, ResourceState.IndirectArgument)]
    [InlineData(QueueKind.Compute, ResourceState.CopyDestination)]
    [InlineData(QueueKind.Compute, ResourceState.HostAccess)]
    [InlineData(QueueKind.Transfer, ResourceState.CopyDestination)]
    [InlineData(QueueKind.Transfer, ResourceState.HostAccess)]
    public void AStateTheQueueCanDoSurvivesItsClamp(QueueKind kind, ResourceState state) {
        Assert.NotEqual(
            PipelineStageFlags.None,
            VulkanBarriers.ToStage(state) & VulkanBarriers.SupportedStages(kind)
        );
    }
}
