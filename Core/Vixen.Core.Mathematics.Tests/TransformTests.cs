// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     The matrix and quaternion cases that generated inputs will not reach: degenerate transforms,
///     mirroring, antiparallel rotations, and the boundaries where a formula switches branches.
/// </summary>
public class TransformTests {
    const float Tolerance = 1e-4f;

    [Fact]
    public void A_mirrored_transform_decomposes_with_a_negative_X_scale() {
        // Only the product of the three scales is recoverable, so by convention the sign lands on
        // X. This is why a mirrored model shows up in the inspector as scale.X = -1 rather than as
        // some rotation nobody can undo.
        var mirrored = Matrix4x4.FromScale(new(-1f, 1f, 1f));

        Assert.True(Matrix4x4.Decompose(mirrored, out var scale, out var rotation, out var translation));
        Assert.Equal(-1f, scale.X, 4);
        Assert.Equal(Vector3.Zero, translation);
        Assert.True(Quaternion.SameRotation(Quaternion.Identity, rotation, Tolerance));
        Assert.True(Matrix4x4.NearEqual(mirrored, Matrix4x4.Compose(scale, rotation, translation), Tolerance));
    }

    [Fact]
    public void A_flattened_transform_reports_that_it_has_no_rotation() {
        // An object scaled to nothing on one axis has no recoverable orientation. Saying so beats
        // returning a normalised NaN.
        var flattened = Matrix4x4.FromScale(new(1f, 0f, 1f));

        Assert.False(Matrix4x4.Decompose(flattened, out var scale, out var rotation, out _));
        Assert.Equal(0f, scale.Y);
        Assert.Equal(Quaternion.Identity, rotation);
    }

    [Fact]
    public void A_singular_matrix_reports_that_it_cannot_be_inverted() {
        var singular = Matrix4x4.FromScale(new(1f, 0f, 1f));

        Assert.False(Matrix4x4.Invert(singular, out var result));
        Assert.Equal(Matrix4x4.Identity, result);

        Assert.False(Matrix3x3.Invert(Matrix3x3.FromScale(new(0f, 1f, 1f)), out var result3));
        Assert.Equal(Matrix3x3.Identity, result3);
    }

    [Fact]
    public void The_normal_matrix_differs_from_the_model_matrix_under_non_uniform_scale() {
        var squashed = Matrix4x4.FromScale(new(2f, 0.5f, 1f));
        var normalMatrix = Matrix3x3.Normal(squashed);

        // The inverse transpose of diag(2, 0.5, 1) is diag(0.5, 2, 1) — the reciprocal, which is
        // exactly the correction a squashed surface needs.
        Assert.Equal(0.5f, normalMatrix.M11, 4);
        Assert.Equal(2f, normalMatrix.M22, 4);
        Assert.Equal(1f, normalMatrix.M33, 4);

        // A uniform scale needs no correction beyond its own reciprocal, so the two agree in
        // direction — which is why the bug hides until someone scales an axis on its own.
        var uniform = Matrix3x3.Normal(Matrix4x4.FromUniformScale(2f));
        Assert.Equal(0.5f, uniform.M11, 4);
        Assert.Equal(uniform.M11, uniform.M22, 4);
    }

    [Fact]
    public void The_rotation_between_two_directions_takes_one_to_the_other() {
        var rotation = Quaternion.FromToRotation(Vector3.UnitX, Vector3.UnitY);
        Assert.True(Vector3.NearEqual(Vector3.UnitY, Quaternion.Transform(Vector3.UnitX, rotation), Tolerance));

        // Already aligned: nothing to do.
        Assert.True(Quaternion.SameRotation(Quaternion.Identity, Quaternion.FromToRotation(Vector3.UnitX, Vector3.UnitX)));
    }

