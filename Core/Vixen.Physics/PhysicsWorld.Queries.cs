// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using JoltPhysicsSharp;
using Vixen.Physics.Bodies;
using Vixen.Physics.Interop;
using Vixen.Physics.Queries;
using Vixen.Physics.Shapes;
using JoltRay = JoltPhysicsSharp.Ray;
using Quaternion = Vixen.Core.Mathematics.Quaternion;
using Vector3 = Vixen.Core.Mathematics.Vector3;

namespace Vixen.Physics;

public sealed partial class PhysicsWorld {
    // Reused across queries so a cast allocates nothing. Cleared on the way in rather than the way
    // out, because a caller that keeps the returned span until the next query gets stale data either
    // way and clearing late at least makes the span valid for as long as the documentation says.
    readonly List<RayCastResult> rayResults = [];
    readonly List<CollideShapeResult> overlapResults = [];
    readonly List<ShapeCastResult> castResults = [];
    readonly List<RayHit> rayHits = [];
    readonly List<ShapeOverlap> shapeOverlaps = [];

    /// <summary>Finds the first thing a ray hits.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">Which way it points. Need not be normalised.</param>
    /// <param name="distance">How far it reaches, in metres.</param>
    /// <param name="hit">What it hit.</param>
    /// <param name="filter">What it is allowed to see.</param>
    /// <returns><see langword="true" /> if it hit anything.</returns>
    /// <remarks>
    ///     Closest hit, which is what "what is in front of me" means and what a line-of-sight test
    ///     needs. Jolt walks the tree with the current best fraction as its cutoff, so this is
    ///     strictly cheaper than collecting everything and sorting.
    /// </remarks>
    public bool Raycast(
        Vector3 origin,
        Vector3 direction,
        float distance,
        out RayHit hit,
        QueryFilter filter = default
    ) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        hit = default;

        var length = direction.Length();

        if (length < 1e-9f || !(distance > 0f)) {
            return false;
        }

        var unit = direction / length;
        Prepare(filter);

        // Jolt's Ray.Direction carries the length: the reported fraction is along it, so scaling it
        // to the query distance is what makes fraction 1 mean "the far end" rather than "one metre".
        var ray = new JoltRay(JoltMath.ToJolt(origin), JoltMath.ToJolt(unit * distance));

        if (!system.NarrowPhaseQuery.CastRay(in ray, out RayCastResult result, default, queryLayerFilter, queryBodyFilter)) {
            return false;
        }

