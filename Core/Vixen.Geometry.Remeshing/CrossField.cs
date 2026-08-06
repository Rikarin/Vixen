// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>A 4-RoSy cross field: one direction per vertex, standing for four.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D5.</b> Per-vertex, in the tangent plane, represented by one direction
///         standing for four. The energy is the standard one — over each edge, the smallest angle
///         between the two endpoints' representatives after the best of the four rotations.
///     </para>
///     <para>
///         ⚠ <b>The representative is a direction in <i>world</i> space and never an angle in the
///         vertex's frame, and that is what makes the frame-seeding trap a non-event.</b>
///         <see cref="TangentFrame" /> is seeded from the first neighbour of the ordered one-ring, so
///         two adjacent vertices whose rings start in different places have frames that differ by an
///         arbitrary rotation. A field stored as an angle in that frame is compared across the edge in
///         two inconsistent bases, and it converges to something that is locally smooth in the numbers
///         and visibly torn in the geometry — the classic failure, and one that no amount of iterating
///         fixes because the energy it is minimizing is the wrong one. Storing the direction itself
///         means the only thing the frame is ever used for is the curvature fit, where a rotation of
///         the basis rotates the answer with it.
///     </para>
/// </remarks>
sealed class CrossField {
    readonly Vector3[] directions;

    internal CrossField(Vector3[] directions) {
        this.directions = directions;
    }

    /// <summary>How many vertices it covers.</summary>
    public int Count => directions.Length;

    /// <summary>The representative at a vertex.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>A unit direction in the vertex's tangent plane, or zero where the vertex has no plane.</returns>
    public Vector3 Direction(int vertex) => directions[vertex];

    /// <summary>Every representative, for a bit-for-bit comparison.</summary>
    public ReadOnlySpan<Vector3> Directions => directions;

    /// <summary>The field's smoothness, as the energy § D5 names.</summary>
    /// <param name="mesh">The surface it was solved on.</param>
    /// <returns>
    ///     The mean over edges of the smallest angle, in radians, between the two endpoints'
    ///     representatives after the best of the four rotations. Zero is a perfectly smooth field.
    /// </returns>
    /// <remarks>
    ///     A mean rather than a sum, so that a coarse mesh and a fine one of the same shape produce
    ///     comparable numbers — which is what makes the hierarchy-against-flat comparison a statement
    ///     about convergence rather than about vertex count.
    /// </remarks>
    public float Energy(ManifoldMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var total = 0f;
        var edges = 0;

        for (var half = 0; half < mesh.Triangles.Length; half++) {
            var twin = mesh.Twin(half);

            if (twin >= 0 && twin < half) {
                continue;
            }

            var from = mesh.Triangles[half];
            var to = mesh.Triangles[ManifoldMesh.Next(half)];

            var here = directions[from];
            var there = Transport(directions[to], mesh.VertexNormal(to), mesh.VertexNormal(from));

            if (here.LengthSquared() <= 0f || there.LengthSquared() <= 0f) {
                continue;
            }

            var best = Align(there, here, mesh.VertexNormal(from));

            total += MathF.Acos(Math.Clamp(Vector3.Dot(best, here), -1f, 1f));
            edges++;
        }

        return edges == 0 ? 0f : total / edges;
    }

    /// <summary>The worst edge in the field, as the same angle <see cref="Energy" /> averages.</summary>
    /// <param name="mesh">The surface it was solved on.</param>
    /// <returns>The largest deviation, in radians. Never more than <c>π/4</c> for a resolved field.</returns>
    /// <remarks>
    ///     ⚠ <b>This is the precondition on the Euler-characteristic invariant, and it is worth
    ///     measuring rather than assuming.</b> A cross field's singularity index is read off period
    ///     jumps — which of the four rotations of a neighbour is meant — and a quarter turn is
    ///     <c>π/2</c>, so an edge across which the field genuinely turns by more than <c>π/4</c> is one
    ///     where "which rotation" has no right answer. The index sum is <c>4χ</c> exactly while every
    ///     edge is under that bound and is not a topological quantity at all once one is over it: a
    ///     coarse mesh, or a constraint fighting the smoothness hard enough, breaks the theorem rather
    ///     than the code.
    /// </remarks>
    public float MaxDeviation(ManifoldMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var worst = 0f;

        for (var half = 0; half < mesh.Triangles.Length; half++) {
            var twin = mesh.Twin(half);

            if (twin >= 0 && twin < half) {
                continue;
            }

            var from = mesh.Triangles[half];
            var to = mesh.Triangles[ManifoldMesh.Next(half)];

            var here = directions[from];
            var there = Transport(directions[to], mesh.VertexNormal(to), mesh.VertexNormal(from));

            if (here.LengthSquared() <= 0f || there.LengthSquared() <= 0f) {
                continue;
            }

            var best = Align(there, here, mesh.VertexNormal(from));

            worst = MathF.Max(worst, MathF.Acos(Math.Clamp(Vector3.Dot(best, here), -1f, 1f)));
        }

        return worst;
    }

