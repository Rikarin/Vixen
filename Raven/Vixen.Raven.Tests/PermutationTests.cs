// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax.Diagnostics;
using static Tests.SemanticTestBase;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     <c>[Permutation]</c> keys: constants whose values arrive from outside the source,
///     one set per effect variant. A branch on a key folds away, so a variant compiles to
///     only the code it uses.
/// </summary>
public class PermutationTests {
    const string Skinned = """
                           package A

                           shader Lit {
                               [Permutation] val UseSkinning: bool = false
                               [Permutation] val TapCount: int = 4

                               var tint: float4

                               func Shade(): float4 {
                                   if (UseSkinning) {
                                       return tint * 2.0f
                                   }

                                   return tint
                               }
                           }

                           """;

    static (Compilation Compilation, IrModule Module) LowerWith(string source, PermutationValues values) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", values, [tree]);
        var semantic = compilation.GetDiagnostics();
        Assert.True(
            semantic.Count == 0,
            "Expected no semantic diagnostics, got:\n" + string.Join("\n", semantic.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.Empty(bag.ToArray());

        return (compilation, module);
    }

    static IReadOnlyList<Diagnostic> DiagnosticsWith(string source, PermutationValues values) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        return Compilation.Create("Test", values, [tree]).GetDiagnostics();
    }

    static FieldSymbol Key(Compilation compilation, string shader, string name) =>
        Assert.Single(FindType(compilation, shader).GetMembers(name).OfType<FieldSymbol>());

    // --- The key is a constant ---------------------------------------------

    [Fact]
    public void A_key_takes_its_declared_default_when_no_value_is_supplied() {
        var (compilation, _) = LowerWith(Skinned, PermutationValues.Empty);

        Assert.Equal(false, Key(compilation, "Lit", "UseSkinning").ConstantValue);
        Assert.Equal(4, Key(compilation, "Lit", "TapCount").ConstantValue);
    }

    [Fact]
    public void A_supplied_value_replaces_the_default() {
        var values = PermutationValues.Create([new("UseSkinning", true), new("TapCount", 9)]);
        var (compilation, _) = LowerWith(Skinned, values);

        Assert.Equal(true, Key(compilation, "Lit", "UseSkinning").ConstantValue);
        Assert.Equal(9, Key(compilation, "Lit", "TapCount").ConstantValue);
    }

    /// <summary>
    ///     A key is const to everything downstream, so it is not a uniform — it must not
    ///     take a constant-buffer slot in a variant that hard-codes it.
    /// </summary>
    [Fact]
    public void A_key_is_constant_and_not_a_uniform() {
        var (compilation, _) = LowerWith(Skinned, PermutationValues.Empty);
        var key = Key(compilation, "Lit", "UseSkinning");

        Assert.True(key.IsPermutation);
        Assert.True(key.IsConst);
        Assert.True(key.IsReadOnly);
        Assert.Equal(ResourceKind.None, key.ResourceKind);

        // A plain shader field of the same shape still is a uniform.
        Assert.Equal(ResourceKind.Uniform, Key(compilation, "Lit", "tint").ResourceKind);
    }

    // --- Folding removes code ---------------------------------------------

