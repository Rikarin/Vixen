// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     The algebraic laws, over generated inputs rather than the handful a person would think to
///     write down. These are the tests that catch a transposed index or a swapped sign in a branch
///     that only fires for one octant — the failure mode example-based tests are worst at.
/// </summary>
/// <remarks>
///     <para>
///         Inputs are kept to a moderate range and transforms are generated as scale-rotation-
///         translation compositions rather than sixteen arbitrary floats. That is not to make the
///         tests easier: an arbitrary 4×4 is usually near-singular, so inverting it measures
///         floating-point conditioning rather than whether the cofactors are right, and the engine
///         never produces one.
///     </para>
///     <para>
///         Tolerances are relative (see <see cref="MathUtil.NearEqual" />) and deliberately loose
///         enough to survive the reassociation the JIT is allowed to do. A property test that has to
///         be tightened until it passes is testing the tolerance.
///     </para>
/// </remarks>
public class AlgebraicPropertyTests {
    const float Tolerance = 1e-3f;

    static readonly Gen<float> Scalar = Gen.Float[-10f, 10f];
    static readonly Gen<float> Angle = Gen.Float[-MathUtil.Pi, MathUtil.Pi];

    static readonly Gen<Vector3> Vectors =
        Gen.Select(Scalar, Scalar, Scalar, (x, y, z) => new Vector3(x, y, z));

    static readonly Gen<Vector3> NonZeroVectors =
        Vectors.Where(static v => v.LengthSquared() > 0.01f);

    static readonly Gen<Quaternion> Rotations =
        Gen.Select(NonZeroVectors, Angle, static (axis, angle) => Quaternion.FromAxisAngle(axis, angle));

    static readonly Gen<Vector3> Scales =
        Gen.Select(Gen.Float[0.25f, 4f], Gen.Float[0.25f, 4f], Gen.Float[0.25f, 4f], (x, y, z) => new Vector3(x, y, z));

    static readonly Gen<Matrix4x4> Transforms =
        Gen.Select(Scales, Rotations, Vectors, static (scale, rotation, translation) =>
            Matrix4x4.Compose(scale, rotation, translation)
        );

    [Fact]
    public void Normalising_gives_unit_length() =>
        NonZeroVectors.Sample(v => Assert.True(MathUtil.NearEqual(1f, Vector3.Normalize(v).Length(), Tolerance)));

    [Fact]
    public void The_dot_product_commutes() =>
        Gen.Select(Vectors, Vectors).Sample(pair => {
                var (a, b) = pair;
                Assert.True(MathUtil.NearEqual(Vector3.Dot(a, b), Vector3.Dot(b, a), Tolerance));
            }
        );

    [Fact]
    public void The_cross_product_is_perpendicular_to_both_operands() =>
        Gen.Select(Vectors, Vectors).Sample(pair => {
                var (a, b) = pair;
                var cross = Vector3.Cross(a, b);

                // Scaled by the magnitudes, because the dot of three ten-unit vectors carries the
                // rounding of all three.
                var scale = MathF.Max(1f, cross.Length() * MathF.Max(a.Length(), b.Length()));
                Assert.True(MathF.Abs(Vector3.Dot(cross, a)) <= Tolerance * scale);
                Assert.True(MathF.Abs(Vector3.Dot(cross, b)) <= Tolerance * scale);
            }
        );

    [Fact]
    public void The_cross_product_anticommutes() =>
        Gen.Select(Vectors, Vectors).Sample(pair => {
                var (a, b) = pair;
                Assert.True(Vector3.NearEqual(Vector3.Cross(a, b), -Vector3.Cross(b, a), Tolerance));
            }
        );

    [Fact]
    public void Matrix_multiplication_is_associative() =>
        Gen.Select(Transforms, Transforms, Transforms).Sample(triple => {
                var (a, b, c) = triple;
                Assert.True(Matrix4x4.NearEqual((a * b) * c, a * (b * c), Tolerance));
            }
        );

    [Fact]
    public void Multiplying_by_the_identity_changes_nothing() =>
        Transforms.Sample(m => {
                Assert.True(Matrix4x4.NearEqual(m, m * Matrix4x4.Identity, Tolerance));
                Assert.True(Matrix4x4.NearEqual(m, Matrix4x4.Identity * m, Tolerance));
            }
        );

    [Fact]
    public void A_matrix_times_its_inverse_is_the_identity() =>
        Transforms.Sample(m => {
                Assert.True(Matrix4x4.Invert(m, out var inverse));
                Assert.True(Matrix4x4.NearEqual(Matrix4x4.Identity, m * inverse, Tolerance));
                Assert.True(Matrix4x4.NearEqual(Matrix4x4.Identity, inverse * m, Tolerance));
            }
        );

    [Fact]
    public void Transposing_twice_gives_the_original() =>
        Transforms.Sample(m => Assert.Equal(m, Matrix4x4.Transpose(Matrix4x4.Transpose(m))));

    [Fact]
    public void The_determinant_of_a_product_is_the_product_of_the_determinants() =>
        Gen.Select(Transforms, Transforms).Sample(pair => {
                var (a, b) = pair;
                var expected = Matrix4x4.Determinant(a) * Matrix4x4.Determinant(b);
                Assert.True(MathUtil.NearEqual(expected, Matrix4x4.Determinant(a * b), Tolerance));
            }
        );

