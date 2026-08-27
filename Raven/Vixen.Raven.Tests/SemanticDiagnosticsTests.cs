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

    // --- Recursion ---------------------------------------------------------

    /// <summary>
    ///     A body that reaches itself is refused, and the message names the route — <c>RVN2139</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nothing reported this before, in any phase.</b> Both signatures are complete
    ///         before either body is bound, so <c>RVN2005</c> — which is about resolution that does
    ///         not terminate — never fires; lowering terminates because every pass behind the binder
    ///         carries a visited set with a comment saying the language has no recursion; and the
    ///         emitter happily writes the cycle out. What refused it was <c>spirv-val</c>, with
    ///         <c>[VUID-StandaloneSpirv-None-04634]</c>, on a machine that happened to have one
    ///         installed.
    ///     </para>
    ///     <para>
    ///         Found by <c>Vixen.Fuzz</c>'s <c>raven</c> target — the first row is the reduction of
    ///         <c>Corpus/raven/b3f413d871e6a766.bin</c>, a one-token mutation of the shipped compute
    ///         example that turned <c>float(id.x)</c> into <c>Weight(id)</c>.
    ///     </para>
    /// </remarks>
    [Theory]
    // Direct: the shape the fuzzer found, on an expression body.
    [InlineData("    func F(x: float): float => F(x) * 2f\n", "S.F → S.F")]
    // Through a second function, which is the route nobody sees by reading.
    [InlineData("    func F(x: float): float => G(x)\n    func G(x: float): float => F(x)\n", "S.F → S.G → S.F")]
    // Through a property's getter, which is a function the backend emits like any other.
    [InlineData(
        "    var P: float {\n        get => F(1f)\n    }\n\n    func F(x: float): float => P\n",
        "S.P.get → S.F → S.P.get"
    )]
    public void A_call_graph_with_a_cycle_is_refused(string members, string route) {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                $$"""
                  package A

                  shader S {
                  {{members}}
                      [ComputeShader(1, 1, 1)]
                      func Main() {
                      }
                  }

                  """,
                "RVN2139"
            )
        );

        Assert.Contains(route, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    ///     Reading a property whose <em>setter</em> closes the ring is not a cycle, and a check that
    ///     added an edge to both accessors would say it was.
    /// </summary>
    /// <remarks>
    ///     ⚠ The one over-approximation this check had to avoid. An edge per accessor is cheaper and
    ///     turns a legal shader into a hard error, which is a worse failure than the one being fixed:
    ///     an author cannot suppress it and cannot see why it is wrong.
    /// </remarks>
    [Fact]
    public void A_reference_is_an_edge_to_the_accessor_it_runs() =>
        AssertNoDiagnostics(
            """
            package A

            struct S {
                var backing: float

                var P: float {
                    get => backing
                    set => backing = F(value)
                }

                func F(x: float): float => P * x
            }

            """
        );

    /// <summary>Two functions calling one shared helper is a diamond and not a cycle.</summary>
    [Fact]
    public void A_call_graph_that_merely_reconverges_is_not_a_cycle() =>
        AssertNoDiagnostics(
            """
            package A

            shader S {
                func Shared(x: float): float => x * 2f
                func Left(x: float): float => Shared(x)
                func Right(x: float): float => Shared(x) + Left(x)

                [ComputeShader(1, 1, 1)]
                func Main() {
                    var total = Right(1f)
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

    // --- Recursive layout (RVN2008) ----------------------------------------

    /// <summary>
    ///     A struct whose storage reaches itself is <c>RVN2008</c>, and the message carries the
    ///     route rather than the name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The pre-fix behaviour was not a wrong diagnostic but no diagnostic at all</b>,
    ///         and then, for anything that actually used the type, a stack overflow in
    ///         <c>SpirvTypes.Type</c> — the CLR ending the process at the guard page with nothing
    ///         left to report it. <c>RVN2005</c> could not catch it because resolution terminates:
    ///         <c>var f: T</c> resolves to <c>T</c> in one step and is perfectly well-defined. It is
    ///         the <em>size</em> that does not exist, which is a question nothing asked until the
    ///         backend asked it.
    ///     </para>
    ///     <para>
    ///         These bind and nothing more, for the reason the <c>RVN2005</c> group above gives:
    ///         the error is in the semantic model, and taking them through codegen is what used to
    ///         kill the test host.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_struct_containing_itself_cannot_be_laid_out() {
        var diagnostics = Diagnose(
            """
            package P

            struct T {
                var f: T
            }

            """
        );

        var recursive = Assert.Single(diagnostics, d => d.Id == "RVN2008");
        Assert.Equal(DiagnosticSeverity.Error, recursive.Severity);
        Assert.Contains("P.T", recursive.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("T.f: P.T", recursive.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The case the message exists for: neither declaration is wrong by itself, so naming only
    ///     one of them would send the author to a file where nothing looks amiss.
    /// </summary>
    [Fact]
    public void A_cycle_through_a_second_struct_names_the_whole_route() {
        var diagnostics = Diagnose(
            """
            package P

            struct A {
                var b: B
            }

            struct B {
                var a: A
            }

            """
        );

        var fromA = Assert.Single(diagnostics, d => d.Id == "RVN2008" && d.GetMessage().Contains("'P.A'"));
        Assert.Contains("A.b: P.B → B.a: P.A", fromA.GetMessage(), StringComparison.Ordinal);

        // Both declarations are reported, because either one is a place the cycle can be broken.
        Assert.Contains(diagnostics, d => d.Id == "RVN2008" && d.GetMessage().Contains("'P.B'"));
    }

    /// <summary>
    ///     An array of the type is the same infinity by a different route — <c>T[4]</c> is four
    ///     <c>T</c>s laid out end to end — so the walk goes through the element type.
    /// </summary>
    [Theory]
    [InlineData("var f: T[4]", "T.f: P.T[4]")]
    [InlineData("var f: T[2][3]", "T.f: P.T[3][2]")]
    public void A_fixed_size_array_of_the_type_is_the_same_infinity(string field, string route) {
        var diagnostics = Diagnose(
            $$"""
              package P

              struct T {
                  {{field}}
              }

              """
        );

        var recursive = Assert.Single(diagnostics, d => d.Id == "RVN2008");
        Assert.Contains(route, recursive.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>An array of a struct that contains one is caught for the same reason.</summary>
    [Fact]
    public void An_array_of_a_struct_that_contains_the_type_is_caught() {
        var diagnostics = Diagnose(
            """
            package P

            struct A {
                var b: B[2]
            }

            struct B {
                var a: A
            }

            """
        );

        var fromA = Assert.Single(diagnostics, d => d.Id == "RVN2008" && d.GetMessage().Contains("'P.A'"));
        Assert.Contains("A.b: P.B[2] → B.a: P.A", fromA.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A generic that holds itself has no fixed point to stop monomorphisation at, whether the
    ///     argument repeats or grows — so the comparison is on the definition rather than on the
    ///     constructed type.
    /// </summary>
    [Theory]
    [InlineData("var next: Node<T>")]
    [InlineData("var next: Node<Node<T>>")]
    public void A_generic_struct_that_holds_itself_is_caught(string field) {
        var diagnostics = Diagnose(
            $$"""
              package P

              struct Node<T> {
                  {{field}}
              }

              """
        );

        Assert.Single(diagnostics, d => d.Id == "RVN2008");
    }

    /// <summary>
    ///     Reaching yourself through somebody else's type argument. Only the <em>substituted</em>
    ///     member closes this — <c>B</c>'s field reads <c>T</c> in its own declaration — which is
    ///     why the walk reads the constructed type's members rather than the definition's.
    /// </summary>
    [Fact]
    public void A_cycle_through_a_type_argument_is_caught() {
        var diagnostics = Diagnose(
            """
            package P

            struct A {
                var b: B<A>
            }

            struct B<T> {
                var t: T
            }

            """
        );

        var recursive = Assert.Single(diagnostics, d => d.Id == "RVN2008");
        Assert.Contains("A.b: P.B<P.A> → B.t: P.A", recursive.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A value's storage is its bases' fields then its own, so a base whose field is of the
    ///     derived type is the same cycle — and it is not <c>RVN2007</c>, because nothing here
    ///     inherits from itself.
    /// </summary>
    [Fact]
    public void A_cycle_closed_through_an_inherited_field_is_caught() {
        var diagnostics = Diagnose(
            """
            package P

            struct A: B {
            }

            struct B {
                var a: A
            }

            """
        );

        var recursive = Assert.Single(diagnostics, d => d.Id == "RVN2008");
        Assert.Contains("'P.A'", recursive.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Id == "RVN2007");
    }

    /// <summary>
    ///     ⚠ <b>What must not be caught.</b> Nesting one struct inside two others, and instantiating
    ///     a generic with an instantiation of itself, are both finite — the check has to distinguish
    ///     "reaches the same definition twice on one path" from "reaches it twice in the module".
    /// </summary>
    /// <remarks>
    ///     Raven has no pointer and no reference, so there is no legal self-reference to admit here.
    ///     The nearest shape is <c>Buffer&lt;T&gt;</c>, which is an indirection — but it is a
    ///     descriptor, and a descriptor may only be a shader field (<c>RVN2053</c>), so a struct can
    ///     never hold one in the first place.
    /// </remarks>
    [Fact]
    public void A_type_reached_twice_by_different_routes_is_not_a_cycle() {
        AssertNoDiagnostics(
            """
            package P

            struct Leaf {
                var x: float
            }

            struct Box<T> {
                var value: T
            }

            struct Top {
                var direct: Leaf
                var boxed: Box<Leaf>
                var twice: Box<Box<Leaf>>
                var many: Leaf[4]
            }

            """
        );
    }

    /// <summary>
    ///     A member taken of a method group is reported rather than swallowed — <c>RVN2011</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A method group is the one receiver typed as an error without an error having
    ///         been reported</b>, so the guard that suppresses a cascade from an already-reported
    ///         receiver used to swallow it — the same shape as <c>RVN2030</c> and a namespace, and
    ///         found the same way. <c>min.y</c> is one token away from <c>scale.y</c>, and it
    ///         compiled with nothing reported at all: the <c>val</c> took a void type, the multiply
    ///         around it took a void result, and the SPIR-V emitter wrote <c>OpFMul %void</c>.
    ///     </para>
    ///     <para>
    ///         <c>Corpus/raven/431a0d0b2f2420d6.bin</c>, <c>dc5d446954c9cf71.bin</c> and
    ///         <c>f6f5082753f2dd15.bin</c> are the three nightly findings this one line closes, and
    ///         the first two are the pair that named the fault: <c>OpFMul</c> with a non-float
    ///         result and <c>OpIMul</c> with a non-int one, from the same expression, which is what
    ///         says the opcode came from the operands and the result type from nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_member_of_a_method_group_is_reported() {
        var diagnostic = Assert.Single(AssertDiagnostics(InMethod("        var x = min.y"), "RVN2011"));

        Assert.Contains("min", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("'y'", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>An empty collection literal has no element type to infer — <c>RVN2140</c>.</summary>
    /// <remarks>
    ///     ⚠ Every position that asks what <c>[]</c> is already rejected it, which is why this took
    ///     a fuzzer to find: the survivor was <c>[]</c> as an expression statement, where nothing
    ///     asks. It bound to <c>?[0]</c>, and the emitter wrote <c>OpCompositeConstruct %void</c> —
    ///     <c>Corpus/raven/9352e56acef97227.bin</c>.
    /// </remarks>
    [Fact]
    public void An_empty_collection_literal_is_reported() {
        var diagnostic = Assert.Single(AssertDiagnostics(InMethod("        []"), "RVN2140"));

        Assert.Contains("[]", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    // --- RVN2141: an expression statement that cannot do anything -----------

    /// <summary>
    ///     Each form that stands alone as a statement, evaluates, and leaves nothing behind.
    /// </summary>
    /// <remarks>
    ///     ⚠ The <c>Probe</c> body is one statement, so this is the whole method: an expression
    ///     statement is the only position in the language where nothing asks what a value is for,
    ///     which is the same hole <c>RVN2140</c> came out of.
    /// </remarks>
    [Theory]
    [InlineData("        v")]
    [InlineData("        v + 1f")]
    [InlineData("        v == 1f")]
    [InlineData("        -v")]
    [InlineData("        float3(v, v, v)")]
    [InlineData("        v > 0f ? 1f : 2f")]
    public void An_expression_statement_that_does_nothing_is_reported(string body) =>
        Assert.Single(AssertDiagnostics(InMethod(body, "    val v: float\n"), "RVN2141"));

    /// <summary>
    ///     The layout that made this a miscompile rather than a curiosity: a sum broken over two
    ///     lines with the operator leading the second.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is <c>Raven/Library/Pipeline/ClusterRaster.rvn</c> before <c>852bcca0</c>,
    ///         and <c>Terrain/GrassScatter.rvn</c> and <c>Terrain/Impostor.rvn</c> before this
    ///         commit.</b> A newline ends a statement (README § Line breaks), so the two lines are
    ///         <c>total = total</c> — a legal assignment that stores what it just loaded — and a
    ///         unary <c>+</c> nobody reads. Every one of the three compiled clean, dispatched, and
    ///         produced geometry that did not move: the shipped <c>GrassScatter.comp.spv</c> holds
    ///         the jitter term as an <c>OpFMul</c> with no consumer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mirror layout was already an error, which is what hid this one.</b>
    ///         Trailing the operator is <c>RVN1001</c>, "expected an expression, found end of
    ///         line" — so the arrangement an author reaches for once that is refused is the one
    ///         that said nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_sum_continued_onto_the_next_line_is_reported() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                InMethod(
                    """
                            var total = 0f
                            total = total
                                + v
                    """,
                    "    val v: float\n"
                ),
                "RVN2141"
            )
        );

        Assert.Contains("newline ends a statement", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A range whose ends share no type is reported rather than defaulted to <c>int</c>.
    /// </summary>
    /// <remarks>
    ///     The companion to <c>SpirvValidationTests</c>'s converted-endpoints row: where a common
    ///     type exists both ends are converted to it, and where none does the range is not quietly
    ///     given an element type neither end could produce.
    /// </remarks>
    [Fact]
    public void A_range_whose_ends_have_no_common_type_is_reported() {
        Assert.Single(AssertDiagnostics(InMethod("        for (i in true .. 4) {\n        }"), "RVN2020"));
    }

    // --- RVN2054: a member written straight into a file ---------------------

    /// <summary>
    ///     A <c>func</c>, a <c>const val</c>, a <c>var</c>, an <c>init</c> and an <c>operator</c>,
    ///     each written at package level, each named for what it is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ Every one of these parsed clean and reported nothing at all before <c>RVN2054</c>.
    ///         The compilation unit and a type body share one <c>ParseMemberDeclaration</c>, so the
    ///         syntax is real; <c>Compilation.EnsureDeclarations</c> kept the members
    ///         <c>TypeDeclarationInfo.From</c> yields a type for and dropped these without a word.
    ///         The body was never bound, so an undefined name inside it was silent too — see
    ///         <see cref="A_body_at_package_level_is_reported_at_the_declaration_not_inside_it" />.
    ///     </para>
    ///     <para>
    ///         An error rather than a warning: a namespace in this language holds namespaces and
    ///         types and nothing else, so a declaration here is not merely unusual, it is
    ///         unreachable — a call to it is <c>RVN2010</c> at the call site, which points at the
    ///         one line that was right.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("func Luminance(c: float3): float {\n    return c.x\n}", "Luminance", "function")]
    [InlineData("const val Bias = 0.5f", "Bias", "value")]
    [InlineData("var counter: int", "counter", "variable")]
    [InlineData("init() {\n}", "init", "constructor")]
    [InlineData("var exposure: float {\n    get => 1f\n}", "exposure", "property")]
    // ⚠ Every arm of the naming switch is exercised, this one included: they are reachable
    // because the parser reaches them, not because a grammar for files was ever written.
    [InlineData("float operator +(a: float, b: float) {\n    return a\n}", "+", "operator")]
    public void A_member_at_package_level_is_reported(string member, string name, string kind) {
        var diagnostic = Assert.Single(
            AssertDiagnostics($"package A\n\n{member}\n\nshader S {{\n}}\n", "RVN2054")
        );

        Assert.True(diagnostic.IsError);
        Assert.Contains(name, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains(kind, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The report lands on the declaration, and the body it would have had is not bound.
    /// </summary>
    /// <remarks>
    ///     This is the half that was the bug rather than the half that is the rule. The undefined
    ///     name below is what the author would have been told about had the function been anywhere
    ///     the language can hold one; here they are told the function itself has nowhere to be,
    ///     which is the thing to fix first and the only thing worth saying twice.
    /// </remarks>
    [Fact]
    public void A_body_at_package_level_is_reported_at_the_declaration_not_inside_it() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                """
                package A

                func Luminance(c: float3): float {
                    return nrmalize(c).x
                }

                shader S {
                }

                """,
                "RVN2054"
            )
        );

        Assert.Contains("Luminance", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A shader, a struct, a protocol and an enum are what a file <em>does</em> hold, and a
    ///     member inside any of them is where a member goes.
    /// </summary>
    [Fact]
    public void The_four_type_declarations_are_not_reported() =>
        CompileClean(
            """
            package A

            enum Mode {
                Flat,
                Lit
            }

            protocol IFeature {
                func F(): float
            }

            struct Vertex {
                var uv: float2
            }

            shader S: IFeature {
                const val Bias = 0.5f

                func F(): float {
                    return Bias
                }
            }

            """
        );

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
