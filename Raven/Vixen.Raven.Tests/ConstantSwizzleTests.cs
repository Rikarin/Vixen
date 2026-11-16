// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;
using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     A swizzle of a constant vector is a constant. A texture graph's two-number parameter is declared as a
///     <c>const val</c> of a <c>float2</c> and read by an expression as <c>Offset.x</c> (Patina #181), which folded to
///     nothing before: the vector folded and the swizzle over it did not.
/// </summary>
public class ConstantSwizzleTests {
    [Fact]
    public void A_lane_of_a_constant_vector_folds_and_takes_part_in_arithmetic() {
        var shader = FindType(
            CompileClean(
                """
                package A

                shader S {
                    const val Offset: float2 = float2(0.25f, 3f)
                    const val X: float = Offset.x
                    const val Y: float = Offset.y * 2f + Offset.r
                    const val Both: float2 = Offset.yx
                }

                """
            ),
            "S"
        );

        Assert.Equal(0.25f, GetMember<FieldSymbol>(shader, "X").ConstantValue);
        Assert.Equal(6.25f, GetMember<FieldSymbol>(shader, "Y").ConstantValue);
        Assert.Equal([3f, 0.25f], (object?[])GetMember<FieldSymbol>(shader, "Both").ConstantValue!);
    }

    [Fact]
    public void A_lane_of_a_vector_that_is_no_constant_is_no_constant() {
        var shader = FindType(
            CompileClean(
                """
                package A

                shader S {
                    var Offset: float2
                    var X: float = Offset.x
                }

                """
            ),
            "S"
        );

        Assert.Null(GetMember<FieldSymbol>(shader, "X").ConstantValue);
    }
}
