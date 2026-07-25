// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Xunit;

namespace Tests;

/// <summary>
///     The packing rules, pinned to the numbers the OpenGL/Vulkan specs give.
/// </summary>
/// <remarks>
///     These are golden values, not derived ones: if the implementation changes its mind about
///     <c>float3</c> padding the arithmetic still agrees with itself, and only a literal
///     expected number catches it. This is the file that stops a host and a backend disagreeing
///     about where a member lives.
/// </remarks>
public class ShaderLayoutTests {
    static IrVectorType Vec(int size, IrScalarType? component = null) => new(component ?? IrScalarType.Float, size);

    static IrMatrixType Mat(int rows, int columns) => new(IrScalarType.Float, rows, columns);

    static IrStructType Struct(string name, params IrType[] fields) {
        var result = new IrStructType(name);
        result.SetFields([.. fields.Select((f, i) => new IrField($"f{i}", f))]);
        return result;
    }

    // --- Scalars and vectors ------------------------------------------------

    [Theory]
    [InlineData(1, 4, 4)]   // float:  aligns 4, occupies 4
    [InlineData(2, 8, 8)]   // float2: aligns 8
    [InlineData(3, 16, 12)] // float3: aligns 16 but occupies 12 — the classic trap
    [InlineData(4, 16, 16)]
    public void Vector_alignment_and_size_follow_the_spec(int lanes, int alignment, int size) {
        Assert.Equal(alignment, ShaderLayout.Alignment(Vec(lanes)));
        Assert.Equal(size, ShaderLayout.Size(Vec(lanes)));
    }

    [Fact]
    public void A_double_is_eight_bytes_and_its_vectors_scale_with_it() {
        Assert.Equal(8, ShaderLayout.Size(IrScalarType.Double));
        Assert.Equal(8, ShaderLayout.Alignment(IrScalarType.Double));

        // dvec2 aligns to 16, dvec3/dvec4 to 32.
        Assert.Equal(16, ShaderLayout.Alignment(Vec(2, IrScalarType.Double)));
        Assert.Equal(32, ShaderLayout.Alignment(Vec(3, IrScalarType.Double)));
        Assert.Equal(24, ShaderLayout.Size(Vec(3, IrScalarType.Double)));
    }

    /// <summary>
    ///     The canonical std140 hazard: a <c>float3</c> occupies 12 bytes but the next member
    ///     starts at 16, and a host packing them back to back writes into the wrong place.
    /// </summary>
    [Fact]
    public void A_float_after_a_float3_sits_in_the_padding() {
        var (offsets, size) = ShaderLayout.Members([Vec(3), IrScalarType.Float]);

        Assert.Equal([0, 12], offsets);
        Assert.Equal(16, size);
    }

    [Fact]
    public void A_float3_after_a_float_is_pushed_to_the_next_sixteen() {
        var (offsets, size) = ShaderLayout.Members([IrScalarType.Float, Vec(3)]);

        // The float3 cannot start at 4: it aligns to 16.
        Assert.Equal([0, 16], offsets);
        Assert.Equal(32, size);
    }

    [Fact]
    public void A_float2_aligns_to_eight_not_sixteen() {
        var (offsets, size) = ShaderLayout.Members([IrScalarType.Float, Vec(2)]);

        Assert.Equal([0, 8], offsets);
        Assert.Equal(16, size);
    }

    // --- Arrays --------------------------------------------------------------

    /// <summary>
    ///     In std140 every array element is padded to 16, so an array of scalars costs four
    ///     times what a host would naively allocate.
    /// </summary>
    [Fact]
    public void An_array_of_floats_costs_sixteen_bytes_an_element_in_std140() {
        var array = new IrArrayType(IrScalarType.Float, 4);

        Assert.Equal(16, ShaderLayout.ArrayStride(array));
        Assert.Equal(64, ShaderLayout.Size(array));
    }

    [Fact]
    public void The_same_array_is_tightly_packed_in_std430() {
        var array = new IrArrayType(IrScalarType.Float, 4);

        Assert.Equal(4, ShaderLayout.ArrayStride(array, LayoutRule.Std430));
        Assert.Equal(16, ShaderLayout.Size(array, LayoutRule.Std430));
    }

    [Fact]
    public void An_array_of_float3_is_padded_in_both_rules() {
        var array = new IrArrayType(Vec(3), 4);

        // A float3 aligns to 16 on its own, so std430 cannot pack it either.
        Assert.Equal(16, ShaderLayout.ArrayStride(array));
        Assert.Equal(16, ShaderLayout.ArrayStride(array, LayoutRule.Std430));
        Assert.Equal(64, ShaderLayout.Size(array, LayoutRule.Std430));
    }

