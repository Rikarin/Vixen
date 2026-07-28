// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Core.Mathematics;
using Numerics = System.Numerics;

namespace Vixen.Physics.Interop;

/// <summary>
///     The only place Vixen's mathematics types and the ones the Jolt binding speaks are converted
///     into one another.
/// </summary>
/// <remarks>
///     <para>
///         <b>Vectors and quaternions are the same bytes.</b> <see cref="Vector3" /> is three
///         sequential floats and so is <see cref="Numerics.Vector3" />; <see cref="Quaternion" /> is
///         X, Y, Z, W and so is <see cref="Numerics.Quaternion" />. Both engines are right-handed,
///         Y-up, with counter-clockwise positive rotation — see
///         <c>Vixen.Core.Mathematics/Conventions.md</c> — so a unit quaternion naming a rotation is
///         literally the same four numbers in each. <see cref="Unsafe.BitCast{TFrom,TTo}" /> is
///         therefore not a reinterpretation that happens to work, it is the identity, and it costs
///         nothing.
///     </para>
///     <para>
///         <b>Matrices are not.</b> Jolt's <c>Mat44</c> is four columns of four floats with the
///         translation in the fourth column; Vixen stores rows with the translation in the fourth
///         <i>row</i>. Read as the other's layout, one is the transpose of the other — which is not a
///         subtle difference but is a silent one, because a transposed rotation is still a rotation
///         and only the direction is wrong. <see cref="ToJolt(in Matrix4x4)" /> transposes, its
///         inverse transposes back, and <c>JoltMathTests</c> pins both against a body whose transform
///         Jolt itself reports.
///     </para>
///     <para>
///         Nothing here is public. A caller of <see cref="PhysicsWorld" /> works in Vixen types
///         throughout and never sees a Jolt one; that is the point of the wrapper, and letting a
///         conversion escape would make it half a wrapper.
///     </para>
/// </remarks>
static class JoltMath {
    /// <summary>Reinterprets a Vixen vector as the binding's.</summary>
    /// <param name="value">The vector.</param>
    /// <returns>The same three floats.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Numerics.Vector3 ToJolt(in Vector3 value) => Unsafe.BitCast<Vector3, Numerics.Vector3>(value);

    /// <summary>Reinterprets the binding's vector as Vixen's.</summary>
    /// <param name="value">The vector.</param>
    /// <returns>The same three floats.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 ToVixen(in Numerics.Vector3 value) => Unsafe.BitCast<Numerics.Vector3, Vector3>(value);

    /// <summary>Reinterprets a Vixen quaternion as the binding's.</summary>
    /// <param name="value">The rotation.</param>
    /// <returns>The same four floats.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Numerics.Quaternion ToJolt(in Quaternion value) =>
        Unsafe.BitCast<Quaternion, Numerics.Quaternion>(value);

    /// <summary>Reinterprets the binding's quaternion as Vixen's.</summary>
    /// <param name="value">The rotation.</param>
    /// <returns>The same four floats.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion ToVixen(in Numerics.Quaternion value) =>
        Unsafe.BitCast<Numerics.Quaternion, Quaternion>(value);

    /// <summary>Transposes a Vixen matrix into Jolt's column-major layout.</summary>
    /// <param name="value">The row-major matrix.</param>
    /// <returns>The matrix Jolt will read as the same transform.</returns>
    public static Numerics.Matrix4x4 ToJolt(in Matrix4x4 value) =>
        new(
            value.M11, value.M21, value.M31, value.M41,
            value.M12, value.M22, value.M32, value.M42,
            value.M13, value.M23, value.M33, value.M43,
            value.M14, value.M24, value.M34, value.M44
        );

    /// <summary>Transposes a matrix Jolt produced into Vixen's row-major layout.</summary>
    /// <param name="value">The column-major matrix.</param>
    /// <returns>The same transform, stored Vixen's way.</returns>
    public static Matrix4x4 ToVixen(in Numerics.Matrix4x4 value) =>
        new(
            value.M11, value.M21, value.M31, value.M41,
            value.M12, value.M22, value.M32, value.M42,
            value.M13, value.M23, value.M33, value.M43,
            value.M14, value.M24, value.M34, value.M44
        );

    /// <summary>Builds the rigid transform Jolt's shape queries want, from a position and a rotation.</summary>
    /// <param name="position">Where.</param>
    /// <param name="rotation">Which way.</param>
    /// <returns>The transform in Jolt's layout.</returns>
    /// <remarks>
    ///     Composed directly rather than by going through <see cref="Matrix4x4.Compose" /> and
    ///     transposing, because the query path runs per cast and the intermediate is pure overhead.
    ///     The two agree, and <c>JoltMathTests</c> says so.
    /// </remarks>
    public static Numerics.Matrix4x4 ComposeRigid(in Vector3 position, in Quaternion rotation) {
        var matrix = Numerics.Matrix4x4.CreateFromQuaternion(ToJolt(rotation));

        // Numerics.CreateFromQuaternion is row-major like Vixen's, so the rotation basis needs the
        // same transpose the general conversion does; only the translation moves to column four.
        return new(
            matrix.M11, matrix.M21, matrix.M31, position.X,
            matrix.M12, matrix.M22, matrix.M32, position.Y,
            matrix.M13, matrix.M23, matrix.M33, position.Z,
            0f, 0f, 0f, 1f
        );
    }
}
