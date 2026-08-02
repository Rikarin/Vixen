// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>A place on a primitive's surface, in that primitive's own parameterisation.</summary>
/// <param name="Face">
///     Which patch. <c>−1</c> for the one surface a round shape mostly is; <c>0</c>–<c>5</c> for a
///     box's faces in the order <c>+X −X +Y −Y +Z −Z</c>; <c>0</c> and <c>1</c> for the top and
///     bottom cap of a capsule, cylinder or cone.
/// </param>
/// <param name="U">Around, in <c>[0, 1)</c>. The angle for a round shape.</param>
/// <param name="V">Along, in <c>[0, 1]</c>. Zero at the bottom.</param>
/// <remarks>
///     ⚠ <b>Normalised, and that is the whole point of the type.</b> Scale a shape and the same
///     <c>(face, u, v)</c> resolves to a different world point that means <b>the same place on the
///     body</b> — a hand on the belly of a slim character resolves to the belly of a heavy one, and
///     the same clip works on both. There is no cheap correspondence between the vertices of two
///     meshes and there is an exact one between the surfaces of two boxes, which is why proxy shapes
///     exist at all and why a mesh cannot substitute for them.
/// </remarks>
public readonly record struct SurfacePoint(int Face, float U, float V) {
    /// <summary>The middle of a round shape's side.</summary>
    public static SurfacePoint Side => new(-1, 0f, 0.5f);
}

/// <summary>A resolved place on a surface: where it is, which way it faces, and which way is along.</summary>
/// <param name="Position">Where, in the shape's own space.</param>
/// <param name="Normal">Which way the surface faces there. Unit length.</param>
/// <param name="Tangent">
///     Which way <c>U</c> increases there, made perpendicular to the normal. Unit length.
/// </param>
public readonly record struct SurfaceSample(Vector3 Position, Vector3 Normal, Vector3 Tangent) {
    /// <summary>The frame at that place: <c>+Y</c> outward, <c>+X</c> along <c>U</c>.</summary>
    /// <returns>The rotation.</returns>
    /// <remarks>
    ///     <b><c>+Y</c> is the outward normal</b>, matching the convention that a shape's own long
    ///     axis is <c>Y</c> — so an orientation authored against a surface reads the same way whether
    ///     the surface is the side of a limb or the top of a crate.
    /// </remarks>
    public Quaternion Rotation() {
        var up = Normal.LengthSquared() > 1e-8f ? Vector3.Normalize(Normal) : Vector3.Up;
        var along = Tangent - (up * Vector3.Dot(Tangent, up));

        along = along.LengthSquared() > 1e-8f
            ? Vector3.Normalize(along)
            : Vector3.Normalize(Vector3.Cross(up, MathF.Abs(up.Y) > 0.99f ? Vector3.Forward : Vector3.Up));

        // ⚠ `along × up`, not `up × along`. The other order gives a left-handed basis, whose matrix
        // decomposes to a rotation with a negative scale — silently, and in a way that shows up much
        // later as a mirrored contact frame.
        var side = Vector3.Cross(along, up);

        // Row-vector: the rows are where the basis axes land. Conventions.md.
        return BoneTransform.FromMatrix(
            new Matrix4x4(
                along.X, along.Y, along.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                side.X, side.Y, side.Z, 0f,
                0f, 0f, 0f, 1f
            )
        ).Rotation;
    }
}

/// <summary>Where a point is on a primitive, and where a primitive's surface is at a point.</summary>
/// <remarks>
///     <para>
///         Pure geometry with no dependency on anything but the maths, which is what keeps
///         <c>Vixen.Animation</c> off <c>Vixen.Physics</c> — the one place
///         [D0](../../../docs/plan/34-move-sets-and-pose-constraints.md) says that boundary would
///         otherwise break.
///     </para>
///     <para>
///         ⚠ <b><see cref="Project" /> and <see cref="Evaluate" /> are exact inverses of each other
///         through the residual, and that is the property to hold on to.</b> Projection is not always
///         the true closest point — a tapered capsule's side is treated as a lerped-radius cone rather
///         than as the exact tangent conic, and the caps as hemispheres of the end radii — but the
///         residual absorbs whatever the projection missed, so an authored point round-trips to
///         itself on the body it was authored on and lands somewhere sensible on any other. A bake
///         that quietly moved a contact would be much worse than one that approximates the surface.
///     </para>
/// </remarks>
public static class ShapeGeometry {
    const float Epsilon = 1e-6f;