    /// <summary>Rotates a tangent direction from one normal's plane into another's.</summary>
    /// <param name="direction">The direction, in <paramref name="from" />'s plane.</param>
    /// <param name="from">The normal it belongs to.</param>
    /// <param name="to">The normal it should belong to.</param>
    /// <returns>The rotated direction, in <paramref name="to" />'s plane.</returns>
    /// <remarks>
    ///     ⚠ <b>A rotation and not a projection, and the Euler-characteristic test is what tells them
    ///     apart.</b> Projecting onto the target plane and renormalising is one line shorter, is very
    ///     nearly the same for two nearly-parallel normals, and is not an isometry — so the holonomy
    ///     it accumulates round a vertex is not the angle defect, and the singularity indices it
    ///     produces do not sum to the Euler characteristic. The shortest-arc rotation does, exactly,
    ///     which is what makes <c>SingularityTests</c> an invariant rather than an approximation.
    /// </remarks>
    public static Vector3 Transport(Vector3 direction, Vector3 from, Vector3 to) {
        if (direction.LengthSquared() <= 0f) {
            return Vector3.Zero;
        }

        var axis = Vector3.Cross(from, to);
        var sine = axis.Length();
        var cosine = Vector3.Dot(from, to);

        if (sine <= 0f) {
            // Parallel, or exactly opposed. The first needs no rotation; the second has no shortest
            // arc at all, and the direction is left where it is rather than picked arbitrarily.
            return direction;
        }

        axis /= sine;

        return (direction * cosine)
            + (Vector3.Cross(axis, direction) * sine)
            + (axis * Vector3.Dot(axis, direction) * (1f - cosine));
    }

    /// <summary>Of the four directions a cross stands for, the one nearest a reference.</summary>
    /// <param name="direction">The representative.</param>
    /// <param name="reference">What to be near. Need not be a unit vector.</param>
    /// <param name="normal">The plane both lie in.</param>
    /// <returns>The best of <c>d</c>, <c>n × d</c>, <c>−d</c> and <c>−n × d</c>.</returns>
    public static Vector3 Align(Vector3 direction, Vector3 reference, Vector3 normal) {
        var across = Vector3.Cross(normal, direction);

        var one = Vector3.Dot(direction, reference);
        var two = Vector3.Dot(across, reference);

        // ⚠ Compared by absolute value first, so the choice is between the two <i>axes</i> and then
        // between the two ends of the winner. Four separate dot comparisons pick the same answer and
        // read as though the ninety-degree symmetry were four independent cases rather than one.
        if (MathF.Abs(one) >= MathF.Abs(two)) {
            return one >= 0f ? direction : -direction;
        }

        return two >= 0f ? across : -across;
    }

    /// <summary>Which of the four rotations <see cref="Align" /> chose, as a quarter-turn count.</summary>
    /// <param name="direction">The representative.</param>
    /// <param name="reference">What to be near.</param>
    /// <param name="normal">The plane both lie in.</param>
    /// <returns>Zero, one, two or three quarter turns anticlockwise about <paramref name="normal" />.</returns>
    public static int Rotation(Vector3 direction, Vector3 reference, Vector3 normal) {
        var across = Vector3.Cross(normal, direction);

        var one = Vector3.Dot(direction, reference);
        var two = Vector3.Dot(across, reference);

        if (MathF.Abs(one) >= MathF.Abs(two)) {
            return one >= 0f ? 0 : 2;
        }

        return two >= 0f ? 1 : 3;
    }
}
