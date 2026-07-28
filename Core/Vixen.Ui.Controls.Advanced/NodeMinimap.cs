// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>The whole graph, small, in the corner of the canvas.</summary>
/// <remarks>
///     <para>
///         <b>Drawn rather than built.</b> A minimap of ten thousand nodes is ten thousand
///         rectangles, and as elements that would be ten thousand style nodes and layout boxes for a
///         picture nobody clicks on individually — the one control in this assembly where custom
///         drawing is cheaper than the cascade by three orders of magnitude rather than by a
///         constant.
///     </para>
///     <para>
///         ⚠ <b>The scale is the graph's extent, not a fixed zoom.</b> A minimap at a fixed fraction
///         of the canvas's zoom shows nothing at all once the user has zoomed in, which is when they
///         need it. It also means the picture moves when a node is dragged past the old edge, which
///         is correct and looks like a bug until you know why.
///     </para>
/// </remarks>
public sealed partial class NodeMinimap : Control {
    bool dragging;

    /// <inheritdoc />
    protected override string TagName => "node-minimap";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The canvas it is an overview of.</summary>
    public NodeCanvas? Canvas { get; internal set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();
        AddHandler<PointerEvent>(static (element, args) => ((NodeMinimap) element).Pointed(args));
    }

    /// <summary>What the whole picture covers, in graph units.</summary>
    /// <remarks>
    ///     <para>
    ///         The nodes and the viewport together, so that panning off into empty space still shows
    ///         where the viewport went rather than sliding it off the edge of a map of the nodes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>O(nodes) every time it is read</b>, which is why nothing here reads it twice.
    ///         <see cref="Projection" /> takes it and the scale together, once, and everything that
    ///         draws or converts a coordinate works from that — the version that computed both
    ///         inside <c>ToScreen</c> was quadratic in the size of the graph and took four minutes
    ///         to draw ten thousand nodes.
    ///     </para>
    /// </remarks>
    public Rectangle Extent {
        get {
            if (Canvas is not { } canvas) {
                return Rectangle.Empty;
            }

            var bounds = canvas.View;

            foreach (var node in canvas.Graph.Nodes) {
                bounds = Rectangle.Union(bounds, canvas.RectOf(node));
            }

            return bounds;
        }
    }

    /// <summary>How many minimap pixels one graph unit is.</summary>
    public float Scale => Projection().Scale;

    /// <summary>The extent, the scale and the centring offset, worked out together.</summary>
    /// <remarks>
    ///     The slack is what centres the picture in whichever direction has room to spare, so a wide
    ///     graph in a square map sits in the middle of it rather than along the top edge.
    /// </remarks>
    Fit Projection() {
        var extent = Extent;
        var bounds = Bounds;

        if (extent.Width <= 0f || extent.Height <= 0f || bounds.Width <= 0f || bounds.Height <= 0f) {
            return new Fit(extent, 0f, default);
        }

        var scale = MathF.Min(bounds.Width / extent.Width, bounds.Height / extent.Height);

        return new Fit(
            extent,
            scale,
            new Vector2(
                bounds.X + ((bounds.Width - (extent.Width * scale)) * 0.5f),
                bounds.Y + ((bounds.Height - (extent.Height * scale)) * 0.5f)
            )
        );
    }

    /// <summary>Where a graph point lands on the map, in document space.</summary>
    /// <param name="point">The graph point.</param>
    /// <returns>The document point.</returns>
    public Vector2 ToScreen(Vector2 point) => Projection().ToScreen(point);

    /// <summary>Where a point on the map is, in graph units.</summary>
    /// <param name="x">Its x, in document space.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The graph point.</returns>
    public Vector2 ToGraph(float x, float y) {
        var fit = Projection();

        return fit.Scale <= 0f
            ? default
            : new Vector2(
                fit.Extent.X + ((x - fit.Origin.X) / fit.Scale),
                fit.Extent.Y + ((y - fit.Origin.Y) / fit.Scale)
            );
    }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var fit = Projection();

        if (Canvas is not { } canvas || fit.Scale <= 0f) {
            return;
        }

        var foreground = context.Foreground;

        foreach (var node in canvas.Graph.Nodes) {
            var rectangle = canvas.RectOf(node);
            var origin = fit.ToScreen(rectangle.Position);

            context.FillRectangle(
                new Rectangle(
                    origin.X,
                    origin.Y,
                    // ⚠ At least a pixel. A node a hundred graph units wide in a map scaled to a
                    // thousandth is a rectangle of a tenth of a pixel, which the rasteriser drops —
                    // and a minimap of a large graph would then be blank.
                    MathF.Max(1f, rectangle.Width * fit.Scale),
                    MathF.Max(1f, rectangle.Height * fit.Scale)
                ),
                canvas.Selection.Contains(node) ? canvas.WireActiveColor : foreground,
                1f
            );
        }

        var view = canvas.View;
        var corner = fit.ToScreen(view.Position);

        var window = new Rectangle(corner.X, corner.Y, view.Width * fit.Scale, view.Height * fit.Scale);

        context.FillRectangle(window, canvas.MarqueeColor);
        context.StrokeRectangle(window, canvas.WireActiveColor, 1f);
    }

    readonly record struct Fit(Rectangle Extent, float Scale, Vector2 Origin) {
        public Vector2 ToScreen(Vector2 point) =>
            new(Origin.X + ((point.X - Extent.X) * Scale), Origin.Y + ((point.Y - Extent.Y) * Scale));
    }

    /// <remarks>
    ///     ⚠ <b>The click centres the view rather than putting the corner there.</b> A map is read as
    ///     "I want to be looking at that", and a corner-anchored jump puts what was pointed at in the
    ///     top-left of the canvas, which is never what was meant.
    /// </remarks>
    void Pointed(PointerEvent args) {
        if (Canvas is not { } canvas) {
            return;
        }

        switch (args.Action) {
            case PointerAction.Pressed when args.Button == PointerButton.Primary:
                dragging = true;
                Document.CapturePointer(this);

                Centre(canvas, args);
                break;

            case PointerAction.Moved when dragging:
                Centre(canvas, args);
                break;

            case PointerAction.Released when dragging:
                dragging = false;
                Document.ReleasePointer();

                break;

            default:
                return;
        }

        args.Handled = true;
    }

    void Centre(NodeCanvas canvas, PointerEvent args) {
        var point = ToGraph(args.X, args.Y);
        var view = canvas.View;

        canvas.Pan = new Vector2(point.X - (view.Width * 0.5f), point.Y - (view.Height * 0.5f));
    }
}
