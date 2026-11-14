// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>A curvature by secondary rays, as <see cref="CurvatureSource.Rays" /> asks (Patina #148).</summary>
/// <remarks>
///     <para>
///         <b>How far the surface stands below its own tangent plane a sampling radius away, round the point.</b> A
///         surface of mean curvature <i>H</i> falls by about <c>H·r²/2</c> at a distance <i>r</i>, averaged over the
///         directions round it, so the curvature is minus twice the mean height over <c>r²</c>: a sphere of radius
///         <i>R</i> reads about <c>1/R</c>, positive where the surface is convex, the sign
///         <see cref="MeanCurvature" /> gives an edge.
///     </para>
///     <para>
///         ⚠ <b>Each direction is a ray across, then a ray down.</b> Across, from just above the point, a steep face
///         met short of the radius is a surface that rose: it counts as high as the wall reaches past where it was
///         met, so the floor of a crease reads concave. Otherwise a ray drops from a radius above the place a radius
///         away, through the tangent plane to a radius below it.
///     </para>
///     <para>
///         ⚠ <b>A ray down that meets nothing is a drop, or the end of an open sheet.</b> Past a hard edge the side
///         face runs along the ray and is never struck; read as nothing, the edge, the thing a curvature map is baked
///         for, read flat. So a ray goes back under the place towards the point, and a face met there is the solid's
///         side: the surface dropped a whole radius. Meeting nothing, there is no surface that way at all, and the
///         direction is left out, as <see cref="MeanCurvature" /> leaves out an open rim rather than bake it bright.
///     </para>
///     <para>
///         <b>A bend at the radius's scale.</b> A hard edge of a low poly source reads as tight as the radius, which
///         the mesh's own curvature cannot say of an edge with no faces to bend over, and detail much finer than the
///         radius is stepped over.
///     </para>
/// </remarks>
static class RayCurvature {
    /// <summary>The cosine under which a face met across counts as a wall: more than sixty degrees from level.</summary>
    const float Steep = 0.5f;

    /// <summary>The curvature round a point of the source.</summary>
    /// <param name="tree">The source's rays.</param>
    /// <param name="surface">The source, for the faces the rays meet.</param>
    /// <param name="point">Where the bake's ray landed.</param>
    /// <param name="normal">The source's unit normal there.</param>
    /// <param name="reach">How far round it to look as a length, and how far off the surface a ray starts.</param>
    /// <param name="count">How many directions.</param>
    /// <param name="turn">The rotation of the first direction, in turns, so neighbouring texels look different ways.</param>
    /// <returns>The signed curvature, in reciprocal model units, or zero with nothing round the point.</returns>
    public static float At(
        BakeTree tree,
        SourceSurface surface,
        Vector3 point,
        Vector3 normal,
        (float Radius, float Bias) reach,
        int count,
        float turn
    ) {
        var radius = reach.Radius;

        if (!(radius > 0f) || count <= 0) {
            return 0f;
        }

        var (tangent, bitangent) = HemisphereSampler.Basis(normal);
        Probe probe = new(tree, surface, point, normal, radius, reach.Bias);
        var height = 0f;
        var found = 0;

        for (var ray = 0; ray < count; ray++) {
            var angle = (ray + turn) * (2f * MathF.PI / count);

            if (probe.Height(tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle)) is { } rise) {
                height += rise;
                found++;
            }
        }

        return found == 0 ? 0f : -2f * (height / found) / (radius * radius);
    }

    /// <summary>The rays round one point.</summary>
    readonly record struct Probe(
        BakeTree Tree,
        SourceSurface Surface,
        Vector3 Point,
        Vector3 Normal,
        float Radius,
        float Bias
    ) {
        /// <summary>
        ///     How far above the tangent plane the surface is a radius away in one direction, within a radius, or null
        ///     when there is no surface that way.
        /// </summary>
        /// <remarks>
        ///     ⚠ <b>Only a steep face met across is a wall.</b> A smooth hollow rises through a ray a bias above its
        ///     floor within a few hundredths of a radius, and counted as a wall it read twenty times its curvature: the
        ///     ray down measures it exactly, so a face less steep than <see cref="Steep" /> is left to it.
        /// </remarks>
        public float? Height(Vector3 across) {
            var wall = Tree.Raycast(Point + Normal * Bias, across * Radius);

            if (wall.Triangle >= 0 && MathF.Abs(Vector3.Dot(Surface.NormalOf(wall.Triangle), Normal)) < Steep) {
                return Radius * (1f - wall.Distance);
            }

            var above = Point + across * Radius + Normal * Radius;
            var down = Normal * (-2f * Radius);
            var hit = Tree.Raycast(above, down);

            // ⚠ A ray straight through an edge two triangles share misses by an ulp now and then, and one miss on a
            // flat sheet read as a bend of a radius in sixteen; a second ray a bias aside tells a slip from a drop.
            if (hit.Triangle < 0) {
                hit = Tree.Raycast(above + Vector3.Cross(Normal, across) * Bias, down);
            }

            if (hit.Triangle >= 0) {
                return Math.Clamp(Vector3.Dot(hit.Point - Point, Normal), -Radius, Radius);
            }

            var side = Tree.Raycast(above + down, across * -Radius);

            return side.Triangle >= 0 ? -Radius : null;
        }
    }
}
