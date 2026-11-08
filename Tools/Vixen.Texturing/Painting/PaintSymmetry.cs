// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Texturing.Painting;

/// <summary>A plane the stroke is mirrored through, in the mesh's own space.</summary>
/// <param name="Normal">Which way the plane faces. Unit.</param>
/// <param name="Offset">How far along that normal the plane is from the origin.</param>
/// <remarks>
///     <para>
///         <b>⚠ The mirror is applied to the <em>ray</em> and never to the atlas, and
///         <c>PaintSession</c>'s own remarks say why that is a result rather than a shortcut.</b> A
///         plane mirrors a point in object space; the mirrored point lands on a different triangle,
///         which is in a different chart, at an unrelated place in the atlas. There is no transform
///         of the atlas that performs it — so symmetry is the one stroke-level effect that can only
///         be supplied by whatever holds the geometry, which is <see cref="PaintProjection" />.
///     </para>
///     <para>
///         ⚠ <b>Whether the mesh is symmetric is the artist's claim and is not checked here.</b> A
///         mirrored ray against an asymmetric model hits something else, or nothing; both are
///         ordinary answers a viewport shows by painting somewhere unexpected or nowhere, and
///         neither is a state this type could distinguish from a model that is symmetric except
///         where the artist is working.
///     </para>
///     <para>
///         ⚠ <b>Mirroring a ray reverses its handedness and that is exactly what makes it hit.</b>
///         A reflected direction strikes the reflected geometry, which for a symmetric mesh is the
///         same soup — so the triangle it finds is the one the artist means. <c>Backface</c> is not
///         consulted for the same reason it is not consulted on the primary ray: the winding a
///         mirrored triangle presents is the mirror's doing rather than the model's.
///     </para>
/// </remarks>
public readonly record struct PaintSymmetry(Vector3 Normal, float Offset) {
    /// <summary>The plane <c>x = 0</c>, which is what a character rigged down its own axis wants.</summary>
    public static PaintSymmetry X { get; } = new(Vector3.UnitX, 0f);

    /// <summary>The plane <c>y = 0</c>.</summary>
    public static PaintSymmetry Y { get; } = new(Vector3.UnitY, 0f);

    /// <summary>The plane <c>z = 0</c>.</summary>
    public static PaintSymmetry Z { get; } = new(Vector3.UnitZ, 0f);

    /// <summary>A plane facing a direction and passing through a point.</summary>
    /// <param name="normal">Which way it faces. Normalised here.</param>
    /// <param name="through">A point on it.</param>
    /// <returns>The plane.</returns>
    /// <exception cref="ArgumentException">The normal has no direction.</exception>
    public static PaintSymmetry Across(Vector3 normal, Vector3 through) {
        var length = normal.Length();

        if (!(length > 0f) || !float.IsFinite(length)) {
            throw new ArgumentException(
                "A symmetry plane with no normal has no side, so a mirrored stroke would land on top "
                + "of the one it mirrors.",
                nameof(normal)
            );
        }

        var unit = normal / length;

        return new(unit, Vector3.Dot(unit, through));
    }

    /// <summary>The reflection of a point.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Its mirror.</returns>
    public Vector3 Mirror(Vector3 point) => point - (2f * (Vector3.Dot(Normal, point) - Offset) * Normal);

    /// <summary>The reflection of a direction, which the offset does not touch.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>Its mirror.</returns>
    /// <remarks>
    ///     ⚠ <b>Without the offset, because a direction is a difference of two points and the offset
    ///     cancels.</b> Reflecting a ray's direction as though it were a point is the classic version
    ///     of this bug: it is invisible for a plane through the origin, which is the only plane
    ///     anybody tests with.
    /// </remarks>
    public Vector3 MirrorDirection(Vector3 direction) => direction - (2f * Vector3.Dot(Normal, direction) * Normal);

    /// <summary>The reflection of a ray: its origin mirrored as a point, its direction as a direction.</summary>
    /// <param name="ray">The ray.</param>
    /// <returns>Its mirror.</returns>
    public Ray Mirror(Ray ray) => new(Mirror(ray.Origin), MirrorDirection(ray.Direction));
}

