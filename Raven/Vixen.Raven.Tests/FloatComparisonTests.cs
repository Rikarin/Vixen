// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.CodeGenTestBase;
using static Tests.SpirvTestBase;

namespace Tests;

/// <summary>
///     What <c>==</c> and <c>!=</c> on floats mean when an operand is a NaN, per backend.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Raven's two backends do not agree about <c>!=</c>, and nothing could see it</b> —
///         <see href="https://github.com/Rikarin/Vixen/issues/1226">#1226</see>. The SPIR-V backend
///         emits <c>OpFOrdNotEqual</c>, which is <em>false</em> when either operand is a NaN; the
///         GLSL backend spells the operator through as <c>!=</c>, which every GLSL compiler lowers
///         to <c>OpFUnordNotEqual</c> and which is <em>true</em> on a NaN. So one Raven source
///         compiled for Vulkan and compiled for GLES takes different branches on the same input,
///         and no test in the tree asked.
///     </para>
///     <para>
///         ⚠ <b>It also makes <c>a != b</c> stop being the negation of <c>a == b</c> on the Vulkan
///         path</b>: both are false when <c>a</c> is a NaN, because <c>==</c> is ordered in both
///         backends and is the half that agrees. C, C++, GLSL, HLSL and MSL all read <c>!=</c> as
///         unordered, so the SPIR-V backend is the outlier rather than the GLSL one.
///     </para>
///     <para>
///         <b>This file pins the state rather than choosing it.</b> Which reading Raven should have
///         is a language decision with a blast radius — every committed <c>.spv</c> holding a float
///         <c>!=</c> is rebuilt by changing it — and #1226 owns it. What was missing until now is
///         that the disagreement was observable only by disassembling a module: the emitter could
///         have been changed either way, in either backend, with nothing going red. These tests are
///         held in both directions, so the commit that settles #1226 has to rewrite them, which is
///         the point.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is paired with its opposite</b>, because "the listing does not
///         contain <c>OpFUnordNotEqual</c>" is also true of a listing that contains nothing at all.
///         A <c>DoesNotContain</c> that is not guarded by the matching <c>Contains</c> is a test
///         that a generation failure would pass.
///     </para>
/// </remarks>
public class FloatComparisonTests {
    /// <summary>A fragment shader whose only comparison is one float inequality.</summary>
    const string Inequality = """
                              package A

                              shader S {
                                  stream var value: float4

                                  [FragmentShader]
                                  [Semantic("SV_Target")]
                                  func Fragment(): float4 {
                                      if (value.z != 0f) {
                                          return float4(1, 1, 1, 1)
                                      }

                                      return float4(0, 0, 0, 1)
                                  }
                              }

                              """;

    /// <summary>A fragment shader whose only comparison is one float equality.</summary>
    const string Equality = """
                            package A

                            shader S {
                                stream var value: float4

                                [FragmentShader]
                                [Semantic("SV_Target")]
                                func Fragment(): float4 {
                                    if (value.z == 0f) {
                                        return float4(1, 1, 1, 1)
                                    }

                                    return float4(0, 0, 0, 1)
                                }
                            }

                            """;

    [Fact]
    public void TheSpirvBackendReadsNotEqualOnFloatsAsOrderedSoANaNTakesTheFalseBranch() {
        var listing = One(Inequality).Code;

        Assert.Contains("OpFOrdNotEqual", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpFUnordNotEqual", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGlslBackendSpellsNotEqualThroughSoANaNTakesTheTrueBranch() {
        var unit = GenerateOne(Inequality);

        // The operator reaches the emitted text unqualified, and GLSL § 5.9 makes that the
        // unordered comparison -- `glslc` lowers it to `OpFUnordNotEqual`, which is the opposite
        // of what the SPIR-V backend emits for this same line.
        Assert.Contains("!=", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("lessThan", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("greaterThan", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void BothBackendsReadEqualOnFloatsAsOrdered() {
        var listing = One(Equality).Code;

        Assert.Contains("OpFOrdEqual", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpFUnordEqual", listing, StringComparison.Ordinal);

        // ⚠ The half that agrees, and the reason `!=` stops being the negation of `==` on the
        // Vulkan path: `a == b` and `a != b` are both false there when `a` is a NaN.
        var unit = GenerateOne(Equality);

        Assert.Contains("==", unit, StringComparison.Ordinal);
    }
}