        hit = Describe(result, origin, unit, distance);
        return true;
    }

    /// <summary>Finds everything a ray hits, nearest first.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">Which way it points.</param>
    /// <param name="distance">How far it reaches, in metres.</param>
    /// <param name="filter">What it is allowed to see.</param>
    /// <returns>The hits, valid until the next query.</returns>
    /// <remarks>
    ///     The span is a view into a buffer the world reuses, so a caller that needs the hits past
    ///     the next query copies them. That is the same contract <c>DebugDraw.Lines</c> has, and it
    ///     is what keeps a query that finds forty hits from allocating a forty-element array.
    /// </remarks>
    public ReadOnlySpan<RayHit> RaycastAll(
        Vector3 origin,
        Vector3 direction,
        float distance,
        QueryFilter filter = default
    ) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        rayResults.Clear();
        rayHits.Clear();

        var length = direction.Length();

        if (length < 1e-9f || !(distance > 0f)) {
            return [];
        }

        var unit = direction / length;
        Prepare(filter);

        var ray = new JoltRay(JoltMath.ToJolt(origin), JoltMath.ToJolt(unit * distance));
        var settings = new RayCastSettings();

        system.NarrowPhaseQuery.CastRay(
            in ray,
            settings,
            CollisionCollectorType.AllHitSorted,
            rayResults,
            default,
            queryLayerFilter,
            queryBodyFilter,
            default
        );

        foreach (var result in rayResults) {
            rayHits.Add(Describe(result, origin, unit, distance));
        }

        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rayHits);
    }

    /// <summary>Finds everything a shape overlaps where it is put.</summary>
    /// <param name="shape">The shape to test with.</param>
    /// <param name="position">Where to put it.</param>
    /// <param name="rotation">Which way to face it.</param>
    /// <param name="filter">What it is allowed to see.</param>
    /// <returns>The overlaps, valid until the next query.</returns>
    /// <remarks>
    ///     This is the primitive behind "can the character stand here", "what is inside the blast
    ///     radius" and "does the placed building intersect anything". The shape is one from
    ///     <see cref="Shapes" />, so an overlap test costs no shape construction.
    /// </remarks>
    public ReadOnlySpan<ShapeOverlap> Overlap(
        ShapeId shape,
        Vector3 position,
        Quaternion rotation = default,
        QueryFilter filter = default
    ) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        overlapResults.Clear();
        shapeOverlaps.Clear();
        Prepare(filter);

        var native = Shapes.Resolve(shape);
        var facing = rotation == default ? Quaternion.Identity : rotation;
        var transform = JoltMath.ComposeRigid(position, facing);
        var scale = System.Numerics.Vector3.One;
        var baseOffset = System.Numerics.Vector3.Zero;

        system.NarrowPhaseQuery.CollideShape(
            native,
            in scale,
            in transform,
            in baseOffset,
            CollisionCollectorType.AllHit,
            overlapResults,
            default,
            queryLayerFilter,
            queryBodyFilter,
            default
        );

        foreach (var result in overlapResults) {
            // PenetrationAxis points from the queried shape into the body and is not normalised;
            // callers want a direction and a depth, which is what a separating vector is.
            var axis = JoltMath.ToVixen(result.PenetrationAxis);
            var axisLength = axis.Length();
            var normal = axisLength > 1e-9f ? axis / axisLength : Vector3.UnitY;

            shapeOverlaps.Add(
                new(
                    new BodyHandle(result.BodyID2.ID),
                    JoltMath.ToVixen(result.ContactPointOn1),
                    normal,
                    result.PenetrationDepth
                )
            );
        }

        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(shapeOverlaps);
    }

    /// <summary>Finds everything a sphere overlaps.</summary>
    /// <param name="centre">Where.</param>
    /// <param name="radius">How big.</param>
    /// <param name="filter">What it is allowed to see.</param>
    /// <returns>The overlaps, valid until the next query.</returns>
    public ReadOnlySpan<ShapeOverlap> OverlapSphere(Vector3 centre, float radius, QueryFilter filter = default) =>
        Overlap(Shapes.Sphere(radius), centre, Quaternion.Identity, filter);

    /// <summary>Finds everything a box overlaps.</summary>
    /// <param name="centre">Where.</param>
    /// <param name="halfExtents">Half its size along each axis.</param>
    /// <param name="rotation">Which way it faces.</param>
    /// <param name="filter">What it is allowed to see.</param>
    /// <returns>The overlaps, valid until the next query.</returns>
    public ReadOnlySpan<ShapeOverlap> OverlapBox(
        Vector3 centre,
        Vector3 halfExtents,
        Quaternion rotation = default,
        QueryFilter filter = default
    ) =>
        Overlap(Shapes.Box(halfExtents), centre, rotation, filter);

    /// <summary>Sweeps a shape and reports where it first hits something.</summary>
    /// <param name="shape">The shape to sweep.</param>
    /// <param name="origin">Where the sweep starts.</param>
    /// <param name="rotation">Which way the shape faces throughout. A sweep does not rotate.</param>
    /// <param name="motion">How far and which way to sweep.</param>
    /// <param name="hit">Where it first hit.</param>
    /// <param name="filter">What it is allowed to see.</param>
    /// <returns><see langword="true" /> if it hit anything.</returns>
    /// <remarks>
    ///     What a thick raycast is, and what a projectile that must not tunnel uses when giving the
    ///     body continuous motion quality is not enough. A hit at fraction zero means the shape
    ///     started already overlapping — see <see cref="ShapeCastHit.Fraction" />.
    /// </remarks>
    public bool ShapeCast(
        ShapeId shape,
        Vector3 origin,
        Quaternion rotation,
        Vector3 motion,
        out ShapeCastHit hit,
        QueryFilter filter = default
    ) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        hit = default;
        castResults.Clear();

        if (motion.LengthSquared() < 1e-12f) {
            return false;
        }

        Prepare(filter);

        var native = Shapes.Resolve(shape);
        var facing = rotation == default ? Quaternion.Identity : rotation;
        var transform = JoltMath.ComposeRigid(origin, facing);
        var direction = JoltMath.ToJolt(motion);
        var baseOffset = System.Numerics.Vector3.Zero;

        system.NarrowPhaseQuery.CastShape(
            native,
            in transform,
            in direction,
            in baseOffset,
            CollisionCollectorType.ClosestHit,
            castResults,
            default,
            queryLayerFilter,
            queryBodyFilter,
            default
        );

        if (castResults.Count == 0) {
            return false;
        }

        var result = castResults[0];
        var axis = JoltMath.ToVixen(result.PenetrationAxis);
        var axisLength = axis.Length();

        hit = new(
            new BodyHandle(result.BodyID2.ID),
            JoltMath.ToVixen(result.ContactPointOn2),
            axisLength > 1e-9f ? -(axis / axisLength) : Vector3.UnitY,
            result.Fraction
        );

        return true;
    }

    /// <summary>Whether a point is inside anything.</summary>
    /// <param name="point">The point.</param>
    /// <param name="filter">What counts.</param>
    /// <returns><see langword="true" /> if some body contains it.</returns>
    public bool CheckPoint(Vector3 point, QueryFilter filter = default) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var found = new List<CollidePointResult>(1);
        Prepare(filter);
        var target = JoltMath.ToJolt(point);

        system.NarrowPhaseQuery.CollidePoint(
            in target,
            CollisionCollectorType.AnyHit,
            found,
            default,
            queryLayerFilter,
            queryBodyFilter,
            default
        );

        return found.Count > 0;
    }

    RayHit Describe(in RayCastResult result, Vector3 origin, Vector3 unit, float distance) {
        var handle = new BodyHandle(result.BodyID.ID);
        var position = origin + (unit * (result.Fraction * distance));
        var normal = Vector3.Zero;

        if (IsAlive(handle)) {
            var id = new BodyID(result.BodyID.ID);
            var lockInterface = system.BodyLockInterfaceNoLock;
            var transformed = system.BodyInterface.GetTransformedShape(in lockInterface, in id);
            var subShape = new SubShapeID(result.subShapeID2);
            var world = JoltMath.ToJolt(position);
            normal = JoltMath.ToVixen(transformed.GetWorldSpaceSurfaceNormal(in subShape, in world));
        }

        return new(handle, position, normal, result.Fraction * distance, result.Fraction);
    }

    void Prepare(in QueryFilter filter) {
        queryLayerFilter.Mask = filter.Layers;
        queryBodyFilter.Ignore = filter.IgnoreBody;
        queryBodyFilter.IncludeSensors = filter.IncludeSensors;
    }
}
