// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls;

/// <summary>The handful of glyphs the controls themselves are made of.</summary>
/// <remarks>
///     <para>
///         Not an icon set — an application brings its own, and this is eight shapes without which
///         the controls in this assembly cannot be drawn at all: a checkbox has to have a tick and a
///         combo box has to have a chevron. Shipping them here is what makes the set work out of the
///         box on a machine with no content pipeline and no fonts registered.
///     </para>
///     <para>
///         ⚠ <b>One instance each, shared by every control that uses it.</b> A
///         <see cref="PathBuilder" /> is mutable, so this is only safe because nothing here ever
///         hands one out to be drawn into — <see cref="Icon" /> scales into a buffer of its own and
///         reads this. A caller that mutates one of these has changed every checkbox in the process,
///         which is worth knowing and is the same bargain as any other shared immutable-by-convention
///         resource.
///     </para>
///     <para>
///         All eight are authored against the 24×24 grid <see cref="Icon.ViewBox" /> defaults to, so
///         they interchange with Material, Lucide and Feather without a per-icon box.
///     </para>
/// </remarks>
public static class ControlIcons {
    /// <summary>A tick. The checkbox.</summary>
    public static PathBuilder Check { get; } = Stroke(
        [new Vector2(4f, 12.5f), new Vector2(9.5f, 18f), new Vector2(20f, 6.5f)],
        2.2f
    );

    /// <summary>A horizontal bar. A checkbox that is neither on nor off.</summary>
    public static PathBuilder Dash { get; } = Stroke([new Vector2(5f, 12f), new Vector2(19f, 12f)], 2.2f);

    /// <summary>A chevron pointing down. What a combo box and an expander open with.</summary>
    public static PathBuilder ChevronDown { get; } = Stroke(
        [new Vector2(5f, 9f), new Vector2(12f, 16f), new Vector2(19f, 9f)],
        2.2f
    );

    /// <summary>A chevron pointing up.</summary>
    public static PathBuilder ChevronUp { get; } = Stroke(
        [new Vector2(5f, 15f), new Vector2(12f, 8f), new Vector2(19f, 15f)],
        2.2f
    );

    /// <summary>A chevron pointing right. A collapsed tree node, a breadcrumb separator.</summary>
    public static PathBuilder ChevronRight { get; } = Stroke(
        [new Vector2(9f, 5f), new Vector2(16f, 12f), new Vector2(9f, 19f)],
        2.2f
    );

    /// <summary>A chevron pointing left.</summary>
    public static PathBuilder ChevronLeft { get; } = Stroke(
        [new Vector2(15f, 5f), new Vector2(8f, 12f), new Vector2(15f, 19f)],
        2.2f
    );

    /// <summary>A cross. What closes a dialog, a drawer and a removable tag.</summary>
    public static PathBuilder Close { get; } = Cross();

    /// <summary>A magnifying glass. The search box.</summary>
    public static PathBuilder Search { get; } = Magnifier();

    /// <summary>Turns a polyline into a fillable outline of a given width.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Filled rather than stroked, and the reason is the draw list.</b> A stroke command
    ///         carries one thickness in document space, so an icon scaled from 24 to 16 would keep
    ///         the line weight it was authored with and read as heavy — while the same icon at 32
    ///         would read as spidery. Expanding the outline here makes the thickness part of the
    ///         geometry, so it scales with everything else.
    ///     </para>
    ///     <para>
    ///         The expansion is the cheap one: each segment becomes a quad, and the corners are
    ///         covered by a square at each joint rather than by a mitre. At these sizes the
    ///         difference is invisible and the arithmetic is four lines instead of forty.
    ///     </para>
    /// </remarks>
    static PathBuilder Stroke(ReadOnlySpan<Vector2> points, float width) {
        var path = new PathBuilder();
        var half = width * 0.5f;

        for (var i = 0; i + 1 < points.Length; i++) {
            var from = points[i];
            var to = points[i + 1];

            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var length = MathF.Sqrt((dx * dx) + (dy * dy));

            if (length <= float.Epsilon) {
                continue;
            }

            var nx = -dy / length * half;
            var ny = dx / length * half;

            path.MoveTo(new Vector2(from.X + nx, from.Y + ny));
            path.LineTo(new Vector2(to.X + nx, to.Y + ny));
            path.LineTo(new Vector2(to.X - nx, to.Y - ny));
            path.LineTo(new Vector2(from.X - nx, from.Y - ny));
            path.Close();
        }

        // The joints, as squares. Skipped at the ends, where a butt cap is what a tick wants
        // anyway — a rounded one would need a curve per end for a difference of a pixel.
        for (var i = 1; i + 1 < points.Length; i++) {
            path.AddRectangle(new Rectangle(points[i].X - half, points[i].Y - half, width, width));
        }

        return path;
    }

    static PathBuilder Cross() {
        var path = Stroke([new Vector2(6f, 6f), new Vector2(18f, 18f)], 2.2f);
        var other = Stroke([new Vector2(18f, 6f), new Vector2(6f, 18f)], 2.2f);

        foreach (var segment in other.Segments) {
            switch (segment.Verb) {
                case PathVerb.Move:
                    path.MoveTo(segment.P2);
                    break;

                case PathVerb.Line:
                    path.LineTo(segment.P2);
                    break;

                default:
                    path.Close();
                    break;
            }
        }

        return path;
    }

    /// <summary>A circle with a handle, as an annulus plus a bar.</summary>
    /// <remarks>
    ///     The ring is two ellipses wound the same way and filled with the even-odd rule, which is
    ///     what <see cref="Icon.FillRule" /> is set to for this one. Winding the inner one backwards
    ///     to get a non-zero hole would work equally well and would make the icon's correctness
    ///     depend on a detail of <see cref="PathBuilder.AddEllipse" /> nobody would think to check.
    /// </remarks>
    static PathBuilder Magnifier() {
        var path = new PathBuilder();
        path.AddEllipse(new Rectangle(3f, 3f, 14f, 14f));
        path.AddEllipse(new Rectangle(5f, 5f, 10f, 10f));

        var handle = Stroke([new Vector2(15.5f, 15.5f), new Vector2(21f, 21f)], 2.4f);

        foreach (var segment in handle.Segments) {
            switch (segment.Verb) {
                case PathVerb.Move:
                    path.MoveTo(segment.P2);
                    break;

                case PathVerb.Line:
                    path.LineTo(segment.P2);
                    break;

                default:
                    path.Close();
                    break;
            }
        }

        return path;
    }
}
