// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Normalising a direction without the absolute floor <see cref="Vector3.Normalize" /> has.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Vector3.Normalize</c> returns zero for anything shorter than
///         <c>MathUtil.ZeroTolerance</c>, which is <c>1e-6</c> and is an absolute length.</b> That is
///         the failure docs/plan/41 § D3 names and this repository has been bitten by twice, sitting
///         in the shared maths — and it is invisible until something is measured at an unusual size,
///         because at unit scale nothing is ever that short.
///     </para>
///     <para>
///         ⚠ <b>The cross product is where it bites first, because it scales as the <i>square</i> of
///         the model.</b> A sphere a millimetre across has triangles whose cross products are
///         <c>1e-9</c>, so every triangle normal on it comes back as the zero vector, every vertex
///         normal with it, and the field solve, the curvature fit and the singularity extraction all
///         quietly return nothing at all rather than failing. Measured: at a thousandth scale the
///         singularity extraction found <i>no</i> singularities on a sphere, which is not a number
///         that can be right for any surface.
///     </para>
///     <para>
///         The fix is the textbook one: divide by the largest component first, so the length is of
///         order one whatever the model is, and only an exactly-zero vector is refused. It costs three
///         comparisons and a divide.
///     </para>
/// </remarks>
static class ScaleSafe {
    /// <summary>A unit vector, or zero only when every component is exactly zero.</summary>
    /// <param name="value">The direction.</param>
    /// <returns>The unit direction.</returns>
    public static Vector3 Unit(Vector3 value) {
        var largest = MathF.Max(MathF.Max(MathF.Abs(value.X), MathF.Abs(value.Y)), MathF.Abs(value.Z));

        if (largest <= 0f || !float.IsFinite(largest)) {
            return Vector3.Zero;
        }

        var scaled = value / largest;
        var length = MathF.Sqrt(scaled.LengthSquared());

        return length > 0f ? scaled / length : Vector3.Zero;
    }

    /// <summary>A triangle's unit normal, from edges normalised before the cross rather than after.</summary>
    /// <param name="mesh">The surface.</param>
    /// <param name="triangle">Its index.</param>
    /// <returns>The normal, or zero for a triangle with no area at all.</returns>
    /// <remarks>
    ///     ⚠ Two normalisations rather than one, and the first is what makes the guard a statement
    ///     about the <i>angle</i> between the edges instead of about the model's size.
    /// </remarks>
    public static Vector3 Normal(ManifoldMesh mesh, int triangle) {
        ArgumentNullException.ThrowIfNull(mesh);

        var corners = mesh.Corners(triangle);
        var one = Unit(mesh.Positions[corners[1]] - mesh.Positions[corners[0]]);
        var two = Unit(mesh.Positions[corners[2]] - mesh.Positions[corners[0]]);

        return one.LengthSquared() <= 0f || two.LengthSquared() <= 0f
            ? Vector3.Zero
            : Unit(Vector3.Cross(one, two));
    }

    /// <summary>A direction projected into a plane and normalised, scale-free throughout.</summary>
    /// <param name="along">The direction.</param>
    /// <param name="normal">The plane's normal.</param>
    /// <returns>The unit direction in the plane, or zero where there is none.</returns>
    public static Vector3 Flatten(Vector3 along, Vector3 normal) {
        var direction = Unit(along);

        if (direction.LengthSquared() <= 0f || normal.LengthSquared() <= 0f) {
            return Vector3.Zero;
        }

        return Unit(direction - (normal * Vector3.Dot(direction, normal)));
    }
}
