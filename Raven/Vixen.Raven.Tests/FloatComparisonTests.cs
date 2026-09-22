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
///         ⚠ <b>Raven's two backends did not agree about <c>!=</c>, and nothing could see it</b> —
///         <see href="https://github.com/Rikarin/Vixen/issues/1226">#1226</see>. The SPIR-V backend
///         emitted <c>OpFOrdNotEqual</c>, which is <em>false</em> when either operand is a NaN; the
///         GLSL backend spells the operator through as <c>!=</c>, which every GLSL compiler lowers
///         to <c>OpFUnordNotEqual</c> and which is <em>true</em> on a NaN. So one Raven source
///         compiled for Vulkan and compiled for GLES took different branches on the same input,
///         and no test in the tree asked.
///     </para>
///     <para>
///         ⚠ <b>It also made <c>a != b</c> stop being the negation of <c>a == b</c> on the Vulkan
///         path</b>: both were false when <c>a</c> was a NaN, because <c>==</c> is ordered in both
///         backends. C, C++, GLSL, HLSL and MSL all read <c>!=</c> as unordered, so the SPIR-V
///         backend was the outlier rather than the GLSL one — and the GLSL one is the one that
///         cannot be spelled otherwise, since GLSL has no ordered <c>!=</c> and <c>!(a == b)</c>
///         lowers to <c>OpFOrdEqual</c> + <c>OpLogicalNot</c>, which is unordered-not-equal again.
///     </para>
///     <para>
///         <b>Settled: <c>!=</c> on floats is unordered on every target.</b> The SPIR-V backend
///         emits <c>OpFUnordNotEqual</c>, which is what the GLSL backend already meant, what every
///         neighbouring language means, and what makes <c>!=</c> the negation of <c>==</c> again.
///         The cost was measured before deciding: of fifty-nine committed modules, two carried a
///         float <c>!=</c> — <c>UiBox.frag.spv</c> and <c>UiMask.frag.spv</c> — and both were
///         regenerated in the same commit. These tests hold the decision in both directions, so
///         reverting it has to rewrite them.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is paired with its opposite</b>, because "the listing does not
///         contain <c>OpFOrdNotEqual</c>" is also true of a listing that contains nothing at all.
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

    /// <summary>A fragment shader comparing two vectors component-wise with <c>!=</c>.</summary>
    /// <remarks>
    ///     The vector form goes through the same arm of the emitter, but a listing that proves the
    ///     scalar form proves nothing about a shaped operand: SPIR-V's <c>OpFUnordNotEqual</c> takes
    ///     vectors and yields a vector of bools, and this is what says Raven reaches for it there too.
    /// </remarks>
    const string VectorInequality = """
                                    package A

                                    shader S {
                                        stream var value: float4

                                        [FragmentShader]
                                        [Semantic("SV_Target")]
                                        func Fragment(): float4 {
                                            if (any(value.xy != float2(0f, 0f))) {
                                                return float4(1, 1, 1, 1)
                                            }

                                            return float4(0, 0, 0, 1)
                                        }
                                    }

                                    """;

    [Fact]
    public void TheSpirvBackendReadsNotEqualOnFloatsAsUnorderedSoANaNTakesTheTrueBranch() {
        var listing = One(Inequality).Code;

        Assert.Contains("OpFUnordNotEqual", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpFOrdNotEqual", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSpirvBackendReadsNotEqualOnVectorsAsUnorderedToo() {
        var listing = One(VectorInequality).Code;

        Assert.Contains("OpFUnordNotEqual", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpFOrdNotEqual", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGlslBackendSpellsNotEqualThroughSoANaNTakesTheTrueBranch() {
        var unit = GenerateOne(Inequality);

        // The operator reaches the emitted text unqualified, and GLSL § 5.9 makes that the
        // unordered comparison -- `glslc` lowers it to `OpFUnordNotEqual`, which is what the SPIR-V
        // backend emits for this same line now.
        Assert.Contains("!=", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("lessThan", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("greaterThan", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void BothBackendsReadEqualOnFloatsAsOrdered() {
        var listing = One(Equality).Code;

        Assert.Contains("OpFOrdEqual", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpFUnordEqual", listing, StringComparison.Ordinal);

        // The half that always agreed, and the reason the other half had to move: with `==`
        // ordered and `!=` unordered, `a != b` is `!(a == b)` for every `a` and `b`, NaN included.
        var unit = GenerateOne(Equality);

        Assert.Contains("==", unit, StringComparison.Ordinal);
    }
}