    [Fact]
    public void A_branch_on_a_false_key_is_not_emitted() {
        var (_, module) = LowerWith(Skinned, PermutationValues.Empty);
        var body = PrintFunction(module, "Shade");

        // No branch survives, and neither does the multiply that was inside it.
        Assert.DoesNotContain("if", body, StringComparison.Ordinal);
        Assert.DoesNotContain("mul", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_branch_is_emitted_when_the_key_is_true() {
        var values = PermutationValues.Create([new("UseSkinning", true)]);
        var (_, module) = LowerWith(Skinned, values);
        var body = PrintFunction(module, "Shade");

        Assert.DoesNotContain("if", body, StringComparison.Ordinal);
        Assert.Contains("mul", body, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two variants must differ, otherwise the fold is not doing anything and the
    ///     assertions above would pass for the wrong reason.
    /// </summary>
    [Fact]
    public void The_two_variants_lower_to_different_code() {
        var (_, off) = LowerWith(Skinned, PermutationValues.Empty);
        var (_, on) = LowerWith(Skinned, PermutationValues.Create([new("UseSkinning", true)]));

        Assert.NotEqual(PrintFunction(off, "Shade"), PrintFunction(on, "Shade"));
    }

    [Fact]
    public void A_non_constant_condition_still_emits_a_branch() {
        var (_, module) = LowerWith(
            """
            package A

            shader S {
                var factor: float

                func Probe(): float {
                    if (factor > 1.0f) {
                        return 2.0f
                    }

                    return 1.0f
                }
            }

            """,
            PermutationValues.Empty
        );

        Assert.Contains("if", PrintFunction(module, "Probe"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_while_false_loop_is_dropped_but_while_true_is_kept() {
        var (_, dropped) = LowerWith(
            """
            package A

            shader S {
                const val Never: bool = false

                func Probe(): int {
                    var i = 0
                    while (Never) {
                        i = i + 1
                    }

                    return i
                }
            }

            """,
            PermutationValues.Empty
        );

        Assert.DoesNotContain("loop", PrintFunction(dropped, "Probe"), StringComparison.Ordinal);
    }

    // --- UsedPermutationKeys ----------------------------------------------

    /// <summary>
    ///     The whole point: the cache key is the set of keys that mattered, not the set
    ///     declared. A shader with twenty flags has a million define combinations but any
    ///     one entry point reads a handful.
    /// </summary>
    [Fact]
    public void Only_keys_that_were_read_are_reported_as_used() {
        var (compilation, _) = LowerWith(Skinned, PermutationValues.Empty);

        // TapCount is declared but never mentioned in a body.
        Assert.Equal(["UseSkinning"], compilation.UsedPermutationKeys);
    }

    [Fact]
    public void Reading_a_second_key_adds_it() {
        var (compilation, _) = LowerWith(
            """
            package A

            shader S {
                [Permutation] val First: bool = false
                [Permutation] val Second: bool = false
                [Permutation] val Unused: bool = true

                func Probe(): int {
                    if (First) {
                        return 1
                    }

                    if (Second) {
                        return 2
                    }

                    return 3
                }
            }

            """,
            PermutationValues.Empty
        );

        Assert.Equal(["First", "Second"], compilation.UsedPermutationKeys);
    }

    /// <summary>
    ///     A read that folding made unreachable does not count. With <c>First</c> true the
    ///     method returns before <c>Second</c> is ever consulted, so the output does not
    ///     depend on it — and the variants that differ only in <c>Second</c> share a cache
    ///     entry, which is exactly the collapsing this set exists to enable.
    /// </summary>
    [Fact]
    public void A_read_made_unreachable_by_folding_does_not_count() {
        const string Source = """
                              package A

                              shader S {
                                  [Permutation] val First: bool = true
                                  [Permutation] val Second: bool = false

                                  func Probe(): int {
                                      if (First) {
                                          return 1
                                      }

                                      if (Second) {
                                          return 2
                                      }

                                      return 3
                                  }
                              }

                              """;

        var (compilation, off) = LowerWith(Source, PermutationValues.Empty);
        Assert.Equal(["First"], compilation.UsedPermutationKeys);

        var (_, on) = LowerWith(Source, PermutationValues.Create([new("Second", true)]));
        Assert.Equal(PrintFunction(off, "Probe"), PrintFunction(on, "Probe"));
    }

    [Fact]
    public void Code_after_a_folded_return_is_not_emitted() {
        var (_, module) = LowerWith(Skinned, PermutationValues.Create([new("UseSkinning", true)]));
        var body = PrintFunction(module, "Shade");

        // `if (UseSkinning) { return … }` folds to a bare return, so the fall-through
        // `return tint` below it is unreachable and must not be emitted.
        Assert.Equal(1, body.Split("ret").Length - 1);
    }

    [Fact]
    public void Supplying_an_unread_key_does_not_make_it_used() {
        var values = PermutationValues.Create([new("TapCount", 8)]);
        var (compilation, _) = LowerWith(Skinned, values);

        Assert.DoesNotContain("TapCount", compilation.UsedPermutationKeys);
    }

    /// <summary>
    ///     Two variants differing only in an unread key produce identical code, which is
    ///     the property that makes the reported set safe to cache on.
    /// </summary>
    [Fact]
    public void Varying_an_unread_key_produces_identical_code() {
        var (_, four) = LowerWith(Skinned, PermutationValues.Create([new("TapCount", 4)]));
        var (_, eight) = LowerWith(Skinned, PermutationValues.Create([new("TapCount", 8)]));

        Assert.Equal(PrintFunction(four, "Shade"), PrintFunction(eight, "Shade"));
    }

    [Fact]
    public void A_key_no_shader_declares_is_ignored() {
        // The engine passes a whole effect's settings to every module it compiles.
        var values = PermutationValues.Create([new("SomeOtherShadersFlag", true)]);
        var (compilation, _) = LowerWith(Skinned, values);

        Assert.DoesNotContain("SomeOtherShadersFlag", compilation.UsedPermutationKeys);
    }

    // --- Validation --------------------------------------------------------

    [Fact]
    public void A_permutation_outside_a_shader_is_rejected() =>
        AssertDiagnostics(
            """
            package A

            struct S {
                [Permutation] val Flag: bool = false
            }

            """,
            "RVN2060"
        );

    [Fact]
    public void A_mutable_permutation_is_rejected() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                [Permutation] var Flag: bool = false
            }

            """,
            "RVN2061"
        );

    [Theory]
    [InlineData("float", "1.0f")]
    [InlineData("double", "1.0")]
    public void A_permutation_of_an_unsupported_type_is_rejected(string type, string literal) =>
        AssertDiagnostics(
            $$"""
              package A

              shader S {
                  [Permutation] val Key: {{type}} = {{literal}}
              }

              """,
            "RVN2062"
        );

    [Fact]
    public void A_permutation_without_a_default_is_rejected() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                [Permutation] val Flag: bool
            }

            """,
            "RVN2063"
        );

    [Fact]
    public void A_supplied_value_of_the_wrong_type_is_rejected() {
        var diagnostics = DiagnosticsWith(
            """
            package A

            shader S {
                [Permutation] val Flag: bool = false
            }

            """,
            PermutationValues.Create([new("Flag", 3)])
        );

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RVN2064", diagnostic.Id);
        Assert.Contains("Flag", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void Assigning_to_a_permutation_is_rejected_with_a_reason() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                """
                package A

                shader S {
                    [Permutation] val Flag: bool = false

                    func Probe() {
                        Flag = true
                    }
                }

                """,
                "RVN2065"
            )
        );

        Assert.Contains("fixed when the shader is compiled", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A switched-off permutation is still bound, so its code is still type-checked.
    ///     This is the main thing this has over textual <c>#if</c>: a variant you are not
    ///     currently building cannot quietly rot.
    /// </summary>
    [Fact]
    public void Code_in_a_dead_branch_is_still_type_checked() =>
        AssertDiagnostics(
            """
            package A

            shader S {
                [Permutation] val Flag: bool = false

                func Probe(): int {
                    if (Flag) {
                        return misspelled
                    }

                    return 0
                }
            }

            """,
            "RVN2010"
        );
}

/// <summary>Parsing and validating the value set itself.</summary>
public class PermutationValuesTests {
    [Fact]
    public void Empty_supplies_nothing() {
        Assert.Equal(0, PermutationValues.Empty.Count);
        Assert.False(PermutationValues.Empty.Contains("Anything"));
    }

    [Theory]
    [InlineData("Flag", true)]
    [InlineData("Flag=true", true)]
    [InlineData("Flag=false", false)]
    public void A_bare_name_means_true(string define, bool expected) =>
        Assert.Equal(expected, PermutationValues.Parse([define]).GetValueOrDefault("Flag"));

    [Fact]
    public void Numbers_parse_as_int() =>
        Assert.Equal(8, PermutationValues.Parse(["TapCount=8"]).GetValueOrDefault("TapCount"));

    [Fact]
    public void Whitespace_around_a_define_is_trimmed() =>
        Assert.Equal(2, PermutationValues.Parse([" TapCount = 2 "]).GetValueOrDefault("TapCount"));

    [Fact]
    public void Blank_entries_are_skipped() => Assert.Equal(0, PermutationValues.Parse(["", "   "]).Count);

    [Fact]
    public void A_malformed_define_is_rejected() {
        Assert.Throws<ArgumentException>(() => PermutationValues.Parse(["Key=not-a-number"]));
        Assert.Throws<ArgumentException>(() => PermutationValues.Parse(["=4"]));
    }

    [Fact]
    public void An_unsupported_value_type_is_rejected_at_the_boundary() =>
        Assert.Throws<ArgumentException>(() => PermutationValues.Create([new("Key", 1.5)]));

    [Fact]
    public void A_later_entry_wins() =>
        Assert.Equal(2, PermutationValues.Parse(["K=1", "K=2"]).GetValueOrDefault("K"));
}
