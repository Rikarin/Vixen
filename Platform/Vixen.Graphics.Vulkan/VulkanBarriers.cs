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
    ///     <para>
    ///         ⚠ <b><see cref="ResourceState.Undefined" /> is only ever a source, and a source half
    ///         goes through <see cref="SourceStage" /> rather than through here.</b> This returns
    ///         <c>TopOfPipe</c> for it, which is what "no stage of this queue is using it" means and
    ///         is the right answer to the question <em>this</em> function asks. It is the wrong answer
    ///         to "what must the barrier wait for", because the memory has a previous tenant whose
    ///         write is still in flight — see <see cref="SourceStage" />, which is where that
    ///         distinction lives.
    ///     </para>
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

    /// <summary>Which stages a barrier's <em>source</em> half has to order after.</summary>
    /// <param name="state">What the resource was being used for.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same as <see cref="ToStage" /> everywhere except
    ///         <see cref="ResourceState.Undefined" />, and that exception is the whole reason this
    ///         exists.</b> "The contents may be discarded" and "there is nothing to wait for" are not
    ///         the same statement, and reading the first as the second is a race on every aliased
    ///         resource in the frame — on the <em>single-queue</em> path, which is the one every real
    ///         device here runs.
    ///     </para>
    ///     <para>
    ///         <c>TransientResourcePool</c> hands one physical texture to two virtual resources whose
    ///         lifetimes do not overlap, and the graph tracks state per virtual resource — so the
    ///         second one's first transition honestly says it comes from <c>Undefined</c>. The hazard
    ///         it has to order after is not the contents. It is the <em>previous tenant's last write
    ///         to that same image</em>, which is still in flight: <c>TopOfPipe</c> as a source waits
    ///         for nothing at all, so the take-over's layout transition and the new tenant's first
    ///         write may both begin while the old tenant's colour writes are still landing.
    ///     </para>
    ///     <para>
    ///         <b>And it is not only about aliasing within one frame.</b> The pool outlives the
    ///         frame, so every transient after the first frame is a physical resource the previous
    ///         frame was using; with two frames in flight that write is genuinely outstanding when
    ///         this barrier is recorded.
    ///     </para>
    ///     <para>
    ///         <b>Conservative on purpose, and the narrow alternative was considered and rejected.</b>
    ///         The precise source is the previous tenant's stages, which the backend cannot know — the
    ///         RHI states one resource's use, not the memory's history — and which the graph cannot
    ///         supply either, because <c>PlanBarriers</c> runs in <c>Compile</c> and pool slots are
    ///         not handed out until <c>Realise</c>. The only case this over-waits for is a physical
    ///         resource that has never been touched at all, which happens once in its life.
    ///     </para>
    ///     <para>
    ///         <c>AllCommands</c> rather than <c>BottomOfPipe</c>: the two are equivalent as an
    ///         execution dependency, but <c>BottomOfPipe</c> performs no access, and this half needs
    ///         a non-empty <see cref="SourceAccess" /> to make the previous write available rather
    ///         than merely finished.
    ///     </para>
    /// </remarks>
    public static PipelineStageFlags SourceStage(ResourceState state) =>
        state == ResourceState.Undefined ? PipelineStageFlags.AllCommandsBit : ToStage(state);

    /// <summary>Which accesses a barrier's <em>source</em> half has to make available.</summary>
    /// <param name="state">What the resource was being used for.</param>
    /// <remarks>
    ///     The other half of <see cref="SourceStage" />. An execution dependency alone would order the
    ///     previous tenant's write before the new one and still leave it sitting in a cache the new
    ///     write does not go through, so the two could land in either order. <c>MemoryWrite</c> is
    ///     the only honest answer available — which write it was is exactly what this cannot know —
    ///     and it is supported by every queue family's clamp.
    /// </remarks>
    public static AccessFlags SourceAccess(ResourceState state) =>
        state == ResourceState.Undefined ? AccessFlags.MemoryWriteBit : ToAccess(state);

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

    /// <summary>The pipeline stages a queue of this kind is allowed to name in a barrier.</summary>
    /// <param name="kind">Which queue the list being recorded belongs to.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A barrier may not name a stage its queue family does not support</b>, and a
    ///         <c>ResourceState</c> says nothing about which queue it will be recorded on.
    ///         <see cref="ToStage" /> turns <see cref="ResourceState.ShaderRead" /> into the vertex,
    ///         fragment <i>and</i> compute stages — correct on a graphics queue and invalid usage on
    ///         a compute one, which has no vertex or fragment stage to order against. Every hoisted
    ///         compute pass that reads a texture produces exactly that barrier, so without this the
    ///         async scheduler could not record a validation-clean frame on any device with a
    ///         compute family of its own — which is no device this engine has been developed on, and
    ///         is why nothing caught it.
    ///     </para>
    ///     <para>
    ///         Narrowing a stage mask is safe here and only here: the stages being dropped are ones
    ///         that <em>cannot execute on this queue</em>, so there is no work on this queue in them
    ///         to order against. The stages that matter to the other queue are ordered by the
    ///         handover and its wait edge instead, which is the thing a barrier could never have
    ///         done.
    ///     </para>
    ///     <para>
    ///         Keyed on <see cref="QueueKind" /> rather than on the family's flags because
    ///         <c>VulkanDevice</c> already collapses a kind that shares the graphics family down to
    ///         <see cref="QueueKind.Graphics" />, and <c>QueueFamilySelection</c> only ever picks a
    ///         <see cref="QueueKind.Compute" /> family that does not advertise graphics or a
    ///         <see cref="QueueKind.Transfer" /> family that advertises neither. So the kind is the
    ///         capability, and on a one-family device every list is Graphics and nothing is dropped
    ///         — which is what keeps a scheduled frame byte-identical to an unscheduled one there.
    ///     </para>
    /// </remarks>
    public static PipelineStageFlags SupportedStages(QueueKind kind) => kind switch {
        // vkCmdDispatchIndirect is a compute-queue command, so DrawIndirect is supported despite the
        // name. Transfer is implied by both graphics and compute capability.
        QueueKind.Compute => PipelineStageFlags.TopOfPipeBit
            | PipelineStageFlags.DrawIndirectBit
            | PipelineStageFlags.ComputeShaderBit
            | PipelineStageFlags.TransferBit
            | PipelineStageFlags.HostBit
            | PipelineStageFlags.AllCommandsBit
            | PipelineStageFlags.BottomOfPipeBit,

        QueueKind.Transfer => PipelineStageFlags.TopOfPipeBit
            | PipelineStageFlags.TransferBit
            | PipelineStageFlags.HostBit
            | PipelineStageFlags.AllCommandsBit
            | PipelineStageFlags.BottomOfPipeBit,

        _ => unchecked((PipelineStageFlags)~0u)
    };

    /// <summary>The accesses a queue of this kind is allowed to name in a barrier.</summary>
    /// <param name="kind">Which queue the list being recorded belongs to.</param>
    /// <remarks>
    ///     The other half of <see cref="SupportedStages" />, and needed for the same reason: an
    ///     access flag has to be one the stages in the mask can perform, so dropping
    ///     <c>ColorAttachmentOutput</c> from the stages without dropping
    ///     <c>ColorAttachmentWrite</c> from the accesses trades one validation error for another.
    /// </remarks>
    public static AccessFlags SupportedAccess(QueueKind kind) => kind switch {
        QueueKind.Compute => AccessFlags.IndirectCommandReadBit
            | AccessFlags.UniformReadBit
            | AccessFlags.ShaderReadBit
            | AccessFlags.ShaderWriteBit
            | AccessFlags.TransferReadBit
            | AccessFlags.TransferWriteBit
            | AccessFlags.HostReadBit
            | AccessFlags.HostWriteBit
            | AccessFlags.MemoryReadBit
            | AccessFlags.MemoryWriteBit,

        QueueKind.Transfer => AccessFlags.TransferReadBit
            | AccessFlags.TransferWriteBit
            | AccessFlags.HostReadBit
            | AccessFlags.HostWriteBit
            | AccessFlags.MemoryReadBit
            | AccessFlags.MemoryWriteBit,

        _ => unchecked((AccessFlags)~0u)
    };

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