    [Fact]
    public void Antiparallel_directions_still_produce_a_usable_half_turn() {
        // The degenerate case: the cross product vanishes, every perpendicular axis is equally
        // valid, and a naive implementation returns a zero quaternion here.
        foreach (var direction in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(Vector3.One) }) {
            var rotation = Quaternion.FromToRotation(direction, -direction);
            var rotated = Quaternion.Transform(direction, rotation);

            Assert.True(Vector3.NearEqual(-direction, rotated, 1e-3f));
        }
    }

    [Fact]
    public void Angle_and_axis_come_back_out_of_a_rotation() {
        var rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo);

        Assert.Equal(MathUtil.PiOverTwo, rotation.Angle(), 4);
        Assert.True(Vector3.NearEqual(Vector3.UnitY, rotation.Axis(), Tolerance));

        // No rotation has no meaningful axis; a stable answer beats a NaN.
        Assert.Equal(0f, Quaternion.Identity.Angle(), 4);
        Assert.Equal(Vector3.UnitY, Quaternion.Identity.Axis());
    }

    [Fact]
    public void Slerp_takes_the_short_way_round_even_when_the_signs_disagree() {
        var from = Quaternion.FromAxisAngle(Vector3.UnitZ, 0f);
        var to = Quaternion.FromAxisAngle(Vector3.UnitZ, MathUtil.DegreesToRadians(350f));

        // 350° the long way is 350° of travel; the short way is 10° backwards. Halfway along the
        // short arc is -5°, not 175°.
        var halfway = Quaternion.Slerp(from, to, 0.5f);
        var rotated = Quaternion.Transform(Vector3.UnitX, halfway);
        var angle = MathF.Atan2(rotated.Y, rotated.X);

        Assert.Equal(-5f, MathUtil.RadiansToDegrees(angle), 2);
    }

    [Fact]
    public void Slerp_survives_two_nearly_identical_rotations() {
        // sin(theta) underflows here, and the unguarded formula divides by it.
        var from = Quaternion.FromAxisAngle(Vector3.UnitY, 1f);
        var to = Quaternion.FromAxisAngle(Vector3.UnitY, 1f + 1e-7f);
        var result = Quaternion.Slerp(from, to, 0.5f);

        Assert.False(float.IsNaN(result.X + result.Y + result.Z + result.W));
        Assert.Equal(1f, result.Length(), 4);
    }

    [Fact]
    public void Euler_angles_apply_yaw_then_pitch_then_roll() {
        const float yaw = 0.4f;
        const float pitch = -0.9f;
        const float roll = 1.2f;

        var expected = Quaternion.FromAxisAngle(Vector3.UnitY, yaw)
            * Quaternion.FromAxisAngle(Vector3.UnitX, pitch)
            * Quaternion.FromAxisAngle(Vector3.UnitZ, roll);

        Assert.True(Quaternion.SameRotation(expected, Quaternion.FromYawPitchRoll(yaw, pitch, roll), Tolerance));
    }

    [Fact]
    public void The_matrix_indexer_is_one_based_and_checked() {
        var matrix = new Matrix4x4(
            1f, 2f, 3f, 4f,
            5f, 6f, 7f, 8f,
            9f, 10f, 11f, 12f,
            13f, 14f, 15f, 16f
        );

        Assert.Equal(1f, matrix[1, 1]);
        Assert.Equal(7f, matrix[2, 3]);
        Assert.Equal(16f, matrix[4, 4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => matrix[0, 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => matrix[5, 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => matrix[1, 5]);
    }

    [Fact]
    public void The_transform_axes_read_off_the_rows() {
        var rotated = Matrix4x4.FromRotationY(MathUtil.PiOverTwo);

        Assert.True(Vector3.NearEqual(new(0f, 0f, -1f), rotated.Right, Tolerance));
        Assert.True(Vector3.NearEqual(Vector3.Up, rotated.Up, Tolerance));

        // Forward is the negated third row, because right-handed means forward is -Z.
        Assert.True(Vector3.NearEqual(new(-1f, 0f, 0f), rotated.Forward, Tolerance));
    }

    [Fact]
    public void An_off_centre_orthographic_projection_maps_its_rectangle_to_the_unit_cube() {
        var projection = Matrix4x4.OrthographicOffCenter(0f, 100f, 0f, 50f, 0f, 10f);

        var bottomLeft = new Vector4(0f, 0f, 0f, 1f) * projection;
        var topRight = new Vector4(100f, 50f, 0f, 1f) * projection;

        Assert.Equal(-1f, bottomLeft.X, 4);
        Assert.Equal(-1f, bottomLeft.Y, 4);
        Assert.Equal(1f, topRight.X, 4);
        Assert.Equal(1f, topRight.Y, 4);
    }

    [Fact]
    public void The_projections_reject_impossible_arguments() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Matrix4x4.PerspectiveFieldOfView(MathUtil.PiOverTwo, 1f, 1f, 0.5f)
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Matrix4x4.PerspectiveFieldOfView(MathUtil.PiOverTwo, 1f, -1f, 100f)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => Matrix4x4.PerspectiveFieldOfView(MathUtil.Pi, 1f, 1f, 100f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Matrix4x4.Orthographic(0f, 10f, 0f, 1f));
    }

    [Fact]
    public void The_bulk_transform_refuses_a_destination_that_is_too_small() {
        var points = new Vector3[4];
        var destination = new Vector3[3];

        Assert.Throws<ArgumentException>(
            () => Matrix4x4.TransformPositions(points, Matrix4x4.Identity, destination)
        );
    }

    [Fact]
    public void The_bulk_transform_can_write_over_its_own_input() {
        var points = new[] { new Vector3(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f) };
        var translation = Matrix4x4.FromTranslation(new(10f, 20f, 30f));

        Matrix4x4.TransformPositions(points, translation, points);

        Assert.True(Vector3.NearEqual(new(11f, 20f, 30f), points[0], Tolerance));
        Assert.True(Vector3.NearEqual(new(10f, 21f, 30f), points[1], Tolerance));
        Assert.True(Vector3.NearEqual(new(10f, 20f, 31f), points[2], Tolerance));
    }

    [Fact]
    public void Transforming_a_position_applies_the_perspective_divide() {
        var projection = Matrix4x4.PerspectiveFieldOfView(MathUtil.PiOverTwo, 1f, 0.1f, 100f);

        // A point off to one side at twice the distance projects to half the offset.
        var near = Matrix4x4.TransformPosition(new(1f, 0f, -1f), projection);
        var far = Matrix4x4.TransformPosition(new(1f, 0f, -2f), projection);

        Assert.Equal(near.X / 2f, far.X, 4);
    }

    [Fact]
    public void Matrix3x3_composes_and_transforms_like_its_larger_sibling() {
        var rotation = Quaternion.FromAxisAngle(Vector3.UnitZ, MathUtil.PiOverTwo);
        var small = Matrix3x3.FromQuaternion(rotation);
        var large = Matrix4x4.FromQuaternion(rotation);

        Assert.True(
            Vector3.NearEqual(
                Matrix4x4.TransformDirection(Vector3.UnitX, large),
                Matrix3x3.Transform(Vector3.UnitX, small),
                Tolerance
            )
        );

        Assert.True(
            Matrix3x3.NearEqual(
                Matrix3x3.FromMatrix4x4(large * large),
                small * small,
                Tolerance
            )
        );
    }
}