    [Fact]
    public void A_runtime_sized_array_reports_a_stride_but_no_size() {
        var array = new IrArrayType(IrScalarType.Float);

        Assert.Equal(16, ShaderLayout.ArrayStride(array));
        Assert.Equal(0, ShaderLayout.Size(array));
    }

    // --- Matrices ------------------------------------------------------------

    /// <summary>
    ///     A matrix is stored column-major, so the stride is the gap between columns and an
    ///     R×C matrix occupies stride × C.
    /// </summary>
    [Theory]
    [InlineData(2, 2, 16, 32)]
    [InlineData(3, 3, 16, 48)]
    [InlineData(4, 4, 16, 64)]
    [InlineData(4, 2, 16, 32)] // 2 columns of float4
    [InlineData(2, 4, 16, 64)] // 4 columns of float2, each padded to 16 in std140
    public void Matrix_stride_and_size_follow_the_column_count(int rows, int columns, int stride, int size) {
        var matrix = Mat(rows, columns);

        Assert.Equal(stride, ShaderLayout.MatrixStride(matrix));
        Assert.Equal(size, ShaderLayout.Size(matrix));
    }

    [Fact]
    public void A_float2_matrix_packs_tighter_in_std430() {
        var matrix = Mat(2, 4);

        // std430 drops the round-up, so a column of float2 costs 8 rather than 16.
        Assert.Equal(8, ShaderLayout.MatrixStride(matrix, LayoutRule.Std430));
        Assert.Equal(32, ShaderLayout.Size(matrix, LayoutRule.Std430));
    }

    // --- Structs -------------------------------------------------------------

    [Fact]
    public void A_struct_rounds_up_to_sixteen_in_std140() {
        var nested = Struct("Small", IrScalarType.Float);

        Assert.Equal(16, ShaderLayout.Alignment(nested));
        Assert.Equal(16, ShaderLayout.Size(nested));
    }

    [Fact]
    public void The_same_struct_keeps_its_natural_alignment_in_std430() {
        var nested = Struct("Small", IrScalarType.Float);

        Assert.Equal(4, ShaderLayout.Alignment(nested, LayoutRule.Std430));
        Assert.Equal(4, ShaderLayout.Size(nested, LayoutRule.Std430));
    }

    [Fact]
    public void A_member_after_a_struct_starts_on_the_structs_alignment() {
        var nested = Struct("Small", IrScalarType.Float);
        var (offsets, size) = ShaderLayout.Members([IrScalarType.Float, nested, IrScalarType.Float]);

        // The struct aligns to 16, occupies 16, and the trailing float follows it.
        Assert.Equal([0, 16, 32], offsets);
        Assert.Equal(48, size);
    }

    [Fact]
    public void A_nested_struct_contributes_its_worst_member_alignment() {
        var inner = Struct("Inner", Vec(4));
        var outer = Struct("Outer", IrScalarType.Float, inner);

        Assert.Equal(16, ShaderLayout.Alignment(outer));
        Assert.Equal(32, ShaderLayout.Size(outer));
    }

    [Fact]
    public void An_empty_block_still_has_a_valid_alignment() {
        var (offsets, size) = ShaderLayout.Members([]);

        Assert.Empty(offsets);
        Assert.Equal(0, size);
    }

    // --- The two rules side by side ------------------------------------------

    /// <summary>
    ///     One block measured both ways. std430 exists precisely so a storage buffer does not
    ///     pay std140's padding, and this is the difference in one place.
    /// </summary>
    [Fact]
    public void The_two_rules_differ_only_where_the_round_up_applies() {
        IrType[] block = [
            IrScalarType.Float,
            new IrArrayType(IrScalarType.Float, 4),
            Vec(3)
        ];

        var (std140Offsets, std140Size) = ShaderLayout.Members(block);
        var (std430Offsets, std430Size) = ShaderLayout.Members(block, LayoutRule.Std430);

        // std140: the array aligns to 16 and each element is padded to 16.
        Assert.Equal([0, 16, 80], std140Offsets);
        Assert.Equal(96, std140Size);

        // std430: the array aligns to 4 and packs tight, so everything moves down.
        Assert.Equal([0, 4, 32], std430Offsets);
        Assert.Equal(48, std430Size);
    }
}
