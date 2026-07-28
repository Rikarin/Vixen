// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Baking;

/// <summary>
///     A piece of the world that stamps an area id onto whatever walkable surface is inside it.
/// </summary>
/// <remarks>
///     <para>
///         Areas are how a navmesh knows that this ground is water, that one is a road, and the other
///         is somewhere the guards would rather not walk. Nothing about the geometry says so — a
///         puddle and a paving stone are both a floor — so it is authored, and this is the authoring
///         shape: a convex prism, an area id, and a height range.
///     </para>
///     <para>
///         <b>It is applied before the surface is partitioned</b>, which is what makes the area
///         boundary a polygon boundary: the region sweep only continues a run through cells of the
///         same area, so a polygon is never half road. Stamping the finished polygons instead would
///         put the boundary wherever the polygons happened to fall.
///     </para>
///     <para>
///         Convex, because the containment test is per voxel column of the volume's footprint and a
///         concave test is several times the work for a shape that can be expressed as two volumes.
///         <see cref="Box" /> is the case everyone actually authors.
///     </para>
/// </remarks>
public sealed class NavAreaVolume {
    readonly Vector3[] shape;

    NavAreaVolume(Vector3[] shape, float minimumY, float maximumY, byte area) {
        this.shape = shape;
        MinimumY = minimumY;
        MaximumY = maximumY;
        Area = area;
        Bounds = BoundingBox.FromPoints(shape);
    }

    /// <summary>The area id stamped onto everything inside.</summary>
    public byte Area { get; }

    /// <summary>The lowest surface height it affects.</summary>
    public float MinimumY { get; }

    /// <summary>The highest.</summary>
    public float MaximumY { get; }

    /// <summary>The footprint, for a caller that wants to draw it.</summary>
    public ReadOnlySpan<Vector3> Shape => shape;

    /// <summary>What the footprint spans in XZ. The Y of this is the footprint's, not the volume's.</summary>
    public BoundingBox Bounds { get; }

    /// <summary>A volume shaped like a box.</summary>
    /// <param name="bounds">The box.</param>
    /// <param name="area">The area to stamp.</param>
    /// <returns>The volume.</returns>
    public static NavAreaVolume Box(BoundingBox bounds, byte area) => new(
        [
            new(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z),
            new(bounds.Minimum.X, bounds.Minimum.Y, bounds.Maximum.Z),
            new(bounds.Maximum.X, bounds.Minimum.Y, bounds.Maximum.Z),
            new(bounds.Maximum.X, bounds.Minimum.Y, bounds.Minimum.Z)
        ],
        bounds.Minimum.Y,
        bounds.Maximum.Y,
        area
    );

    /// <summary>A volume shaped like a prism over a convex footprint.</summary>
    /// <param name="footprint">The footprint in XZ, convex, in either winding. Y is ignored.</param>
    /// <param name="minimumY">The lowest surface height it affects.</param>
    /// <param name="maximumY">The highest.</param>
    /// <param name="area">The area to stamp.</param>
    /// <returns>The volume.</returns>
    /// <exception cref="ArgumentException">The footprint has fewer than three points, or the height range is inverted.</exception>
    public static NavAreaVolume Convex(ReadOnlySpan<Vector3> footprint, float minimumY, float maximumY, byte area) {
        if (footprint.Length < 3) {
            throw new ArgumentException($"A footprint needs at least three points and has {footprint.Length}.", nameof(footprint));
        }

        if (maximumY < minimumY) {
            throw new ArgumentException($"The height range runs from {minimumY} to {maximumY}, which is backwards.", nameof(maximumY));
        }

        return new([.. footprint], minimumY, maximumY, area);
    }

    /// <summary>A volume shaped like an upright cylinder.</summary>
    /// <param name="centre">The centre of its base.</param>
    /// <param name="radius">How wide.</param>
    /// <param name="height">How tall, upwards from the base.</param>
    /// <param name="area">The area to stamp. <see cref="NavArea.Null" /> makes it an obstacle.</param>
    /// <param name="segments">How many sides the polygon approximating the circle has.</param>
    /// <returns>The volume.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The radius or height is not positive, or there are fewer than three segments.</exception>
    /// <remarks>
    ///     <para>
    ///         A polygon rather than a circle, so that there is one containment test rather than two.
    ///         Twelve sides is within 3.5 % of the circle it approximates, which is a long way inside
    ///         the cell size anything is going to be voxelised at — a smaller error than the grid can
    ///         express is an error nobody can see.
    ///     </para>
    ///     <para>
    ///         The polygon is drawn <i>outside</i> the circle rather than inside it, so a crate of a
    ///         given radius is never narrower than asked for. Under-covering an obstacle is a hole in
    ///         the wall; over-covering it is a few centimetres of floor nobody stands on.
    ///     </para>
    /// </remarks>
    public static NavAreaVolume Cylinder(Vector3 centre, float radius, float height, byte area, int segments = 12) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 3);

        var outer = radius / MathF.Cos(MathF.PI / segments);
        var footprint = new Vector3[segments];

        for (var index = 0; index < segments; index++) {
            var angle = index / (float)segments * MathF.Tau;

            footprint[index] = new(
                centre.X + (outer * MathF.Cos(angle)),
                centre.Y,
                centre.Z + (outer * MathF.Sin(angle))
            );
        }

        return new(footprint, centre.Y, centre.Y + height, area);
    }

    /// <summary>Whether a point on the surface is inside the volume.</summary>
    internal bool Contains(Vector3 point) =>
        point.Y >= MinimumY && point.Y <= MaximumY && NavGeometry.ContainsPoint2D(point, shape);
}
