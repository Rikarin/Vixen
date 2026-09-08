// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.Texturing.Painting;

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
readonly record struct PaintSymmetry(Vector3 Normal, float Offset) {
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
