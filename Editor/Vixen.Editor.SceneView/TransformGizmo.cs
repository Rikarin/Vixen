// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>What a target held when a drag began, so the drag can be recomputed from the start.</summary>
/// <param name="Position">Where it was, in world space.</param>
/// <param name="Rotation">How it was turned, in world space.</param>
/// <param name="Scale">How big it was.</param>
readonly record struct TargetState(Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>The handles that move, turn and resize what is selected.</summary>
/// <remarks>
///     <para>
///         <b>Every drag is recomputed from where it started, never accumulated.</b> The gizmo
///         records what each target held on mouse-down and applies the whole of the drag to that,
///         which is what makes snapping land on the grid rather than near it, makes a drag that goes
///         out and comes back end where it began, and makes floating-point drift impossible instead
///         of merely small. An implementation that adds each frame's delta to the current value has
///         all three bugs and none of them are reproducible.
///     </para>
///     <para>
///         <b>It is arithmetic over an interface, with no renderer in it.</b> What the handles
///         <i>look</i> like belongs to the viewport; where they are, what is under the pointer and
///         what a drag means are here, so all of it is testable with no device.
///     </para>
///     <para>
///         ⚠ <b>The handles are a constant size on screen.</b> A gizmo in world units is a dot at
///         forty metres and fills the viewport at half a metre. Everything below that is measured in
///         world units is scaled by <see cref="WorldPerPixel" />, which is the one place the camera's
///         distance enters the arithmetic.
///     </para>
/// </remarks>
public sealed class TransformGizmo {
    readonly List<IGizmoTarget> targets = [];
    readonly List<TargetState> initial = [];

    Vector3 dragOrigin;
    Matrix4x4 dragBasis = Matrix4x4.Identity;
    Vector3 dragStartPoint;
    float dragStartScalar;
    float dragStartAngle;

    /// <summary>How long an arm is, in render pixels.</summary>
    public float HandleLength { get; set; } = 90f;

    /// <summary>How near the pointer has to be to an arm to grab it, in render pixels.</summary>
    public float GrabRadius { get; set; } = 9f;

    /// <summary>How far along the arms the plane quads sit, as a fraction of the arm.</summary>
    public float PlaneOffset { get; set; } = 0.35f;

    /// <summary>How big the plane quads are, as a fraction of the arm.</summary>
    public float PlaneSize { get; set; } = 0.22f;

    /// <summary>How big the middle box is, in render pixels.</summary>
    public float CentreRadius { get; set; } = 12f;

    /// <summary>What the gizmo changes.</summary>
    public GizmoMode Mode { get; set; } = GizmoMode.Translate;

    /// <summary>Which axes the handles point along.</summary>
    public GizmoSpace Space { get; set; } = GizmoSpace.World;

    /// <summary>Where the handles sit when several things are selected.</summary>
    public PivotMode Pivot { get; set; } = PivotMode.Pivot;

    /// <summary>What a drag rounds to.</summary>
    public SnapSettings Snap { get; } = new();

    /// <summary>The handle the pointer is over, if any.</summary>
    public GizmoHandle Hovered { get; set; }

    /// <summary>The handle being dragged, if any.</summary>
    public GizmoHandle Active { get; private set; }

    /// <summary>Whether a drag is under way.</summary>
    public bool IsDragging => Active != GizmoHandle.None;

    /// <summary>What the gizmo is moving, in selection order.</summary>
    public IReadOnlyList<IGizmoTarget> Targets => targets;

    /// <summary>Points the gizmo at a selection.</summary>
    /// <param name="selection">What to move, in selection order. The last one is the primary.</param>
    /// <exception cref="InvalidOperationException">A drag is under way.</exception>
    public void Attach(IEnumerable<IGizmoTarget> selection) {
        ArgumentNullException.ThrowIfNull(selection);

        if (IsDragging) {
            throw new InvalidOperationException(
                "The selection changed while a gizmo drag was under way. Finish or cancel the drag "
                + "first; changing it here would apply the rest of the drag to objects that were not "
                + "under the pointer when it started."
            );
        }

        targets.Clear();
        targets.AddRange(selection);
    }

    /// <summary>Where the handles sit, in world space.</summary>
    /// <returns>The origin, or the world origin when nothing is selected.</returns>
    public Vector3 Origin {
        get {
            if (targets.Count == 0) {
                return Vector3.Zero;
            }

            if (Pivot == PivotMode.Pivot || targets.Count == 1) {
                // The last one added, which is what every editor means by "the one you clicked last".
                return targets[^1].Position;
            }

            var total = Vector3.Zero;

            foreach (var target in targets) {
                total += target.Position;
            }

            return total / targets.Count;
        }
    }

    /// <summary>Which way the arms point.</summary>
    /// <param name="camera">The camera, for screen space.</param>
    /// <returns>A rotation whose rows are the X, Y and Z arms.</returns>
    /// <remarks>
    ///     ⚠ <b>Several objects selected forces world space.</b> "Local" has no answer when the
    ///     selection disagrees about which way local is, and picking the primary's would make
    ///     dragging X move nineteen objects along an axis that is not theirs.
    /// </remarks>
    public Matrix4x4 Basis(EditorCamera camera) {
        ArgumentNullException.ThrowIfNull(camera);

        if (targets.Count == 0) {
            return Matrix4x4.Identity;
        }

        return Space switch {
            GizmoSpace.Local when targets.Count == 1 => Matrix4x4.FromQuaternion(targets[0].Rotation),
            GizmoSpace.Parent when targets.Count == 1 => Orthonormal(targets[0].ParentToWorld),
            GizmoSpace.Screen => camera.Rotation,
            _ => Matrix4x4.Identity
        };
    }

    /// <summary>How many world units one render pixel is, at the gizmo's origin.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="height">How tall the viewport is, in render pixels.</param>
    /// <returns>The scale.</returns>
    /// <remarks>
    ///     ⚠ <b>Measured at the origin, not at the near plane.</b> In perspective the answer depends
    ///     on how far away the thing is, and the thing is the gizmo — so it is the distance from the
    ///     eye to the gizmo's origin along the view direction that matters, not the camera's own
    ///     orbit distance, which differs the moment the gizmo is not what the camera is orbiting.
    /// </remarks>
    public float WorldPerPixel(EditorCamera camera, int height) {
        ArgumentNullException.ThrowIfNull(camera);

        if (height <= 0) {
            return 1f;
        }

        if (camera.IsOrthographic) {
            return camera.OrthographicHeight / height;
        }

        var depth = MathF.Max(Vector3.Dot(Origin - camera.Position, camera.Forward), camera.NearPlane);

        return 2f * depth * MathF.Tan(camera.FieldOfView * 0.5f) / height;
    }

    /// <summary>Which handle is under a point.</summary>
    /// <param name="point">Where, in render pixels from the viewport's top-left.</param>
    /// <param name="camera">The camera.</param>
    /// <param name="width">How wide the viewport is, in render pixels.</param>
    /// <param name="height">How tall.</param>
    /// <returns>The handle, or <see cref="GizmoHandle.None" />.</returns>
    /// <remarks>
    ///     <para>
    ///         Hit-tested in <i>screen</i> space rather than by ray against solid handle geometry.
    ///         An arm is a line a few pixels wide and a ray test against a cylinder that thin misses
    ///         it more often than it hits; measuring the pointer's distance to the projected line is
    ///         what makes a gizmo feel like it has the grab tolerance it looks like it has.
    ///     </para>
    ///     <para>
    ///         The middle is tested first, then the planes, then the arms. Innermost first, because
    ///         the plane quads overlap the arms they sit between and the arms all overlap at the
    ///         origin — testing the other way round makes the plane handles unreachable.
    ///     </para>
    /// </remarks>
    public GizmoHandle HitTest(Vector2 point, EditorCamera camera, int width, int height) {
        ArgumentNullException.ThrowIfNull(camera);

        if (targets.Count == 0 || Mode == GizmoMode.None || width <= 0 || height <= 0) {
            return GizmoHandle.None;
        }

        var origin = Origin;
        var basis = Basis(camera);
        var scale = WorldPerPixel(camera, height) * HandleLength;
        var centre = Flat(camera.Project(origin, width, height));

        if (Mode is GizmoMode.Scale or GizmoMode.Transform
            && Vector2.Distance(point, centre) <= CentreRadius) {
            return GizmoHandle.Uniform;
        }

        if (Mode is GizmoMode.Rotate) {
            // The screen-facing ring sits outside the three axis rings, so it is tested first.
            var radius = HandleLength;
            var distance = Vector2.Distance(point, centre);

            if (MathF.Abs(distance - (radius * 1.15f)) <= GrabRadius) {
                return GizmoHandle.Screen;
            }

            return RingUnder(point, camera, origin, basis, scale, width, height);
        }

        if (Mode is GizmoMode.Translate or GizmoMode.Transform
            && PlaneUnder(point, camera, origin, basis, scale, width, height) is var plane
            && plane != GizmoHandle.None) {
            return plane;
        }

        for (var axis = 0; axis < 3; axis++) {
            var end = Flat(camera.Project(origin + (Axis(basis, axis) * scale), width, height));

            if (DistanceToSegment(point, centre, end) <= GrabRadius) {
                return axis switch {
                    0 => GizmoHandle.AxisX,
                    1 => GizmoHandle.AxisY,
                    _ => GizmoHandle.AxisZ
                };
            }
        }

        return GizmoHandle.None;
    }

    /// <summary>Starts a drag.</summary>
    /// <param name="handle">Which handle was grabbed.</param>
    /// <param name="ray">The ray under the pointer.</param>
    /// <param name="camera">The camera.</param>
    /// <returns>Whether a drag started.</returns>
    public bool Begin(GizmoHandle handle, Ray ray, EditorCamera camera) {
        ArgumentNullException.ThrowIfNull(camera);

        if (handle == GizmoHandle.None || targets.Count == 0) {
            return false;
        }

        Active = handle;
        dragOrigin = Origin;
        dragBasis = Basis(camera);

        initial.Clear();

        foreach (var target in targets) {
            initial.Add(new(target.Position, target.Rotation, target.Scale));
        }

        switch (Mode) {
            case GizmoMode.Rotate:
                dragStartAngle = AngleOn(ray, handle, camera);
                break;

            case GizmoMode.Scale:
            case GizmoMode.Transform when handle == GizmoHandle.Uniform:
                dragStartScalar = ScalarOn(ray, handle, camera);
                break;

            default:
                dragStartPoint = PointOn(ray, handle, camera);
                break;
        }

        return true;
    }

    /// <summary>Applies the whole of the drag so far.</summary>
    /// <param name="ray">The ray under the pointer now.</param>
    /// <param name="camera">The camera.</param>
    /// <returns>Whether anything moved.</returns>
    public bool Drag(Ray ray, EditorCamera camera) {
        ArgumentNullException.ThrowIfNull(camera);

        if (!IsDragging) {
            return false;
        }

        return Mode switch {
            GizmoMode.Rotate => Turn(ray, camera),
            GizmoMode.Scale => Resize(ray, camera),
            GizmoMode.Transform when Active == GizmoHandle.Uniform => Resize(ray, camera),
            _ => Move(ray, camera)
        };
    }

    /// <summary>Ends the drag, leaving everything where it is.</summary>
    public void End() {
        Active = GizmoHandle.None;
        initial.Clear();
    }

    /// <summary>Ends the drag and puts everything back where it started.</summary>
    /// <returns>Whether there was a drag to cancel.</returns>
    /// <remarks>
    ///     What Escape does. Immediate rather than deferred to a command being rolled back, so that
    ///     the viewport is redrawn from the model the instant the key is pressed — the same reason
    ///     <c>CommandTransaction.Cancel</c> rolls back at once.
    /// </remarks>
    public bool Cancel() {
        if (!IsDragging) {
            return false;
        }

        for (var index = 0; index < targets.Count && index < initial.Count; index++) {
            targets[index].Position = initial[index].Position;
            targets[index].Rotation = initial[index].Rotation;
            targets[index].Scale = initial[index].Scale;
        }

        End();
        return true;
    }

    /// <summary>What each target held when the drag began, for the undo command.</summary>
    /// <returns>The states, in target order.</returns>
    public IReadOnlyList<(Vector3 Position, Quaternion Rotation, Vector3 Scale)> Captured() =>
        initial.Select(static state => (state.Position, state.Rotation, state.Scale)).ToArray();

    bool Move(Ray ray, EditorCamera camera) {
        var offset = PointOn(ray, Active, camera) - dragStartPoint;

        if (Active is GizmoHandle.AxisX or GizmoHandle.AxisY or GizmoHandle.AxisZ) {
            var axis = Axis(dragBasis, Active - GizmoHandle.AxisX);
            offset = axis * Snap.Position(Vector3.Dot(offset, axis));
        } else {
            // A plane drag snaps in the gizmo's own axes rather than the world's, so a translate on
            // a rotated object's XY plane still lands on that object's grid.
            offset = Local(dragBasis, Snap.Position(Unlocal(dragBasis, offset)));
        }

        for (var index = 0; index < targets.Count && index < initial.Count; index++) {
            targets[index].Position = initial[index].Position + offset;
        }

        return offset.LengthSquared() > 0f;
    }

    bool Turn(Ray ray, EditorCamera camera) {
        var axis = AxisOf(Active, dragBasis, camera);
        var angle = Snap.Rotation(AngleOn(ray, Active, camera) - dragStartAngle);

        if (angle == 0f) {
            return false;
        }

        var turn = Quaternion.FromAxisAngle(axis, angle);

        for (var index = 0; index < targets.Count && index < initial.Count; index++) {
            var state = initial[index];

            // Rotation is about the gizmo's origin, not each object's own, so a group turns as a
            // group. With one object selected the two are the same point and it spins in place.
            targets[index].Position = dragOrigin + Quaternion.Transform(state.Position - dragOrigin, turn);
            targets[index].Rotation = state.Rotation * turn;
        }

        return true;
    }

    bool Resize(Ray ray, EditorCamera camera) {
        var current = ScalarOn(ray, Active, camera);

        if (MathF.Abs(dragStartScalar) < MathUtil.ZeroTolerance) {
            return false;
        }

        // Measured as a ratio of distances from the origin, so the object is the size the pointer
        // says it is however far the drag started from the middle.
        var factor = Snap.Scale(current / dragStartScalar);

        if (factor <= 0f) {
            factor = MathUtil.ZeroTolerance;
        }

        var lanes = Active switch {
            GizmoHandle.AxisX => new Vector3(factor, 1f, 1f),
            GizmoHandle.AxisY => new Vector3(1f, factor, 1f),
            GizmoHandle.AxisZ => new Vector3(1f, 1f, factor),
            _ => new Vector3(factor)
        };

        for (var index = 0; index < targets.Count && index < initial.Count; index++) {
            var state = initial[index];

            targets[index].Scale = new(state.Scale.X * lanes.X, state.Scale.Y * lanes.Y, state.Scale.Z * lanes.Z);

            if (targets.Count > 1) {
                targets[index].Position = dragOrigin + ((state.Position - dragOrigin) * factor);
            }
        }

        return true;
    }

    Vector3 PointOn(Ray ray, GizmoHandle handle, EditorCamera camera) {
        switch (handle) {
            case GizmoHandle.AxisX or GizmoHandle.AxisY or GizmoHandle.AxisZ: {
                var axis = Axis(dragBasis, handle - GizmoHandle.AxisX);

                return ClosestOnLine(ray, dragOrigin, axis);
            }

            case GizmoHandle.PlaneYZ:
                return OnPlane(ray, Axis(dragBasis, 0));

            case GizmoHandle.PlaneZX:
                return OnPlane(ray, Axis(dragBasis, 1));

            case GizmoHandle.PlaneXY:
                return OnPlane(ray, Axis(dragBasis, 2));

            default:
                return OnPlane(ray, camera.Forward);
        }
    }

    float AngleOn(Ray ray, GizmoHandle handle, EditorCamera camera) {
        var axis = AxisOf(handle, dragBasis, camera);
        var point = OnPlane(ray, axis) - dragOrigin;

        // Two perpendiculars in the ring's plane, so the angle is measured in the plane rather than
        // by projecting to screen — which would run backwards whenever the ring is seen from behind.
        var reference = MathF.Abs(Vector3.Dot(axis, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        var u = Vector3.Normalize(Vector3.Cross(reference, axis));
        var v = Vector3.Cross(axis, u);

        return MathF.Atan2(Vector3.Dot(point, v), Vector3.Dot(point, u));
    }

    float ScalarOn(Ray ray, GizmoHandle handle, EditorCamera camera) {
        if (handle is GizmoHandle.AxisX or GizmoHandle.AxisY or GizmoHandle.AxisZ) {
            var axis = Axis(dragBasis, handle - GizmoHandle.AxisX);

            return Vector3.Dot(ClosestOnLine(ray, dragOrigin, axis) - dragOrigin, axis);
        }

        return (OnPlane(ray, camera.Forward) - dragOrigin).Length();
    }

    Vector3 OnPlane(Ray ray, Vector3 normal) {
        var plane = new Plane(normal, -Vector3.Dot(normal, dragOrigin));

        // A ray parallel to the plane has no answer, so the drag holds where it was rather than
        // flinging the selection to infinity — which is what an unchecked intersection does when the
        // camera happens to line up with the plane.
        return ray.Intersects(plane, out var distance) ? ray.GetPoint(distance) : dragStartPoint;
    }

    GizmoHandle PlaneUnder(
        Vector2 point,
        EditorCamera camera,
        Vector3 origin,
        Matrix4x4 basis,
        float scale,
        int width,
        int height
    ) {
        Span<GizmoHandle> handles = [GizmoHandle.PlaneYZ, GizmoHandle.PlaneZX, GizmoHandle.PlaneXY];

        for (var index = 0; index < 3; index++) {
            var first = Axis(basis, (index + 1) % 3);
            var second = Axis(basis, (index + 2) % 3);

            // ⚠ A plane seen edge-on is not offered. Its quad projects to a sliver lying along the
            // third arm, so hit-testing it would take that arm's clicks — and dragging in a plane you
            // are looking along the edge of is not a thing anybody can aim anyway. Every editor hides
            // these handles for the same reason; here it also has to affect the *test*, because a
            // handle that is hidden and still grabbable is worse than one that is neither.
            if (MathF.Abs(Vector3.Dot(Vector3.Cross(first, second), camera.Forward)) < 0.15f) {
                continue;
            }

            var corner = origin + ((first + second) * (scale * PlaneOffset));

            var a = Flat(camera.Project(corner, width, height));
            var b = Flat(camera.Project(corner + (first * (scale * PlaneSize)), width, height));
            var c = Flat(camera.Project(corner + (second * (scale * PlaneSize)), width, height));
            var d = Flat(camera.Project(corner + ((first + second) * (scale * PlaneSize)), width, height));

            if (InQuad(point, a, b, d, c)) {
                return handles[index];
            }
        }

        return GizmoHandle.None;
    }

    GizmoHandle RingUnder(
        Vector2 point,
        EditorCamera camera,
        Vector3 origin,
        Matrix4x4 basis,
        float scale,
        int width,
        int height
    ) {
        var best = GizmoHandle.None;
        var nearest = GrabRadius;

        for (var axis = 0; axis < 3; axis++) {
            var normal = Axis(basis, axis);
            var reference = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            var u = Vector3.Normalize(Vector3.Cross(reference, normal));
            var v = Vector3.Cross(normal, u);

            // Sampled rather than solved: a circle in three dimensions projects to an ellipse whose
            // closest point is a quartic, and thirty-two samples is both faster and shorter than the
            // closed form for a test whose tolerance is nine pixels.
            var previous = Vector2.Zero;

            for (var step = 0; step <= 32; step++) {
                var angle = step / 32f * MathF.Tau;
                var world = origin + ((u * MathF.Cos(angle)) + (v * MathF.Sin(angle))) * scale;
                var screen = Flat(camera.Project(world, width, height));

                if (step > 0) {
                    var distance = DistanceToSegment(point, previous, screen);

                    if (distance < nearest) {
                        nearest = distance;

                        best = axis switch {
                            0 => GizmoHandle.AxisX,
                            1 => GizmoHandle.AxisY,
                            _ => GizmoHandle.AxisZ
                        };
                    }
                }

                previous = screen;
            }
        }

        return best;
    }

    static Vector3 AxisOf(GizmoHandle handle, Matrix4x4 basis, EditorCamera camera) =>
        handle switch {
            GizmoHandle.AxisX => Axis(basis, 0),
            GizmoHandle.AxisY => Axis(basis, 1),
            GizmoHandle.AxisZ => Axis(basis, 2),
            _ => camera.Forward
        };

    static Vector3 Axis(Matrix4x4 basis, int index) =>
        index switch {
            0 => Vector3.Normalize(new(basis.M11, basis.M12, basis.M13)),
            1 => Vector3.Normalize(new(basis.M21, basis.M22, basis.M23)),
            _ => Vector3.Normalize(new(basis.M31, basis.M32, basis.M33))
        };

    static Vector3 Local(Matrix4x4 basis, Vector3 value) =>
        (Axis(basis, 0) * value.X) + (Axis(basis, 1) * value.Y) + (Axis(basis, 2) * value.Z);

    static Vector3 Unlocal(Matrix4x4 basis, Vector3 value) =>
        new(
            Vector3.Dot(value, Axis(basis, 0)),
            Vector3.Dot(value, Axis(basis, 1)),
            Vector3.Dot(value, Axis(basis, 2))
        );

    /// <summary>Strips the translation and the scale out of a matrix, leaving the axes.</summary>
    static Matrix4x4 Orthonormal(Matrix4x4 matrix) {
        var x = Vector3.Normalize(new Vector3(matrix.M11, matrix.M12, matrix.M13));
        var y = Vector3.Normalize(new Vector3(matrix.M21, matrix.M22, matrix.M23));
        var z = Vector3.Normalize(new Vector3(matrix.M31, matrix.M32, matrix.M33));

        return x.IsZero || y.IsZero || z.IsZero
            ? Matrix4x4.Identity
            : new(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);
    }

    /// <summary>The point on an infinite line that a ray passes nearest to.</summary>
    /// <remarks>
    ///     <para>
    ///         What an axis drag is: the pointer is a ray and the arm is a line, and where the object
    ///         goes is where those two come closest. The degenerate case — looking straight down the
    ///         arm — has no nearest point, and the drag holds where it was rather than dividing by
    ///         zero.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The ray's direction is normalised here rather than assumed.</b>
    ///         <c>Viewport.GetPickingRay</c> hands back <c>far − near</c>, which is a thousand units
    ///         long — the closed form below is only right for unit vectors, and with an unnormalised
    ///         one it lands the object somewhere between a thousandth and a millionth of the way along
    ///         the arm. That reads as "the gizmo does not move things", which is a long way from where
    ///         the mistake is.
    ///     </para>
    /// </remarks>
    static Vector3 ClosestOnLine(Ray ray, Vector3 point, Vector3 direction) {
        var along = Vector3.Normalize(ray.Direction);

        if (along.IsZero) {
            return point;
        }

        // ⚠ From the ray's origin *to* the line's point, not the other way round. Reversing it
        // negates the parameter, so the object tracks the pointer's mirror image — which looks like
        // an inverted axis rather than like a sign error, and only on some axes from some angles.
        var w = point - ray.Origin;
        var b = Vector3.Dot(direction, along);
        var d = Vector3.Dot(direction, w);
        var e = Vector3.Dot(along, w);
        var denominator = 1f - (b * b);

        if (MathF.Abs(denominator) < 1e-5f) {
            return point;
        }

        return point + (direction * (((b * e) - d) / denominator));
    }

    static Vector2 Flat(Vector3 projected) => new(projected.X, projected.Y);

    static float DistanceToSegment(Vector2 point, Vector2 from, Vector2 to) {
        var span = to - from;
        var lengthSquared = span.LengthSquared();

        if (lengthSquared <= float.Epsilon) {
            return Vector2.Distance(point, from);
        }

        var t = Math.Clamp(Vector2.Dot(point - from, span) / lengthSquared, 0f, 1f);

        return Vector2.Distance(point, from + (span * t));
    }

    /// <summary>Whether a point is inside a convex quad given in order.</summary>
    static bool InQuad(Vector2 point, Vector2 a, Vector2 b, Vector2 c, Vector2 d) =>
        InTriangle(point, a, b, c) || InTriangle(point, a, c, d);

    static bool InTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c) {
        var d1 = Cross(point - a, b - a);
        var d2 = Cross(point - b, c - b);
        var d3 = Cross(point - c, a - c);

        var negative = d1 < 0f || d2 < 0f || d3 < 0f;
        var positive = d1 > 0f || d2 > 0f || d3 > 0f;

        // Inside means every edge answers the same way. Both flags set means the point is outside,
        // and this holds whichever winding the quad projected to — which changes when the gizmo is
        // seen from the other side.
        return !(negative && positive);
    }

    static float Cross(Vector2 left, Vector2 right) => (left.X * right.Y) - (left.Y * right.X);
}
