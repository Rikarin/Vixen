// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     The <c>switch</c> statement, which used to parse and bind and then be rejected by lowering.
/// </summary>
/// <remarks>
///     <para>
///         It desugars into an if/else chain over equality tests, so neither backend needed anything
///         new: both already emit structured <c>if</c>. SPIR-V has <c>OpSwitch</c> and GLSL has
///         <c>switch</c>, so a dedicated IR node could produce a jump table later — but it would have
///         to be built twice, and nothing a shader switches over is large enough to care.
///     </para>
///     <para>
///         The switch <em>expression</em> form went the other way and was removed with the patterns:
///         it is sugar for this plus an assignment, and neither target has an expression form. See
///         docs/plan/07 § J.
///     </para>
/// </remarks>
public class SwitchTests {
    static string Fragment(string body) =>
        GenerateOne(
            $$"""
              package A

              shader S {
                  var mode: int
                  var tint: float4

                  [FragmentShader]
                  func Fragment(): float4 {
              {{body}}
                  }
              }

              """
        );

    /// <summary>
    ///     The governing expression is evaluated once, into a local. Testing it per section would
    ///     re-run whatever produced it, and it may be a call.
    /// </summary>
    [Fact]
    public void The_governing_expression_is_evaluated_once() {
        var glsl = Fragment(
            """
                    switch (mode) {
                        case 0:
                            return tint
                        default:
                            return tint * 2f
                    }
            """
        );

        // One read of `mode`, stored to a local that the tests then read.
        Assert.Equal(1, Occurrences(glsl, "= mode;"));
        Assert.Contains("int switch_;", glsl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_case_becomes_an_equality_test_and_default_becomes_the_final_else() {
        var glsl = Fragment(
            """
                    var result = tint
                    switch (mode) {
                        case 0:
                            result = tint * 2f
                            break
                        default:
                            result = tint * 3f
                    }

                    return result
            """
        );

        Assert.Contains("== 0)", glsl, StringComparison.Ordinal);
        Assert.Contains("} else {", glsl, StringComparison.Ordinal);

        // No `switch` survives into the output: it is an if/else chain.
        Assert.DoesNotContain("switch (", glsl, StringComparison.Ordinal);
    }

    /// <summary>Several labels on one section become a disjunction of the tests.</summary>
    [Fact]
    public void Labels_sharing_a_section_are_tested_together() {
        var glsl = Fragment(
            """
                    var result = tint
                    switch (mode) {
                        case 1:
                        case 2:
                            result = tint * 3f
                            break
                        default:
                            result = tint
                    }

                    return result
            """
        );

        Assert.Contains("== 1)", glsl, StringComparison.Ordinal);
        Assert.Contains("== 2)", glsl, StringComparison.Ordinal);
        Assert.Contains(" || ", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Sections do not fall through, so a trailing <c>break</c> is redundant and dropped.
    ///     Reaching the end of the block is what leaving the switch already means.
    /// </summary>
    [Fact]
    public void A_trailing_break_is_dropped_rather_than_emitted() {
        var glsl = Fragment(
            """
                    var result = tint
                    switch (mode) {
                        case 0:
                            result = tint * 2f
                            break
                    }

                    return result
            """
        );

        Assert.DoesNotContain("break;", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Only a <em>trailing</em> break is the switch's. One inside a loop in the section still
    ///     belongs to the loop, and has to survive.
    /// </summary>
    /// <remarks>
    ///     Counted as a difference rather than an absolute, because a counted loop's own desugaring
    ///     emits a <c>break</c> for its exit test — so the absolute number says nothing on its own.
    /// </remarks>
    [Fact]
    public void A_break_inside_a_loop_in_a_section_still_belongs_to_the_loop() {
        const string WithBreak = """
                                         var result = tint
                                         switch (mode) {
                                             case 0:
                                                 for (i in 0 .. 4) {
                                                     if (i == 2) {
                                                         break
                                                     }

                                                     result = result * 2f
                                                 }

                                                 break
                                         }

                                         return result
                                 """;

        const string WithoutBreak = """
                                            var result = tint
                                            switch (mode) {
                                                case 0:
                                                    for (i in 0 .. 4) {
                                                        result = result * 2f
                                                    }

                                                    break
                                            }

                                            return result
                                    """;

        Assert.Equal(
            Occurrences(Fragment(WithoutBreak), "break;") + 1,
            Occurrences(Fragment(WithBreak), "break;")
        );
    }

    /// <summary>A switch with no default simply has no final else.</summary>
    [Fact]
    public void A_switch_without_a_default_leaves_the_value_alone() {
        var glsl = Fragment(
            """
                    var result = tint
                    switch (mode) {
                        case 0:
                            result = tint * 2f
                    }

                    return result
            """
        );

        Assert.Contains("== 0)", glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("} else {", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Each section is its own scope, so a local in one case does not leak into the next.
    /// </summary>
    [Fact]
    public void A_local_in_one_section_does_not_leak_into_the_next() {
        var glsl = Fragment(
            """
                    var result = tint
                    switch (mode) {
                        case 0:
                            val scale = 2f
                            result = tint * scale
                            break
                        case 1:
                            val scale = 3f
                            result = tint * scale
                            break
                    }

                    return result
            """
        );

        Assert.NotEmpty(glsl);
    }

    /// <summary>
    ///     The whole point of desugaring rather than adding an IR node: both backends emit it, and
    ///     both reference tools accept the result.
    /// </summary>
    [Fact]
    public void Both_backends_emit_a_switch() {
        const string Source = """
                              package A

                              shader S {
                                  var mode: int
                                  var tint: float4

                                  [FragmentShader]
                                  func Fragment(): float4 {
                                      var result = tint
                                      switch (mode) {
                                          case 0:
                                          case 1:
                                              result = tint * 2f
                                              break
                                          default:
                                              result = tint * 3f
                                      }

                                      return result
                                  }
                              }

                              """;

        // `One` puts the module through spirv-val.
        Assert.NotEmpty(SpirvTestBase.One(Source).Code);

        if (ReferenceCompiler.Glslc is null) {
            return;
        }

        var unit = Assert.Single(GenerateClean(Source));
        Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
    }

    static int Occurrences(string text, string needle) {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }
}
