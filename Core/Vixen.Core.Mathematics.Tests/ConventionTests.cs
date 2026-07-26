// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Xunit;

namespace Vixen.Core.Mathematics.Tests;

/// <summary>
///     Every line of <c>Conventions.md</c>, asserted. These are the tests that make the document
///     true rather than aspirational: change a sign anywhere in the library and one of them fails
///     with a name that says which convention was broken.
/// </summary>
/// <remarks>
///     The shader half of the same conventions is pinned by Raven's own <c>ConventionTests</c>,
///     which reads them out of the compiled SPIR-V. Between the two, the CPU and the GPU cannot
///     drift apart silently.
/// </remarks>
public class ConventionTests {
    const float Tolerance = 1e-5f;

    [Fact]
    public void The_coordinate_system_is_right_handed() {
        // The defining property: X cross Y is +Z, not -Z.
        Assert.True(Vector3.NearEqual(Vector3.UnitZ, Vector3.Cross(Vector3.UnitX, Vector3.UnitY)));
        Assert.True(Vector3.NearEqual(Vector3.UnitX, Vector3.Cross(Vector3.UnitY, Vector3.UnitZ)));
        Assert.True(Vector3.NearEqual(Vector3.UnitY, Vector3.Cross(Vector3.UnitZ, Vector3.UnitX)));
    }

    [Fact]
    public void Up_is_positive_Y_and_forward_is_negative_Z() {
        Assert.Equal(new(0f, 1f, 0f), Vector3.Up);
        Assert.Equal(new(0f, 0f, -1f), Vector3.Forward);
        Assert.Equal(new(1f, 0f, 0f), Vector3.Right);

        // Forward, up and right form a right-handed frame in that order.
        Assert.True(Vector3.NearEqual(Vector3.Right, Vector3.Cross(Vector3.Up, -Vector3.Forward)));
    }

