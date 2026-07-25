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
        AssertDiagnostics(InMethod("        var x = flag[0]", "    val flag: bool\n"), "RVN2044");

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

            class First : Second { }

            class Second : First { }

            """,
            "RVN2007"
        );

    [Fact]
    public void Wrong_type_argument_count_is_reported() =>
        AssertDiagnostics(
            """
            package A

            class Box<T> {
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
