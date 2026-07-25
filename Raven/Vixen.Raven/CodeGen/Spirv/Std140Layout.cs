// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;

namespace Vixen.Raven.CodeGen.Spirv;

/// <summary>
///     Vulkan's standard uniform buffer layout — what GLSL calls <c>std140</c>.
///     SPIR-V has no implicit layout: every member of a block carries an explicit
///     <c>Offset</c>, every array an <c>ArrayStride</c> and every matrix a
///     <c>MatrixStride</c>, and the host has to agree with all of them. Computing it
///     here means the same rules produce the decorations and the sizes.
/// </summary>
public static class Std140Layout {
    /// <summary>Alignment in bytes, before the rule that rounds blocks up to 16.</summary>
    public static int Alignment(IrType type) =>
        type switch {
            IrScalarType scalar => ScalarSize(scalar),
            // A two-lane vector aligns to twice its component; three and four lanes
            // both align to four, which is why a vec3 leaves a hole behind it.
            IrVectorType vector => ScalarSize(vector.Component) * (vector.Size == 2 ? 2 : 4),
            IrMatrixType matrix => MatrixStride(matrix),
            IrArrayType array => RoundUp(Alignment(array.Element), 16),
            IrStructType structType => RoundUp(structType.Fields.Max(f => Alignment(f.Type)), 16),
            _ => 4
        };

    /// <summary>Bytes occupied, which for a vec3 is 12 even though it aligns to 16.</summary>
    public static int Size(IrType type) =>
        type switch {
            IrScalarType scalar => ScalarSize(scalar),
            IrVectorType vector => ScalarSize(vector.Component) * vector.Size,
            IrMatrixType matrix => MatrixStride(matrix) * matrix.Columns,
            IrArrayType array => ArrayStride(array) * (array.Length ?? 0),
            IrStructType structType => StructSize(structType),
            _ => 4
        };

    /// <summary>
    ///     The gap between consecutive matrix columns. A SPIR-V matrix is a column
    ///     vector type repeated, so this is the column vector's alignment rounded up
    ///     to 16 — the same rule an array of vectors follows.
    /// </summary>
    public static int MatrixStride(IrMatrixType matrix) =>
        RoundUp(Alignment(new IrVectorType(matrix.Component, matrix.Rows)), 16);

    /// <summary>The gap between consecutive array elements.</summary>
    public static int ArrayStride(IrArrayType array) =>
        RoundUp(Math.Max(Size(array.Element), Alignment(array.Element)), 16);

    /// <summary>
    ///     Byte offsets for a run of members laid out in order, plus the total size.
    /// </summary>
    public static (int[] Offsets, int Size) Members(IReadOnlyList<IrType> members) {
        var offsets = new int[members.Count];
        var offset = 0;

        for (var i = 0; i < members.Count; i++) {
            offset = RoundUp(offset, Alignment(members[i]));
            offsets[i] = offset;
            offset += Size(members[i]);
        }

        var alignment = members.Count == 0 ? 16 : RoundUp(members.Max(Alignment), 16);
        return (offsets, RoundUp(offset, alignment));
    }

    static int StructSize(IrStructType structType) => Members([.. structType.Fields.Select(f => f.Type)]).Size;

    static int ScalarSize(IrScalarType scalar) => scalar.Kind == IrTypeKind.Double ? 8 : 4;

    static int RoundUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