/// <summary>A rigid motion of the mesh's own space: a turn or a reflection, then a shift.</summary>
/// <param name="X">Where the x axis goes. Unit, and at right angles to the other two.</param>
/// <param name="Y">Where the y axis goes.</param>
/// <param name="Z">Where the z axis goes.</param>
/// <param name="Shift">What is added after the axes have moved a point.</param>
/// <remarks>
///     <para>
///         <b>A symmetry copy is one of these, and both kinds are the same three columns.</b> A mirror's axes
///         are left-handed and a turn's are right-handed; nothing that applies one needs to know which it has,
///         because <see cref="PaintView" /> carries its right axis rather than crossing it, and a mirrored
///         camera is left-handed on purpose.
///     </para>
///     <para>
///         ⚠ <b>Lengths are kept, so a direction's dot product with a moved direction is its inverse's.</b>
///         That is what lets a copy weigh a point by where the stroke itself would have it: turning the frame
///         a weight is measured in is the same as turning the point back.
///     </para>
/// </remarks>
public readonly record struct PaintIsometry(Vector3 X, Vector3 Y, Vector3 Z, Vector3 Shift) {
    /// <summary>Nothing moves.</summary>
    public static PaintIsometry Identity { get; } = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);

    /// <summary>The reflection through a plane.</summary>
    /// <param name="plane">The plane.</param>
    /// <returns>The reflection.</returns>
    public static PaintIsometry Reflection(PaintSymmetry plane) =>
        new(
            plane.MirrorDirection(Vector3.UnitX),
            plane.MirrorDirection(Vector3.UnitY),
            plane.MirrorDirection(Vector3.UnitZ),
            2f * plane.Offset * plane.Normal
        );

    /// <summary>A turn about a line.</summary>
    /// <param name="axis">Which way the line runs. Normalised here.</param>
    /// <param name="through">A point on it.</param>
    /// <param name="angle">How far, in radians, anticlockwise looking back down the axis.</param>
    /// <returns>The turn.</returns>
    /// <exception cref="ArgumentException">The axis has no direction.</exception>
    public static PaintIsometry Rotation(Vector3 axis, Vector3 through, float angle) {
        var unit = Unit(axis);
        var (sin, cos) = MathF.SinCos(angle);
        var turned = new PaintIsometry(
            Turn(Vector3.UnitX, unit, sin, cos),
            Turn(Vector3.UnitY, unit, sin, cos),
            Turn(Vector3.UnitZ, unit, sin, cos),
            Vector3.Zero
        );

        return turned with { Shift = through - turned.Direction(through) };
    }

    /// <summary>A direction moved, which the shift does not touch.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>Where it points now.</returns>
    public Vector3 Direction(Vector3 direction) => (X * direction.X) + (Y * direction.Y) + (Z * direction.Z);

    /// <summary>A point moved.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Where it is now.</returns>
    public Vector3 Point(Vector3 point) => Direction(point) + Shift;

    /// <summary>Rodrigues: a vector keeps its part along the axis and turns the rest.</summary>
    static Vector3 Turn(Vector3 v, Vector3 unit, float sin, float cos) =>
        (v * cos) + (Vector3.Cross(unit, v) * sin) + (unit * (Vector3.Dot(unit, v) * (1f - cos)));

    static Vector3 Unit(Vector3 direction) {
        var length = direction.Length();

        if (!(length > 0f) || !float.IsFinite(length)) {
            throw new ArgumentException(
                "A turn with no axis has no copies: every one would land on the stroke itself.",
                nameof(direction)
            );
        }

        return direction / length;
    }
}

/// <summary>Every copy a symmetric stroke makes of itself, the stroke itself not among them.</summary>
/// <remarks>
///     <para>
///         <b>Planar and radial symmetry are the same thing to a stroke: a list of rigid motions.</b> A mirror is
///         one reflection; a radial symmetry of <i>n</i> is <i>n</i> − 1 turns about one line. Whoever holds the
///         geometry applies each to the camera or to the stamp's place on the mesh, as <see cref="PaintSymmetry" />
///         says, and never to the atlas.
///     </para>
///     <para>
///         ⚠ <b>The line or the plane goes through a point the caller names</b>, which is what lets a model that is
///         not centred on its own origin be painted symmetrically: <c>PaintProjection.Bounds</c>'s centre is the
///         usual answer. A copy of a stroke on a model that is not symmetric lands somewhere else or nowhere, and
///         this cannot tell the difference, for <see cref="PaintSymmetry" />'s reason.
///     </para>
/// </remarks>
public sealed class PaintSymmetryGroup {
    /// <summary>The most strokes a radial symmetry makes, the stroke among them.</summary>
    /// <remarks>Each copy is a raycast and a walk per stamp, so the cost of a stamp is linear in this.</remarks>
    public const int MaximumCount = 64;

    readonly PaintIsometry[] copies;

    PaintSymmetryGroup(PaintIsometry[] copies) => this.copies = copies;

    /// <summary>The copies, in order.</summary>
    public IReadOnlyList<PaintIsometry> Copies => copies;

    /// <summary>A stroke and its reflection through a plane.</summary>
    /// <param name="plane">The plane.</param>
    /// <returns>The symmetry.</returns>
    public static PaintSymmetryGroup Mirror(PaintSymmetry plane) => new([PaintIsometry.Reflection(plane)]);

    /// <summary>A stroke repeated evenly round a line.</summary>
    /// <param name="axis">Which way the line runs.</param>
    /// <param name="through">A point on it.</param>
    /// <param name="count">How many strokes there are in all, the stroke among them: 2 to <see cref="MaximumCount" />.</param>
    /// <returns>The symmetry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is out of range.</exception>
    /// <exception cref="ArgumentException">The axis has no direction.</exception>
    public static PaintSymmetryGroup Radial(Vector3 axis, Vector3 through, int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumCount);

        var copies = new PaintIsometry[count - 1];

        for (var at = 1; at < count; at++) {
            copies[at - 1] = PaintIsometry.Rotation(axis, through, MathF.Tau * at / count);
        }

        return new(copies);
    }
}
