// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.CodeGen.Spirv;
using Xunit;

namespace Tests;

/// <summary>
///     What a name in an <c>OpName</c> may be — <see cref="SpirvNames" />.
/// </summary>
/// <remarks>
///     <para>
///         The bug these exist for was invisible everywhere a shader is usually checked. The module
///         validated, <c>spirv-val</c> was silent, the GLSL backend was fine, and every unit test
///         passed — and then <c>vkCreateComputePipelines</c> returned
///         <c>ErrorInitializationFailed</c> on Apple hardware with no mention of a name, because
///         MoltenVK turns <c>OpName</c> into an MSL variable declaration and MSL is C++, where
///         <c>and</c> is how you spell <c>&amp;&amp;</c>.
///     </para>
///     <para>
///         Raven lowers <c>a &amp;&amp; b</c> into a local so <c>b</c> can be skipped, and called that
///         local <c>and</c> — so <em>every</em> shader with a short-circuiting operand that needs
///         guarding was one no Metal driver would take.
///     </para>
/// </remarks>
public class SpirvNameTests {
    /// <summary>The alternative operator spellings, which are not identifiers in C++ at all.</summary>
    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("not")]
    [InlineData("xor")]
    [InlineData("compl")]
    [InlineData("bitand")]
    [InlineData("not_eq")]
    public void AnAlternativeOperatorSpellingIsSuffixed(string name) => Assert.Equal(name + "_", SpirvNames.Of(name));

    /// <summary>C++ and MSL keywords GLSL leaves alone, which is why nothing else caught them.</summary>
    [Theory]
    [InlineData("operator")]
    [InlineData("namespace")]
    [InlineData("nullptr")]
    [InlineData("constexpr")]
    [InlineData("device")]
    [InlineData("constant")]
    [InlineData("kernel")]
    public void AKeywordTheGlslListDoesNotHaveIsSuffixed(string name) => Assert.Equal(name + "_", SpirvNames.Of(name));

    /// <summary>
    ///     An ordinary name is left exactly as it was.
    /// </summary>
    /// <remarks>
    ///     Only exact matches are touched. Mangling anything that merely contains a keyword would
    ///     make every disassembly harder to read to fix nothing — <c>andThen</c> is a good identifier
    ///     in every language this reaches.
    /// </remarks>
    [Theory]
    [InlineData("cond")]
    [InlineData("andThen")]
    [InlineData("orbit")]
    [InlineData("notify")]
    [InlineData("visibility")]
    [InlineData("")]
    public void AnOrdinaryNameIsUntouched(string name) => Assert.Equal(name, SpirvNames.Of(name));

    /// <summary>
    ///     A shader whose <c>&amp;&amp;</c> guards a call emits no local a C++ compiler would refuse.
    /// </summary>
    /// <remarks>
    ///     The end-to-end form of the same claim, and the shape that actually occurred: the right
    ///     operand is a call, so it has to be guarded, so the lowering makes a local for it. A
    ///     condition over two plain comparisons never gets one, which is why most of the shader
    ///     library was unaffected and the one pass that had this was not.
    /// </remarks>
    [Fact]
    public void AGuardedShortCircuitEmitsNoReservedName() {
        var spirv = CodeGenTestBase.GenerateOne(
            """
            package A

            shader S {
                var cutoff: float = 1f
                var hits: RWBuffer<uint>

                static func Reaches(value: float): bool => value > 0f

                [ComputeShader(64)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    if (cutoff > 0f && Reaches(float(id.x))) {
                        hits[int(id.x)] = 1u
                    }
                }
            }
            """,
            "spirv"
        );

        Assert.Contains("OpName", spirv, StringComparison.Ordinal);
        Assert.DoesNotContain("\"and\"", spirv, StringComparison.Ordinal);
        Assert.Contains("\"and_\"", spirv, StringComparison.Ordinal);
    }
}
