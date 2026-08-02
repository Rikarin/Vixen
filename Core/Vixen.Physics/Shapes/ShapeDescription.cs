// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Physics.Shapes;

/// <summary>Which kind of collision volume a description names.</summary>
public enum ShapeKind {
    /// <summary>A sphere of a given radius, centred on the body's origin.</summary>
    Sphere,

    /// <summary>A box, given as half its extents along each axis.</summary>
    Box,

    /// <summary>A capsule along the Y axis: a cylinder of a half height, capped with hemispheres.</summary>
    Capsule,

    /// <summary>A cylinder along the Y axis.</summary>
    Cylinder,

    /// <summary>An infinite half-space. Static geometry only.</summary>
    Plane,

    /// <summary>The convex hull of a point cloud.</summary>
    ConvexHull,

    /// <summary>A triangle mesh. Concave, and so static or kinematic only.</summary>
    Mesh,

    /// <summary>Several shapes, each with its own offset from the body's origin.</summary>
    Compound,

    /// <summary>
    ///     A regular grid of heights over the XZ plane. Concave, and so static or kinematic only.
    /// </summary>
    /// <remarks>
    ///     What terrain collides with. A mesh of the same ground would be a bounding-volume hierarchy
    ///     over two hundred thousand triangles that has to be rebuilt whenever a brush touches it; a
    ///     height field is the samples themselves, block-compressed, and rebuilding one tile of a
    ///     terrain rebuilds one shape. See [docs/plan/31 § D10].
    /// </remarks>
    HeightField
}

/// <summary>Where a height field's samples sit in the body's space.</summary>
/// <param name="SampleCount">How many samples along each of X and Z. The grid is square.</param>
/// <param name="Offset">Where sample (0, 0) sits, before the height is added.</param>
/// <param name="Scale">
///     The spacing between samples in X and Z, and what one unit of a sample means in Y.
/// </param>
/// <remarks>
///     <para>
///         The surface is <c>Offset + Scale * (x, height[z * SampleCount + x], z)</c> for integer
///         <c>x</c> and <c>z</c> in <c>[0, SampleCount)</c> — which is Jolt's own definition, kept
///         literally so that a value copied out of its documentation means the same thing here.
///     </para>
///     <para>
///         ⚠ <b><see cref="SampleCount" /> samples span <see cref="SampleCount" /> − 1 quads.</b> A
///         tile of 127 quads is 128 samples, and 128 is the number that has to be a power of two.
///         This is the same constraint Unreal states as "a power of two value minus one" for its
///         section sizes, arrived at from the other end.
///     </para>
/// </remarks>
[DataContract]
public readonly record struct HeightFieldPlacement(int SampleCount, Vector3 Offset, Vector3 Scale) {
    /// <summary>How many quads the grid spans along each axis.</summary>
    public int QuadCount => SampleCount - 1;

    /// <summary>Where one sample sits, given its height.</summary>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <param name="height">The stored height at that sample.</param>
    /// <returns>The position in the body's space.</returns>
    public Vector3 PositionOf(int x, int z, float height) =>
        Offset + new Vector3(x * Scale.X, height * Scale.Y, z * Scale.Z);

    /// <summary>Everything the grid occupies, given the range of its samples.</summary>
    /// <param name="minHeight">The smallest stored height, ignoring holes.</param>
    /// <param name="maxHeight">The largest stored height, ignoring holes.</param>
    /// <returns>The bounds in the body's space.</returns>
    /// <remarks>
    ///     Both corners are computed through <see cref="PositionOf" /> and then reduced, because a
    ///     negative <c>Scale.Y</c> is legal and swaps which height is the top.
    /// </remarks>
    public BoundingBox BoundsOf(float minHeight, float maxHeight) {
        var last = SampleCount - 1;
        var low = PositionOf(0, 0, minHeight);
        var high = PositionOf(last, last, maxHeight);
        return new(Vector3.Min(low, high), Vector3.Max(low, high));
    }
}

/// <summary>One child of a compound shape: a shape, and where it sits relative to the parent.</summary>
/// <param name="Shape">The child shape, already registered.</param>
/// <param name="Position">Its offset from the compound's origin.</param>
/// <param name="Rotation">Its rotation relative to the compound.</param>
[DataContract]
public readonly record struct CompoundChild(ShapeId Shape, Vector3 Position, Quaternion Rotation) {
    /// <summary>A child at the compound's own origin, unrotated.</summary>
    /// <param name="shape">The child shape.</param>
    /// <returns>The child.</returns>
    public static CompoundChild At(ShapeId shape) => new(shape, Vector3.Zero, Quaternion.Identity);
}

