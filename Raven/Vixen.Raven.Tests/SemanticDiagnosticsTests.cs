// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Diagnostics;
using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>Phase 2b: malformed programs produce targeted semantic errors.</summary>
public class SemanticDiagnosticsTests {
    [Fact]
    public void Undefined_name_is_reported() {
        var diagnostic = Assert.Single(AssertDiagnostics(InMethod("        var x = missing"), "RVN2010"));
        Assert.Contains("missing", diagnostic.GetMessage());
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void Unknown_type_is_reported_once() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                """
                package A

                shader S {
                    val value: Missing
                }

                """,
                "RVN2002"
            )
        );

        Assert.Contains("Missing", diagnostic.GetMessage());
    }

    [Fact]
    public void Unknown_member_names_the_receiver_type() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(InMethod("        var x = v.missing", "    val v: float3\n"), "RVN2011")
        );

        Assert.Contains("float3", diagnostic.GetMessage());
        Assert.Contains("missing", diagnostic.GetMessage());
    }

    [Fact]
    public void Implicit_narrowing_is_rejected() {
        var diagnostic = Assert.Single(AssertDiagnostics(InMethod("        val narrow: int = 1.5"), "RVN2020"));
        Assert.Contains("'double' to 'int'", diagnostic.GetMessage());
    }

    [Fact]
    public void Undefined_operator_names_both_operand_types() {
        var diagnostic = Assert.Single(AssertDiagnostics(InMethod("        var x = true - 1"), "RVN2022"));

        Assert.Contains("'-'", diagnostic.GetMessage());
        Assert.Contains("bool", diagnostic.GetMessage());
    }

    [Fact]
    public void Non_bool_condition_is_rejected() =>
        AssertDiagnostics(
            InMethod(
                """
                        if (1) {
                        }
                """
            ),
            "RVN2024"
        );

    [Fact]
    public void Calling_a_non_method_is_rejected() =>
        AssertDiagnostics(InMethod("        v()", "    val v: float3\n"), "RVN2030");

    [Fact]
    public void No_applicable_overload_lists_the_argument_types() {
        var diagnostic = Assert.Single(AssertDiagnostics(InMethod("        var x = dot(1, true)"), "RVN2031"));

        Assert.Contains("dot", diagnostic.GetMessage());
    }

    [Fact]
    public void Wrong_argument_count_is_reported() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                func Take(value: int) { }

                func Use() {
                    Take()
                }
            }

            """,
            "RVN2033"
        );

    [Fact]
    public void Assigning_to_a_val_is_rejected() =>
        AssertDiagnostics(
            InMethod(
                """
                        val fixed = 1
                        fixed = 2
                """
            ),
            "RVN2040"
        );

    [Fact]
    public void Assigning_to_a_getter_only_property_is_rejected() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                var backing: int

                var readable: int {
                    get => backing
                }

                func Use() {
                    readable = 1
                }
            }

            """,
            "RVN2040"
        );

    [Fact]
    public void Returning_a_value_from_a_void_method_is_rejected() =>
        AssertDiagnostics(InMethod("        return 1"), "RVN2042");

    [Fact]
    public void Returning_nothing_from_a_typed_method_is_rejected() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                func Get(): int {
                    return
                }
            }

            """,
            "RVN2043"
        );

    [Fact]
    public void Indexing_a_non_indexable_value_is_rejected() =>
        // groupshared, because a plain `val flag: bool` is a binding and a binding cannot hold a
        // boolean (RVN2137) — which would be a second diagnostic this test is not about.
        AssertDiagnostics(InMethod("        var x = flag[0]", "    groupshared var flag: bool\n"), "RVN2044");

    [Fact]
    public void Iterating_a_non_sequence_is_rejected() =>
        AssertDiagnostics(
            InMethod(
                """
                        for (i in 42) {
                        }
                """
            ),
            "RVN2045"
        );

    [Fact]
    public void Duplicate_members_are_reported_on_the_second_declaration() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                val value: int
                val value: float
            }

            """,
            "RVN2001"
        );

    [Fact]
    public void Duplicate_method_signatures_are_reported_but_overloads_are_not() {
        AssertNoDiagnostics(
            """
            package A

            shader S {
                func Take(value: int) { }
                func Take(value: float) { }
            }

            """
        );

        AssertDiagnostics(
            """
            package A

            shader S {
                func Take(value: int) { }
                func Take(value: int) { }
            }

            """,
            "RVN2001"
        );
    }

    [Fact]
    public void Duplicate_locals_are_reported() =>
        AssertDiagnostics(
            InMethod(
                """
                        val x = 1
                        val x = 2
                """
            ),
            "RVN2001"
        );

    [Fact]
    public void A_field_with_neither_type_nor_initializer_is_reported() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                val lonely
            }

            """,
            "RVN2006"
        );

    [Fact]
    public void Cyclic_inheritance_is_reported_rather_than_looping() =>
        AssertDiagnostics(
            """
            package A

            struct First : Second { }

            struct Second : First { }

            """,
            "RVN2007"
        );

    [Fact]
    public void Wrong_type_argument_count_is_reported() =>
        AssertDiagnostics(
            """
            package A

            struct Box<T> {
                val value: T
            }

            shader S {
                val bad: Box<int, float>
            }

            """,
            "RVN2004"
        );

    [Fact]
    public void A_type_used_as_a_value_is_reported() =>
        AssertDiagnostics(InMethod("        var x = float3 + 1"), "RVN2013");

    [Fact]
    public void Self_outside_a_type_would_be_reported() =>
        // `base` in a type with no base type is the reachable form of this check.
        AssertDiagnostics(
            """
            package A

            shader S {
                func Use() {
                    var x = base
                }
            }

            """,
            "RVN2015"
        );

    [Fact]
    public void One_mistake_produces_one_diagnostic() {
        // The error type absorbs downstream uses instead of cascading.
        var diagnostics = Diagnose(
            InMethod(
                """
                        var x = missing
                        var y = x + 1
                        var z = y * 2f
                """
            )
        );

        Assert.Single(diagnostics);
    }

    // --- Bindings that a storage class cannot hold -------------------------
    //
    // All three found by the `raven` fuzz target against spirv-val, from one-token edits of
    // Example2.rvn. Each compiled with nothing reported and emitted a module the validator
    // rejected — which is the class of defect no crash-finder finds, because nothing crashed.

    /// <summary>
    ///     A boolean binding is refused — <c>RVN2137</c>.
    /// </summary>
    /// <remarks>
    ///     The corpus input is <c>[D] val UseSoftKnee: bool = true</c>: an unrecognised attribute
    ///     where <c>[Permutation]</c> was written, so the key stopped being folded and became a
    ///     uniform. SPIR-V allows <c>OpTypeBool</c> only in storage classes that are not externally
    ///     visible, and the emitted module said
    ///     <c>%…PerMaterialUniforms = OpVariable %… Uniform</c> over a block containing one.
    /// </remarks>
    [Fact]
    public void A_boolean_binding_is_refused() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(InMethod("        var x = flag", "    var flag: bool\n"), "RVN2137")
        );

        Assert.Contains("flag", diagnostic.GetMessage());
        Assert.Contains("[Permutation]", diagnostic.GetMessage());
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    ///     A boolean reached through a struct or an array is refused too.
    /// </summary>
    /// <remarks>
    ///     The validator's complaint is about the block, and a <c>bool</c> reaches the block as a
    ///     member just as easily as by being the field's own type. Checking only the field's type
    ///     would have left the same invalid module one <c>struct</c> away.
    /// </remarks>
    [Theory]
    [InlineData("    var flags: Flags\n")]
    [InlineData("    var flags: Flags[4]\n")]
    public void A_boolean_inside_a_bindings_type_is_refused(string members) =>
        AssertDiagnostics(
            $$"""
              package A

              struct Flags {
                  var enabled: bool
              }

              shader S {
              {{members}}
                  func Probe() {
                  }
              }

              """,
            "RVN2137"
        );

    /// <summary>
    ///     The two places a boolean is still legal: a <c>[Permutation]</c> key and
    ///     <c>groupshared</c>.
    /// </summary>
    /// <remarks>
    ///     Worth asserting rather than assuming, because the shipped library holds some thirty
    ///     boolean permutation keys and a rule written one predicate too wide would have refused
    ///     every one of them. A key is folded at every use and never reaches a storage class;
    ///     workgroup storage is one of the classes SPIR-V names as allowed.
    /// </remarks>
    [Theory]
    [InlineData("    [Permutation] val Flag: bool = true\n")]
    [InlineData("    groupshared var flag: bool\n")]
    public void A_boolean_that_is_not_a_binding_is_allowed(string members) =>
        AssertNoDiagnostics(
            $$"""
              package A

              shader S {
              {{members}}
                  func Probe() {
                  }
              }

              """
        );

    // --- Attributes --------------------------------------------------------

    /// <summary>
    ///     An attribute the compiler does not read is named rather than dropped — <c>RVN2138</c>.
    /// </summary>
    /// <remarks>
    ///     A warning, and the reason it is worth one is the corpus input above: dropping an
    ///     attribute in silence does not merely fail to add something, it changes what the
    ///     declaration <em>is</em>. <c>[D]</c> where <c>[Permutation]</c> was meant turns a
    ///     compile-time key into a uniform, and every variant of the shader collapses into one.
    /// </remarks>
    [Fact]
    public void An_unrecognised_attribute_is_reported() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                """
                package A

                shader S {
                    [Permuation] val Flag: uint = 1u

                    func Probe() {
                    }
                }

                """,
                "RVN2138"
            )
        );

        Assert.Contains("Permuation", diagnostic.GetMessage());
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    /// <summary>An attribute on a parameter is swept too, which is where a [Semantic] is written.</summary>
    [Fact]
    public void An_unrecognised_attribute_on_a_parameter_is_reported() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                func Probe([Semantics("POSITION")] position: float4) {
                }
            }

            """,
            "RVN2138"
        );

    /// <summary>Every name the compiler does read stays quiet.</summary>
    [Fact]
    public void The_recognised_attributes_are_not_reported() =>
        AssertNoDiagnostics(
            """
            package A

            shader S {
                [Permutation] val Flag: bool = true
                [PerFrame] var time: float
                [PushConstant] var offset: float4

                [ComputeShader(8, 8, 1)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                }
            }

            """
        );

    // --- Calls -------------------------------------------------------------

    /// <summary>
    ///     Calling a namespace is reported — <c>RVN2030</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The second corpus input, <c>val over = Vixen(1, 1, 1, 1)</c>. It bound with nothing
    ///         reported because a namespace answers <c>ErrorTypeSymbol</c> when asked for its type —
    ///         it has no type, and there is nothing else to answer — so <c>BindInvocation</c>'s
    ///         guard against reporting a callee something has <em>already</em> complained about read
    ///         it as one.
    ///     </para>
    ///     <para>
    ///         What came out the far end was a <c>val</c> bound to a void-typed value and
    ///         <c>OpConstantNull %void</c>, which is invalid however it is reached — <c>void</c> is
    ///         the one type with no null value.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Calling_a_namespace_is_reported() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                """
                package Vixen.Test

                shader S {
                    func Probe() {
                        val over = Vixen(1, 1, 1, 1)
                    }
                }

                """,
                "RVN2030"
            )
        );

        Assert.Contains("Vixen", diagnostic.GetMessage());
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    ///     A signature that reaches its own type through an array size is <c>RVN2005</c>, not a
    ///     stack overflow.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ **These bind and nothing more.** <see cref="SemanticTestBase.Diagnose" /> stops at
    ///         the semantic model, which is where the cycle lives; taking them through codegen would
    ///         add nothing and the pre-fix behaviour was not a failing assertion but the CLR ending
    ///         the process at the guard page, with no thread left to report anything. That is also
    ///         why the fuzz harness found this and could not write it down.
    ///     </para>
    ///     <para>
    ///         Four routes rather than one, because the guard is keyed by the symbol and so closes
    ///         the family: a return type, a parameter type, two signatures sizing arrays by each
    ///         other, and a <c>val</c> parameter sizing its own type. All four went to the guard
    ///         page before, and all four now name the symbol they are circular through.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_return_type_sized_by_its_own_call_is_circular() {
        var diagnostics = Diagnose(
            """
            package P

            shader S {
                func F(): float[F()] {
                    return 1f
                }
            }

            """
        );

        var circular = Assert.Single(diagnostics, d => d.Id == "RVN2005");
        Assert.Contains("F", circular.GetMessage());
        Assert.Equal(DiagnosticSeverity.Error, circular.Severity);
    }

    [Fact]
    public void A_parameter_type_sized_by_its_own_call_is_circular() {
        var diagnostics = Diagnose(
            """
            package P

            shader S {
                func F(x: float[F(1f)]): float {
                    return 1f
                }
            }

            """
        );

        var circular = Assert.Single(diagnostics, d => d.Id == "RVN2005");
        Assert.Contains("x", circular.GetMessage());
    }

    [Fact]
    public void Two_return_types_sized_by_each_other_are_circular() {
        var diagnostics = Diagnose(
            """
            package P

            shader S {
                func A(): float[B()] {
                    return 1f
                }

                func B(): float[A()] {
                    return 1f
                }
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2005");
    }

    [Fact]
    public void A_value_parameter_sized_by_itself_is_circular() {
        var diagnostics = Diagnose(
            """
            package P

            shader S<val N: int[N]> {
                [FragmentShader]
                func Main(): float4 {
                    return float4(0f, 0f, 0f, 1f)
                }
            }

            """
        );

        var circular = Assert.Single(diagnostics, d => d.Id == "RVN2005");
        Assert.Contains("N", circular.GetMessage());
    }

    /// <summary>Wraps a method body in a shader so error cases stay readable.</summary>
    static string InMethod(string body, string members = "") =>
        $$"""
          package A

          shader S {
          {{members}}
              func Probe() {
          {{body}}
              }
          }

          """;
}
