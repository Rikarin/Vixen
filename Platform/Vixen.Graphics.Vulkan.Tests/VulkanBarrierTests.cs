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
    [Fact]
    public void UndefinedIsATopOfPipeSourceWithNoAccess() {
        Assert.Equal(PipelineStageFlags.TopOfPipeBit, VulkanBarriers.ToStage(ResourceState.Undefined));
        Assert.Equal(AccessFlags.None, VulkanBarriers.ToAccess(ResourceState.Undefined));
        Assert.Equal(ImageLayout.Undefined, VulkanBarriers.ToLayout(ResourceState.Undefined));
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
}
