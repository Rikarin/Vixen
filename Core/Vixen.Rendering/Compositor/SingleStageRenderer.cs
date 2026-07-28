// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Draws one stage from one view.
/// </summary>
/// <remarks>
///     <para>
///         The leaf of almost every compositor graph, and the smallest thing that is worth being a
///         node: "the opaque stage, from the camera, into this pass". A forward compositor is three
///         of these; a deferred one is a G-buffer stage, a lighting pass and a forward stage for what
///         the G-buffer cannot represent.
///     </para>
///     <para>
///         <see cref="Collect" /> is what puts the stage in the view's mask. Doing it here rather
///         than asking a host to set the mask is the point of the collect phase: what a view is culled
///         and sorted for is decided by the compositor that draws it, so a stage nothing draws costs
///         no culling and a stage that is drawn cannot have been forgotten in the mask.
///     </para>
/// </remarks>
public sealed class SingleStageRenderer : SceneRenderer {
    /// <summary>The view to draw from.</summary>
    public required RenderView View { get; init; }

    /// <summary>The stage to draw.</summary>
    public required RenderStage Stage { get; init; }

    /// <summary>The per-view block to bind before drawing, if the frame has one.</summary>
    /// <remarks>
    ///     Bound here rather than by the render system, because a per-view set is a
    ///     <em>descriptor</em> and the render system owns none — and rather than by the pass, because
    ///     a pass may draw several views and the block is one of them. This node is the smallest
    ///     thing that knows both which view and which command list.
    /// </remarks>
    public ViewConstants? Constants { get; set; }

    /// <inheritdoc />
    protected internal override void Collect(GraphicsCompositor compositor) {
        ArgumentNullException.ThrowIfNull(compositor);

        compositor.Use(View, Stage);
    }

    /// <inheritdoc />
    protected internal override void Record(GraphicsCompositor compositor, RenderDrawContext context) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(context);

        context.CommandList.PushDebugGroup(ToString());

        // Handed to the context rather than bound here: a set is bound against a pipeline's layout, so
        // it cannot go down before the first pipeline does. See RenderDrawContext.ViewConstants.
        var previous = context.ViewConstants;
        context.ViewConstants = Constants;

        compositor.System.Record(View, Stage, context);
        context.ViewConstants = previous;
        context.CommandList.PopDebugGroup();
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? $"{View.Name}/{Stage.Name}" : Name;
}
