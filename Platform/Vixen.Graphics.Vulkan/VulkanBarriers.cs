// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using Silk.NET.Vulkan;

namespace Vixen.Graphics.Vulkan;

/// <summary>What a resource state means to Vulkan: a stage, an access mask, and a layout.</summary>
/// <remarks>
///     <para>
///         The RHI states one thing — what a resource is about to be used for — and Vulkan wants
///         three, which is the whole reason <c>ResourceState</c> exists
///         ([05](../../docs/plan/05-graphics-rhi.md)). Getting the split wrong is the single most
///         expensive class of bug in a Vulkan backend: too narrow a stage mask is a race that appears
///         on one vendor's driver and not another's, and too wide a one is a stall nobody can find
///         because nothing is wrong with the picture.
///     </para>
///     <para>
///         Pure, and therefore tested — including the combinations, which is where a
///         switch-per-state gets it wrong. <c>ResourceState</c> is a flags enum precisely so that
///         "depth-tested while sampled by the shader" is expressible, and that combination has a
///         layout of its own rather than falling back to <c>General</c>.
///     </para>
/// </remarks>
static class VulkanBarriers {
    /// <summary>The states that each imply a distinct image layout.</summary>
    const ResourceState LayoutBearing = ResourceState.ShaderRead
        | ResourceState.ShaderWrite
        | ResourceState.ColourTarget
        | ResourceState.DepthStencilWrite
        | ResourceState.DepthStencilRead
        | ResourceState.CopySource
        | ResourceState.CopyDestination
        | ResourceState.Present;

    /// <summary>Which pipeline stages touch a resource in this state.</summary>
    /// <param name="state">What it is being used for.</param>
    /// <remarks>
    ///     <see cref="ResourceState.Undefined" /> becomes <c>TopOfPipe</c> as a source and is only
    ///     ever a source: it means "whatever was there before does not matter", so there is nothing to
    ///     wait for.
    /// </remarks>
    public static PipelineStageFlags ToStage(ResourceState state) {
        if (state == ResourceState.Undefined) {
            return PipelineStageFlags.TopOfPipeBit;
        }

        var stages = PipelineStageFlags.None;

        if ((state & ResourceState.VertexInput) != 0) {
            stages |= PipelineStageFlags.VertexInputBit;
        }

        if ((state & ResourceState.IndirectArgument) != 0) {
            stages |= PipelineStageFlags.DrawIndirectBit;
        }

        // A uniform or a sampled texture can be read by any stage that runs shaders, and the RHI
        // does not say which. Naming them all is correct and costs a little parallelism; guessing
        // fragment-only is a race on the first vertex shader that reads a uniform.
        if ((state & (ResourceState.UniformRead | ResourceState.ShaderRead | ResourceState.ShaderWrite)) != 0) {
            stages |= PipelineStageFlags.VertexShaderBit
                | PipelineStageFlags.FragmentShaderBit
                | PipelineStageFlags.ComputeShaderBit;
        }

        if ((state & ResourceState.ColourTarget) != 0) {
            stages |= PipelineStageFlags.ColorAttachmentOutputBit;
        }

        // Both fragment-test stages: depth is written by the late test and read by the early one,
        // and a barrier that names only one of them lets the other overlap the transition.
        if ((state & (ResourceState.DepthStencilWrite | ResourceState.DepthStencilRead)) != 0) {
            stages |= PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        }

        if ((state & (ResourceState.CopySource | ResourceState.CopyDestination)) != 0) {
            stages |= PipelineStageFlags.TransferBit;
        }

        if ((state & ResourceState.HostAccess) != 0) {
            stages |= PipelineStageFlags.HostBit;
        }

        // Presentation is not a pipeline stage. BottomOfPipe as a source means "after everything",
        // which is what handing an image to the presentation engine has to wait for.
        if ((state & ResourceState.Present) != 0) {
            stages |= PipelineStageFlags.BottomOfPipeBit;
        }

        return stages == PipelineStageFlags.None ? PipelineStageFlags.TopOfPipeBit : stages;
    }