    /// <summary>Where the surface is at a coordinate.</summary>
    /// <param name="kind">Which primitive.</param>
    /// <param name="dimensions">How big it is.</param>
    /// <param name="point">The coordinate.</param>
    /// <returns>The place, in the shape's own space.</returns>
    public static SurfaceSample Evaluate(ShapeKind kind, in ShapeParams dimensions, in SurfacePoint point) =>
        kind switch {
            ShapeKind.Box or ShapeKind.TaperedBox => EvaluateBox(kind, dimensions, point),
            ShapeKind.Sphere => EvaluateSphere(dimensions, point),
            ShapeKind.Capsule or ShapeKind.TaperedCapsule => EvaluateCapsule(dimensions, point),
            _ => EvaluateCylinder(kind, dimensions, point)
        };

    /// <summary>The coordinate nearest a point, and how far off the surface it was.</summary>
    /// <param name="kind">Which primitive.</param>
    /// <param name="dimensions">How big it is.</param>
    /// <param name="local">The point, in the shape's own space.</param>
    /// <param name="residual">
    ///     What is left over, in the <em>surface's</em> frame — normal, tangent and side. Stored
    ///     separately and applied unscaled, so a deliberate one-centimetre gap stays one centimetre
    ///     on a body twice the size.
    /// </param>
    /// <returns>The coordinate.</returns>
    public static SurfacePoint Project(
        ShapeKind kind,
        in ShapeParams dimensions,
        Vector3 local,
        out Vector3 residual
    ) {
        var point = kind switch {
            ShapeKind.Box or ShapeKind.TaperedBox => ProjectBox(kind, dimensions, local),
            ShapeKind.Sphere => ProjectSphere(dimensions, local),
            ShapeKind.Capsule or ShapeKind.TaperedCapsule => ProjectCapsule(dimensions, local),
            _ => ProjectCylinder(kind, dimensions, local)
        };

        var sample = Evaluate(kind, dimensions, point);
        var rotation = sample.Rotation();
        residual = Quaternion.Transform(local - sample.Position, Quaternion.Conjugate(rotation));

        return point;
    }

    // ------------------------------------------------------------------ box

    static SurfaceSample EvaluateBox(ShapeKind kind, in ShapeParams dimensions, in SurfacePoint point) {
        var face = Math.Clamp(point.Face, 0, 5);
        var axis = face / 2;
        var sign = (face & 1) == 0 ? 1f : -1f;
        var height = dimensions.Extents.Y;
        var u = (point.U * 2f) - 1f;
        var v = (point.V * 2f) - 1f;

        // The two axes that are not the face's own, in a fixed order so a coordinate authored on one
        // body reads the same on another: U runs along the later axis for an X face and along X
        // otherwise, V along the remaining one.
        var (across, along) = axis switch {
            0 => (2, 1),
            1 => (0, 2),
            _ => (0, 1)
        };

        var y = axis == 1 ? sign * height : v * height;
        var taper = Taper(kind, dimensions, y);
        var position = Vector3.Zero;

        position = With(position, axis, sign * Component(taper, axis));
        position = With(position, across, u * Component(taper, across));
        position = With(position, along, axis == 1 ? v * Component(taper, along) : y);

        var normal = With(Vector3.Zero, axis, sign);

        if (kind is ShapeKind.TaperedBox && axis != 1 && height > Epsilon) {
            // A slanted side. The face is x = e(y), so the outward normal tilts by the slope.
            var slope = (Component(dimensions.TopExtents, axis) - Component(dimensions.Extents, axis))
                / (2f * height);

            normal = Vector3.Normalize(With(new Vector3(0f, -slope * sign, 0f), axis, sign));
        }

        return new(position, normal, With(Vector3.Zero, across, 1f));
    }

