// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Shapes;

namespace Vixen.Physics.Diagnostics;

/// <summary>What a physics debug pass draws.</summary>
[Flags]
public enum PhysicsDebugOverlay {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Every body's collision volume, as a wireframe.</summary>
    Colliders = 1 << 0,

    /// <summary>The contact points and normals from the last step.</summary>
    Contacts = 1 << 1,

    /// <summary>Each body's axis-aligned bounds — what the broad phase actually sees.</summary>
    Bounds = 1 << 2,

    /// <summary>Each body's local axes.</summary>
    Axes = 1 << 3,

    /// <summary>Every constraint's anchors, the error between them, and its axis.</summary>
    Constraints = 1 << 4,

    /// <summary>
    ///     The colliders, the contacts and the constraints, which is what an investigation usually
    ///     wants — and what [13](../../../docs/plan/13-diagnostics.md) § Overlays specifies.
    /// </summary>
    Default = Colliders | Contacts | Constraints
}

/// <summary>
///     Turns a <see cref="PhysicsWorld" /> into lines in a <see cref="DebugDraw" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Drawn from Vixen's own shape descriptions, not from Jolt's debug renderer.</b> Jolt has
///         one, and using it would mean implementing a native renderer interface whose callbacks
///         arrive as triangles — for geometry whose whole purpose is to be lines. Every shape the
///         registry holds is already described exactly, so a box is its twelve edges and a capsule is
///         its rings, drawn straight into the accumulator every other subsystem uses.
///     </para>
///     <para>
///         The exception is a mesh or a convex hull, which is drawn as its bounding box. Wireframing
///         a hundred-thousand-triangle level would produce more lines than the debug renderer can
///         hold and would tell nobody anything; the bounds answer the question that is actually being
///         asked, which is "is this body where I think it is".
///     </para>
///     <para>
///         <b>Colour carries the state.</b> Asleep is grey, static is dark green, kinematic is blue,
///         awake and dynamic is bright green, and a sensor is yellow. That mapping is what makes "the
///         crate has gone to sleep" and "the crate is static" — which look identical in every other
///         respect — tell themselves apart at a glance.
///     </para>
/// </remarks>
public sealed class PhysicsDebugDraw {
    const int RingSegments = 16;

    /// <summary>A static body's colour.</summary>
    public static Color4 StaticColour => new(0.2f, 0.5f, 0.2f, 1f);

    /// <summary>A kinematic body's colour.</summary>
    public static Color4 KinematicColour => new(0.3f, 0.5f, 1f, 1f);

    /// <summary>An awake dynamic body's colour.</summary>
    public static Color4 AwakeColour => new(0.3f, 1f, 0.3f, 1f);

    /// <summary>A sleeping dynamic body's colour.</summary>
    public static Color4 SleepingColour => new(0.5f, 0.5f, 0.5f, 1f);

    /// <summary>A sensor's colour.</summary>
    public static Color4 SensorColour => new(1f, 0.9f, 0.2f, 1f);

    /// <summary>A contact point's colour.</summary>
    public static Color4 ContactColour => new(1f, 0.3f, 0.1f, 1f);

    /// <summary>A constraint's anchors and axis.</summary>
    public static Color4 ConstraintColour => new(0.9f, 0.4f, 1f, 1f);

    /// <summary>The segment between a constraint's two anchors — its unresolved error.</summary>
    /// <remarks>
    ///     Deliberately the same red as a contact: both mean "the solver has not finished", and a
    ///     joint whose error segment is long enough to see is a joint that is being pulled apart.
    /// </remarks>
    public static Color4 ConstraintErrorColour => new(1f, 0.3f, 0.1f, 1f);

    /// <summary>What to draw.</summary>
    public PhysicsDebugOverlay Overlay { get; set; } = PhysicsDebugOverlay.Default;

    /// <summary>How long a contact normal is drawn, in metres.</summary>
    public float ContactNormalLength { get; set; } = 0.25f;

    /// <summary>How big a constraint's anchor cross is drawn, in metres.</summary>
    public float ConstraintAnchorSize { get; set; } = 0.1f;

    /// <summary>How long a constraint's axis is drawn, in metres.</summary>
    public float ConstraintAxisLength { get; set; } = 0.5f;

    /// <summary>Draws a world's bodies and last-step contacts.</summary>
    /// <param name="world">The world.</param>
    /// <param name="draw">Where the lines go.</param>
    /// <param name="bodies">
    ///     Which bodies to draw. Passing the ones an ECS bridge knows about is cheaper than asking
    ///     Jolt for every body it has, and is what <c>PhysicsDebugDrawSystem</c> does.
    /// </param>
    public void Draw(PhysicsWorld world, DebugDraw draw, ReadOnlySpan<BodyHandle> bodies) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(draw);

        if (!draw.Enabled || Overlay == PhysicsDebugOverlay.None) {
            return;
        }

