// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Geometry;

namespace Vixen.Editor.Blockout;

/// <summary>One point of a knife stroke: which face, and where on its boundary.</summary>
/// <param name="Face">The face the pointer was over.</param>
/// <param name="Point">Where the cut passes, in the mesh's own space, snapped to that face's boundary.</param>
public readonly record struct KnifePoint(int Face, Vector3 Point);

/// <summary>doc 24 § P3's knife gesture: a path the pointer draws, previewed before it commits.</summary>
/// <remarks>
///     <para>
///         <b>§ P3 called the knife "the one row of the table left undone" and said what it needed:
///         a kernel primitive — <see cref="MeshOperations.Knife" /> — and a gesture round it.</b> This
///         is the gesture. It holds the points, snaps each one, previews the path, and turns the
///         stroke into cuts; the arithmetic is all in the kernel, where a cube and an assertion can
///         reach it.
///     </para>
///     <para>
///         ⚠ <b>Every point lands on a face's boundary, which is what makes the primitive usable at
///         all.</b> "Split this face between these two points" is only defined for points on its rim,
///         so a click in the middle of a face has to become a click on its nearest edge. Corners and
///         midpoints win inside a fraction of the edge's own length — § P3's "snapping to edges and
///         midpoints" — and the fraction is relative because an absolute radius is a claim about how
///         big the model is.
///     </para>
///     <para>
///         ⚠ <b>The stroke is one command and it is not applied a segment at a time.</b> § P3: "a
///         cut's new vertices renumber the face table, so the cut has to be a single command". Every
///         segment becomes a <see cref="KnifeCut" /> and they all go into one call.
///     </para>
///     <para>
///         ⚠ <b>A segment across a face boundary produces a cut for <i>both</i> faces, and the kernel
///         is the arbiter.</b> A click on a shared edge belongs to the face on either side of it, and
///         which one the picker happened to answer with is not something a designer chose;
///         <see cref="MeshOperations.Knife" /> refuses whichever of the two the segment does not
///         actually cross, which is one rule about what is on a boundary rather than two.
///     </para>
/// </remarks>
public sealed class BlockoutKnife {
    readonly List<KnifePoint> points = [];

    /// <summary>How near a corner or a midpoint counts, as a fraction of the edge's own length.</summary>
    public float SnapFraction { get; set; } = 0.2f;

    /// <summary>Whether the tool is holding the pointer.</summary>
    public bool IsArmed { get; set; }

    /// <summary>The points placed so far, in order.</summary>
    public IReadOnlyList<KnifePoint> Points => points;

    /// <summary>Where the next point would go, or <see langword="null" /> when the pointer is off the mesh.</summary>
    public KnifePoint? Hover { get; private set; }

    /// <summary>Follows the pointer.</summary>
    /// <param name="mesh">The mesh being cut.</param>
    /// <param name="placement">Where it is, in world space.</param>
    /// <param name="ray">The pointer's ray, in world space.</param>
    /// <returns>Whether the pointer is over the mesh.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    public bool Track(EditMesh mesh, in Matrix4x4 placement, Ray ray) {
        ArgumentNullException.ThrowIfNull(mesh);

        Hover = Resolve(mesh, placement, ray, SnapFraction);

