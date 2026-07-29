// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;
using Vixen.Raven.Symbols;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     <c>asfloat</c>, <c>asint</c> and <c>asuint</c> — docs/plan/07 § F's "no
///     <c>asfloat</c>/<c>asuint</c>".
/// </summary>
/// <remarks>
///     These are what makes a packed storage buffer readable. Without them the only way to change
///     a value's type is a conversion, and a conversion changes the bits — so a normal packed into
///     a <c>uint</c> could be written by the host and never read back.
/// </remarks>
public class BitCastTests {
    static string Shader(string body) =>
        $$"""
          package A

          shader S {
              var packed: Buffer<uint>

              [FragmentShader]
              func Fragment(): float4 {
          {{body}}
              }
          }

          """;

    [Theory]
    [InlineData("asfloat", "float")]
    [InlineData("asint", "int")]
    [InlineData("asuint", "uint")]
    public void Each_name_is_declared_over_every_source_type_and_lane_count(string name, string target) {
        var overloads = Intrinsics.Lookup(name);

        // Three source component types × four lane counts.
        Assert.Equal(12, overloads.Count);

        foreach (var overload in overloads) {
            var parameter = Assert.Single(overload.Parameters);
            var lanes = ((PrimitiveTypeSymbol)parameter.Type).ComponentCount;

            Assert.Equal(
                lanes == 1 ? target : target + lanes,
                overload.ReturnType.ToDisplayString()
            );
        }
    }

    [Fact]
    public void A_reinterpretation_reaches_GLSL_as_the_named_bits_function() {
        var unit = Assert.Single(
            GenerateClean(
                Shader(
                    """
                            val bits = packed[0]
                            val value = asfloat(bits)
                            val back = asuint(value)
                            val signed = asint(value)
                            return float4(value, float(back), float(signed), 1)
                    """
                )
            )
        );

        Assert.Contains("uintBitsToFloat(", unit.Code, StringComparison.Ordinal);
        Assert.Contains("floatBitsToUint(", unit.Code, StringComparison.Ordinal);
        Assert.Contains("floatBitsToInt(", unit.Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     GLSL has no <c>intBitsToUint</c> because it does not need one: its constructor between
    ///     the two signednesses is defined to keep the bit pattern.
    /// </summary>
    [Fact]
    public void Crossing_only_signedness_uses_the_constructor() {
        var unit = Assert.Single(
            GenerateClean(
                Shader(
                    """
                            val bits = packed[0]
                            val signed = asint(bits)
                            return float4(float(signed), 0, 0, 1)
                    """
                )
            )
        );

        Assert.Contains("int(", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("BitsTo", unit.Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Reinterpreting a type as itself is nothing, and has to emit nothing: SPIR-V's
    ///     <c>OpBitcast</c> is invalid when its operand and result types are equal.
    /// </summary>
    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void An_identity_reinterpretation_emits_no_instruction(string target) {
        GenerateClean(
            Shader(
                """
                        val bits = packed[0]
                        val same = asuint(bits)
                        return float4(float(same), 0, 0, 1)
                """
            ),
            target
        );
    }

    [Fact]
    public void A_vector_reinterpretation_keeps_its_lane_count() {
        var module = LoweringTestBase.Lower(
            """
            package A

            shader S {
                func Unpack(v: uint3): float3 {
                    return asfloat(v)
                }
            }

            """
        );

        var body = LoweringTestBase.PrintFunction(module, "Unpack");
        Assert.Contains($"{IrIntrinsic.BitCast.ToString().ToLowerInvariant()} ", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vec<f32,3>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_round_trip_through_SPIR_V_validates() {
        if (!SpirvTestBase.ValidatorAvailable) {
            return;
        }

        var listing = ReferenceCompiler.Disassemble(
            SpirvTestBase.One(
                    Shader(
                        """
                                val bits = packed[0]
                                val value = asfloat(bits)
                                return float4(value, float(asuint(value)), 0, 1)
                        """
                    )
                )
                .Binary!
        );

        Assert.Contains("OpBitcast", listing, StringComparison.Ordinal);
    }
}
