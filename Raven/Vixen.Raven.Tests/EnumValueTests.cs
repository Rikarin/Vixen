// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;
using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     Enum member values are constant expressions, not just literals. An earlier
///     version silently substituted the declaration ordinal for anything that was
///     not a literal token — <c>C = B</c> compiled as 2, <c>D = 2 + 3</c> as 3,
///     <c>E = -1</c> as its ordinal — with no diagnostic. These pin the fix.
/// </summary>
public class EnumValueTests {
    [Fact]
    public void A_member_may_reference_an_earlier_member() {
        var mode = FindType(
            CompileClean(
                """
                package A

                enum Mode {
                    A,
                    B = 5,
                    C = B
                }

                """
            ),
            "Mode"
        );

        Assert.Equal(5, GetMember<FieldSymbol>(mode, "C").ConstantValue);
    }

    [Fact]
    public void A_member_may_be_a_constant_expression() {
        var mode = FindType(
            CompileClean(
                """
                package A

                enum Mode {
                    A = 2 + 3,
                    B = 1 << 4,
                    C = -1,
                    D = 8 | 4
                }

                """
            ),
            "Mode"
        );

        Assert.Equal(5, GetMember<FieldSymbol>(mode, "A").ConstantValue);
        Assert.Equal(16, GetMember<FieldSymbol>(mode, "B").ConstantValue);
        Assert.Equal(-1, GetMember<FieldSymbol>(mode, "C").ConstantValue);
        Assert.Equal(12, GetMember<FieldSymbol>(mode, "D").ConstantValue);
    }

    [Fact]
    public void An_implicit_value_continues_from_the_previous_member() {
        var mode = FindType(
            CompileClean(
                """
                package A

                enum Mode {
                    A,
                    B,
                    C = 10,
                    D,
                    E
                }

                """
            ),
            "Mode"
        );

        Assert.Equal(0, GetMember<FieldSymbol>(mode, "A").ConstantValue);
        Assert.Equal(1, GetMember<FieldSymbol>(mode, "B").ConstantValue);
        Assert.Equal(10, GetMember<FieldSymbol>(mode, "C").ConstantValue);
        Assert.Equal(11, GetMember<FieldSymbol>(mode, "D").ConstantValue);
        Assert.Equal(12, GetMember<FieldSymbol>(mode, "E").ConstantValue);
    }

    [Fact]
    public void A_non_constant_initializer_is_reported() =>
        AssertDiagnostics(
            """
            package A

            enum Mode {
                A = 1.5
            }

            """,
            "RVN2094"
        );

    [Fact]
    public void A_circular_definition_is_reported() {
        var diagnostics = Diagnose(
            """
            package A

            enum Mode {
                A = B,
                B = A
            }

            """
        );

        Assert.Contains("RVN2005", diagnostics.Select(d => d.Id));
    }

    /// <summary>
    ///     The same evaluator serves <c>const</c> fields, which previously supported only a
    ///     literal initializer and silently had no value otherwise.
    /// </summary>
    [Fact]
    public void A_const_field_may_be_a_constant_expression() {
        var shader = FindType(
            CompileClean(
                """
                package A

                shader S {
                    const val Taps = 4
                    const val Radius = Taps * 2 + 1
                }

                """
            ),
            "S"
        );

        Assert.Equal(9, GetMember<FieldSymbol>(shader, "Radius").ConstantValue);
    }
}