    /// <summary>Which accesses a resource in this state is subject to.</summary>
    /// <param name="state">What it is being used for.</param>
    public static AccessFlags ToAccess(ResourceState state) {
        var access = AccessFlags.None;

        if ((state & ResourceState.VertexInput) != 0) {
            access |= AccessFlags.VertexAttributeReadBit | AccessFlags.IndexReadBit;
        }

        if ((state & ResourceState.IndirectArgument) != 0) {
            access |= AccessFlags.IndirectCommandReadBit;
        }

        if ((state & ResourceState.UniformRead) != 0) {
            access |= AccessFlags.UniformReadBit;
        }

        if ((state & ResourceState.ShaderRead) != 0) {
            access |= AccessFlags.ShaderReadBit;
        }

        if ((state & ResourceState.ShaderWrite) != 0) {
            access |= AccessFlags.ShaderWriteBit;
        }

        if ((state & ResourceState.ColourTarget) != 0) {
            access |= AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;
        }

        if ((state & ResourceState.DepthStencilWrite) != 0) {
            access |= AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
        }

        if ((state & ResourceState.DepthStencilRead) != 0) {
            access |= AccessFlags.DepthStencilAttachmentReadBit;
        }

        if ((state & ResourceState.CopySource) != 0) {
            access |= AccessFlags.TransferReadBit;
        }

        if ((state & ResourceState.CopyDestination) != 0) {
            access |= AccessFlags.TransferWriteBit;
        }

        if ((state & ResourceState.HostAccess) != 0) {
            access |= AccessFlags.HostReadBit | AccessFlags.HostWriteBit;
        }

        // Present takes no access mask. The specification is explicit that the presentation engine's
        // access is not covered by an access flag, and naming one is a validation error.
        return access;
    }

    /// <summary>Which layout an image in this state has to be in.</summary>
    /// <param name="state">What it is being used for.</param>
    /// <remarks>
    ///     An image has exactly one layout, so a state naming two incompatible uses has to widen to
    ///     <c>General</c> — which is legal everywhere and optimal nowhere. The one combination worth
    ///     special-casing is depth-tested-while-sampled, because a shadow map or a depth-prepass
    ///     re-read is common and <c>DepthStencilReadOnlyOptimal</c> serves both.
    /// </remarks>
    public static ImageLayout ToLayout(ResourceState state) {
        if (state == ResourceState.Undefined) {
            return ImageLayout.Undefined;
        }

        // Read-only depth that a shader also samples. Both uses are reads and the layout permits
        // both, so this is a genuine optimum rather than a widening.
        if ((state & ~(ResourceState.DepthStencilRead | ResourceState.ShaderRead)) == 0
            && (state & ResourceState.DepthStencilRead) != 0) {
            return ImageLayout.DepthStencilReadOnlyOptimal;
        }

        return BitOperations.PopCount((uint)(state & LayoutBearing)) switch {
            0 => ImageLayout.General,
            1 => SingleLayout(state),
            _ => ImageLayout.General
        };
    }

    static ImageLayout SingleLayout(ResourceState state) {
        if ((state & ResourceState.ColourTarget) != 0) {
            return ImageLayout.ColorAttachmentOptimal;
        }

        if ((state & ResourceState.DepthStencilWrite) != 0) {
            return ImageLayout.DepthStencilAttachmentOptimal;
        }

        if ((state & ResourceState.DepthStencilRead) != 0) {
            return ImageLayout.DepthStencilReadOnlyOptimal;
        }

        if ((state & ResourceState.ShaderWrite) != 0) {
            // A storage image is General. There is no "storage optimal" layout, which is why writing
            // through a storage image gives up whatever compression the hardware had.
            return ImageLayout.General;
        }

        if ((state & ResourceState.ShaderRead) != 0) {
            return ImageLayout.ShaderReadOnlyOptimal;
        }

        if ((state & ResourceState.CopySource) != 0) {
            return ImageLayout.TransferSrcOptimal;
        }

        if ((state & ResourceState.CopyDestination) != 0) {
            return ImageLayout.TransferDstOptimal;
        }

        if ((state & ResourceState.Present) != 0) {
            return ImageLayout.PresentSrcKhr;
        }

        return ImageLayout.General;
    }
}