    static SurfacePoint ProjectBox(ShapeKind kind, in ShapeParams dimensions, Vector3 local) {
        var height = dimensions.Extents.Y;
        var y = MathUtil.Clamp(local.Y, -height, height);
        var taper = Taper(kind, dimensions, y);

        // Which face is nearest: the one the point is furthest through, measured as a fraction of the
        // half-extent so a long thin box does not always answer with its long face.
        var best = 0;
        var deepest = float.NegativeInfinity;

        for (var axis = 0; axis < 3; axis++) {
            var extent = MathF.Max(Component(axis == 1 ? dimensions.Extents : taper, axis), Epsilon);
            var reach = MathF.Abs(Component(local, axis)) / extent;

            if (reach > deepest) {
                deepest = reach;
                best = axis;
            }
        }

        var sign = Component(local, best) >= 0f ? 0 : 1;
        var face = (best * 2) + sign;
        var (across, along) = best switch {
            0 => (2, 1),
            1 => (0, 2),
            _ => (0, 1)
        };

        var u = Fraction(Component(local, across), Component(taper, across));

        var v = best == 1
            ? Fraction(Component(local, along), Component(taper, along))
            : Fraction(y, height);

        return new(face, u, v);
    }

    static Vector3 Taper(ShapeKind kind, in ShapeParams dimensions, float y) {
        if (kind is not (ShapeKind.TaperedBox or ShapeKind.TaperedCapsule or ShapeKind.Cone)) {
            return dimensions.Extents;
        }

        var height = dimensions.Extents.Y;
        var t = height <= Epsilon ? 1f : MathUtil.Saturate(((y / height) + 1f) * 0.5f);

        return Vector3.Lerp(dimensions.Extents, dimensions.TopExtents, t);
    }

    // ------------------------------------------------------------------ sphere

    /// <summary>
    ///     ⚠ A sphere scaled per-axis is an ellipsoid, and both directions of this pair have to know
    ///     it. The parameterisation is on the <em>unit</em> sphere and the extents are applied
    ///     afterwards, so the angles mean the same thing on a body that is wider than it is deep — and
    ///     the normal is not the direction to the point, which is only true when the three extents
    ///     agree.
    /// </summary>
    static SurfaceSample EvaluateSphere(in ShapeParams dimensions, in SurfacePoint point) {
        var direction = Direction(point.U, point.V);
        var extents = dimensions.Extents;

        return new(
            direction * extents,
            Normalize(Divide(direction, extents), direction),
            Tangent(point.U) * extents
        );
    }

    static SurfacePoint ProjectSphere(in ShapeParams dimensions, Vector3 local) =>
        Angles(Divide(local, dimensions.Extents), -1);

    static Vector3 Divide(Vector3 value, Vector3 by) =>
        new(
            MathF.Abs(by.X) <= Epsilon ? 0f : value.X / by.X,
            MathF.Abs(by.Y) <= Epsilon ? 0f : value.Y / by.Y,
            MathF.Abs(by.Z) <= Epsilon ? 0f : value.Z / by.Z
        );

