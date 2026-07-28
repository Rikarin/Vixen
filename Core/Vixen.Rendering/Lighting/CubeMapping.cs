// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Lighting;

/// <summary>
///     Which direction a cube-map texel looks in, and which texel a direction lands on.
/// </summary>
/// <remarks>
///     <para>
///         Everything that bakes an environment needs this: a spherical-harmonic projection walks
///         every texel and asks where it points, and a prefilter pass walks the hemisphere and asks
///         which texel a sample lands on. Both have to agree with the convention the GPU uses, or an
///         environment comes out mirrored, rotated by ninety degrees, or subtly wrong at the seams —
///         none of which looks like a bug in a bake.
///     </para>
///     <para>
///         <strong>The convention is not invented here.</strong> <see cref="Direction" /> is derived
///         from <see cref="ShadowProjections.Cube" /> by unprojecting the face's own matrix, which is
///         the same matrix a point light renders its shadow cube with and which is already asserted to
///         tile the sphere with no gap and no overlap. Two conventions in one engine is how a probe
///         ends up disagreeing with a shadow, so there is one.
///     </para>
///     <para>
///         <see cref="Locate" /> is the inverse, and it is the major-axis rule rather than six matrix
///         multiplies — a prefilter takes millions of samples. That makes it a second implementation
///         of the same idea, which is exactly the shape that drifts, so a test holds it against the
///         unprojection over thousands of random directions.
///     </para>
/// </remarks>
public static class CubeMapping {
    /// <summary>The six faces, in layer order.</summary>
    public static readonly CubeFace[] Faces = [
        CubeFace.PositiveX, CubeFace.NegativeX,
        CubeFace.PositiveY, CubeFace.NegativeY,
        CubeFace.PositiveZ, CubeFace.NegativeZ
    ];

    /// <summary>The inverse of each face's view-projection, built once.</summary>
    /// <remarks>
    ///     A depth of one metre and a range of two, because the matrix is only ever used to turn a
    ///     clip-space point back into a direction: the near and far planes cancel in the normalise,
    ///     and any pair that brackets the sample depth would do.
    /// </remarks>
    static readonly Matrix4x4[] Unproject = BuildUnprojections();

    /// <summary>Where a point on a face looks, as a unit vector.</summary>
    /// <param name="face">Which face.</param>
    /// <param name="u">Horizontal position across the face, −1 to 1.</param>
    /// <param name="v">Vertical position down the face, −1 to 1.</param>
    public static Vector3 Direction(CubeFace face, float u, float v) {
        // Half way down the depth range, which is inside the frustum under either clip convention —
        // this has no business knowing whether the engine's depth runs zero-to-one or reversed.
        var point = Matrix4x4.TransformPosition(new(u, v, 0.5f), Unproject[(int)face]);
        return Vector3.Normalize(point);
    }

    /// <summary>Which face a direction lands on, and where on it.</summary>
    /// <remarks>
    ///     <para>
    ///         The major-axis rule: a direction belongs to the face its largest component points at,
    ///         and the other two components divided by that one are where on it. Undefined for a zero
    ///         vector, which is not a direction.
    ///     </para>
    ///     <para>
    ///         ⚠ The horizontal axis runs the opposite way from the one the D3D and GL cube-map
    ///         tables give, on every face. That is not a mistake here: the engine's convention comes
    ///         from <see cref="ShadowProjections.Cube" />'s look-at matrices, and it mirrors u. Every
    ///         sign in this function was wrong in exactly that way when it was written from the
    ///         published table, and the only thing that noticed was the test that holds this against
    ///         <see cref="Direction" /> — a mirrored environment is still an environment.
    ///     </para>
    /// </remarks>
    public static (CubeFace Face, float U, float V) Locate(Vector3 direction) {
        var absolute = new Vector3(MathF.Abs(direction.X), MathF.Abs(direction.Y), MathF.Abs(direction.Z));

        if (absolute.X >= absolute.Y && absolute.X >= absolute.Z) {
            var scale = 1f / absolute.X;

            return direction.X > 0f
                ? (CubeFace.PositiveX, direction.Z * scale, direction.Y * scale)
                : (CubeFace.NegativeX, -direction.Z * scale, direction.Y * scale);
        }

        if (absolute.Y >= absolute.Z) {
            var scale = 1f / absolute.Y;

            return direction.Y > 0f
                ? (CubeFace.PositiveY, direction.X * scale, direction.Z * scale)
                : (CubeFace.NegativeY, direction.X * scale, -direction.Z * scale);
        }

        var depth = 1f / absolute.Z;

        return direction.Z > 0f
            ? (CubeFace.PositiveZ, -direction.X * depth, direction.Y * depth)
            : (CubeFace.NegativeZ, direction.X * depth, direction.Y * depth);
    }

    /// <summary>
    ///     How much of the sphere one texel covers, in steradians.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Not constant across a face, and that is the whole reason this exists: a cube's corner
    ///         texels are further from the centre of projection and more oblique to it, so they cover
    ///         roughly a fifth of the solid angle a centre texel does. A projection that weighted
    ///         every texel equally would tilt the whole environment toward its corners.
    ///     </para>
    ///     <para>
    ///         The closed form is the standard one: the area of a spherical rectangle, evaluated as
    ///         the sum of four <c>atan2</c> terms over the texel's corners. Exact rather than the
    ///         <c>4 / ((1+u²+v²)^1.5)</c> approximation, because the two disagree by a percent at the
    ///         corners and the corners are exactly where a low-order projection is least forgiving.
    ///     </para>
    /// </remarks>
    /// <param name="u">The texel's left edge, −1 to 1.</param>
    /// <param name="v">The texel's top edge, −1 to 1.</param>
    /// <param name="size">The texel's width and height in the same units.</param>
    public static float SolidAngle(float u, float v, float size) =>
        Area(u + size, v + size) - Area(u, v + size) - Area(u + size, v) + Area(u, v);

    /// <summary>The solid angle of the rectangle from the face's origin out to (u, v).</summary>
    static float Area(float u, float v) => MathF.Atan2(u * v, MathF.Sqrt((u * u) + (v * v) + 1f));

    static Matrix4x4[] BuildUnprojections() {
        var inverses = new Matrix4x4[6];

        foreach (var face in Faces) {
            if (!Matrix4x4.Invert(ShadowProjections.Cube(Vector3.Zero, face, 2f, 1f), out var inverse)) {
                throw new InvalidOperationException(
                    $"The cube projection for {face} is singular, which it cannot be — a 90° "
                    + "perspective with a positive range always inverts."
                );
            }

            inverses[(int)face] = inverse;
        }

        return inverses;
    }
}
