// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     What a feature needs in order to record: somewhere to record to, and what to resolve
///     shaders through.
/// </summary>
/// <remarks>
///     <para>
///         Passed rather than held, because recording is the phase that is meant to run on several
///         threads at once: each gets its own <see cref="CommandList" /> and they share everything
///         else. A feature that reached for a command list on the system would be a feature that
///         cannot be parallelised, and it would compile.
///     </para>
///     <para>
///         The device is here too, and only for the one thing a recording feature legitimately needs
///         it for: creating a pipeline it discovers it is missing. Everything else it wants —
///         buffers, textures, descriptor sets — belongs to a resource it was given rather than to
///         the moment of drawing.
///     </para>
/// </remarks>
public sealed class RenderDrawContext(ICommandList commandList, EffectSystem effects) {
    /// <summary>Where this thread records.</summary>
    public ICommandList CommandList { get; } = commandList;

    /// <summary>Where a shader variant comes from.</summary>
    public EffectSystem Effects { get; } = effects;

    /// <summary>The device, for a feature that has to create a pipeline it did not have.</summary>
    public IGraphicsDevice? Device { get; init; }

    /// <summary>The view being recorded.</summary>
    public RenderView? View { get; internal set; }

    /// <summary>The stage being recorded.</summary>
    public RenderStage? Stage { get; internal set; }

    /// <summary>
    ///     The per-view block for <see cref="View" />, for a feature to bind after its first pipeline.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Handed over rather than bound by whoever set it, and the reason is the RHI's shape:
    ///         <see cref="ICommandList.BindDescriptorSet" /> takes no pipeline layout and infers one
    ///         from the pipeline that is bound, so binding a set before the first pipeline is
    ///         undefined — which the Vulkan backend refuses outright rather than letting through.
    ///     </para>
    ///     <para>
    ///         So the compositor node says <em>what</em> and a feature says <em>when</em>. Once per
    ///         run is enough: the four-set convention makes every pipeline in a frame layout-
    ///         compatible up to set 1, which is what lets a set survive a pipeline change at all.
    ///     </para>
    /// </remarks>
    public ViewConstants? ViewConstants { get; set; }

    /// <summary>
    ///     The frame's own set — the environment, the probes, the shadow atlas, the sun.
    /// </summary>
    /// <remarks>
    ///     Bound the same way and at the same moment as <see cref="ViewConstants" />, and for the same
    ///     reason: a set cannot be bound before the first pipeline. It is separate from the view's
    ///     because the two have different lifetimes and different shapes — set 0 belongs to the pass
    ///     drawing and holds resources, set 1 is a block every shader in the frame agrees on.
    /// </remarks>
    public SceneConstants? SceneConstants { get; set; }

    /// <summary>
    ///     The variant currently being drawn with, for a sub-feature that has to agree with it.
    /// </summary>
    /// <remarks>
    ///     Set by <see cref="Features.MeshRenderFeature" /> around the sub-features it calls, because
    ///     it is what resolved the variant and they are what contribute to the draw. A push constant
    ///     is the case it exists for: the stages a range covers are the <em>shader's</em>, and a
    ///     feature that pushed to the ones it guessed is refused by the validation layers.
    /// </remarks>
    public Effect? Effect { get; set; }

    /// <summary>The formats of the pass currently open.</summary>
    /// <remarks>
    ///     Set by whatever opened the pass — <see cref="Compositor.RenderPassRenderer" /> in a
    ///     composed frame — and read by any feature that has to build a pipeline. It is on the
    ///     context rather than on the stage because a stage is drawn into more than one pass, and a
    ///     pipeline built for the wrong formats is one the validation layers reject and a driver
    ///     silently mis-renders.
    /// </remarks>
    public RenderOutput Output { get; set; }

    /// <summary>
    ///     The region of the target the open pass is drawing into, in that target's own pixels.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Always the whole answer, never null, and that is what makes a per-view rect
    ///         composable.</b> <see cref="Compositor.RenderPassRenderer" /> sets it to its own
    ///         <c>Viewport</c> where a document named one and to the whole of its first attachment
    ///         otherwise, so a node underneath can multiply <see cref="RenderView.ViewportRect" /> by
    ///         it without asking anyone how big anything is. A nullable one would have pushed that
    ///         question down to every consumer, and the honest fallback — the attachment's size — is
    ///         only knowable where the attachments are.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Restored by whoever narrows it</b>, the way <see cref="Output" /> and
    ///         <see cref="SceneConstants" /> already are. Two split-screen halves are two sibling
    ///         nodes in one pass, and a third sibling that wanted the whole target would otherwise
    ///         inherit the second player's half — a frame that draws, reports nothing, and is wrong
    ///         in a quarter of the screen.
    ///     </para>
    ///     <para>
    ///         Zero-sized before any pass has opened, which is the state a context handed to a
    ///         feature outside a pass is honestly in.
    ///     </para>
    /// </remarks>
    public Viewport Viewport { get; set; }
}
