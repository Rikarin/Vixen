// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A rotation, stored as a unit quaternion. Four floats instead of a matrix's nine, no gimbal
///     lock, and it interpolates — which is why every transform, every animation channel and every
///     bone pose is one of these rather than Euler angles.
/// </summary>
/// <remarks>
///     <para>
///         <b>Composition reads left to right</b>, matching the matrices:
///         <c>a * b</c> is "rotate by <c>a</c>, then by <c>b</c>", and
///         <c>Matrix4x4.FromQuaternion(a * b)</c> equals
///         <c>Matrix4x4.FromQuaternion(a) * Matrix4x4.FromQuaternion(b)</c>. Underneath, that is the
///         Hamilton product with its arguments swapped; the swap lives here, once, so that no caller
///         has to think about it.
///     </para>
///     <para>
///         Positive angles are counter-clockwise looking down the axis toward the origin. See
///         <c>Conventions.md</c>.
///     </para>
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Quaternion : IEquatable<Quaternion>, IFormattable, ISpanFormattable {
    /// <summary>Number of components.</summary>
    public const int ComponentCount = 4;

    /// <summary>The X component of the vector part.</summary>
    public readonly float X;

    /// <summary>The Y component of the vector part.</summary>
    public readonly float Y;

    /// <summary>The Z component of the vector part.</summary>
    public readonly float Z;

    /// <summary>The scalar part.</summary>
    public readonly float W;

    /// <summary>The rotation that does nothing.</summary>
    public static Quaternion Identity => new(0f, 0f, 0f, 1f);

    /// <summary>Builds a quaternion from its components. Rarely what a caller wants directly.</summary>
    /// <param name="x">The X component of the vector part.</param>
    /// <param name="y">The Y component of the vector part.</param>
    /// <param name="z">The Z component of the vector part.</param>
    /// <param name="w">The scalar part.</param>
    public Quaternion(float x, float y, float z, float w) {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    /// <summary>Builds a quaternion from a vector part and a scalar part.</summary>
    /// <param name="vector">The vector part.</param>
    /// <param name="scalar">The scalar part.</param>
    public Quaternion(Vector3 vector, float scalar) {
        X = vector.X;
        Y = vector.Y;
        Z = vector.Z;
        W = scalar;
    }

    /// <summary>The vector part.</summary>
    public Vector3 Xyz => new(X, Y, Z);

    /// <summary>Whether this is the identity rotation, to within the default tolerance.</summary>
    public bool IsIdentity => NearEqual(this, Identity);

    /// <summary>The components as a span. Valid as long as the quaternion it came from.</summary>
    /// <returns>A span of <see cref="ComponentCount" /> floats.</returns>
    [UnscopedRef]
    public ReadOnlySpan<float> AsSpan() => MemoryMarshal.CreateReadOnlySpan(in X, ComponentCount);

    /// <summary>The length. Unit for any rotation; drifts from 1 as rotations accumulate.</summary>
    /// <returns>The length.</returns>
    public float Length() => MathF.Sqrt(LengthSquared());

    /// <summary>The squared length.</summary>
    /// <returns>The squared length.</returns>
    public float LengthSquared() => (X * X) + (Y * Y) + (Z * Z) + (W * W);

    /// <summary>The angle of the rotation, in radians, in <c>[0, π]</c>.</summary>
    /// <returns>The angle.</returns>
    public float Angle() {
        var normalized = Normalize(this);
        return 2f * MathF.Acos(MathUtil.Clamp(MathF.Abs(normalized.W), -1f, 1f));
    }

    /// <summary>The axis of the rotation, or <see cref="Vector3.UnitY" /> if there is no rotation.</summary>
    /// <returns>The unit-length axis.</returns>
    public Vector3 Axis() {
        var lengthSquared = (X * X) + (Y * Y) + (Z * Z);
        return lengthSquared < MathUtil.ZeroTolerance * MathUtil.ZeroTolerance
            ? Vector3.UnitY
            : Xyz * (1f / MathF.Sqrt(lengthSquared));
    }

    /// <summary>The rotation of <paramref name="angle" /> radians about <paramref name="axis" />.</summary>
    /// <param name="axis">The axis. Normalised internally.</param>
    /// <param name="angle">The angle in radians, counter-clockwise looking down the axis.</param>
    /// <returns>The rotation.</returns>
    public static Quaternion FromAxisAngle(Vector3 axis, float angle) {
        var unit = Vector3.Normalize(axis);
        if (unit.IsZero) {
            return Identity;
        }

        var half = angle * 0.5f;
        return new(unit * MathF.Sin(half), MathF.Cos(half));
    }

    /// <summary>
    ///     The rotation described by three Euler angles, applied yaw then pitch then roll.
    /// </summary>
    /// <param name="yaw">Rotation about Y, in radians.</param>
    /// <param name="pitch">Rotation about X, in radians.</param>
    /// <param name="roll">Rotation about Z, in radians.</param>
    /// <returns>The rotation.</returns>
    /// <remarks>
    ///     <para>
    ///         Euler angles exist here because designers think in them and because they serialise
    ///         readably. Nothing inside the engine stores them: the order is a convention, three
    ///         different orders give three different rotations from the same numbers, and the ambiguity
    ///         is exactly why the runtime representation is a quaternion.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The axes are the body's, not the world's, and the factor order below is the
    ///         opposite of the order the name reads in.</b> <see cref="Concatenate" /> composes left to
    ///         right — <c>a * b</c> applies <c>a</c> first — so writing the three factors in the
    ///         reading order yaw, pitch, roll would turn about three <em>fixed</em> axes: it would
    ///         pitch about the world's X rather than about the one the yaw just turned. That is the
    ///         same rotation only while the yaw is zero, which is why it survives every test that
    ///         varies one angle at a time. What it costs when both are non-zero is a camera that rolls
    ///         as it turns — the horizon tilts, and the view creeps off the thing it was aimed at.
    ///     </para>
    ///     <para>
    ///         So roll is applied first and yaw last, which composes the intrinsic Y-X-Z rotation that
    ///         "yaw, pitch, roll" means everywhere else — <c>System.Numerics</c>'s
    ///         <c>CreateFromYawPitchRoll</c> included. <c>PlayerLook.Forward</c> builds the same
    ///         direction out of sines and cosines by hand and the two now agree by construction.
    ///     </para>
    /// </remarks>
    public static Quaternion FromYawPitchRoll(float yaw, float pitch, float roll) =>
        FromAxisAngle(Vector3.UnitZ, roll)
        * FromAxisAngle(Vector3.UnitX, pitch)
        * FromAxisAngle(Vector3.UnitY, yaw);

    /// <summary>The shortest rotation taking one direction to another.</summary>
    /// <param name="from">The starting direction. Normalised internally.</param>
    /// <param name="to">The target direction. Normalised internally.</param>
    /// <returns>The rotation.</returns>
    public static Quaternion FromToRotation(Vector3 from, Vector3 to) {
        var start = Vector3.Normalize(from);
        var end = Vector3.Normalize(to);
        var dot = Vector3.Dot(start, end);

        if (dot >= 1f - MathUtil.ZeroTolerance) {
            return Identity;
        }

        // Antiparallel: the rotation is a half turn about *any* perpendicular axis, and there is no
        // shortest one. Pick a stable perpendicular rather than letting the cross product vanish.
        if (dot <= -1f + MathUtil.ZeroTolerance) {
            var axis = Vector3.Cross(Vector3.UnitX, start);
            if (axis.LengthSquared() < MathUtil.ZeroTolerance) {
                axis = Vector3.Cross(Vector3.UnitY, start);
            }

            return FromAxisAngle(axis, MathUtil.Pi);
        }

        var cross = Vector3.Cross(start, end);
        return Normalize(new(cross, 1f + dot));
    }

    /// <summary>The quaternion scaled to unit length; <see cref="Identity" /> if degenerate.</summary>
    /// <param name="value">The quaternion to normalise.</param>
    /// <returns>The unit quaternion.</returns>
    public static Quaternion Normalize(Quaternion value) {
        var lengthSquared = value.LengthSquared();
        if (lengthSquared < MathUtil.ZeroTolerance * MathUtil.ZeroTolerance) {
            return Identity;
        }

        var inverse = 1f / MathF.Sqrt(lengthSquared);
        return new(value.X * inverse, value.Y * inverse, value.Z * inverse, value.W * inverse);
    }

    /// <summary>Negates the vector part, which for a unit quaternion is the inverse rotation.</summary>
    /// <param name="value">The quaternion.</param>
    /// <returns>The conjugate.</returns>
    public static Quaternion Conjugate(Quaternion value) => new(-value.X, -value.Y, -value.Z, value.W);

    /// <summary>The rotation that undoes <paramref name="value" />.</summary>
    /// <param name="value">The quaternion. Need not be unit length.</param>
    /// <returns>The inverse rotation.</returns>
    public static Quaternion Inverse(Quaternion value) {
        var lengthSquared = value.LengthSquared();
        if (lengthSquared < MathUtil.ZeroTolerance * MathUtil.ZeroTolerance) {
            return Identity;
        }

        var inverse = 1f / lengthSquared;
        return new(-value.X * inverse, -value.Y * inverse, -value.Z * inverse, value.W * inverse);
    }

    /// <summary>The dot product, whose sign says whether two rotations take the same path.</summary>
    /// <param name="left">The first quaternion.</param>
    /// <param name="right">The second quaternion.</param>
    /// <returns>The scalar product.</returns>
    public static float Dot(Quaternion left, Quaternion right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z) + (left.W * right.W);

    /// <summary>
    ///     Composes two rotations: apply <paramref name="first" />, then <paramref name="second" />.
    /// </summary>
    /// <param name="first">The rotation applied first.</param>
    /// <param name="second">The rotation applied second.</param>
    /// <returns>The combined rotation.</returns>
    public static Quaternion Concatenate(Quaternion first, Quaternion second) {
        // The Hamilton product with the arguments swapped, because Hamilton composes right to left
        // and every other composition in this library reads left to right.
        var (ax, ay, az, aw) = (second.X, second.Y, second.Z, second.W);
        var (bx, by, bz, bw) = (first.X, first.Y, first.Z, first.W);

        return new(
            (aw * bx) + (ax * bw) + (ay * bz) - (az * by),
            (aw * by) - (ax * bz) + (ay * bw) + (az * bx),
            (aw * bz) + (ax * by) - (ay * bx) + (az * bw),
            (aw * bw) - (ax * bx) - (ay * by) - (az * bz)
        );
    }

    /// <summary>Rotates a vector.</summary>
    /// <param name="value">The vector to rotate.</param>
    /// <param name="rotation">The rotation. Expected to be unit length.</param>
    /// <returns>The rotated vector.</returns>
    public static Vector3 Transform(Vector3 value, Quaternion rotation) {
        // v + 2w(q × v) + 2(q × (q × v)) — the same result as q·v·q* for a unit quaternion, at
        // roughly half the multiplies and with no intermediate quaternion.
        var axis = rotation.Xyz;
        var scaled = Vector3.Cross(axis, value) * 2f;
        return value + (scaled * rotation.W) + Vector3.Cross(axis, scaled);
    }

    /// <summary>
    ///     Interpolates along the shortest arc at constant angular velocity. The correct blend for
    ///     two poses; <see cref="Nlerp" /> is the cheaper approximation.
    /// </summary>
    /// <param name="from">The rotation at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The rotation at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant, clamped to <c>[0, 1]</c>.</param>
    /// <returns>The interpolated rotation.</returns>
    public static Quaternion Slerp(Quaternion from, Quaternion to, float amount) {
        var t = MathUtil.Saturate(amount);
        var dot = Dot(from, to);

        // q and -q are the same rotation but opposite ends of the arc; flipping takes the short way.
        var target = to;
        if (dot < 0f) {
            dot = -dot;
            target = new(-to.X, -to.Y, -to.Z, -to.W);
        }

        // Nearly parallel: sin(theta) underflows and the arc is indistinguishable from a chord.
        if (dot > 1f - 1e-4f) {
            return Normalize(Lerp(from, target, t));
        }

        var theta = MathF.Acos(dot);
        var sinTheta = MathF.Sin(theta);
        var fromScale = MathF.Sin((1f - t) * theta) / sinTheta;
        var toScale = MathF.Sin(t * theta) / sinTheta;

        return new(
            (from.X * fromScale) + (target.X * toScale),
            (from.Y * fromScale) + (target.Y * toScale),
            (from.Z * fromScale) + (target.Z * toScale),
            (from.W * fromScale) + (target.W * toScale)
        );
    }

    /// <summary>
    ///     Interpolates linearly and renormalises. Cheaper than <see cref="Slerp" /> and visually
    ///     identical for the small steps an animation blend actually takes; the angular velocity is
    ///     not constant, which only shows up across a wide arc.
    /// </summary>
    /// <param name="from">The rotation at <paramref name="amount" /> = 0.</param>
    /// <param name="to">The rotation at <paramref name="amount" /> = 1.</param>
    /// <param name="amount">The interpolant, clamped to <c>[0, 1]</c>.</param>
    /// <returns>The interpolated rotation.</returns>
    public static Quaternion Nlerp(Quaternion from, Quaternion to, float amount) {
        var t = MathUtil.Saturate(amount);
        var target = Dot(from, to) < 0f ? new Quaternion(-to.X, -to.Y, -to.Z, -to.W) : to;
        return Normalize(Lerp(from, target, t));
    }

    /// <summary>Whether two rotations agree to within a tolerance, component by component.</summary>
    /// <param name="left">The first quaternion.</param>
    /// <param name="right">The second quaternion.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if every component is within tolerance.</returns>
    /// <remarks>
    ///     Component-wise, so <c>q</c> and <c>-q</c> compare unequal even though they are the same
    ///     rotation. Compare <c>Dot</c> against 1 if what matters is the rotation rather than the
    ///     representation.
    /// </remarks>
    public static bool NearEqual(Quaternion left, Quaternion right, float tolerance = MathUtil.ZeroTolerance) =>
        MathUtil.NearEqual(left.X, right.X, tolerance)
        && MathUtil.NearEqual(left.Y, right.Y, tolerance)
        && MathUtil.NearEqual(left.Z, right.Z, tolerance)
        && MathUtil.NearEqual(left.W, right.W, tolerance);

    /// <summary>Whether two quaternions describe the same rotation, sign included or not.</summary>
    /// <param name="left">The first quaternion.</param>
    /// <param name="right">The second quaternion.</param>
    /// <param name="tolerance">The tolerance on the dot product.</param>
    /// <returns><see langword="true" /> if they rotate identically.</returns>
    public static bool SameRotation(Quaternion left, Quaternion right, float tolerance = MathUtil.ZeroTolerance) =>
        MathF.Abs(Dot(Normalize(left), Normalize(right))) >= 1f - tolerance;

    /// <inheritdoc cref="Concatenate" />
    /// <param name="first">The rotation applied first.</param>
    /// <param name="second">The rotation applied second.</param>
    /// <returns>The combined rotation.</returns>
    public static Quaternion operator *(Quaternion first, Quaternion second) => Concatenate(first, second);

    /// <summary>Exact component-wise equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first quaternion.</param>
    /// <param name="right">The second quaternion.</param>
    /// <returns><see langword="true" /> if every component is equal.</returns>
    public static bool operator ==(Quaternion left, Quaternion right) =>
        left.X == right.X && left.Y == right.Y && left.Z == right.Z && left.W == right.W;

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first quaternion.</param>
    /// <param name="right">The second quaternion.</param>
    /// <returns><see langword="true" /> if any component differs.</returns>
    public static bool operator !=(Quaternion left, Quaternion right) => !(left == right);

    /// <summary>Converts to the BCL quaternion, which has the same layout and the same components.</summary>
    /// <param name="value">The quaternion to convert.</param>
    /// <returns>The equivalent <see cref="System.Numerics.Quaternion" />.</returns>
    public static implicit operator System.Numerics.Quaternion(Quaternion value) =>
        new(value.X, value.Y, value.Z, value.W);

    /// <summary>Converts from the BCL quaternion.</summary>
    /// <param name="value">The quaternion to convert.</param>
    /// <returns>The equivalent <see cref="Quaternion" />.</returns>
    public static implicit operator Quaternion(System.Numerics.Quaternion value) =>
        new(value.X, value.Y, value.Z, value.W);

    /// <summary>Splits the quaternion into its components.</summary>
    /// <param name="x">The X component.</param>
    /// <param name="y">The Y component.</param>
    /// <param name="z">The Z component.</param>
    /// <param name="w">The scalar part.</param>
    public void Deconstruct(out float x, out float y, out float z, out float w) {
        x = X;
        y = Y;
        z = Z;
        w = W;
    }

    /// <inheritdoc />
    public bool Equals(Quaternion other) => this == other;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Quaternion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        VectorFormat.ToString(format, formatProvider, AsSpan());

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) =>
        VectorFormat.TryFormat(destination, out charsWritten, format, provider, AsSpan());

    static Quaternion Lerp(Quaternion from, Quaternion to, float amount) =>
        new(
            from.X + ((to.X - from.X) * amount),
            from.Y + ((to.Y - from.Y) * amount),
            from.Z + ((to.Z - from.Z) * amount),
            from.W + ((to.W - from.W) * amount)
        );
}
