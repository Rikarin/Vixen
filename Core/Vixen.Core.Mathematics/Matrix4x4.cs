// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A 4×4 transform: **row-vector convention with row-major storage**, translation in
///     <c>M41..M43</c>, composing left to right. <c>world = local * parent</c>, and a point is
///     transformed as <c>v * M</c>.
/// </summary>
/// <remarks>
///     <para>
///         This is ADR-003's convention and it is not negotiable per-call-site: it is the same one
///         the shader side uses, and the derivation showing that a <c>ColMajor</c>-decorated shader
///         matrix and this row-major storage are the same sixty-four bytes is in
///         <c>docs/plan/07 § E</c>. Read <c>Conventions.md</c> before changing anything here.
///     </para>
///     <para>
///         Depth is reverse-Z over <c>[0, 1]</c>: the projections map the near plane to 1 and the far
///         plane to 0.
///     </para>
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Matrix4x4 : IEquatable<Matrix4x4>, IFormattable, ISpanFormattable {
    /// <summary>Number of components.</summary>
    public const int ComponentCount = 16;

    /// <summary>Row 1, column 1.</summary>
    public readonly float M11;

    /// <summary>Row 1, column 2.</summary>
    public readonly float M12;

    /// <summary>Row 1, column 3.</summary>
    public readonly float M13;

    /// <summary>Row 1, column 4.</summary>
    public readonly float M14;

    /// <summary>Row 2, column 1.</summary>
    public readonly float M21;

    /// <summary>Row 2, column 2.</summary>
    public readonly float M22;

    /// <summary>Row 2, column 3.</summary>
    public readonly float M23;

    /// <summary>Row 2, column 4.</summary>
    public readonly float M24;

    /// <summary>Row 3, column 1.</summary>
    public readonly float M31;

    /// <summary>Row 3, column 2.</summary>
    public readonly float M32;

    /// <summary>Row 3, column 3.</summary>
    public readonly float M33;

    /// <summary>Row 3, column 4.</summary>
    public readonly float M34;

    /// <summary>Row 4, column 1 — the X translation.</summary>
    public readonly float M41;

    /// <summary>Row 4, column 2 — the Y translation.</summary>
    public readonly float M42;

    /// <summary>Row 4, column 3 — the Z translation.</summary>
    public readonly float M43;

    /// <summary>Row 4, column 4.</summary>
    public readonly float M44;

    /// <summary>The transform that does nothing.</summary>
    public static Matrix4x4 Identity =>
        new(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f
        );

    /// <summary>Builds a matrix from its sixteen elements, in row-major order.</summary>
    /// <param name="m11">Row 1, column 1.</param>
    /// <param name="m12">Row 1, column 2.</param>
    /// <param name="m13">Row 1, column 3.</param>
    /// <param name="m14">Row 1, column 4.</param>
    /// <param name="m21">Row 2, column 1.</param>
    /// <param name="m22">Row 2, column 2.</param>
    /// <param name="m23">Row 2, column 3.</param>
    /// <param name="m24">Row 2, column 4.</param>
    /// <param name="m31">Row 3, column 1.</param>
    /// <param name="m32">Row 3, column 2.</param>
    /// <param name="m33">Row 3, column 3.</param>
    /// <param name="m34">Row 3, column 4.</param>
    /// <param name="m41">Row 4, column 1.</param>
    /// <param name="m42">Row 4, column 2.</param>
    /// <param name="m43">Row 4, column 3.</param>
    /// <param name="m44">Row 4, column 4.</param>
    public Matrix4x4(
        float m11, float m12, float m13, float m14,
        float m21, float m22, float m23, float m24,
        float m31, float m32, float m33, float m34,
        float m41, float m42, float m43, float m44
    ) {
        M11 = m11;
        M12 = m12;
        M13 = m13;
        M14 = m14;
        M21 = m21;
        M22 = m22;
        M23 = m23;
        M24 = m24;
        M31 = m31;
        M32 = m32;
        M33 = m33;
        M34 = m34;
        M41 = m41;
        M42 = m42;
        M43 = m43;
        M44 = m44;
    }

    /// <summary>Builds a matrix from its four rows.</summary>
    /// <param name="row1">The first row.</param>
    /// <param name="row2">The second row.</param>
    /// <param name="row3">The third row.</param>
    /// <param name="row4">The fourth row, whose XYZ is the translation.</param>
    public Matrix4x4(Vector4 row1, Vector4 row2, Vector4 row3, Vector4 row4)
        : this(
            row1.X, row1.Y, row1.Z, row1.W,
            row2.X, row2.Y, row2.Z, row2.W,
            row3.X, row3.Y, row3.Z, row3.W,
            row4.X, row4.Y, row4.Z, row4.W
        ) { }

    /// <summary>The first row.</summary>
    public Vector4 Row1 => new(M11, M12, M13, M14);

    /// <summary>The second row.</summary>
    public Vector4 Row2 => new(M21, M22, M23, M24);

    /// <summary>The third row.</summary>
    public Vector4 Row3 => new(M31, M32, M33, M34);

    /// <summary>The fourth row, whose XYZ is the translation.</summary>
    public Vector4 Row4 => new(M41, M42, M43, M44);

    /// <summary>The translation the matrix applies.</summary>
    public Vector3 Translation => new(M41, M42, M43);

    /// <summary>The X axis of the transformed frame — the first row's XYZ.</summary>
    public Vector3 Right => new(M11, M12, M13);

    /// <summary>The Y axis of the transformed frame — the second row's XYZ.</summary>
    public Vector3 Up => new(M21, M22, M23);

    /// <summary>
    ///     The forward direction of the transformed frame: the negated third row, because
    ///     right-handed means forward is −Z.
    /// </summary>
    public Vector3 Forward => new(-M31, -M32, -M33);

    /// <summary>The element at <paramref name="row" />, <paramref name="column" />, one-based.</summary>
    /// <param name="row">The row, 1 to 4.</param>
    /// <param name="column">The column, 1 to 4.</param>
    /// <returns>The element.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A subscript is outside 1 to 4.</exception>
    public float this[int row, int column] {
        get {
            ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(row, 4);
            ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(column, 4);
            return AsSpan()[((row - 1) * 4) + (column - 1)];
        }
    }

    /// <summary>The elements in row-major order. Valid as long as the matrix it came from.</summary>
    /// <returns>A span of <see cref="ComponentCount" /> floats.</returns>
    [UnscopedRef]
    public ReadOnlySpan<float> AsSpan() => MemoryMarshal.CreateReadOnlySpan(in M11, ComponentCount);

    /// <summary>Whether this is the identity, to within the default tolerance.</summary>
    public bool IsIdentity => NearEqual(this, Identity);

    /// <summary>Moves by <paramref name="translation" />.</summary>
    /// <param name="translation">The offset.</param>
    /// <returns>The transform.</returns>
    public static Matrix4x4 FromTranslation(Vector3 translation) =>
        new(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            translation.X, translation.Y, translation.Z, 1f
        );

    /// <summary>Scales each axis independently.</summary>
    /// <param name="scale">The per-axis scale.</param>
    /// <returns>The transform.</returns>
    public static Matrix4x4 FromScale(Vector3 scale) =>
        new(
            scale.X, 0f, 0f, 0f,
            0f, scale.Y, 0f, 0f,
            0f, 0f, scale.Z, 0f,
            0f, 0f, 0f, 1f
        );

    /// <summary>Scales every axis equally.</summary>
    /// <param name="scale">The uniform scale.</param>
    /// <returns>The transform.</returns>
    /// <remarks>
    ///     Named rather than overloading <see cref="FromScale(Vector3)" />, because a target-typed
    ///     <c>new(…)</c> carries no type for overload resolution to work with: with both overloads
    ///     present, <c>FromScale(new(1f, 2f, 3f))</c> is a compile error at every call site.
    /// </remarks>
    public static Matrix4x4 FromUniformScale(float scale) => FromScale(new Vector3(scale));

    /// <summary>Rotates about the X axis, counter-clockwise looking down +X toward the origin.</summary>
    /// <param name="radians">The angle in radians.</param>
    /// <returns>The transform.</returns>
    public static Matrix4x4 FromRotationX(float radians) {
        var (sin, cos) = MathF.SinCos(radians);
        return new(
            1f, 0f, 0f, 0f,
            0f, cos, sin, 0f,
            0f, -sin, cos, 0f,
            0f, 0f, 0f, 1f
        );
    }

    /// <summary>Rotates about the Y axis, counter-clockwise looking down +Y toward the origin.</summary>
    /// <param name="radians">The angle in radians.</param>
    /// <returns>The transform.</returns>
    public static Matrix4x4 FromRotationY(float radians) {
        var (sin, cos) = MathF.SinCos(radians);
        return new(
            cos, 0f, -sin, 0f,
            0f, 1f, 0f, 0f,
            sin, 0f, cos, 0f,
            0f, 0f, 0f, 1f
        );
    }

    /// <summary>Rotates about the Z axis, counter-clockwise looking down +Z toward the origin.</summary>
    /// <param name="radians">The angle in radians.</param>
    /// <returns>The transform.</returns>
    public static Matrix4x4 FromRotationZ(float radians) {
        var (sin, cos) = MathF.SinCos(radians);
        return new(
            cos, sin, 0f, 0f,
            -sin, cos, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f
        );
    }

    /// <summary>The rotation a quaternion describes, as a matrix.</summary>
    /// <param name="rotation">The rotation. Expected to be unit length.</param>
    /// <returns>The transform.</returns>
    public static Matrix4x4 FromQuaternion(Quaternion rotation) {
        var (x, y, z, w) = rotation;
        var (xx, yy, zz) = (x * x, y * y, z * z);
        var (xy, xz, yz) = (x * y, x * z, y * z);
        var (wx, wy, wz) = (w * x, w * y, w * z);

        return new(
            1f - (2f * (yy + zz)), 2f * (xy + wz), 2f * (xz - wy), 0f,
            2f * (xy - wz), 1f - (2f * (xx + zz)), 2f * (yz + wx), 0f,
            2f * (xz + wy), 2f * (yz - wx), 1f - (2f * (xx + yy)), 0f,
            0f, 0f, 0f, 1f
        );
    }

    /// <summary>
    ///     The single transform that scales, then rotates, then translates — the order every scene
    ///     graph wants and the one it is easy to get backwards by composing by hand.
    /// </summary>
    /// <param name="scale">The per-axis scale.</param>
    /// <param name="rotation">The rotation.</param>
    /// <param name="translation">The offset.</param>
    /// <returns>The transform.</returns>
    public static Matrix4x4 Compose(Vector3 scale, Quaternion rotation, Vector3 translation) {
        var matrix = FromQuaternion(rotation);
        return new(
            matrix.M11 * scale.X, matrix.M12 * scale.X, matrix.M13 * scale.X, 0f,
            matrix.M21 * scale.Y, matrix.M22 * scale.Y, matrix.M23 * scale.Y, 0f,
            matrix.M31 * scale.Z, matrix.M32 * scale.Z, matrix.M33 * scale.Z, 0f,
            translation.X, translation.Y, translation.Z, 1f
        );
    }

    /// <summary>
    ///     Splits an affine transform back into scale, rotation and translation.
    /// </summary>
    /// <param name="matrix">The transform to split.</param>
    /// <param name="scale">The per-axis scale.</param>
    /// <param name="rotation">The rotation.</param>
    /// <param name="translation">The offset.</param>
    /// <returns>
    ///     <see langword="false" /> if the matrix has no usable rotation — a zero or near-zero axis,
    ///     which no choice of outputs can represent. The outputs are still filled in with the best
    ///     available answer.
    /// </returns>
    /// <remarks>
    ///     Shear cannot be recovered: a sheared matrix decomposes to *some* scale and rotation whose
    ///     product is not the original. Nothing in the engine produces shear, and the alternative is
    ///     a polar decomposition nobody would use.
    /// </remarks>
    public static bool Decompose(
        in Matrix4x4 matrix,
        out Vector3 scale,
        out Quaternion rotation,
        out Vector3 translation
    ) {
        translation = matrix.Translation;

        var x = new Vector3(matrix.M11, matrix.M12, matrix.M13);
        var y = new Vector3(matrix.M21, matrix.M22, matrix.M23);
        var z = new Vector3(matrix.M31, matrix.M32, matrix.M33);

        var scaleX = x.Length();
        var scaleY = y.Length();
        var scaleZ = z.Length();

        // A negative determinant means an odd number of axes are mirrored. Only the product is
        // recoverable, so by convention the sign goes on X — the same choice every engine makes,
        // and the reason a mirrored model shows up as scale.X = -1 rather than as a rotation.
        if (Determinant(matrix) < 0f) {
            scaleX = -scaleX;
        }

        scale = new(scaleX, scaleY, scaleZ);

        if (MathUtil.IsZero(scaleX) || MathUtil.IsZero(scaleY) || MathUtil.IsZero(scaleZ)) {
            rotation = Quaternion.Identity;
            return false;
        }

        var basis = new Matrix4x4(
            new(x / scaleX, 0f),
            new(y / scaleY, 0f),
            new(z / scaleZ, 0f),
            Vector4.UnitW
        );

        rotation = ToQuaternion(basis);
        return true;
    }

    /// <summary>The rotation part of a matrix, as a quaternion. Assumes no scale.</summary>
    /// <param name="matrix">The matrix. Its upper-left 3×3 must be orthonormal.</param>
    /// <returns>The rotation.</returns>
    public static Quaternion ToQuaternion(in Matrix4x4 matrix) {
        var trace = matrix.M11 + matrix.M22 + matrix.M33;

        // Four branches rather than one because the shared denominator vanishes whenever the
        // rotation approaches a half turn about the corresponding axis; each branch picks the
        // component that is largest there, so the division is always the well-conditioned one.
        if (trace > 0f) {
            var s = MathF.Sqrt(trace + 1f) * 2f;
            return new(
                (matrix.M23 - matrix.M32) / s,
                (matrix.M31 - matrix.M13) / s,
                (matrix.M12 - matrix.M21) / s,
                0.25f * s
            );
        }

        if (matrix.M11 > matrix.M22 && matrix.M11 > matrix.M33) {
            var s = MathF.Sqrt(1f + matrix.M11 - matrix.M22 - matrix.M33) * 2f;
            return new(
                0.25f * s,
                (matrix.M12 + matrix.M21) / s,
                (matrix.M31 + matrix.M13) / s,
                (matrix.M23 - matrix.M32) / s
            );
        }

        if (matrix.M22 > matrix.M33) {
            var s = MathF.Sqrt(1f + matrix.M22 - matrix.M11 - matrix.M33) * 2f;
            return new(
                (matrix.M12 + matrix.M21) / s,
                0.25f * s,
                (matrix.M23 + matrix.M32) / s,
                (matrix.M31 - matrix.M13) / s
            );
        }

        var t = MathF.Sqrt(1f + matrix.M33 - matrix.M11 - matrix.M22) * 2f;
        return new(
            (matrix.M31 + matrix.M13) / t,
            (matrix.M23 + matrix.M32) / t,
            0.25f * t,
            (matrix.M12 - matrix.M21) / t
        );
    }

    /// <summary>
    ///     A right-handed view matrix: the transform from world space into the camera's space.
    /// </summary>
    /// <param name="eye">The camera's position in world space.</param>
    /// <param name="target">The point it looks at.</param>
    /// <param name="up">The approximate up direction; need not be perpendicular to the view.</param>
    /// <returns>The view transform.</returns>
    public static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up) {
        // Right-handed: the camera looks down -Z, so its +Z axis points backwards, at the viewer.
        var back = Vector3.Normalize(eye - target);
        var right = Vector3.Normalize(Vector3.Cross(up, back));
        var trueUp = Vector3.Cross(back, right);

        return new(
            right.X, trueUp.X, back.X, 0f,
            right.Y, trueUp.Y, back.Y, 0f,
            right.Z, trueUp.Z, back.Z, 0f,
            -Vector3.Dot(right, eye), -Vector3.Dot(trueUp, eye), -Vector3.Dot(back, eye), 1f
        );
    }

    /// <summary>
    ///     A right-handed perspective projection with **reverse-Z** depth: the near plane maps to 1
    ///     and the far plane to 0.
    /// </summary>
    /// <param name="fieldOfView">The vertical field of view in radians.</param>
    /// <param name="aspectRatio">Width divided by height.</param>
    /// <param name="nearPlane">Distance to the near plane. Positive.</param>
    /// <param name="farPlane">Distance to the far plane. Greater than the near plane.</param>
    /// <returns>The projection.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <remarks>
    ///     Reverse-Z is not an optimisation to switch on later. Float depth has its precision
    ///     concentrated near zero, so putting the *far* plane there is what stops distant geometry
    ///     z-fighting. The depth test is <c>GREATER</c> and depth clears to <b>0</b>.
    /// </remarks>
    public static Matrix4x4 PerspectiveFieldOfView(
        float fieldOfView,
        float aspectRatio,
        float nearPlane,
        float farPlane
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fieldOfView);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fieldOfView, MathUtil.Pi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(aspectRatio);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nearPlane);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(farPlane, nearPlane);

        var height = 1f / MathF.Tan(fieldOfView * 0.5f);
        var width = height / aspectRatio;
        var range = nearPlane / (farPlane - nearPlane);

        return new(
            width, 0f, 0f, 0f,
            0f, height, 0f, 0f,
            0f, 0f, range, -1f,
            0f, 0f, farPlane * range, 0f
        );
    }

    /// <summary>
    ///     A right-handed reverse-Z perspective projection with no far plane. The far plane's only
    ///     purpose under reverse-Z is to bound the depth range, and pushing it to infinity costs
    ///     nothing in precision — so the choice is free and one fewer thing to tune.
    /// </summary>
    /// <param name="fieldOfView">The vertical field of view in radians.</param>
    /// <param name="aspectRatio">Width divided by height.</param>
    /// <param name="nearPlane">Distance to the near plane. Positive.</param>
    /// <returns>The projection.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public static Matrix4x4 PerspectiveFieldOfViewInfinite(
        float fieldOfView,
        float aspectRatio,
        float nearPlane
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fieldOfView);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fieldOfView, MathUtil.Pi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(aspectRatio);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nearPlane);

        var height = 1f / MathF.Tan(fieldOfView * 0.5f);
        var width = height / aspectRatio;

        return new(
            width, 0f, 0f, 0f,
            0f, height, 0f, 0f,
            0f, 0f, 0f, -1f,
            0f, 0f, nearPlane, 0f
        );
    }

    /// <summary>
    ///     A right-handed orthographic projection with reverse-Z depth, centred on the origin.
    /// </summary>
    /// <param name="width">The width of the view volume.</param>
    /// <param name="height">The height of the view volume.</param>
    /// <param name="nearPlane">Distance to the near plane.</param>
    /// <param name="farPlane">Distance to the far plane. Greater than the near plane.</param>
    /// <returns>The projection.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public static Matrix4x4 Orthographic(float width, float height, float nearPlane, float farPlane) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(farPlane, nearPlane);

        var range = 1f / (farPlane - nearPlane);

        return new(
            2f / width, 0f, 0f, 0f,
            0f, 2f / height, 0f, 0f,
            0f, 0f, range, 0f,
            0f, 0f, farPlane * range, 1f
        );
    }

    /// <summary>
    ///     A right-handed orthographic projection with reverse-Z depth and an arbitrary rectangle,
    ///     for shadow cascades and UI.
    /// </summary>
    /// <param name="left">The left edge.</param>
    /// <param name="right">The right edge.</param>
    /// <param name="bottom">The bottom edge.</param>
    /// <param name="top">The top edge.</param>
    /// <param name="nearPlane">Distance to the near plane.</param>
    /// <param name="farPlane">Distance to the far plane. Greater than the near plane.</param>
    /// <returns>The projection.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The planes are the wrong way round.</exception>
    public static Matrix4x4 OrthographicOffCenter(
        float left,
        float right,
        float bottom,
        float top,
        float nearPlane,
        float farPlane
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(farPlane, nearPlane);

        var width = 1f / (right - left);
        var height = 1f / (top - bottom);
        var range = 1f / (farPlane - nearPlane);

        return new(
            2f * width, 0f, 0f, 0f,
            0f, 2f * height, 0f, 0f,
            0f, 0f, range, 0f,
            -(left + right) * width, -(top + bottom) * height, farPlane * range, 1f
        );
    }

    /// <summary>Composes two transforms: apply <paramref name="left" />, then <paramref name="right" />.</summary>
    /// <param name="left">The transform applied first.</param>
    /// <param name="right">The transform applied second.</param>
    /// <returns>The combined transform.</returns>
    public static Matrix4x4 Multiply(in Matrix4x4 left, in Matrix4x4 right) =>
        Vector128.IsHardwareAccelerated ? MultiplyVectorized(left, right) : MultiplyScalar(left, right);

    /// <summary>
    ///     The vectorised product. Split out from <see cref="Multiply" /> so the benchmark can
    ///     measure it against <see cref="MultiplyScalar" /> — <c>IsHardwareAccelerated</c> is a JIT
    ///     constant, so without the split there is no way to run the other path and the speedup
    ///     stays an assumption.
    /// </summary>
    /// <param name="left">The transform applied first.</param>
    /// <param name="right">The transform applied second.</param>
    /// <returns>The combined transform.</returns>
    internal static Matrix4x4 MultiplyVectorized(in Matrix4x4 left, in Matrix4x4 right) {
        // Rows are loaded sixteen bytes at a time, straight out of the matrix. Reaching them
        // through the Row properties instead — four field reads assembled into a Vector4 and then
        // reinterpreted — measured *slower than the scalar path*, because the JIT emits the gather
        // rather than folding it into a load. Benchmarks/Vixen.Benchmarks.Math is what caught it.
        ref var leftElements = ref Unsafe.As<Matrix4x4, float>(ref Unsafe.AsRef(in left));
        ref var rightElements = ref Unsafe.As<Matrix4x4, float>(ref Unsafe.AsRef(in right));

        var r1 = Vector128.LoadUnsafe(ref rightElements, 0);
        var r2 = Vector128.LoadUnsafe(ref rightElements, 4);
        var r3 = Vector128.LoadUnsafe(ref rightElements, 8);
        var r4 = Vector128.LoadUnsafe(ref rightElements, 12);

        Unsafe.SkipInit(out Matrix4x4 result);
        ref var output = ref Unsafe.As<Matrix4x4, float>(ref result);

        Combine(Vector128.LoadUnsafe(ref leftElements, 0), r1, r2, r3, r4).StoreUnsafe(ref output, 0);
        Combine(Vector128.LoadUnsafe(ref leftElements, 4), r1, r2, r3, r4).StoreUnsafe(ref output, 4);
        Combine(Vector128.LoadUnsafe(ref leftElements, 8), r1, r2, r3, r4).StoreUnsafe(ref output, 8);
        Combine(Vector128.LoadUnsafe(ref leftElements, 12), r1, r2, r3, r4).StoreUnsafe(ref output, 12);

        return result;

        // One output row is the weighted sum of all four input rows, the weights being that row's
        // own elements. Each weight is a lane broadcast — one shuffle instruction — so the whole
        // row costs four multiplies and three adds instead of sixteen dot products.
        static Vector128<float> Combine(
            Vector128<float> row,
            Vector128<float> r1,
            Vector128<float> r2,
            Vector128<float> r3,
            Vector128<float> r4
        ) =>
            (Vector128.Shuffle(row, Vector128.Create(0, 0, 0, 0)) * r1)
            + (Vector128.Shuffle(row, Vector128.Create(1, 1, 1, 1)) * r2)
            + (Vector128.Shuffle(row, Vector128.Create(2, 2, 2, 2)) * r3)
            + (Vector128.Shuffle(row, Vector128.Create(3, 3, 3, 3)) * r4);
    }

    /// <summary>The scalar product — the reference the vectorised path is checked against.</summary>
    /// <param name="left">The transform applied first.</param>
    /// <param name="right">The transform applied second.</param>
    /// <returns>The combined transform.</returns>
    internal static Matrix4x4 MultiplyScalar(in Matrix4x4 left, in Matrix4x4 right) =>
        new(
            (left.M11 * right.M11) + (left.M12 * right.M21) + (left.M13 * right.M31) + (left.M14 * right.M41),
            (left.M11 * right.M12) + (left.M12 * right.M22) + (left.M13 * right.M32) + (left.M14 * right.M42),
            (left.M11 * right.M13) + (left.M12 * right.M23) + (left.M13 * right.M33) + (left.M14 * right.M43),
            (left.M11 * right.M14) + (left.M12 * right.M24) + (left.M13 * right.M34) + (left.M14 * right.M44),
            (left.M21 * right.M11) + (left.M22 * right.M21) + (left.M23 * right.M31) + (left.M24 * right.M41),
            (left.M21 * right.M12) + (left.M22 * right.M22) + (left.M23 * right.M32) + (left.M24 * right.M42),
            (left.M21 * right.M13) + (left.M22 * right.M23) + (left.M23 * right.M33) + (left.M24 * right.M43),
            (left.M21 * right.M14) + (left.M22 * right.M24) + (left.M23 * right.M34) + (left.M24 * right.M44),
            (left.M31 * right.M11) + (left.M32 * right.M21) + (left.M33 * right.M31) + (left.M34 * right.M41),
            (left.M31 * right.M12) + (left.M32 * right.M22) + (left.M33 * right.M32) + (left.M34 * right.M42),
            (left.M31 * right.M13) + (left.M32 * right.M23) + (left.M33 * right.M33) + (left.M34 * right.M43),
            (left.M31 * right.M14) + (left.M32 * right.M24) + (left.M33 * right.M34) + (left.M34 * right.M44),
            (left.M41 * right.M11) + (left.M42 * right.M21) + (left.M43 * right.M31) + (left.M44 * right.M41),
            (left.M41 * right.M12) + (left.M42 * right.M22) + (left.M43 * right.M32) + (left.M44 * right.M42),
            (left.M41 * right.M13) + (left.M42 * right.M23) + (left.M43 * right.M33) + (left.M44 * right.M43),
            (left.M41 * right.M14) + (left.M42 * right.M24) + (left.M43 * right.M34) + (left.M44 * right.M44)
        );

    /// <summary>Transposes the matrix.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns>The transpose.</returns>
    public static Matrix4x4 Transpose(in Matrix4x4 matrix) =>
        new(
            matrix.M11, matrix.M21, matrix.M31, matrix.M41,
            matrix.M12, matrix.M22, matrix.M32, matrix.M42,
            matrix.M13, matrix.M23, matrix.M33, matrix.M43,
            matrix.M14, matrix.M24, matrix.M34, matrix.M44
        );

    /// <summary>The determinant — the signed volume scale the transform applies.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns>The determinant.</returns>
    public static float Determinant(in Matrix4x4 matrix) {
        var a = (matrix.M31 * matrix.M42) - (matrix.M32 * matrix.M41);
        var b = (matrix.M31 * matrix.M43) - (matrix.M33 * matrix.M41);
        var c = (matrix.M31 * matrix.M44) - (matrix.M34 * matrix.M41);
        var d = (matrix.M32 * matrix.M43) - (matrix.M33 * matrix.M42);
        var e = (matrix.M32 * matrix.M44) - (matrix.M34 * matrix.M42);
        var f = (matrix.M33 * matrix.M44) - (matrix.M34 * matrix.M43);

        return (matrix.M11 * ((matrix.M22 * f) - (matrix.M23 * e) + (matrix.M24 * d)))
            - (matrix.M12 * ((matrix.M21 * f) - (matrix.M23 * c) + (matrix.M24 * b)))
            + (matrix.M13 * ((matrix.M21 * e) - (matrix.M22 * c) + (matrix.M24 * a)))
            - (matrix.M14 * ((matrix.M21 * d) - (matrix.M22 * b) + (matrix.M23 * a)));
    }

    /// <summary>Inverts the matrix.</summary>
    /// <param name="matrix">The matrix to invert.</param>
    /// <param name="result">The inverse, or <see cref="Identity" /> if there is none.</param>
    /// <returns><see langword="false" /> if the matrix is singular.</returns>
    public static bool Invert(in Matrix4x4 matrix, out Matrix4x4 result) {
        var a = (matrix.M11 * matrix.M22) - (matrix.M12 * matrix.M21);
        var b = (matrix.M11 * matrix.M23) - (matrix.M13 * matrix.M21);
        var c = (matrix.M11 * matrix.M24) - (matrix.M14 * matrix.M21);
        var d = (matrix.M12 * matrix.M23) - (matrix.M13 * matrix.M22);
        var e = (matrix.M12 * matrix.M24) - (matrix.M14 * matrix.M22);
        var f = (matrix.M13 * matrix.M24) - (matrix.M14 * matrix.M23);
        var g = (matrix.M31 * matrix.M42) - (matrix.M32 * matrix.M41);
        var h = (matrix.M31 * matrix.M43) - (matrix.M33 * matrix.M41);
        var i = (matrix.M31 * matrix.M44) - (matrix.M34 * matrix.M41);
        var j = (matrix.M32 * matrix.M43) - (matrix.M33 * matrix.M42);
        var k = (matrix.M32 * matrix.M44) - (matrix.M34 * matrix.M42);
        var l = (matrix.M33 * matrix.M44) - (matrix.M34 * matrix.M43);

        var determinant = (a * l) - (b * k) + (c * j) + (d * i) - (e * h) + (f * g);
        if (MathF.Abs(determinant) < float.Epsilon) {
            result = Identity;
            return false;
        }

        var inverse = 1f / determinant;

        result = new(
            ((matrix.M22 * l) - (matrix.M23 * k) + (matrix.M24 * j)) * inverse,
            ((-matrix.M12 * l) + (matrix.M13 * k) - (matrix.M14 * j)) * inverse,
            ((matrix.M42 * f) - (matrix.M43 * e) + (matrix.M44 * d)) * inverse,
            ((-matrix.M32 * f) + (matrix.M33 * e) - (matrix.M34 * d)) * inverse,
            ((-matrix.M21 * l) + (matrix.M23 * i) - (matrix.M24 * h)) * inverse,
            ((matrix.M11 * l) - (matrix.M13 * i) + (matrix.M14 * h)) * inverse,
            ((-matrix.M41 * f) + (matrix.M43 * c) - (matrix.M44 * b)) * inverse,
            ((matrix.M31 * f) - (matrix.M33 * c) + (matrix.M34 * b)) * inverse,
            ((matrix.M21 * k) - (matrix.M22 * i) + (matrix.M24 * g)) * inverse,
            ((-matrix.M11 * k) + (matrix.M12 * i) - (matrix.M14 * g)) * inverse,
            ((matrix.M41 * e) - (matrix.M42 * c) + (matrix.M44 * a)) * inverse,
            ((-matrix.M31 * e) + (matrix.M32 * c) - (matrix.M34 * a)) * inverse,
            ((-matrix.M21 * j) + (matrix.M22 * h) - (matrix.M23 * g)) * inverse,
            ((matrix.M11 * j) - (matrix.M12 * h) + (matrix.M13 * g)) * inverse,
            ((-matrix.M41 * d) + (matrix.M42 * b) - (matrix.M43 * a)) * inverse,
            ((matrix.M31 * d) - (matrix.M32 * b) + (matrix.M33 * a)) * inverse
        );

        return true;
    }

    /// <summary>Transforms a point, applying the translation.</summary>
    /// <param name="position">The point.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed point, with the perspective divide applied if there is one.</returns>
    public static Vector3 TransformPosition(Vector3 position, in Matrix4x4 matrix) {
        var transformed = TransformVector4(new(position, 1f), matrix);
        return MathUtil.IsOne(transformed.W) || MathUtil.IsZero(transformed.W)
            ? transformed.Xyz
            : transformed.Xyz / transformed.W;
    }

    /// <summary>
    ///     Transforms a direction, ignoring the translation. For normals under a non-uniform scale,
    ///     transform by the inverse transpose instead — see <see cref="Matrix3x3.Normal" />.
    /// </summary>
    /// <param name="direction">The direction.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed direction.</returns>
    public static Vector3 TransformDirection(Vector3 direction, in Matrix4x4 matrix) =>
        new(
            (direction.X * matrix.M11) + (direction.Y * matrix.M21) + (direction.Z * matrix.M31),
            (direction.X * matrix.M12) + (direction.Y * matrix.M22) + (direction.Z * matrix.M32),
            (direction.X * matrix.M13) + (direction.Y * matrix.M23) + (direction.Z * matrix.M33)
        );

    /// <summary>Transforms a homogeneous vector, without the perspective divide.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed vector.</returns>
    public static Vector4 TransformVector4(Vector4 value, in Matrix4x4 matrix) =>
        Vector128.IsHardwareAccelerated
            ? TransformVector4Vectorized(value, matrix)
            : TransformVector4Scalar(value, matrix);

    /// <summary>The vectorised transform. Split out for the same reason as
    ///     <see cref="MultiplyVectorized" />.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed vector.</returns>
    internal static Vector4 TransformVector4Vectorized(Vector4 value, in Matrix4x4 matrix) {
        ref var elements = ref Unsafe.As<Matrix4x4, float>(ref Unsafe.AsRef(in matrix));
        var lanes = value.AsVector128();

        return Vector4.FromVector128(
            (Vector128.Shuffle(lanes, Vector128.Create(0, 0, 0, 0)) * Vector128.LoadUnsafe(ref elements, 0))
            + (Vector128.Shuffle(lanes, Vector128.Create(1, 1, 1, 1)) * Vector128.LoadUnsafe(ref elements, 4))
            + (Vector128.Shuffle(lanes, Vector128.Create(2, 2, 2, 2)) * Vector128.LoadUnsafe(ref elements, 8))
            + (Vector128.Shuffle(lanes, Vector128.Create(3, 3, 3, 3)) * Vector128.LoadUnsafe(ref elements, 12))
        );
    }

    /// <summary>The scalar transform — the reference.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed vector.</returns>
    internal static Vector4 TransformVector4Scalar(Vector4 value, in Matrix4x4 matrix) =>
        new(
            (value.X * matrix.M11) + (value.Y * matrix.M21) + (value.Z * matrix.M31) + (value.W * matrix.M41),
            (value.X * matrix.M12) + (value.Y * matrix.M22) + (value.Z * matrix.M32) + (value.W * matrix.M42),
            (value.X * matrix.M13) + (value.Y * matrix.M23) + (value.Z * matrix.M33) + (value.W * matrix.M43),
            (value.X * matrix.M14) + (value.Y * matrix.M24) + (value.Z * matrix.M34) + (value.W * matrix.M44)
        );

    /// <summary>
    ///     Transforms a run of points in one call. Culling and skinning do this a million times a
    ///     frame, and hoisting the matrix out of the loop is most of why this exists.
    /// </summary>
    /// <param name="source">The points to transform.</param>
    /// <param name="matrix">The transform.</param>
    /// <param name="destination">Where to write the results. May be the same span as the source.</param>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is shorter than the source.</exception>
    public static void TransformPositions(
        ReadOnlySpan<Vector3> source,
        in Matrix4x4 matrix,
        Span<Vector3> destination
    ) {
        if (destination.Length < source.Length) {
            throw new ArgumentException("The destination is shorter than the source.", nameof(destination));
        }

        var row1 = matrix.Row1.AsVector128();
        var row2 = matrix.Row2.AsVector128();
        var row3 = matrix.Row3.AsVector128();
        var row4 = matrix.Row4.AsVector128();

        for (var i = 0; i < source.Length; i++) {
            var point = source[i];
            var transformed = (Vector128.Create(point.X) * row1)
                + (Vector128.Create(point.Y) * row2)
                + (Vector128.Create(point.Z) * row3)
                + row4;

            destination[i] = new(transformed[0], transformed[1], transformed[2]);
        }
    }

    /// <summary>Transforms a run of directions in one call, ignoring the translation.</summary>
    /// <param name="source">The directions to transform.</param>
    /// <param name="matrix">The transform.</param>
    /// <param name="destination">Where to write the results. May be the same span as the source.</param>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is shorter than the source.</exception>
    public static void TransformDirections(
        ReadOnlySpan<Vector3> source,
        in Matrix4x4 matrix,
        Span<Vector3> destination
    ) {
        if (destination.Length < source.Length) {
            throw new ArgumentException("The destination is shorter than the source.", nameof(destination));
        }

        for (var i = 0; i < source.Length; i++) {
            destination[i] = TransformDirection(source[i], matrix);
        }
    }

    /// <summary>Whether two matrices agree to within a tolerance, element by element.</summary>
    /// <param name="left">The first matrix.</param>
    /// <param name="right">The second matrix.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if every element is within tolerance.</returns>
    public static bool NearEqual(in Matrix4x4 left, in Matrix4x4 right, float tolerance = MathUtil.ZeroTolerance) {
        var a = left.AsSpan();
        var b = right.AsSpan();

        for (var i = 0; i < ComponentCount; i++) {
            if (!MathUtil.NearEqual(a[i], b[i], tolerance)) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc cref="Multiply" />
    /// <param name="left">The transform applied first.</param>
    /// <param name="right">The transform applied second.</param>
    /// <returns>The combined transform.</returns>
    public static Matrix4x4 operator *(Matrix4x4 left, Matrix4x4 right) => Multiply(left, right);

    /// <summary>Transforms a row vector: <c>v * M</c>, the convention this library uses throughout.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed vector.</returns>
    public static Vector4 operator *(Vector4 value, Matrix4x4 matrix) => TransformVector4(value, matrix);

    /// <summary>Exact element-wise equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first matrix.</param>
    /// <param name="right">The second matrix.</param>
    /// <returns><see langword="true" /> if every element is equal.</returns>
    public static bool operator ==(Matrix4x4 left, Matrix4x4 right) => left.Equals(right);

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first matrix.</param>
    /// <param name="right">The second matrix.</param>
    /// <returns><see langword="true" /> if any element differs.</returns>
    public static bool operator !=(Matrix4x4 left, Matrix4x4 right) => !(left == right);

    /// <summary>
    ///     Converts to the BCL matrix. A reinterpretation, not a transpose: the BCL is also
    ///     row-major with the translation in <c>M41..M43</c>.
    /// </summary>
    /// <param name="value">The matrix to convert.</param>
    /// <returns>The equivalent <see cref="System.Numerics.Matrix4x4" />.</returns>
    public static implicit operator System.Numerics.Matrix4x4(Matrix4x4 value) =>
        new(
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44
        );

    /// <summary>Converts from the BCL matrix, which has the same layout.</summary>
    /// <param name="value">The matrix to convert.</param>
    /// <returns>The equivalent <see cref="Matrix4x4" />.</returns>
    public static implicit operator Matrix4x4(System.Numerics.Matrix4x4 value) =>
        new(
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44
        );

    /// <inheritdoc />
    public bool Equals(Matrix4x4 other) =>
        M11 == other.M11 && M12 == other.M12 && M13 == other.M13 && M14 == other.M14
        && M21 == other.M21 && M22 == other.M22 && M23 == other.M23 && M24 == other.M24
        && M31 == other.M31 && M32 == other.M32 && M33 == other.M33 && M34 == other.M34
        && M41 == other.M41 && M42 == other.M42 && M43 == other.M43 && M44 == other.M44;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Matrix4x4 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() {
        // Element by element rather than over the raw bytes: float.GetHashCode normalises -0 and 0
        // to the same value, and hashing the bytes would give two matrices that compare equal two
        // different hashes.
        var hash = default(HashCode);
        foreach (var element in AsSpan()) {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }

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
}
