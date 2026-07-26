// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     A modifier that changes nothing where it stands is named (<c>RVN2093</c>)
///     rather than silently ignored, statement attributes are named
///     (<c>RVN2095</c>) because nothing reads them, and a <c>where</c> clause is
///     enforced (<c>RVN2096</c>) rather than stored and forgotten.
/// </summary>
public class ModifierAndConstraintTests {
    [Theory]
    // `override` participates only in method dispatch; on a field it does nothing.
    [InlineData("package A\n\nshader S {\n    override var x: float\n}\n")]
    // `compose` declares a shader-typed slot, which only a field can be.
    [InlineData("package A\n\nshader S {\n    compose func M() {\n    }\n}\n")]
    // No modifier means anything on a type declaration.
    [InlineData("package A\n\nstatic shader S {\n}\n")]
    [InlineData("package A\n\nreadonly struct P {\n    var x: float\n}\n")]
    // Or on an `init`.
    [InlineData("package A\n\nstruct P {\n    var x: float\n\n    static init() {\n        x = 1f\n    }\n}\n")]
    public void A_modifier_with_no_effect_is_a_warning(string source) {
        var diagnostics = Diagnose(source);

        Assert.Contains("RVN2093", diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void A_modifier_in_its_place_is_silent() =>
        AssertNoDiagnostics(
            """
            package A

            shader S {
                const val Taps = 4
                readonly var bound: float
                static func Shared(): int => Taps

                [VertexShader]
                func VS(pos: float4): float4 => pos
            }

            """
        );

    [Fact]
    public void Attributes_on_a_statement_are_a_warning() {
        var diagnostics = Diagnose(
            """
            package A

            shader S {
                func M() {
                    [Unroll]
                    for (i in 0 .. 4) {
                    }
                }
            }

            """
        );

        Assert.Contains("RVN2095", diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void A_type_argument_must_satisfy_the_constraint() {
        var diagnostics = Diagnose(
            """
            package A

            protocol Shaded {
                func Tint(): float4
            }

            struct Box<T> where T : Shaded {
                var item: T
            }

            struct Good : Shaded {
                func Tint(): float4 => float4(1, 1, 1, 1)
            }

            struct Holder {
                var bad: Box<float>
            }

            """
        );

        Assert.Contains("RVN2096", diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void A_satisfying_type_argument_is_silent() =>
        AssertNoDiagnostics(
            """
            package A

            protocol Shaded {
                func Tint(): float4
            }

            struct Box<T> where T : Shaded {
                var item: T
            }

            struct Good : Shaded {
                func Tint(): float4 => float4(1, 1, 1, 1)
            }

            struct Holder {
                var good: Box<Good>
            }

            """
        );
}