        return Hover is not null;
    }

    /// <summary>Places the point the pointer is over.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Place() {
        if (Hover is not { } point) {
            return false;
        }

        points.Add(point);

        return true;
    }

    /// <summary>Throws the stroke away.</summary>
    public void Cancel() {
        points.Clear();

        Hover = null;
        IsArmed = false;
    }

    /// <summary>The stroke as cuts, one per segment per face the segment could belong to.</summary>
    /// <returns>The cuts, which is empty for a stroke of fewer than two points.</returns>
    public IReadOnlyList<KnifeCut> Cuts() {
        var cuts = new List<KnifeCut>();

        for (var step = 0; step + 1 < points.Count; step++) {
            var from = points[step];
            var to = points[step + 1];

            cuts.Add(new(from.Face, from.Point, to.Point));

            if (to.Face != from.Face) {
                cuts.Add(new(to.Face, from.Point, to.Point));
            }
        }

        return cuts;
    }

    /// <summary>Draws the stroke and the segment the pointer is dragging out of its last point.</summary>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="placement">The entity's world matrix.</param>
    /// <param name="placed">What a committed segment is drawn in.</param>
    /// <param name="pending">What the segment following the pointer is drawn in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draw" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Two colours, because the two halves are different promises.</b> What is behind the
    ///     pointer is where the cut <i>will</i> go and what is in front of it is where it would go if
    ///     you clicked now — one colour for both is a stroke a designer cannot tell they have already
    ///     placed.
    /// </remarks>
    public void Preview(GizmoDraw draw, in Matrix4x4 placement, Color4 placed, Color4 pending) {
        ArgumentNullException.ThrowIfNull(draw);

        for (var step = 0; step + 1 < points.Count; step++) {
            draw.Line(
                Matrix4x4.TransformPosition(points[step].Point, placement),
                Matrix4x4.TransformPosition(points[step + 1].Point, placement),
                placed
            );
        }

        if (Hover is not { } hover) {
            return;
        }

        var at = Matrix4x4.TransformPosition(hover.Point, placement);

        if (points.Count > 0) {
            draw.Line(Matrix4x4.TransformPosition(points[^1].Point, placement), at, pending);
        }

        // A cross at the point itself, so a stroke of one point is visible and so a snap that landed
        // on a corner is legible as one. Sized off the segment it is drawn with rather than fixed.
        draw.Cross(at, Reach(points, hover, placement), pending);
    }

    /// <summary>Where the pointer is on the mesh, snapped to the face's boundary.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="placement">Where it is, in world space.</param>
    /// <param name="ray">The pointer's ray, in world space.</param>
    /// <param name="fraction">How near a corner or a midpoint counts, as a fraction of the edge.</param>
    /// <returns>The point, or <see langword="null" /> when the ray misses.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The ray is taken into the mesh's own space rather than the mesh into the world.</b> An
    ///     inverse of one matrix against a transform of every position is the same arithmetic the
    ///     sub-object picker does, and for the same reason: a mesh has thousands of positions and a
    ///     ray has two.
    /// </remarks>
    public static KnifePoint? Resolve(EditMesh mesh, in Matrix4x4 placement, Ray ray, float fraction = 0.2f) {
        ArgumentNullException.ThrowIfNull(mesh);

        if (!Matrix4x4.Invert(placement, out var inverse)) {
            // A zero scale, which has no surface to hit. An entity can be scaled to nothing and back,
            // and a tool that threw would take the editor with it.
            return null;
        }

        var local = new Ray(
            Matrix4x4.TransformPosition(ray.Origin, inverse),
            Matrix4x4.TransformDirection(ray.Direction, inverse)
        );

        var nearest = float.MaxValue;
        var found = -1;
        var at = Vector3.Zero;

        for (var face = 0; face < mesh.FaceCount; face++) {
            var loop = mesh.CornersOf(face);

            // A fan from the first corner. A block-out face is convex nearly always, and where it is
            // not the fan under-covers rather than answering wrongly — the pointer has to be nearer
            // the middle of a reflex n-gon than any of its ears for that to show.
            for (var corner = 1; corner + 1 < loop.Length; corner++) {
                var a = mesh.Positions[loop[0]];
                var b = mesh.Positions[loop[corner]];
                var c = mesh.Positions[loop[corner + 1]];

                if (!local.Intersects(a, b, c, out var distance) || distance < 0f || distance >= nearest) {
                    continue;
                }

                nearest = distance;
                found = face;
                at = local.GetPoint(distance);
            }
        }

        return found < 0 ? null : new(found, Snap(mesh, found, at, fraction));
    }

    /// <summary>The nearest point of a face's boundary, preferring its corners and its midpoints.</summary>
    static Vector3 Snap(EditMesh mesh, int face, Vector3 point, float fraction) {
        var loop = mesh.CornersOf(face);
        var best = float.MaxValue;
        var found = point;

        for (var corner = 0; corner < loop.Length; corner++) {
            var a = mesh.Positions[loop[corner]];
            var b = mesh.Positions[loop[(corner + 1) % loop.Length]];
            var along = b - a;
            var length = along.LengthSquared();

            if (length <= 0f) {
                continue;
            }

            var t = Math.Clamp(Vector3.Dot(point - a, along) / length, 0f, 1f);
            var on = a + (along * t);
            var distance = Vector3.DistanceSquared(on, point);

            if (distance >= best) {
                continue;
            }

            best = distance;

            // ⚠ The corners first and the midpoint after, because a designer aiming at the end of a
            // short edge is aiming at the corner. Reversing them would make the midpoint of a
            // two-corner-wide edge swallow both ends.
            found = t <= fraction
                ? a
                : t >= 1f - fraction
                    ? b
                    : MathF.Abs(t - 0.5f) <= fraction * 0.5f
                        ? a + (along * 0.5f)
                        : on;
        }

        return found;
    }

    /// <summary>How long the cursor cross's arms are: a fraction of the stroke, or of the face.</summary>
    static float Reach(List<KnifePoint> points, in KnifePoint hover, in Matrix4x4 placement) {
        if (points.Count == 0) {
            return 0.05f * MathF.Max(Scale(placement), 1e-4f);
        }

        var span = Vector3.Distance(
            Matrix4x4.TransformPosition(points[^1].Point, placement),
            Matrix4x4.TransformPosition(hover.Point, placement)
        );

        return MathF.Max(span * 0.08f, 0.02f * Scale(placement));
    }

    /// <summary>How big the entity is, as the length of its longest axis.</summary>
    static float Scale(in Matrix4x4 placement) =>
        MathF.Max(
            new Vector3(placement.M11, placement.M12, placement.M13).Length(),
            MathF.Max(
                new Vector3(placement.M21, placement.M22, placement.M23).Length(),
                new Vector3(placement.M31, placement.M32, placement.M33).Length()
            )
        );
}