    static Vector3 Normalize(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > Epsilon ? Vector3.Normalize(value) : Vector3.Normalize(fallback);

    // ------------------------------------------------------------------ capsule

    static SurfaceSample EvaluateCapsule(in ShapeParams dimensions, in SurfacePoint point) {
        var height = dimensions.Extents.Y;

        if (point.Face >= 0) {
            // A cap: a hemisphere of the end's own radius, sitting on the end of the straight part.
            var top = point.Face == 0;
            var radius = top ? dimensions.TopRadius : dimensions.Radius;
            var direction = Direction(point.U, top ? 0.5f + (point.V * 0.5f) : point.V * 0.5f);

            return new(
                new Vector3(0f, top ? height : -height, 0f) + (direction * radius),
                direction,
                Tangent(point.U)
            );
        }

        var y = MathUtil.Lerp(-height, height, point.V);
        var side = Radial(point.U);
        var wide = Taper(ShapeKind.TaperedCapsule, dimensions, y).X;
        var slope = height <= Epsilon ? 0f : (dimensions.TopRadius - dimensions.Radius) / (2f * height);

        return new(
            new Vector3(side.X * wide, y, side.Z * wide),
            Vector3.Normalize(side - (Vector3.Up * slope)),
            Tangent(point.U)
        );
    }

    static SurfacePoint ProjectCapsule(in ShapeParams dimensions, Vector3 local) {
        var height = dimensions.Extents.Y;

        if (local.Y > height) {
            var angles = Angles(local - new Vector3(0f, height, 0f), 0);
            return angles with { V = MathUtil.Saturate((angles.V - 0.5f) * 2f) };
        }

        if (local.Y < -height) {
            var angles = Angles(local + new Vector3(0f, height, 0f), 1);
            return angles with { V = MathUtil.Saturate(angles.V * 2f) };
        }

        return new(-1, Azimuth(local), Fraction(local.Y, height));
    }

    // ------------------------------------------------------------------ cylinder and cone

    static SurfaceSample EvaluateCylinder(ShapeKind kind, in ShapeParams dimensions, in SurfacePoint point) {
        var height = dimensions.Extents.Y;

        if (point.Face >= 0) {
            // A flat cap. V is the fraction of the way out from the axis, so the centre of a disc is
            // reachable — which the side parameterisation cannot express.
            var top = point.Face == 0;
            var radius = top ? Taper(kind, dimensions, height).X : dimensions.Radius;
            var sign = top ? 1f : -1f;

            var out1 = Radial(point.U) * (radius * point.V);

            return new(
                new Vector3(out1.X, sign * height, out1.Z),
                new(0f, sign, 0f),
                Tangent(point.U)
            );
        }

        var y = MathUtil.Lerp(-height, height, point.V);
        var side = Radial(point.U);
        var wide = Taper(kind, dimensions, y).X;
        var slope = height <= Epsilon ? 0f : (Taper(kind, dimensions, height).X - dimensions.Radius) / (2f * height);

        return new(
            new Vector3(side.X * wide, y, side.Z * wide),
            Vector3.Normalize(side - (Vector3.Up * slope)),
            Tangent(point.U)
        );
    }

    static SurfacePoint ProjectCylinder(ShapeKind kind, in ShapeParams dimensions, Vector3 local) {
        var height = dimensions.Extents.Y;
        var y = MathUtil.Clamp(local.Y, -height, height);
        var radius = MathF.Max(Taper(kind, dimensions, y).X, Epsilon);
        var out2 = new Vector3(local.X, 0f, local.Z).Length();

        if (local.Y >= height && out2 < radius) {
            return new(0, Azimuth(local), MathUtil.Saturate(out2 / MathF.Max(Taper(kind, dimensions, height).X, Epsilon)));
        }

        if (local.Y <= -height && out2 < dimensions.Radius) {
            return new(1, Azimuth(local), MathUtil.Saturate(out2 / MathF.Max(dimensions.Radius, Epsilon)));
        }

        return new(-1, Azimuth(local), Fraction(y, height));
    }

    // ------------------------------------------------------------------ shared

    /// <summary>The outward direction at an angle around the shape's own axis.</summary>
    static Vector3 Radial(float u) {
        var azimuth = u * MathUtil.TwoPi;
        return new(MathF.Sin(azimuth), 0f, MathF.Cos(azimuth));
    }

    /// <summary>Which way <c>U</c> increases at that angle — the derivative of <see cref="Radial" />.</summary>
    static Vector3 Tangent(float u) {
        var azimuth = u * MathUtil.TwoPi;
        return new(MathF.Cos(azimuth), 0f, -MathF.Sin(azimuth));
    }

    static Vector3 Direction(float u, float v) {
        var polar = (1f - MathUtil.Saturate(v)) * MathUtil.Pi;
        var azimuth = u * MathUtil.TwoPi;
        var ring = MathF.Sin(polar);

        return new(ring * MathF.Sin(azimuth), MathF.Cos(polar), ring * MathF.Cos(azimuth));
    }

    static SurfacePoint Angles(Vector3 local, int face) {
        var length = local.Length();

        if (length <= Epsilon) {
            return new(face, 0f, 1f);
        }

        var direction = local / length;

        return new(
            face,
            Azimuth(local),
            1f - (MathF.Acos(MathUtil.Clamp(direction.Y, -1f, 1f)) / MathUtil.Pi)
        );
    }

    static float Azimuth(Vector3 local) {
        if (MathF.Abs(local.X) <= Epsilon && MathF.Abs(local.Z) <= Epsilon) {
            return 0f;
        }

        var angle = MathF.Atan2(local.X, local.Z) / MathUtil.TwoPi;
        return angle < 0f ? angle + 1f : angle;
    }

    static float Fraction(float value, float extent) =>
        extent <= Epsilon ? 0.5f : MathUtil.Saturate(((value / extent) + 1f) * 0.5f);

    static float Component(Vector3 value, int axis) => axis switch { 0 => value.X, 1 => value.Y, _ => value.Z };

    static Vector3 With(Vector3 value, int axis, float component) =>
        axis switch {
            0 => new Vector3(component, value.Y, value.Z),
            1 => new Vector3(value.X, component, value.Z),
            _ => new Vector3(value.X, value.Y, component)
        };
}
