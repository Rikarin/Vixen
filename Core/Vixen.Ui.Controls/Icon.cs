// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls;

/// <summary>A small piece of vector art, drawn in the current text colour.</summary>
/// <remarks>
///     <para>
///         <b>A path rather than an image</b>, because an icon is not a picture: it is drawn at
///         whatever size the layout gives it, it changes colour with the text around it, and there
///         are several hundred of them in an editor. A texture per icon per size per colour is how
///         icon sets become the largest thing in a build.
///     </para>
///     <para>
///         ⚠ <b>The geometry is handed in rather than parsed from a string.</b> There is no SVG path
///         parser here, and that is a decision rather than an omission: an icon set is compiled
///         content, so the place to turn <c>"M12 2L2 22h20z"</c> into segments is the asset pipeline,
///         once, rather than every application at start-up. A <see cref="PathBuilder" /> is what
///         comes out the other end, and it is what this takes.
///     </para>
///     <para>
///         <see cref="ViewBox" /> is what makes an icon set interchangeable. Geometry authored
///         against a 24×24 grid is drawn into whatever box the layout produced, scaled uniformly and
///         centred — so a 16-pixel toolbar and a 32-pixel button share one definition, and an icon
///         drawn against a different grid says so instead of coming out the wrong size.
///     </para>
/// </remarks>
public sealed partial class Icon : Control {
    readonly PathBuilder scaled = new();

    /// <inheritdoc />
    protected override string TagName => "icon";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The art, in <see cref="ViewBox" /> coordinates.</summary>
    /// <remarks>
    ///     Shared freely — an icon set is one <see cref="PathBuilder" /> per icon and a hundred
    ///     elements pointing at the same one. Nothing here mutates it; the scaling happens into a
    ///     buffer of this element's own.
    /// </remarks>
    [UiProperty]
    public partial PathBuilder? Geometry { get; set; }

    /// <summary>The grid the geometry was authored against.</summary>
    [UiProperty]
    public partial Rectangle ViewBox { get; set; }

    /// <summary>How the inside of the path is decided.</summary>
    [UiProperty]
    public partial PathFillRule FillRule { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // A zero view box would divide by zero on the first draw, and a default that has to be
        // written by every caller is a default that is sometimes not written. Twenty-four is what
        // Material, Lucide, Feather and Fluent all author against.
        if (ViewBox.IsEmpty) {
            ViewBox = new Rectangle(0f, 0f, 24f, 24f);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Rescaled on every draw rather than when the geometry changes</b>, because the thing
    ///     it is scaled to is the layout result, and nothing tells an element that its box changed —
    ///     a sibling growing resizes this one without touching a property on it. The cost is a walk
    ///     of a path that is a dozen segments long; the alternative is an icon that keeps the size
    ///     it had when the window was a different shape.
    /// </remarks>
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (Geometry is not { Count: > 0 } geometry) {
            return;
        }

        var bounds = context.Bounds;
        if (bounds.Width <= 0f || bounds.Height <= 0f) {
            return;
        }

        // Uniform, and centred in whatever is left over. Fitting each axis separately would let a
        // wide box stretch a circle into an ellipse, which is the one thing every icon set is drawn
        // on a square grid to avoid.
        var scale = MathF.Min(bounds.Width / ViewBox.Width, bounds.Height / ViewBox.Height);
        var offsetX = bounds.X + ((bounds.Width - (ViewBox.Width * scale)) * 0.5f) - (ViewBox.X * scale);
        var offsetY = bounds.Y + ((bounds.Height - (ViewBox.Height * scale)) * 0.5f) - (ViewBox.Y * scale);

        scaled.Clear();

        foreach (var segment in geometry.Segments) {
            var p0 = new Vector2((segment.P0.X * scale) + offsetX, (segment.P0.Y * scale) + offsetY);
            var p1 = new Vector2((segment.P1.X * scale) + offsetX, (segment.P1.Y * scale) + offsetY);
            var p2 = new Vector2((segment.P2.X * scale) + offsetX, (segment.P2.Y * scale) + offsetY);

            // ⚠ The end point is P2 for every verb, including the ones with no control points —
            // which is what makes this switch about the control points only. Reading a line's
            // destination out of P0 gives the origin, so every icon collapses onto the top-left
            // corner of its box and looks like a scaling bug rather than a transcription one.
            switch (segment.Verb) {
                case PathVerb.Move:
                    scaled.MoveTo(p2);
                    break;

                case PathVerb.Line:
                    scaled.LineTo(p2);
                    break;

                case PathVerb.Quadratic:
                    scaled.QuadraticTo(p0, p2);
                    break;

                case PathVerb.Cubic:
                    scaled.CubicTo(p0, p1, p2);
                    break;

                default:
                    scaled.Close();
                    break;
            }
        }

        context.Fill(scaled, context.Foreground, FillRule);
    }
}
