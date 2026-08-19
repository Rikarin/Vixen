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
    /// <remarks>
    ///     <para>
    ///         <b>Declares no pass — it is here to carry the features' degrades into the frame's
    ///         list.</b> <see cref="RootRenderFeature.Degraded" /> is a feature's answer to the same
    ///         question <see cref="SceneRenderer.Degraded" /> is a node's, and a feature is not in the
    ///         compositor tree, so nothing could ever collect one. This node is the smallest thing
    ///         that knows both which features exist and which stage they drew into.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The node walk is only paid for when there is something to report.</b> The normal
    ///         answer is that no feature has a reason at all, which costs one null test per root
    ///         feature; only a feature that <em>has</em> degraded makes this ask whether it drew
    ///         anything into this stage, which is what stops the same reason appearing under every
    ///         stage node in the tree.
    ///     </para>
    /// </remarks>
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);

        var reasons = default(List<string>);

        foreach (var feature in compositor.System.Features) {
            if (feature.Degraded is not { } reason || !Draws(compositor, feature)) {
                continue;
            }

            reasons ??= [];
            reasons.Add($"{feature.Name}: {reason}");
        }

        Degrade(reasons is null ? null : string.Join(" ", reasons));
    }

    /// <summary>Whether any of this stage's sorted work belongs to that feature.</summary>
    bool Draws(GraphicsCompositor compositor, RootRenderFeature feature) {
        var objects = compositor.System.Objects;

        foreach (var node in compositor.System.Nodes(View, Stage)) {
            if (objects[node.Object].FeatureIndex == feature.Index) {
                return true;
            }
        }

        return false;
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
