// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;

namespace Vixen.Editor.NodeGraph;

/// <summary>What a node's output looks like, small, under the node.</summary>
/// <param name="Color">The colour to show when there is no image, or the tint when there is one.</param>
/// <param name="Image">
///     The renderer's handle for a thumbnail rendered into a target, or zero for a flat swatch.
/// </param>
/// <param name="FlipVertically">Whether that target is upside down, as a scene's is.</param>
/// <param name="Unavailable">
///     Whether this node has no picture <i>for a reason</i>, rather than no picture yet.
/// </param>
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
///     <para>
///         ⚠ <b><see cref="Unavailable" /> is the third kind, and it replaced a <c>Label</c> that
///         nothing could ever have read</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1092">#1092</a>. That parameter offered
///         "a few characters over it", every producer passed the default, and the reason no reader
///         was ever written is structural rather than an oversight: <see cref="NodePreviewLayer" />
///         <em>draws</em> — deliberately, for <c>NodeMinimap</c>'s reason — and <c>DrawContext</c>
///         has no text primitive at all. A <c>DrawCommandKind.Text</c> carries a shaped glyph run on
///         a baseline and only <c>DrawListBuilder</c>, walking a laid-out text block, can produce
///         one. So the honest channel for "this one has no picture and that is not a fault" is a
///         flag the layer can draw, and this is it.
///     </para>
///     <para>
///         <b>Why the distinction is worth a field.</b> A source answering <see langword="false" />
///         leaves no swatch, and a <em>missing</em> swatch is indistinguishable from a preview source
///         that has stopped running — which is
///         <a href="https://github.com/Rikarin/Vixen/issues/1089">#1089</a> one level up, where a
///         plugin was being unloaded and the only symptom was a blank canvas. A node whose picture
///         only a host with an asset database could supply is a different state and now looks
///         different.
///     </para>
/// </remarks>
public readonly record struct NodePreview(
    Color4 Color,
    ulong Image = 0,
    bool FlipVertically = false,
    bool Unavailable = false
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

            if (preview.Unavailable) {
                // ⚠ Drawn rather than omitted, which is the whole of #1092. The source is saying
                // "there is no picture here and that is not a fault" — a bitmap only a host with an
                // asset database can resolve, and everything computed from it — and the alternative
                // it had was to answer `false`, which leaves a gap that reads exactly like a preview
                // source that has stopped running.
                Hatch(context, swatch, canvas.WireColor);
            } else if (preview.Image != 0) {
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

    /// <summary>Diagonal bars across a swatch whose picture is somebody else's to supply.</summary>
    /// <param name="context">Where to draw.</param>
    /// <param name="box">The swatch.</param>
    /// <param name="color">The canvas's wire colour, so it belongs to the graph rather than to a theme.</param>
    /// <remarks>
    ///     ⚠ <b>Bars and not a flat colour, because a flat colour is what an ordinary constant node
    ///     shows.</b> The one thing this has to do is be unmistakable for both of the other two
    ///     states — a picture and a swatch — from across a canvas at any zoom, and a hatch over the
    ///     chequer is neither. Four of them at fractions of the swatch's own side, so the count does
    ///     not change with the zoom and the shape is the same at every size.
    ///
    ///     ⚠ Every segment ends on an edge rather than being clipped to one: a swatch is square —
    ///     <see cref="Size" /> by <see cref="Size" />, times the zoom — so a bar from
    ///     <c>(x + offset, top)</c> to <c>(x, top + offset)</c> is inside the box by construction.
    ///     The layer is a sibling of the canvas's own drawing and nothing here establishes a clip, so
    ///     a bar that ran past the corner would be painted over the node above it.
    /// </remarks>
    static void Hatch(DrawContext context, Rectangle box, Color4 color) {
        PathBuilder bars = new();

        for (var bar = 1; bar <= 4; bar++) {
            var offset = box.Width * bar * 0.25f;

            bars.MoveTo(new Vector2(box.X + offset, box.Y));
            bars.LineTo(new Vector2(box.X, box.Y + offset));
        }

        context.Stroke(bars, color, Math.Max(1f, box.Width * 0.03f));
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
