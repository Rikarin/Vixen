// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     A 3×3 matrix: rotations and scales with no translation. The type
///     <see cref="System.Numerics" /> does not have, and the one a shader wants for normals — nine
///     floats instead of sixteen, in a constant buffer that is read per vertex.
/// </summary>
/// <remarks>
///     Same conventions as <see cref="Matrix4x4" />: row-vector, row-major, composing left to right.
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly struct Matrix3x3 : IEquatable<Matrix3x3>, IFormattable, ISpanFormattable {
    /// <summary>Number of components.</summary>
    public const int ComponentCount = 9;

    /// <summary>Row 1, column 1.</summary>
    public readonly float M11;

    /// <summary>Row 1, column 2.</summary>
    public readonly float M12;

    /// <summary>Row 1, column 3.</summary>
    public readonly float M13;

    /// <summary>Row 2, column 1.</summary>
    public readonly float M21;

    /// <summary>Row 2, column 2.</summary>
    public readonly float M22;

    /// <summary>Row 2, column 3.</summary>
    public readonly float M23;

    /// <summary>Row 3, column 1.</summary>
    public readonly float M31;

    /// <summary>Row 3, column 2.</summary>
    public readonly float M32;

    /// <summary>Row 3, column 3.</summary>
    public readonly float M33;

    /// <summary>The transform that does nothing.</summary>
    public static Matrix3x3 Identity =>
        new(
            1f, 0f, 0f,
            0f, 1f, 0f,
            0f, 0f, 1f
        );

    /// <summary>Builds a matrix from its nine elements, in row-major order.</summary>
    /// <param name="m11">Row 1, column 1.</param>
    /// <param name="m12">Row 1, column 2.</param>
    /// <param name="m13">Row 1, column 3.</param>
    /// <param name="m21">Row 2, column 1.</param>
    /// <param name="m22">Row 2, column 2.</param>
    /// <param name="m23">Row 2, column 3.</param>
    /// <param name="m31">Row 3, column 1.</param>
    /// <param name="m32">Row 3, column 2.</param>
    /// <param name="m33">Row 3, column 3.</param>
    public Matrix3x3(
        float m11, float m12, float m13,
        float m21, float m22, float m23,
        float m31, float m32, float m33
    ) {
        M11 = m11;
        M12 = m12;
        M13 = m13;
        M21 = m21;
        M22 = m22;
        M23 = m23;
        M31 = m31;
        M32 = m32;
        M33 = m33;
    }

    /// <summary>Builds a matrix from its three rows.</summary>
    /// <param name="row1">The first row.</param>
    /// <param name="row2">The second row.</param>
    /// <param name="row3">The third row.</param>
    public Matrix3x3(Vector3 row1, Vector3 row2, Vector3 row3)
        : this(row1.X, row1.Y, row1.Z, row2.X, row2.Y, row2.Z, row3.X, row3.Y, row3.Z) { }

    /// <summary>The first row.</summary>
    public Vector3 Row1 => new(M11, M12, M13);

    /// <summary>The second row.</summary>
    public Vector3 Row2 => new(M21, M22, M23);

    /// <summary>The third row.</summary>
    public Vector3 Row3 => new(M31, M32, M33);

    /// <summary>The elements in row-major order. Valid as long as the matrix it came from.</summary>
    /// <returns>A span of <see cref="ComponentCount" /> floats.</returns>
    [UnscopedRef]
    public ReadOnlySpan<float> AsSpan() => MemoryMarshal.CreateReadOnlySpan(in M11, ComponentCount);

    /// <summary>The upper-left 3×3 of a 4×4 — its rotation and scale, without the translation.</summary>
    /// <param name="matrix">The matrix to take from.</param>
    /// <returns>The upper-left block.</returns>
    public static Matrix3x3 FromMatrix4x4(in Matrix4x4 matrix) =>
        new(
            matrix.M11, matrix.M12, matrix.M13,
            matrix.M21, matrix.M22, matrix.M23,
            matrix.M31, matrix.M32, matrix.M33
        );

    /// <summary>The rotation a quaternion describes, as a 3×3 matrix.</summary>
    /// <param name="rotation">The rotation. Expected to be unit length.</param>
    /// <returns>The transform.</returns>
    public static Matrix3x3 FromQuaternion(Quaternion rotation) =>
        FromMatrix4x4(Matrix4x4.FromQuaternion(rotation));

    /// <summary>Scales each axis independently.</summary>
    /// <param name="scale">The per-axis scale.</param>
    /// <returns>The transform.</returns>
    public static Matrix3x3 FromScale(Vector3 scale) =>
        new(
            scale.X, 0f, 0f,
            0f, scale.Y, 0f,
            0f, 0f, scale.Z
        );

    /// <summary>
    ///     The matrix that transforms normals under <paramref name="modelToWorld" />: the inverse
    ///     transpose of its upper-left 3×3.
    /// </summary>
    /// <param name="modelToWorld">The transform the positions use.</param>
    /// <returns>
    ///     The normal matrix, or the plain upper-left block if the transform is singular — an object
    ///     scaled flat has no meaningful normals, and returning something finite keeps a degenerate
    ///     object from filling the G-buffer with NaN.
    /// </returns>
    /// <remarks>
    ///     Transforming a normal by the model matrix is correct only for rotations and uniform
    ///     scales. Under a non-uniform scale it stops being perpendicular to the surface — squash a
    ///     sphere and its normals tilt the wrong way — and the lighting is subtly wrong everywhere
    ///     rather than obviously wrong somewhere. This is the fix, and it is why the renderer uploads
    ///     a separate 3×3.
    /// </remarks>
    public static Matrix3x3 Normal(in Matrix4x4 modelToWorld) =>
        Matrix4x4.Invert(modelToWorld, out var inverted)
            ? Transpose(FromMatrix4x4(inverted))
            : FromMatrix4x4(modelToWorld);

    /// <summary>Composes two transforms: apply <paramref name="left" />, then <paramref name="right" />.</summary>
    /// <param name="left">The transform applied first.</param>
    /// <param name="right">The transform applied second.</param>
    /// <returns>The combined transform.</returns>
    public static Matrix3x3 Multiply(in Matrix3x3 left, in Matrix3x3 right) =>
        new(
            (left.M11 * right.M11) + (left.M12 * right.M21) + (left.M13 * right.M31),
            (left.M11 * right.M12) + (left.M12 * right.M22) + (left.M13 * right.M32),
            (left.M11 * right.M13) + (left.M12 * right.M23) + (left.M13 * right.M33),
            (left.M21 * right.M11) + (left.M22 * right.M21) + (left.M23 * right.M31),
            (left.M21 * right.M12) + (left.M22 * right.M22) + (left.M23 * right.M32),
            (left.M21 * right.M13) + (left.M22 * right.M23) + (left.M23 * right.M33),
            (left.M31 * right.M11) + (left.M32 * right.M21) + (left.M33 * right.M31),
            (left.M31 * right.M12) + (left.M32 * right.M22) + (left.M33 * right.M32),
            (left.M31 * right.M13) + (left.M32 * right.M23) + (left.M33 * right.M33)
        );

    /// <summary>Transposes the matrix.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns>The transpose.</returns>
    public static Matrix3x3 Transpose(in Matrix3x3 matrix) =>
        new(
            matrix.M11, matrix.M21, matrix.M31,
            matrix.M12, matrix.M22, matrix.M32,
            matrix.M13, matrix.M23, matrix.M33
        );

    /// <summary>The determinant.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns>The determinant.</returns>
    public static float Determinant(in Matrix3x3 matrix) =>
        (matrix.M11 * ((matrix.M22 * matrix.M33) - (matrix.M23 * matrix.M32)))
        - (matrix.M12 * ((matrix.M21 * matrix.M33) - (matrix.M23 * matrix.M31)))
        + (matrix.M13 * ((matrix.M21 * matrix.M32) - (matrix.M22 * matrix.M31)));

    /// <summary>Inverts the matrix.</summary>
    /// <param name="matrix">The matrix to invert.</param>
    /// <param name="result">The inverse, or <see cref="Identity" /> if there is none.</param>
    /// <returns><see langword="false" /> if the matrix is singular.</returns>
    public static bool Invert(in Matrix3x3 matrix, out Matrix3x3 result) {
        var determinant = Determinant(matrix);
        if (MathF.Abs(determinant) < float.Epsilon) {
            result = Identity;
            return false;
        }

        var inverse = 1f / determinant;

        result = new(
            ((matrix.M22 * matrix.M33) - (matrix.M23 * matrix.M32)) * inverse,
            ((matrix.M13 * matrix.M32) - (matrix.M12 * matrix.M33)) * inverse,
            ((matrix.M12 * matrix.M23) - (matrix.M13 * matrix.M22)) * inverse,
            ((matrix.M23 * matrix.M31) - (matrix.M21 * matrix.M33)) * inverse,
            ((matrix.M11 * matrix.M33) - (matrix.M13 * matrix.M31)) * inverse,
            ((matrix.M13 * matrix.M21) - (matrix.M11 * matrix.M23)) * inverse,
            ((matrix.M21 * matrix.M32) - (matrix.M22 * matrix.M31)) * inverse,
            ((matrix.M12 * matrix.M31) - (matrix.M11 * matrix.M32)) * inverse,
            ((matrix.M11 * matrix.M22) - (matrix.M12 * matrix.M21)) * inverse
        );

        return true;
    }

    /// <summary>Transforms a vector: <c>v * M</c>.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed vector.</returns>
    public static Vector3 Transform(Vector3 value, in Matrix3x3 matrix) =>
        new(
            (value.X * matrix.M11) + (value.Y * matrix.M21) + (value.Z * matrix.M31),
            (value.X * matrix.M12) + (value.Y * matrix.M22) + (value.Z * matrix.M32),
            (value.X * matrix.M13) + (value.Y * matrix.M23) + (value.Z * matrix.M33)
        );

    /// <summary>Whether two matrices agree to within a tolerance, element by element.</summary>
    /// <param name="left">The first matrix.</param>
    /// <param name="right">The second matrix.</param>
    /// <param name="tolerance">The relative tolerance.</param>
    /// <returns><see langword="true" /> if every element is within tolerance.</returns>
    public static bool NearEqual(in Matrix3x3 left, in Matrix3x3 right, float tolerance = MathUtil.ZeroTolerance) {
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
    public static Matrix3x3 operator *(Matrix3x3 left, Matrix3x3 right) => Multiply(left, right);

    /// <summary>Transforms a row vector: <c>v * M</c>.</summary>
    /// <param name="value">The vector.</param>
    /// <param name="matrix">The transform.</param>
    /// <returns>The transformed vector.</returns>
    public static Vector3 operator *(Vector3 value, Matrix3x3 matrix) => Transform(value, matrix);

    /// <summary>Exact element-wise equality, IEEE semantics. See <see cref="NearEqual" />.</summary>
    /// <param name="left">The first matrix.</param>
    /// <param name="right">The second matrix.</param>
    /// <returns><see langword="true" /> if every element is equal.</returns>
    public static bool operator ==(Matrix3x3 left, Matrix3x3 right) => left.Equals(right);

    /// <summary>The negation of <see cref="op_Equality" />.</summary>
    /// <param name="left">The first matrix.</param>
    /// <param name="right">The second matrix.</param>
    /// <returns><see langword="true" /> if any element differs.</returns>
    public static bool operator !=(Matrix3x3 left, Matrix3x3 right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(Matrix3x3 other) =>
        M11 == other.M11 && M12 == other.M12 && M13 == other.M13
        && M21 == other.M21 && M22 == other.M22 && M23 == other.M23
        && M31 == other.M31 && M32 == other.M32 && M33 == other.M33;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Matrix3x3 other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() {
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