        foreach (var body in bodies) {
            if (!world.IsAlive(body)) {
                continue;
            }

            var colour = ColourOf(world, body);
            world.GetTransform(body, out var position, out var rotation);

            if ((Overlay & PhysicsDebugOverlay.Colliders) != 0) {
                DrawShape(world.Shapes, world.ShapeOf(body), position, rotation, colour, draw);
            }

            if ((Overlay & PhysicsDebugOverlay.Bounds) != 0) {
                draw.Box(world.GetBounds(body), colour);
            }

            if ((Overlay & PhysicsDebugOverlay.Axes) != 0) {
                draw.Axes(Matrix4x4.Compose(Vector3.One, rotation, position), 0.25f);
            }
        }

        if ((Overlay & PhysicsDebugOverlay.Contacts) != 0) {
            foreach (var contact in world.Contacts) {
                if (contact.Phase == Events.ContactPhase.Ended) {
                    continue;
                }

                draw.Ray(contact.Position, contact.Normal * ContactNormalLength, ContactColour);
            }
        }

        if ((Overlay & PhysicsDebugOverlay.Constraints) != 0) {
            DrawConstraints(world, draw);
        }
    }

    void DrawConstraints(PhysicsWorld world, DebugDraw draw) {
        foreach (var handle in world.ConstraintHandles) {
            world.GetConstraintAnchors(handle, out var first, out var second);

            Cross(first, ConstraintAnchorSize, ConstraintColour, draw);
            Cross(second, ConstraintAnchorSize, ConstraintColour, draw);

            // The anchors coincide when the constraint is satisfied, so this segment has no length
            // until something is pulling the joint apart — at which point it is the thing to look at.
            draw.Line(first, second, ConstraintErrorColour);

            var axis = world.GetConstraintAxis(handle);

            if (axis.LengthSquared() > 1e-9f) {
                var centre = (first + second) * 0.5f;
                draw.Line(centre - (axis * ConstraintAxisLength), centre + (axis * ConstraintAxisLength), ConstraintColour);
            }
        }
    }

    static void Cross(Vector3 centre, float size, Color4 colour, DebugDraw draw) {
        draw.Line(centre - (Vector3.UnitX * size), centre + (Vector3.UnitX * size), colour);
        draw.Line(centre - (Vector3.UnitY * size), centre + (Vector3.UnitY * size), colour);
        draw.Line(centre - (Vector3.UnitZ * size), centre + (Vector3.UnitZ * size), colour);
    }

    static Color4 ColourOf(PhysicsWorld world, BodyHandle body) {
        if (world.IsSensor(body)) {
            return SensorColour;
        }

        return world.GetMotion(body) switch {
            BodyMotion.Static => StaticColour,
            BodyMotion.Kinematic => KinematicColour,
            _ => world.IsActive(body) ? AwakeColour : SleepingColour
        };
    }

    /// <summary>Draws one registered shape as a wireframe at a pose.</summary>
    /// <param name="shapes">The registry the shape belongs to.</param>
    /// <param name="shape">The shape.</param>
    /// <param name="position">Where.</param>
    /// <param name="rotation">Which way.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="draw">Where the lines go.</param>
    /// <remarks>Public so an editor gizmo can draw a collider that has no body yet.</remarks>
    public static void DrawShape(
        PhysicsShapes shapes,
        ShapeId shape,
        Vector3 position,
        Quaternion rotation,
        Color4 colour,
        DebugDraw draw
    ) {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(draw);

        if (shape.IsNone) {
            return;
        }

        var description = shapes.Describe(shape);

        switch (description.Kind) {
            case ShapeKind.Sphere:
                DrawSphere(position, description.Radius, colour, draw);
                break;

            case ShapeKind.Box:
                DrawBox(position, rotation, description.Extents, colour, draw);
                break;

            case ShapeKind.Capsule:
                DrawCapsule(position, rotation, description.HalfHeight, description.Radius, colour, draw);
                break;

            case ShapeKind.Cylinder:
                DrawCylinder(position, rotation, description.HalfHeight, description.Radius, colour, draw);
                break;

            case ShapeKind.Plane:
                DrawPlane(description.Extents, description.Radius, colour, draw);
                break;

            case ShapeKind.Compound:
                foreach (var child in shapes.ChildrenOf(shape)) {
                    // Composition reads left to right — see Conventions.md — so the child's own
                    // rotation applies first and the parent's after it.
                    DrawShape(
                        shapes,
                        child.Shape,
                        position + Rotate(child.Position, rotation),
                        child.Rotation * rotation,
                        colour,
                        draw
                    );
                }

                break;

            default:
                // A mesh or a hull. Bounds are the honest answer — see the class remarks.
                DrawPointCloudBounds(shapes.PointsOf(shape), position, rotation, colour, draw);
                break;
        }
    }

    static void DrawSphere(Vector3 centre, float radius, Color4 colour, DebugDraw draw) =>
        draw.Sphere(new(centre, radius), colour);

    static void DrawBox(Vector3 centre, Quaternion rotation, Vector3 halfExtents, Color4 colour, DebugDraw draw) {
        Span<Vector3> corners = stackalloc Vector3[8];

        for (var index = 0; index < 8; index++) {
            var corner = new Vector3(
                (index & 1) == 0 ? -halfExtents.X : halfExtents.X,
                (index & 2) == 0 ? -halfExtents.Y : halfExtents.Y,
                (index & 4) == 0 ? -halfExtents.Z : halfExtents.Z
            );

            corners[index] = centre + Rotate(corner, rotation);
        }

        // The twelve edges of a cube indexed by which bit differs between two corners.
        for (var index = 0; index < 8; index++) {
            for (var bit = 1; bit <= 4; bit <<= 1) {
                var other = index | bit;

                if (other != index) {
                    draw.Line(corners[index], corners[other], colour);
                }
            }
        }
    }

    static void DrawCapsule(
        Vector3 centre,
        Quaternion rotation,
        float halfHeight,
        float radius,
        Color4 colour,
        DebugDraw draw
    ) {
        var up = Rotate(Vector3.UnitY, rotation);
        var right = Rotate(Vector3.UnitX, rotation);
        var forward = Rotate(Vector3.UnitZ, rotation);
        var top = centre + (up * halfHeight);
        var bottom = centre - (up * halfHeight);

        Ring(top, radius, right, forward, colour, draw);
        Ring(bottom, radius, right, forward, colour, draw);

        // The caps as half rings in two planes, which is what tells a capsule from a cylinder.
        Ring(top, radius, right, up, colour, draw, half: true);
        Ring(top, radius, forward, up, colour, draw, half: true);
        Ring(bottom, radius, right, -up, colour, draw, half: true);
        Ring(bottom, radius, forward, -up, colour, draw, half: true);

        foreach (var axis in (ReadOnlySpan<Vector3>)[right, -right, forward, -forward]) {
            draw.Line(top + (axis * radius), bottom + (axis * radius), colour);
        }
    }

    static void DrawCylinder(
        Vector3 centre,
        Quaternion rotation,
        float halfHeight,
        float radius,
        Color4 colour,
        DebugDraw draw
    ) {
        var up = Rotate(Vector3.UnitY, rotation);
        var right = Rotate(Vector3.UnitX, rotation);
        var forward = Rotate(Vector3.UnitZ, rotation);
        var top = centre + (up * halfHeight);
        var bottom = centre - (up * halfHeight);

        Ring(top, radius, right, forward, colour, draw);
        Ring(bottom, radius, right, forward, colour, draw);

        foreach (var axis in (ReadOnlySpan<Vector3>)[right, -right, forward, -forward]) {
            draw.Line(top + (axis * radius), bottom + (axis * radius), colour);
        }
    }

    static void DrawPlane(Vector3 normal, float distance, Color4 colour, DebugDraw draw) {
        var length = normal.Length();

        if (length < 1e-6f) {
            return;
        }

        var unit = normal / length;
        var origin = unit * distance;
        var right = MathF.Abs(unit.Y) > 0.99f ? Vector3.UnitX : Vector3.Normalize(Vector3.Cross(unit, Vector3.UnitY));
        var forward = Vector3.Cross(unit, right);

        // A grid rather than an outline, because an infinite plane has no outline and a lone quad at
        // the origin says nothing about where the surface is under the thing that is falling through it.
        const int Half = 5;
        const float Spacing = 1f;

        for (var step = -Half; step <= Half; step++) {
            var offset = step * Spacing;
            var extent = Half * Spacing;

            draw.Line(
                origin + (right * offset) - (forward * extent),
                origin + (right * offset) + (forward * extent),
                colour
            );

            draw.Line(
                origin + (forward * offset) - (right * extent),
                origin + (forward * offset) + (right * extent),
                colour
            );
        }

        draw.Ray(origin, unit, colour);
    }

    static void DrawPointCloudBounds(
        ReadOnlySpan<Vector3> points,
        Vector3 position,
        Quaternion rotation,
        Color4 colour,
        DebugDraw draw
    ) {
        if (points.IsEmpty) {
            return;
        }

        var minimum = points[0];
        var maximum = points[0];

        foreach (var point in points) {
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }

        var centre = (minimum + maximum) * 0.5f;
        var halfExtents = (maximum - minimum) * 0.5f;
        DrawBox(position + Rotate(centre, rotation), rotation, halfExtents, colour, draw);
    }

    static void Ring(
        Vector3 centre,
        float radius,
        Vector3 right,
        Vector3 up,
        Color4 colour,
        DebugDraw draw,
        bool half = false
    ) {
        var segments = half ? RingSegments / 2 : RingSegments;
        var sweep = half ? MathUtil.Pi : MathUtil.TwoPi;
        var previous = centre + (right * radius);

        for (var step = 1; step <= segments; step++) {
            var angle = step / (float)segments * sweep;
            var next = centre + (right * (MathF.Cos(angle) * radius)) + (up * (MathF.Sin(angle) * radius));
            draw.Line(previous, next, colour);
            previous = next;
        }
    }

    static Vector3 Rotate(Vector3 value, Quaternion rotation) => Quaternion.Transform(value, rotation);
}