    [Fact]
    public void Transforming_by_a_product_is_transforming_twice() =>
        Gen.Select(Transforms, Transforms, Vectors).Sample(triple => {
                var (a, b, v) = triple;
                var once = Matrix4x4.TransformPosition(v, a * b);
                var twice = Matrix4x4.TransformPosition(Matrix4x4.TransformPosition(v, a), b);
                Assert.True(Vector3.NearEqual(once, twice, Tolerance));
            }
        );

    [Fact]
    public void The_bulk_transform_agrees_with_the_scalar_one() =>
        Gen.Select(Transforms, Vectors.Array[1, 32]).Sample(pair => {
                var (matrix, points) = pair;
                var destination = new Vector3[points.Length];
                Matrix4x4.TransformPositions(points, matrix, destination);

                for (var i = 0; i < points.Length; i++) {
                    Assert.True(
                        Vector3.NearEqual(Matrix4x4.TransformPosition(points[i], matrix), destination[i], Tolerance)
                    );
                }
            }
        );

    [Fact]
    public void A_quaternion_round_trips_through_its_matrix() =>
        Rotations.Sample(q => {
                var recovered = Matrix4x4.ToQuaternion(Matrix4x4.FromQuaternion(q));

                // Up to sign: q and -q are the same rotation, and ToQuaternion picks whichever
                // branch is best conditioned, which does not always land on the same one.
                Assert.True(Quaternion.SameRotation(q, recovered, Tolerance));
            }
        );

    [Fact]
    public void A_quaternion_times_its_inverse_is_the_identity() =>
        Rotations.Sample(q => {
                Assert.True(Quaternion.SameRotation(Quaternion.Identity, q * Quaternion.Inverse(q), Tolerance));
                Assert.True(Quaternion.SameRotation(Quaternion.Identity, Quaternion.Inverse(q) * q, Tolerance));
            }
        );

    [Fact]
    public void Rotating_preserves_length() =>
        Gen.Select(Rotations, Vectors).Sample(pair => {
                var (q, v) = pair;
                Assert.True(MathUtil.NearEqual(v.Length(), Quaternion.Transform(v, q).Length(), Tolerance));
            }
        );

    [Fact]
    public void Quaternion_composition_matches_matrix_composition() =>
        Gen.Select(Rotations, Rotations).Sample(pair => {
                var (a, b) = pair;
                Assert.True(
                    Matrix4x4.NearEqual(
                        Matrix4x4.FromQuaternion(a * b),
                        Matrix4x4.FromQuaternion(a) * Matrix4x4.FromQuaternion(b),
                        Tolerance
                    )
                );
            }
        );

    [Fact]
    public void Interpolation_hits_both_endpoints_and_stays_unit_length() =>
        Gen.Select(Rotations, Rotations, Gen.Float[0f, 1f]).Sample(triple => {
                var (a, b, t) = triple;

                Assert.True(Quaternion.SameRotation(a, Quaternion.Slerp(a, b, 0f), Tolerance));
                Assert.True(Quaternion.SameRotation(b, Quaternion.Slerp(a, b, 1f), Tolerance));
                Assert.True(MathUtil.NearEqual(1f, Quaternion.Slerp(a, b, t).Length(), Tolerance));
                Assert.True(MathUtil.NearEqual(1f, Quaternion.Nlerp(a, b, t).Length(), Tolerance));
            }
        );

    [Fact]
    public void Compose_and_Decompose_are_inverses() =>
        Gen.Select(Scales, Rotations, Vectors).Sample(triple => {
                var (scale, rotation, translation) = triple;
                var matrix = Matrix4x4.Compose(scale, rotation, translation);

                Assert.True(Matrix4x4.Decompose(matrix, out var s, out var r, out var t));
                Assert.True(Vector3.NearEqual(scale, s, Tolerance));
                Assert.True(Vector3.NearEqual(translation, t, Tolerance));
                Assert.True(Quaternion.SameRotation(rotation, r, Tolerance));

                // And the recomposed transform is the one we started with, which is the property
                // that actually matters — the split itself is only a means to it.
                Assert.True(Matrix4x4.NearEqual(matrix, Matrix4x4.Compose(s, r, t), Tolerance));
            }
        );

    [Fact]
    public void The_normal_matrix_keeps_normals_perpendicular_under_non_uniform_scale() =>
        Gen.Select(Scales, Rotations, Vectors, Vectors).Sample(quad => {
                var (scale, rotation, translation, tangent) = quad;
                if (tangent.LengthSquared() < 0.01f) {
                    return;
                }

                // Build a normal genuinely perpendicular to the tangent, then check that the pair
                // is still perpendicular after the tangent goes through the model matrix and the
                // normal through the normal matrix. Transforming both by the model matrix is what
                // this is guarding against, and it fails this test as soon as the scale is uneven.
                var normal = Vector3.Cross(tangent, Vector3.UnitY);
                if (normal.LengthSquared() < 0.01f) {
                    normal = Vector3.Cross(tangent, Vector3.UnitX);
                }

                var model = Matrix4x4.Compose(scale, rotation, translation);
                var transformedTangent = Matrix4x4.TransformDirection(tangent, model);
                var transformedNormal = Matrix3x3.Transform(normal, Matrix3x3.Normal(model));

                var dot = Vector3.Dot(Vector3.Normalize(transformedTangent), Vector3.Normalize(transformedNormal));
                Assert.True(MathF.Abs(dot) < 1e-2f);
            }
        );
}
