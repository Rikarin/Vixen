// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;

namespace Vixen.Editor.NodeGraph;

/// <summary>What a node's output looks like, small, under the node.</summary>
/// <param name="Color">The colour to show. Alpha is honoured, over a chequer.</param>
/// <param name="Label">A few characters over it — a number, a width — or empty.</param>
/// <remarks>
///     ⚠ <b>A colour rather than an image, and that is a limit of the framework rather than a
///     design.</b> Doc 11 asks for "live preview thumbnails", which for a shader graph means rendering
///     the node's expression over a quad; <c>Viewport</c> in <c>Vixen.Ui.Controls.Advanced</c> draws a
///     placeholder for exactly this reason — the draw list has no texture command yet. A swatch is
///     what can be drawn honestly today, and it is genuinely useful: it is what a constant, a colour,
///     a mask and a channel split all reduce to. When the draw list grows a texture command this
///     becomes a second case here and nothing else moves.
/// </remarks>
public readonly record struct NodePreview(Color4 Color, string Label = "");

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

            // The chequer first, so a preview with alpha in it reads as translucent rather than as a
            // darker colour — which is the whole question an author asks a mask node's preview.
            Chequer(context, swatch);

            context.FillRectangle(swatch, preview.Color, 2f);
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
