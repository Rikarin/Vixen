// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;
using Vixen.Physics.Interop;
using Xunit;
using Numerics = System.Numerics;

namespace Vixen.Physics.Tests;

/// <summary>
///     Pins the one place Vixen's mathematics and the Jolt binding's meet.
/// </summary>
/// <remarks>
///     A transposed rotation is still a rotation and a reinterpreted vector is still a vector, so
///     every mistake available here compiles, runs, and produces a world that is subtly wrong in a
///     direction nobody can name. These tests check the conversions against values Jolt itself
///     reports rather than against another copy of the same arithmetic.
/// </remarks>
public sealed class JoltMathTests {
    [Fact]
    public void AVectorAndAQuaternionSurviveTheRoundTripExactly() {
        var vector = new Vector3(1.5f, -2.25f, 3.125f);
        var rotation = Quaternion.FromAxisAngle(Vector3.Normalize(new(1f, 2f, 3f)), 0.7f);

        Assert.Equal(vector, JoltMath.ToVixen(JoltMath.ToJolt(vector)));
        Assert.Equal(rotation, JoltMath.ToVixen(JoltMath.ToJolt(rotation)));
    }

    [Fact]
    public void AVectorIsTheSameThreeFloatsInBothEngines() {
        var vector = new Vector3(1f, 2f, 3f);
        var converted = JoltMath.ToJolt(vector);

        Assert.Equal(1f, converted.X);
        Assert.Equal(2f, converted.Y);
        Assert.Equal(3f, converted.Z);
    }

    [Fact]
    public void AMatrixIsTransposedOnTheWayAcrossAndBack() {
        var original = Matrix4x4.Compose(Vector3.One, Quaternion.FromAxisAngle(Vector3.UnitY, 0.4f), new(1f, 2f, 3f));
        var jolt = JoltMath.ToJolt(original);

        // Jolt's translation is the fourth column; Vixen's is the fourth row. That is the whole
        // difference, and it is the one that would otherwise be found by a body appearing in the
        // wrong place with a rotation that looks almost right.
        Assert.Equal(1f, jolt.M14);
        Assert.Equal(2f, jolt.M24);
        Assert.Equal(3f, jolt.M34);

        Assert.Equal(original, JoltMath.ToVixen(jolt));
    }

    /// <summary>
    ///     <c>ComposeRigid</c> builds the transform the shape queries want directly rather than by
    ///     composing and transposing. This is the test that says the shortcut agrees with the long way.
    /// </summary>
    [Fact]
    public void ComposingARigidTransformAgreesWithComposingAndTransposing() {
        var position = new Vector3(-4f, 0.5f, 7f);
        var rotation = Quaternion.FromYawPitchRoll(0.3f, -0.2f, 1.1f);

        var direct = JoltMath.ComposeRigid(position, rotation);
        var indirect = JoltMath.ToJolt(Matrix4x4.Compose(Vector3.One, rotation, position));

        AssertClose(indirect, direct);
    }

    /// <summary>
    ///     And this is the test that says both of them agree with Jolt, which is the only authority
    ///     that matters: it asks a real body for its transform and converts it back.
    /// </summary>
    [Fact]
    public void ATransformReadBackFromABodyMatchesWhatItWasCreatedWith() {
        using var world = new PhysicsWorld();

        var position = new Vector3(1f, 2f, 3f);
        var rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathUtil.PiOverTwo);

        var body = world.CreateBody(
            BodyDescription.Static(world.Shapes.Box(0.5f), position) with { Rotation = rotation }
        );

        world.GetTransform(body, out var readPosition, out var readRotation);

        Assert.Equal(position.X, readPosition.X, 5);
        Assert.Equal(position.Y, readPosition.Y, 5);
        Assert.Equal(position.Z, readPosition.Z, 5);

        // A quarter turn about Y takes +X to −Z. If the conversion transposed the rotation it would
        // take +X to +Z instead, and nothing else in the round trip would notice.
        var turned = Matrix4x4.TransformDirection(
            Vector3.UnitX,
            Matrix4x4.Compose(Vector3.One, readRotation, Vector3.Zero)
        );

        Assert.Equal(0f, turned.X, 4);
        Assert.Equal(-1f, turned.Z, 4);
    }

    static void AssertClose(Numerics.Matrix4x4 expected, Numerics.Matrix4x4 actual) {
        Assert.Equal(expected.M11, actual.M11, 5);
        Assert.Equal(expected.M12, actual.M12, 5);
        Assert.Equal(expected.M13, actual.M13, 5);
        Assert.Equal(expected.M14, actual.M14, 5);
        Assert.Equal(expected.M21, actual.M21, 5);
        Assert.Equal(expected.M22, actual.M22, 5);
        Assert.Equal(expected.M23, actual.M23, 5);
        Assert.Equal(expected.M24, actual.M24, 5);
        Assert.Equal(expected.M31, actual.M31, 5);
        Assert.Equal(expected.M32, actual.M32, 5);
        Assert.Equal(expected.M33, actual.M33, 5);
        Assert.Equal(expected.M34, actual.M34, 5);
        Assert.Equal(expected.M41, actual.M41, 5);
        Assert.Equal(expected.M42, actual.M42, 5);
        Assert.Equal(expected.M43, actual.M43, 5);
        Assert.Equal(expected.M44, actual.M44, 5);
    }
}