/// <summary>
///     What a collision volume is, without being one yet.
/// </summary>
/// <remarks>
///     <para>
///         A description is a value: two boxes with the same half extents are equal, hash the same,
///         and <see cref="PhysicsShapes" /> hands them the same <see cref="ShapeId" /> and the same
///         native shape. That is what makes ten thousand crates cost one <c>BoxShape</c> rather than
///         ten thousand, and it is why this is a struct with an equality contract rather than a class
///         hierarchy with reference identity.
///     </para>
///     <para>
///         <b>The vertex data is not in here.</b> A hull or a mesh carries arrays, and putting them
///         in the value would make equality mean "the same array instance" or else make it O(n) on
///         every registration. Instead a hull, a mesh or a compound is registered by its data once
///         and the description holds the resulting <see cref="ShapeId" />'s underlying index — so the
///         value stays sixteen bytes wide, comparable and blittable, which is what lets a
///         <c>Collider</c> component hold one without becoming a managed component.
///     </para>
/// </remarks>
[DataContract]
public readonly record struct ShapeDescription {
    /// <summary>What kind of shape this is.</summary>
    public ShapeKind Kind { get; init; }

    /// <summary>
    ///     The size, meaning whatever the kind says: half extents for a box, the plane's normal for a
    ///     plane, and nothing for the kinds whose data lives in the registry.
    /// </summary>
    public Vector3 Extents { get; init; }

    /// <summary>
    ///     The primary radius: the sphere's, the capsule's, the cylinder's, or the plane's distance
    ///     from the origin along its normal.
    /// </summary>
    public float Radius { get; init; }

    /// <summary>
    ///     Half the height of the straight part of a capsule, or half a cylinder's total height.
    /// </summary>
    public float HalfHeight { get; init; }

    /// <summary>Which blob of vertex, index or child data in the registry this shape reads.</summary>
    /// <remarks>Meaningless — and zero — for the primitive kinds.</remarks>
    public int DataIndex { get; init; }

    /// <summary>A sphere.</summary>
    /// <param name="radius">Its radius. Must be positive.</param>
    /// <returns>The description.</returns>
    public static ShapeDescription Sphere(float radius) => new() { Kind = ShapeKind.Sphere, Radius = radius };

    /// <summary>A box, given as half its extents.</summary>
    /// <param name="halfExtents">Half the size along each axis. Every component must be positive.</param>
    /// <returns>The description.</returns>
    public static ShapeDescription Box(Vector3 halfExtents) => new() { Kind = ShapeKind.Box, Extents = halfExtents };

    /// <summary>A cube.</summary>
    /// <param name="halfExtent">Half the size along every axis.</param>
    /// <returns>The description.</returns>
    public static ShapeDescription Box(float halfExtent) => Box(new Vector3(halfExtent, halfExtent, halfExtent));

    /// <summary>A capsule standing on the Y axis.</summary>
    /// <param name="halfHeight">Half the height of the cylindrical part, not counting the caps.</param>
    /// <param name="radius">The radius of the caps and the cylinder.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    ///     The total height is <c>2 × (halfHeight + radius)</c>. Measuring the cylinder rather than
    ///     the whole is Jolt's convention and Unity's, and changing it here would make every tuning
    ///     value copied from either one wrong by a radius.
    /// </remarks>
    public static ShapeDescription Capsule(float halfHeight, float radius) =>
        new() { Kind = ShapeKind.Capsule, HalfHeight = halfHeight, Radius = radius };

    /// <summary>A cylinder standing on the Y axis.</summary>
    /// <param name="halfHeight">Half the total height.</param>
    /// <param name="radius">The radius.</param>
    /// <returns>The description.</returns>
    public static ShapeDescription Cylinder(float halfHeight, float radius) =>
        new() { Kind = ShapeKind.Cylinder, HalfHeight = halfHeight, Radius = radius };

    /// <summary>An infinite half-space, solid on the side the normal points away from.</summary>
    /// <param name="normal">Which way is up. Normalised.</param>
    /// <param name="distance">How far the plane is from the origin along the normal.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    ///     Static geometry only — an infinite shape has no mass properties to speak of, and Jolt
    ///     refuses to give one to a dynamic body.
    /// </remarks>
    public static ShapeDescription Plane(Vector3 normal, float distance = 0f) =>
        new() { Kind = ShapeKind.Plane, Extents = normal, Radius = distance };

    /// <summary>Whether this kind can be given to a body that moves under the solver.</summary>
    /// <remarks>
    ///     A concave mesh, a height field and an infinite plane have no usable inertia tensor. Jolt
    ///     reports this as a native assertion during body creation; <see cref="PhysicsWorld" /> checks
    ///     it first so the message names the shape.
    /// </remarks>
    public bool CanBeDynamic =>
        Kind is not (ShapeKind.Mesh or ShapeKind.Plane or ShapeKind.HeightField);

    /// <summary>Renders the kind and its distinguishing measurement.</summary>
    /// <returns>The description in text.</returns>
    public override string ToString() =>
        Kind switch {
            ShapeKind.Sphere => $"Sphere(r {Radius})",
            ShapeKind.Box => $"Box({Extents})",
            ShapeKind.Capsule => $"Capsule(h {HalfHeight}, r {Radius})",
            ShapeKind.Cylinder => $"Cylinder(h {HalfHeight}, r {Radius})",
            ShapeKind.Plane => $"Plane({Extents} @ {Radius})",
            _ => $"{Kind}(#{DataIndex})"
        };
}
