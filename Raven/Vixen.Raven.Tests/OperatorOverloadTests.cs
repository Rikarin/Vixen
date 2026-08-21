// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.CodeGenTestBase;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     User-defined operators, which used to fail at <em>binding</em> — so declaring one compiled
///     and then no call site could reach it.
/// </summary>
/// <remarks>
///     Vector maths on a user type is the reason to have them: a <c>Spectrum</c> wants <c>a + b</c>
///     to read as addition rather than <c>Spectrum.Add(a, b)</c> at every use. Nothing about that
///     needs anything a GPU lacks — it resolves statically to one call. See docs/plan/07 § J.
/// </remarks>
public class OperatorOverloadTests {
    const string Spectrum = """
                            struct Spectrum {
                                var r: float
                                var g: float

                                Spectrum operator +(a: Spectrum, b: Spectrum) => Spectrum(a.r + b.r, a.g + b.g)

                                Spectrum operator *(a: Spectrum, s: float) => Spectrum(a.r * s, a.g * s)

                                Spectrum operator -(a: Spectrum) => Spectrum(-a.r, -a.g)
                            }
                            """;

    static string Fragment(string body) =>
        GenerateOne(
            $$"""
              package A

              {{Spectrum}}

              shader S {
                  [FragmentShader]
                  func Fragment(): float4 {
              {{body}}
                  }
              }

              """
        );

    /// <summary>
    ///     Named for the operator rather than mangled. <c>operator+</c> is not an identifier in
    ///     either target, and letting the GLSL mangler have it would turn every operator on a type
    ///     into <c>operator_</c>, <c>operator_1</c>, … — unreadable in a frame debugger.
    /// </summary>
    [Fact]
    public void A_binary_operator_becomes_a_call_named_for_the_operator() {
        var glsl = Fragment(
            """
                    val x = Spectrum(1f, 2f)
                    val sum = x + x
                    return float4(sum.r, sum.g, 0, 1)
            """
        );

        Assert.Contains("Spectrum Spectrum_Add(Spectrum a, Spectrum b)", glsl, StringComparison.Ordinal);
        Assert.Contains("Spectrum_Add(", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     No receiver is passed and none is declared: both operands are explicit parameters. Getting
    ///     this wrong was the first failure — the signature took a <c>self</c> the call did not pass.
    /// </summary>
    [Fact]
    public void An_operator_takes_its_operands_and_no_receiver() {
        var glsl = Fragment(
            """
                    val x = Spectrum(1f, 2f)
                    val sum = x + x
                    return float4(sum.r, sum.g, 0, 1)
            """
        );

        Assert.DoesNotContain("Spectrum_Add(Spectrum self", glsl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unary_operator_resolves_too() {
        var glsl = Fragment(
            """
                    val x = Spectrum(1f, 2f)
                    val negated = -x
                    return float4(negated.r, negated.g, 0, 1)
            """
        );

        Assert.Contains("Spectrum Spectrum_Subtract(Spectrum a)", glsl, StringComparison.Ordinal);
    }

    /// <summary>Mixed operand types resolve against the type that declares the operator.</summary>
    [Fact]
    public void An_operator_with_a_scalar_operand_resolves() {
        var glsl = Fragment(
            """
                    val x = Spectrum(1f, 2f)
                    val scaled = x * 2f
                    return float4(scaled.r, scaled.g, 0, 1)
            """
        );

        Assert.Contains("Spectrum_Multiply(", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Built-ins win. A user-defined operator is only looked for once the built-in resolution
    ///     has failed, so no declaration can change what the primitives mean.
    /// </summary>
    [Fact]
    public void A_declaration_cannot_change_what_the_primitives_mean() {
        var glsl = GenerateOne(
            """
            package A

            struct Odd {
                var v: float

                float operator +(a: float, b: float) => 0f
            }

            shader S {
                [FragmentShader]
                func Fragment(): float4 {
                    val sum = 1f + 2f
                    return float4(sum, 0, 0, 1)
                }
            }

            """
        );

        // Still ordinary addition, not a call.
        Assert.DoesNotContain("Odd_Add", glsl, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operator_that_does_not_apply_still_reports() =>
        AssertDiagnostics(
            """
            package A

            struct Spectrum {
                var r: float

                Spectrum operator +(a: Spectrum, b: Spectrum) => a
            }

            struct Other {
                var v: float
            }

            shader S {
                [FragmentShader]
                func Fragment(): float4 {
                    var a: Other
                    a.v = 1f
                    var b: Other
                    b.v = 2f
                    val sum = a + b
                    return float4(1, 1, 1, 1)
                }
            }

            """,
            "RVN2022"
        );

    /// <summary>Both backends emit it, and both reference tools accept the result.</summary>
    [Fact]
    public void Both_backends_emit_an_operator_call() {
        var source = $$"""
                       package A

                       {{Spectrum}}

                       shader S {
                           [FragmentShader]
                           func Fragment(): float4 {
                               val x = Spectrum(1f, 2f)
                               val y = -(x + x) * 2f
                               return float4(y.r, y.g, 0, 1)
                           }
                       }

                       """;

        Assert.NotEmpty(SpirvTestBase.One(source).Code);

        Assert.SkipUnless(ReferenceCompiler.Glslc is not null, ReferenceCompiler.HowToInstall);

        var unit = Assert.Single(GenerateClean(source));
        Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
    }
}