    [Fact]
    public void Storage_is_row_major_so_a_row_is_contiguous() {
        var matrix = new Matrix4x4(
            1f, 2f, 3f, 4f,
            5f, 6f, 7f, 8f,
            9f, 10f, 11f, 12f,
            13f, 14f, 15f, 16f
        );

        // Elements run M11 M12 M13 M14 M21 …, so the first four floats are the first row.
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, matrix.AsSpan()[..4].ToArray());
        Assert.Equal(new[] { 13f, 14f, 15f, 16f }, matrix.AsSpan()[12..].ToArray());
    }

    [Fact]
    public void The_translation_is_the_last_row_at_offset_48() {
        var matrix = Matrix4x4.FromTranslation(new(7f, 8f, 9f));

        Assert.Equal(7f, matrix.M41);
        Assert.Equal(8f, matrix.M42);
        Assert.Equal(9f, matrix.M43);

        // Twelve floats in, so the translation triple sits together at byte 48 — which is what lets
        // a shader read it as the fourth column of the transpose without any repacking.
        Assert.Equal(new[] { 7f, 8f, 9f }, matrix.AsSpan().Slice(12, 3).ToArray());
    }

    [Fact]
    public void A_point_is_transformed_as_v_times_M() {
        var translation = Matrix4x4.FromTranslation(new(1f, 2f, 3f));
        var point = new Vector4(10f, 20f, 30f, 1f);

        // Row vector on the left. The translation is picked up because W is 1.
        Assert.True(Vector4.NearEqual(new(11f, 22f, 33f, 1f), point * translation));

        // A direction has W = 0 and so is untouched by the translation.
        Assert.True(Vector4.NearEqual(new(10f, 20f, 30f, 0f), new Vector4(10f, 20f, 30f, 0f) * translation));
    }

    [Fact]
    public void Composition_reads_left_to_right() {
        var scale = Matrix4x4.FromUniformScale(2f);
        var translation = Matrix4x4.FromTranslation(new(10f, 0f, 0f));
        var point = new Vector4(1f, 0f, 0f, 1f);

        // Scale first, then translate: (1,0,0) doubles to (2,0,0), then moves to (12,0,0).
        Assert.True(Vector4.NearEqual(new(12f, 0f, 0f, 1f), point * (scale * translation)));

        // Translate first, then scale: (1,0,0) moves to (11,0,0), then doubles to (22,0,0).
        Assert.True(Vector4.NearEqual(new(22f, 0f, 0f, 1f), point * (translation * scale)));

        // Which is the associativity that makes "read left to right" meaningful.
        Assert.True(Vector4.NearEqual(point * (scale * translation), point * scale * translation));
    }

    [Fact]
    public void Positive_rotations_are_counter_clockwise_looking_down_the_axis() {
        var quarter = MathUtil.PiOverTwo;

        // About Z: X goes to Y.
        var aboutZ = new Vector4(1f, 0f, 0f, 1f) * Matrix4x4.FromRotationZ(quarter);
        Assert.True(Vector4.NearEqual(new(0f, 1f, 0f, 1f), aboutZ, Tolerance));

        // About X: Y goes to Z.
        var aboutX = new Vector4(0f, 1f, 0f, 1f) * Matrix4x4.FromRotationX(quarter);
        Assert.True(Vector4.NearEqual(new(0f, 0f, 1f, 1f), aboutX, Tolerance));

        // About Y: Z goes to X.
        var aboutY = new Vector4(0f, 0f, 1f, 1f) * Matrix4x4.FromRotationY(quarter);
        Assert.True(Vector4.NearEqual(new(1f, 0f, 0f, 1f), aboutY, Tolerance));
    }

    [Fact]
    public void Quaternions_rotate_the_same_way_the_matrices_do() {
        var quarter = MathUtil.PiOverTwo;

        foreach (var (axis, matrix) in new[] {
                     (Vector3.UnitX, Matrix4x4.FromRotationX(quarter)),
                     (Vector3.UnitY, Matrix4x4.FromRotationY(quarter)),
                     (Vector3.UnitZ, Matrix4x4.FromRotationZ(quarter))
                 }) {
            var quaternion = Quaternion.FromAxisAngle(axis, quarter);
            Assert.True(Matrix4x4.NearEqual(matrix, Matrix4x4.FromQuaternion(quaternion), Tolerance));
        }
    }

    [Fact]
    public void Quaternion_composition_reads_left_to_right_like_the_matrices() {
        var first = Quaternion.FromAxisAngle(Vector3.UnitY, 0.7f);
        var second = Quaternion.FromAxisAngle(Vector3.UnitX, -1.1f);

        // The one identity that ties the two representations together. If the Hamilton product were
        // not swapped inside Concatenate, this is the test that would fail.
        Assert.True(
            Matrix4x4.NearEqual(
                Matrix4x4.FromQuaternion(first * second),
                Matrix4x4.FromQuaternion(first) * Matrix4x4.FromQuaternion(second),
                Tolerance
            )
        );

        var point = new Vector3(1f, 2f, 3f);
        Assert.True(
            Vector3.NearEqual(
                Quaternion.Transform(point, first * second),
                Quaternion.Transform(Quaternion.Transform(point, first), second),
                Tolerance
            )
        );
    }

    [Fact]
    public void Rotating_a_vector_by_a_quaternion_matches_rotating_it_by_the_matrix() {
        var rotation = Quaternion.FromYawPitchRoll(0.3f, -0.8f, 1.4f);
        var matrix = Matrix4x4.FromQuaternion(rotation);
        var point = new Vector3(1f, -2f, 3f);

        Assert.True(
            Vector3.NearEqual(
                Quaternion.Transform(point, rotation),
                Matrix4x4.TransformDirection(point, matrix),
                Tolerance
            )
        );
    }

    [Fact]
    public void Perspective_depth_is_reverse_Z_over_zero_to_one() {
        const float near = 0.1f;
        const float far = 1000f;
        var projection = Matrix4x4.PerspectiveFieldOfView(MathUtil.PiOverTwo, 16f / 9f, near, far);

        // The camera looks down -Z, so the near plane is at z = -near.
        var atNear = new Vector4(0f, 0f, -near, 1f) * projection;
        var atFar = new Vector4(0f, 0f, -far, 1f) * projection;

        Assert.Equal(1f, atNear.Z / atNear.W, 4);
        Assert.Equal(0f, atFar.Z / atFar.W, 4);
    }

    [Fact]
    public void Infinite_perspective_keeps_the_near_plane_at_one_and_approaches_zero() {
        const float near = 0.1f;
        var projection = Matrix4x4.PerspectiveFieldOfViewInfinite(MathUtil.PiOverTwo, 16f / 9f, near);

        var atNear = new Vector4(0f, 0f, -near, 1f) * projection;
        var far = new Vector4(0f, 0f, -100000f, 1f) * projection;

        Assert.Equal(1f, atNear.Z / atNear.W, 4);
        Assert.Equal(0f, far.Z / far.W, 4);
    }

    [Fact]
    public void Orthographic_depth_is_reverse_Z_over_zero_to_one() {
        const float near = 1f;
        const float far = 100f;
        var projection = Matrix4x4.Orthographic(20f, 20f, near, far);

        var atNear = new Vector4(0f, 0f, -near, 1f) * projection;
        var atFar = new Vector4(0f, 0f, -far, 1f) * projection;

        Assert.Equal(1f, atNear.Z / atNear.W, 4);
        Assert.Equal(0f, atFar.Z / atFar.W, 4);
    }

    [Fact]
    public void A_look_at_matrix_puts_the_subject_down_negative_Z() {
        var view = Matrix4x4.LookAt(new(0f, 0f, 5f), Vector3.Zero, Vector3.Up);

        // The camera is five units away along +Z and looking back at the origin, so in its own
        // space the origin sits five units in front of it — which is -Z.
        var origin = new Vector4(0f, 0f, 0f, 1f) * view;
        Assert.True(Vector4.NearEqual(new(0f, 0f, -5f, 1f), origin, Tolerance));

        // The camera's own position is the origin of view space.
        var eye = new Vector4(0f, 0f, 5f, 1f) * view;
        Assert.True(Vector4.NearEqual(new(0f, 0f, 0f, 1f), eye, Tolerance));
    }

    [Fact]
    public void The_bcl_matrix_conversion_is_a_reinterpretation_and_not_a_transpose() {
        var vixen = Matrix4x4.Compose(new(1f, 2f, 3f), Quaternion.FromYawPitchRoll(0.4f, 0.5f, 0.6f), new(7f, 8f, 9f));
        System.Numerics.Matrix4x4 bcl = vixen;

        // Same sixty-four bytes in the same order. If either library ever changed its storage, the
        // implicit conversion would silently start producing transposed transforms.
        Assert.Equal(
            MemoryMarshal.AsBytes(vixen.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in bcl, 1)).ToArray()
        );

        // And the BCL agrees about where the translation lives.
        Assert.Equal(vixen.Translation.X, bcl.M41);
        Assert.Equal(vixen.Translation.Y, bcl.M42);
        Assert.Equal(vixen.Translation.Z, bcl.M43);
    }

    [Fact]
    public void Equality_is_exact_and_NearEqual_is_the_approximate_one() {
        var almost = new Vector3(1f, 1f, 1f);
        var nudged = new Vector3(1f + 5e-7f, 1f, 1f);

        // A few ulps apart: not equal, but near enough for any geometric purpose.
        Assert.False(almost == nudged);
        Assert.True(Vector3.NearEqual(almost, nudged));

        // NaN equals nothing, including an identical NaN.
        var nan = new Vector3(float.NaN, 0f, 0f);
        var alsoNan = new Vector3(float.NaN, 0f, 0f);
        Assert.False(nan == alsoNan);
        Assert.True(nan.IsNaN);

        // But the two hash the same, which is the direction the contract actually runs.
        Assert.Equal(nan.GetHashCode(), alsoNan.GetHashCode());

        // Negative zero equals zero and hashes the same.
        Assert.True(new Vector3(-0f, 0f, 0f) == Vector3.Zero);
        Assert.Equal(Vector3.Zero.GetHashCode(), new Vector3(-0f, -0f, -0f).GetHashCode());
    }
}
