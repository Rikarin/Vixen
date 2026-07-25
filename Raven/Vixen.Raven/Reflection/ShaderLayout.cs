// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;

namespace Vixen.Raven.Reflection;

/// <summary>How a block's members are packed into memory.</summary>
public enum LayoutRule {
    /// <summary>
    ///     Uniform-buffer layout. Arrays, structs and matrix columns all round up to 16 bytes,
    ///     which is what makes a <c>float3[4]</c> cost 64 bytes rather than 48.
    /// </summary>
    Std140,

    /// <summary>
    ///     Storage-buffer layout. As <see cref="Std140" />, minus the round-up-to-16 rule: an
    ///     array or struct takes its element's natural alignment, so <c>float[4]</c> is 16
    ///     bytes rather than 64.
    /// </summary>
    Std430
}

/// <summary>
///     The one implementation of the packing rules.
/// </summary>
/// <remarks>
///     <para>
///         SPIR-V has no implicit layout: every block member carries an explicit
///         <c>Offset</c>, every array an <c>ArrayStride</c> and every matrix a
///         <c>MatrixStride</c>. The host writing that buffer has to agree with all of them,
///         and so does GLSL's <c>std140</c>/<c>std430</c>.
///     </para>
///     <para>
///         So there is exactly one implementation and everything reads it — the SPIR-V
///         decorations, the reflection the engine writes constant buffers from, and the tests
///         that pin the numbers. A second copy is how backends come to disagree about
///         <c>float3</c> padding, which is the failure this file exists to prevent.
///     </para>
///     <para>
///         <b>Matrices are stored column-major</b>, matching GLSL's default inside a laid-out
///         block and the <c>ColMajor</c> decoration the SPIR-V backend writes. An R×C matrix is
///         C columns of R components, so <see cref="MatrixStride" /> is the gap between columns.
///     </para>
/// </remarks>
public static class ShaderLayout {
    /// <summary>Alignment in bytes for a member of a block laid out under <paramref name="rule" />.</summary>
    public static int Alignment(IrType type, LayoutRule rule = LayoutRule.Std140) =>
        type switch {
            IrScalarType scalar => ScalarSize(scalar),

            // A two-lane vector aligns to twice its component; three and four lanes both
            // align to four, which is why a vec3 leaves a hole behind it. A one-lane vector
            // aligns like the scalar it is — Raven collapses those to scalars, so this arm is
            // for completeness rather than for code it currently sees.
            IrVectorType vector => ScalarSize(vector.Component) * vector.Size switch {
                <= 1 => 1,
                2 => 2,
                _ => 4
            },

            IrMatrixType matrix => MatrixStride(matrix, rule),

            // The round-up to 16 here is exactly what std430 drops.
            IrArrayType array => Round(Alignment(array.Element, rule), rule),
            IrStructType structType => structType.Fields.Count == 0
                ? Round(4, rule)
                : Round(structType.Fields.Max(f => Alignment(f.Type, rule)), rule),

            _ => 4
        };

    /// <summary>Bytes occupied, which for a vec3 is 12 even though it aligns to 16.</summary>
    public static int Size(IrType type, LayoutRule rule = LayoutRule.Std140) =>
        type switch {
            IrScalarType scalar => ScalarSize(scalar),
            IrVectorType vector => ScalarSize(vector.Component) * vector.Size,
            IrMatrixType matrix => MatrixStride(matrix, rule) * matrix.Columns,
            IrArrayType array => ArrayStride(array, rule) * (array.Length ?? 0),
            IrStructType structType => Members([.. structType.Fields.Select(f => f.Type)], rule).Size,
            _ => 4
        };

    /// <summary>
    ///     The gap between consecutive matrix columns. A column is a vector of
    ///     <see cref="IrMatrixType.Rows" /> components, so this follows the same rule an array
    ///     of those vectors would.
    /// </summary>
    public static int MatrixStride(IrMatrixType matrix, LayoutRule rule = LayoutRule.Std140) {
        ArgumentNullException.ThrowIfNull(matrix);
        return Round(Alignment(new IrVectorType(matrix.Component, matrix.Rows), rule), rule);
    }

    /// <summary>The gap between consecutive array elements.</summary>
    public static int ArrayStride(IrArrayType array, LayoutRule rule = LayoutRule.Std140) {
        ArgumentNullException.ThrowIfNull(array);
        return Round(Math.Max(Size(array.Element, rule), Alignment(array.Element, rule)), rule);
    }

    /// <summary>
    ///     Byte offsets for a run of members laid out in order, plus the block's total size.
    /// </summary>
    public static (int[] Offsets, int Size) Members(
        IReadOnlyList<IrType> members,
        LayoutRule rule = LayoutRule.Std140
    ) {
        ArgumentNullException.ThrowIfNull(members);

        var offsets = new int[members.Count];
        var offset = 0;

        for (var i = 0; i < members.Count; i++) {
            offset = RoundUp(offset, Alignment(members[i], rule));
            offsets[i] = offset;
            offset += Size(members[i], rule);
        }

        var alignment = members.Count == 0
            ? Round(4, rule)
            : Round(members.Max(m => Alignment(m, rule)), rule);

        return (offsets, RoundUp(offset, alignment));
    }

    /// <summary>
    ///     std140 rounds aggregates up to 16 bytes; std430 leaves them at their natural
    ///     alignment. This single difference is the whole of the divergence between the two.
    /// </summary>
    static int Round(int alignment, LayoutRule rule) =>
        rule == LayoutRule.Std140 ? RoundUp(alignment, 16) : alignment;

    static int ScalarSize(IrScalarType scalar) => scalar.Kind == IrTypeKind.Double ? 8 : 4;

    static int RoundUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
