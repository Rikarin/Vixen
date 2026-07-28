// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;

namespace Vixen.Editor.NodeGraph;

/// <summary>What a node's output looks like, small, under the node.</summary>
/// <param name="Color">The colour to show when there is no image, or the tint when there is one.</param>
/// <param name="Label">A few characters over it — a number, a width — or empty.</param>
/// <param name="Image">
///     The renderer's handle for a thumbnail rendered into a target, or zero for a flat swatch.
/// </param>
/// <param name="FlipVertically">Whether that target is upside down, as a scene's is.</param>
/// <remarks>
///     <para>
///         <b>Two kinds, because the two questions are different.</b> Doc 11 asks for live preview
///         thumbnails, which for a shader graph means compiling one node's expression and running it
///         over a quad; that is <see cref="Image" />, and it is drawn exactly the way <c>Viewport</c>
///         draws a scene — a number the renderer was given, handed back in an image command. A
///         <i>swatch</i> is the other kind and is not a placeholder for it: a constant, a colour, a
///         mask and a channel split all reduce to one colour, and rendering a quad to say so would be
///         a render target per node to answer a question a rectangle answers.
///     </para>
///     <para>
///         ⚠ <b>A number the renderer does not know draws nothing at all</b>, which is
///         <c>Viewport.RenderTarget</c>'s warning and applies here for the same reason. A preview that
///         went blank is a target that was recreated and not re-registered — so a source that cannot
///         guarantee its handle is live should answer with a colour instead.
///     </para>
/// </remarks>
public readonly record struct NodePreview(
    Color4 Color,
    string Label = "",
    ulong Image = 0,
    bool FlipVertically = false
);

/// <summary>Where a node's preview comes from.</summary>
/// <remarks>
///     Implemented per graph, because what a node's output <i>is</i> is that graph's business: a
///     shader graph evaluates the node's expression against a fixed input, and a VFX graph has no
///     value to show at all for a spawner. The framework knows only that a node type asked for one —
///     <see cref="NodeAttribute.Preview" /> — and where to draw it.
/// </remarks>
public interface INodePreviewSource {
    /// <summary>The preview for one node, if it has one to show.</summary>
    /// <param name="graph">The graph it is in.</param>
    /// <param name="node">The node.</param>
    /// <param name="definition">Its type.</param>
    /// <param name="preview">What to draw.</param>
    /// <returns><see langword="true" /> if there is anything to draw.</returns>
    bool TryGet(NodeGraphModel graph, GraphNode node, NodeTypeDefinition definition, out NodePreview preview);
}

/// <summary>The swatches hanging under the nodes that asked for one.</summary>
/// <remarks>
///     <para>
///         <b>Drawn rather than built, for <c>NodeMinimap</c>'s reason.</b> A preview is one filled
///         rectangle; as an element it would be a style node, a layout box and a rebind for a picture
///         nobody clicks.
///     </para>
///     <para>
///         ⚠ <b>Under the node rather than inside it.</b> A node's height is
///         <c>NodeCanvas.HeightOf</c>, which is its ports and nothing else, and there is no way to
///         make one taller for a preview without teaching the canvas about previews. Hanging the
///         swatch below the box needs nothing from the canvas and does not move a single port anchor —
///         and it is where Unity's shader graph puts one anyway.
///     </para>
///     <para>
///         ⚠ <b>It does not paint over the minimap.</b> The layer is a child of the canvas and is
///         therefore drawn after every part of it, minimap included, so a node that happens to lie
///         under the overview would otherwise put a swatch on top of it.
///     </para>
/// </remarks>
public sealed class NodePreviewLayer : UiElement {
    /// <inheritdoc />
    protected override string TagName => "node-previews";

    /// <summary>The view it draws for.</summary>
    public NodeGraphView? View { get; internal set; }

    /// <summary>How tall a swatch is, in graph units.</summary>
    public float Size { get; set; } = 44f;

    /// <summary>How far under the node it hangs, in graph units.</summary>
    public float Gap { get; set; } = 4f;

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (View is not { PreviewSource: { } source } view) {
            return;
        }

        var canvas = view.Canvas;
        var reserved = canvas.Minimap.Bounds;

        foreach (var shown in canvas.Visible) {
            if (view.NodeOf(shown) is not { } node
                || view.Definition(node.Type) is not { Preview: true } definition
                || !source.TryGet(view.Graph, node, definition, out var preview)) {
                continue;
            }

            var box = canvas.RectOf(shown);
            var origin = canvas.ToScreen(new Vector2(box.X + ((box.Width - Size) * 0.5f), box.Bottom + Gap));
            var swatch = new Rectangle(origin.X, origin.Y, Size * canvas.Zoom, Size * canvas.Zoom);

            if (swatch.Intersects(reserved)) {
                continue;
            }

            // The chequer first either way, so a preview with alpha in it reads as translucent rather
            // than as a darker colour — which is the whole question an author asks a mask node's
            // preview, and is as true of a rendered thumbnail as of a flat colour.
            Chequer(context, swatch);

            if (preview.Image != 0) {
                // The same command and the same flip question as Viewport: a scene renders with the
                // engine's Y up and an interface draws with Y down, so a target sampled as it stands
                // is upside down.
                context.DrawImage(
                    swatch,
                    preview.Image,
                    preview.Color,
                    preview.FlipVertically ? new Rectangle(0f, 1f, 1f, -1f) : new Rectangle(0f, 0f, 1f, 1f)
                );
            } else {
                context.FillRectangle(swatch, preview.Color, 2f);
            }

            context.StrokeRectangle(swatch, canvas.WireColor, 1f);
        }
    }

    /// <summary>Two greys behind a swatch, so alpha reads as alpha.</summary>
    static void Chequer(DrawContext context, Rectangle box) {
        context.FillRectangle(box, new Color4(0.82f, 0.82f, 0.82f, 1f), 2f);

        var half = box.Width * 0.5f;
        var dark = new Color4(0.62f, 0.62f, 0.62f, 1f);

        context.FillRectangle(new Rectangle(box.X, box.Y, half, box.Height * 0.5f), dark);
        context.FillRectangle(new Rectangle(box.X + half, box.Y + (box.Height * 0.5f), half, box.Height * 0.5f), dark);
    }
}
