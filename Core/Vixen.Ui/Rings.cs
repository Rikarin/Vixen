// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui;

/// <summary>A border ring's centre line, and the marks of a broken one along it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A dashed border is a stroked path and a solid one is a distance field, and the two
///         being different machinery is the whole reason this file exists.</b> The box shader
///         resolves a ring as the difference of two coverages — the outline, and the outline pushed
///         <c>thickness</c> inwards — which is exact at any radius and has no notion of *where along
///         the ring* a fragment is. A dash needs exactly that: an arc length. So a broken border is
///         emitted as <see cref="DrawCommandKind.PathStroke" />, which the tessellator, the geometry
///         builder, the solid pipeline and the software rasteriser already draw, and no shader, no
///         command kind and no second executor is added for it.
///     </para>
///     <para>
///         ⚠ <b>The centre line, not the outer edge.</b> A stroke is centred on its path, so the ring
///         that occupies <c>[edge, edge + thickness]</c> is the box inset by half the thickness with
///         its radii reduced by the same. Stroking the border box itself paints half the line outside
///         the element, which is an outline rather than a border and overlaps whatever is next to it.
///     </para>
///     <para>
///         ⚠ <b>The corners are sampled rather than flattened by <c>PathFlattener</c>, and that is a
///         choice about who owns the tolerance.</b> The dash walk needs arc <i>length</i>, so it needs
///         a polyline whatever else happens; sampling here means the same polyline is measured and
///         emitted, so the marks cannot be distributed along one curve and drawn along a slightly
///         different one. <see cref="CornerSteps" /> is fixed rather than adaptive because a UI radius
///         is a handful of pixels and the error at eight steps is below a tenth of one.
///     </para>
/// </remarks>
static class Rings {
    /// <summary>How many segments each corner quadrant is sampled into.</summary>
    /// <remarks>
    ///     The chord error of an <c>n</c>-segment quarter arc of radius <c>r</c> is
    ///     <c>r(1 − cos(π/4n))</c>, which at eight steps and a radius of 24 — larger than anything in
    ///     the themes here — is under a fiftieth of a pixel.
    /// </remarks>
    public const int CornerSteps = 8;

    /// <summary>The closed centre line of a ring, as a polyline starting at the top-left corner's end.</summary>
    /// <param name="x">The border box's left edge.</param>
    /// <param name="y">Its top edge.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="corners">Its corner radii, on the border box.</param>
    /// <param name="inset">How far in the centre line sits, which is half the thickness.</param>
    /// <param name="into">Where the points go. Cleared first. The closing point is not repeated.</param>
    public static void Outline(
        float x,
        float y,
        float width,
        float height,
        CornerRadii corners,
        float inset,
        List<Vector2> into
    ) {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();

        var left = x + inset;
        var top = y + inset;
        var right = x + width - inset;
        var bottom = y + height - inset;

        if (right <= left || bottom <= top) {
            return;
        }

        // ⚠ Reduced by the inset and clamped at zero rather than scaled. A radius of 2 on a border 6
        // thick has no curve left at the centre line, and a square corner there is what the distance
        // field draws too — the inner edge of a thick border on a small radius is a right angle in
        // CSS as well.
        var halfWidth = (right - left) * 0.5f;
        var halfHeight = (bottom - top) * 0.5f;

        var topLeft = Shrink(corners.TopLeft, inset, halfWidth, halfHeight);
        var topRight = Shrink(corners.TopRight, inset, halfWidth, halfHeight);
        var bottomRight = Shrink(corners.BottomRight, inset, halfWidth, halfHeight);
        var bottomLeft = Shrink(corners.BottomLeft, inset, halfWidth, halfHeight);

        // Clockwise from just after the top-left corner, which is the order the four `border-*`
        // longhands are interned in and the order a reader traces the box in.
        into.Add(new Vector2(left + topLeft.X, top));
        into.Add(new Vector2(right - topRight.X, top));
        Arc(into, new Vector2(right - topRight.X, top + topRight.Y), topRight, -MathF.PI / 2f, 0f);

        into.Add(new Vector2(right, bottom - bottomRight.Y));
        Arc(into, new Vector2(right - bottomRight.X, bottom - bottomRight.Y), bottomRight, 0f, MathF.PI / 2f);

        into.Add(new Vector2(left + bottomLeft.X, bottom));
        Arc(into, new Vector2(left + bottomLeft.X, bottom - bottomLeft.Y), bottomLeft, MathF.PI / 2f, MathF.PI);

        into.Add(new Vector2(left, top + topLeft.Y));
        Arc(into, new Vector2(left + topLeft.X, top + topLeft.Y), topLeft, MathF.PI, 3f * MathF.PI / 2f);
    }

    /// <summary>Appends a broken line's marks along a closed polyline to a path.</summary>
    /// <param name="outline">The closed centre line, as <see cref="Outline" /> produced it.</param>
    /// <param name="thickness">The line's thickness, which sets the mark and gap lengths.</param>
    /// <param name="style">Which broken style.</param>
    /// <param name="marks">Scratch for the distribution.</param>
    /// <param name="into">The path the marks are appended to, one sub-path each.</param>
    /// <returns>Whether anything was appended.</returns>
    /// <remarks>
    ///     ⚠ <b>One path with a sub-path per mark, and one stroke command over all of them.</b> A
    ///     command each would put twenty draw commands on a dashed box, which the frame diff then
    ///     compares one by one every frame; <see cref="PathBuilder.MoveTo" /> is what a sub-path is,
    ///     and the tessellator already treats each as its own contour.
    /// </remarks>
    public static bool Dash(
        IReadOnlyList<Vector2> outline,
        float thickness,
        StrokeStyle style,
        List<DashMark> marks,
        PathBuilder into
    ) {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(into);

        if (outline.Count < 2) {
            return false;
        }

        var total = 0f;

        for (var i = 0; i < outline.Count; i++) {
            total += Vector2.Distance(outline[i], outline[(i + 1) % outline.Count]);
        }

        Dashes.Along(total, thickness, style, marks);

        if (marks.Count == 0) {
            return false;
        }

        var appended = false;

        foreach (var mark in marks) {
            if (Walk(outline, mark.Start, mark.Start + mark.Length, into)) {
                appended = true;
            }
        }

        return appended;
    }

    /// <summary>Emits the sub-path between two distances along a closed polyline.</summary>
    static bool Walk(IReadOnlyList<Vector2> outline, float from, float to, PathBuilder into) {
        if (to <= from) {
            return false;
        }

        var travelled = 0f;
        var started = false;

        for (var i = 0; i < outline.Count; i++) {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Count];
            var length = Vector2.Distance(a, b);

            if (length <= 0f) {
                continue;
            }

            var next = travelled + length;

            if (next > from && travelled < to) {
                var enter = MathF.Max(from, travelled);
                var leave = MathF.Min(to, next);

                if (!started) {
                    into.MoveTo(Vector2.Lerp(a, b, (enter - travelled) / length));
                    started = true;
                }

                into.LineTo(Vector2.Lerp(a, b, (leave - travelled) / length));
            }

            travelled = next;

            if (travelled >= to) {
                break;
            }
        }

        return started;
    }

    /// <summary>Samples one corner quadrant, excluding its first point, which the caller has added.</summary>
    static void Arc(List<Vector2> into, Vector2 centre, Vector2 radii, float from, float to) {
        if (radii.X <= 0f || radii.Y <= 0f) {
            return;
        }

        for (var step = 1; step <= CornerSteps; step++) {
            var angle = from + ((to - from) * step / CornerSteps);
            into.Add(new Vector2(centre.X + (radii.X * MathF.Cos(angle)), centre.Y + (radii.Y * MathF.Sin(angle))));
        }
    }

    /// <summary>A corner radius moved inwards by the inset, clamped to the box it has to fit in.</summary>
    static Vector2 Shrink(Vector2 corner, float inset, float halfWidth, float halfHeight) => new(
        Math.Clamp(corner.X - inset, 0f, halfWidth),
        Math.Clamp(corner.Y - inset, 0f, halfHeight)
    );
}
