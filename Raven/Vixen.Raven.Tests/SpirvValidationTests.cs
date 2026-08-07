// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.SpirvTestBase;

namespace Tests;

/// <summary>
///     Breadth rather than depth: each case is a shape the emitter has to get right,
///     and the assertion is the reference validator's verdict on the module. A
///     listing that reads plausibly proves nothing about a binary format, so this is
///     the test that actually holds the backend up.
/// </summary>
public class SpirvValidationTests {
    [Theory]
    // Control flow
    [InlineData(
        "a while loop",
        """
                var total = 0f
                while (total < 4f) {
                    total += 1f
                }

                return float4(total, 0, 0, 1)
        """
    )]
    [InlineData(
        "a loop that returns out of its middle",
        """
                for (i in 0 .. 4) {
                    if (i == 2) {
                        return float4(1, 0, 0, 1)
                    }
                }

                return float4(0, 0, 0, 1)
        """
    )]
    [InlineData(
        "nested loops breaking out of the inner one",
        """
                var total = 0f
                for (i in 0 .. 3) {
                    for (j in 0 .. 3) {
                        if (j == 1) {
                            break
                        }

                        total += 1f
                    }
                }

                return float4(total, 0, 0, 1)
        """
    )]
    [InlineData(
        "an if with no else",
        """
                var shade = 0f
                if (level > 1f) {
                    shade = 1f
                }

                return float4(shade, 0, 0, 1)
        """
    )]
    [InlineData(
        "an if whose branches both return",
        """
                if (level > 1f) {
                    return float4(1, 0, 0, 1)
                } else {
                    return float4(0, 1, 0, 1)
                }
        """
    )]
    [InlineData(
        "a repeat loop with a continue",
        """
                var total = 0f
                var i = 0
                repeat {
                    i += 1
                    if (i == 2) {
                        continue
                    }

                    total += 1f
                } while (i < 4)

                return float4(total, 0, 0, 1)
        """
    )]
    // Values
    [InlineData(
        "a componentwise comparison",
        """
                val mask = colour < colour
                return all(mask) ? float4(1, 1, 1, 1) : float4(0, 0, 0, 1)
        """
    )]
    [InlineData(
        "boolean operators",
        """
                val both = level > 1f && level < 3f
                val either = level > 1f || level < 3f
                return float4(both ? 1f : 0f, either ? 1f : 0f, 0, 1)
        """
    )]
    [InlineData(
        "negation of every kind",
        """
                val a = -level
                val b = !(level > 1f)
                val c = ~count
                return float4(a, b ? 1f : 0f, float(c), 1)
        """
    )]
    [InlineData(
        "integer arithmetic and its conversions",
        """
                val doubled = count * 2
                val scaled = float(doubled) * level
                return float4(scaled, 0, 0, 1)
        """
    )]
    [InlineData(
        "unsigned arithmetic",
        """
                val half = index / 2u
                return float4(float(half), 0, 0, 1)
        """
    )]
    [InlineData(
        "a float remainder and a modulus",
        """
                val remainder = level % 2f
                val modulus = mod(level, 2f)
                return float4(remainder, modulus, 0, 1)
        """
    )]
    [InlineData(
        "a matrix built from its rows",
        """
                val m = mat3(1, 0, 0, 0, 1, 0, 0, 0, 1)
                return float4(m * colour, 1)
        """
    )]
    [InlineData(
        "a matrix times a matrix",
        """
                return float4((rotation * rotation) * colour, 1)
        """
    )]
    [InlineData(
        "a vector times a matrix",
        """
                return float4(colour * rotation, 1)
        """
    )]
    [InlineData(
        "a scalar broadcast into a matrix",
        """
                val m: mat3 = mat3(level)
                return float4(m * colour, 1)
        """
    )]
    [InlineData(
        "double precision",
        """
                val wide: double = 2.5
                val narrow = float(wide * 2.0)
                return float4(narrow, 0, 0, 1)
        """
    )]
    [InlineData(
        "a swizzle written to in place",
        """
                var v = float4(0, 0, 0, 1)
                v.xy = float2(level, level)
                v.z = level
                return v
        """
    )]
    [InlineData(
        "a struct built, stored and read back",
        """
                var ray: Ray
                ray.origin = colour
                ray.direction = colour
                return float4(ray.At(level), 1)
        """
    )]
    [InlineData(
        "intrinsics over vectors",
        """
                val n = normalize(colour)
                val c = cross(n, colour)
                val d = distance(n, c)
                val s = smoothstep(0f, 1f, d)
                val m = clamp(s, 0f, 1f)
                return float4(c * m, 1)
        """
    )]
    [InlineData(
        "integer minimum and maximum",
        """
                val low = min(count, 3)
                val high = max(count, 3)
                return float4(float(low), float(high), 0, 1)
        """
    )]
    [InlineData(
        "a texture sampled through a separate sampler",
        """
                val sampled = albedo.Sample(linear, float2(level, level))
                return sampled
        """
    )]

    // Splats whose component type changes on the way. Each is a scalar of one component type
    // reaching a vector of another — a conversion the IR spells once and SPIR-V has to spell twice,
    // see `SpirvEmitter.EmitConvert`. Six of the nightly's thirteen `raven` findings were these
    // three shapes, arrived at by six unrelated one-token edits: a local that lost its `f`, a
    // literal that replaced a swizzle, a field whose type became `int`, a parameter whose type
    // became `int`. All three fail without the fix.
    [InlineData(
        "an integer broadcast into a float vector",
        """
                val lit = 0
                return float4(colour * lit, 1)
        """
    )]
    [InlineData(
        "an integer where an intrinsic takes a float vector",
        "        return float4(dot(0, colour), 0, 0, 1)"
    )]
    [InlineData(
        "an integer argument to a float vector parameter",
        "        return albedo.Sample(linear, count)"
    )]

    // A range whose ends are not the same type. The loop variable takes the ends' common type, so
    // the ends have to be converted to it — leaving the limit as it was stored an `i32` through an
    // `f32` pointer, which is `Corpus/raven/244478dae1621493.bin`.
    [InlineData(
        "a loop over a range whose ends differ in type",
        """
                var total = 0f
                for (i in 3f .. 4) {
                    total += i
                }

                return float4(total, 0, 0, 1)
        """
    )]
    public void The_module_is_valid(string what, string body) {
        var unit = One(
            $$"""
              package A

              struct Ray {
                  var origin: float3
                  var direction: float3

                  func At(t: float): float3 => origin + direction * t
              }

              shader S {
                  var level: float
                  var count: int
                  var index: uint
                  var colour: float3
                  var rotation: mat3
                  var albedo: Texture2D
                  var linear: Sampler

                  [FragmentShader]
                  func Fragment(): float4 {
              {{body}}
                  }
              }

              """
        );

        // The name is there so a failure says which shape broke.
        Assert.False(string.IsNullOrEmpty(what));
        Assert.NotEmpty(unit.Code);
    }

    /// <summary>A signed literal broadcast into an unsigned vector converts on the way.</summary>
    /// <remarks>
    ///     ⚠ Its own test rather than another row above, because it is the splat whose component
    ///     conversion is <em>two</em> instructions and not one: signedness alone is an
    ///     <c>OpBitcast</c>, and it is the path <c>EmitIntegerConvert</c> would have taken the
    ///     result type from had the splat not been made to hand it the component. The nightly
    ///     reached it as <c>max(knee, Epsilon)</c> → <c>Weight(1)</c>, a call whose parameter is a
    ///     <c>uint3</c> — <c>Corpus/raven/a86b9e11c8a1034e.bin</c>, which emitted
    ///     <c>OpCompositeConstruct %v3uint %int_1 %int_1 %int_1</c>.
    /// </remarks>
    [Fact]
    public void A_signed_literal_broadcast_into_an_unsigned_vector_is_valid() {
        var listing = Fragment(
            "        return float4(Weigh(1), 0, 0, 1)",
            "    func Weigh(id: uint3): float => float(id.x)\n"
        );

        Assert.Contains("OpBitcast", listing, StringComparison.Ordinal);
    }

    /// <summary>An integer parameter of a fragment stage is <c>Flat</c>, like an integer stream.</summary>
    /// <remarks>
    ///     ⚠ <b>The decoration was applied where the varyings are declared and nowhere else</b>, so
    ///     a <c>stream var</c> of integer type got it and an entry point's own parameter did not —
    ///     and <c>spirv-val</c> refuses the second module for
    ///     <c>VUID-StandaloneSpirv-Flat-04744</c>. There is no interpolation an integer could take,
    ///     which is why the rule is not a preference. Found underneath
    ///     <c>Corpus/raven/5cc192ddcce49da6.bin</c>: its splat was the first error, and this one
    ///     only became visible once that was emitted correctly.
    /// </remarks>
    [Fact]
    public void An_integer_fragment_parameter_is_flat() {
        var listing = Fragment(
            "        return float4(float(tile), 0, 0, 1)",
            signature: "func Fragment(tile: int): float4"
        );

        Assert.Contains($"OpDecorate {IdNamed(listing, "in_tile")} Flat", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vertex_stage_with_the_same_body_is_valid_too() {
        // The stage changes which instructions are legal — derivatives and
        // implicit levels of detail are fragment-only — so it is worth its own run.
        var unit = One(
            """
            package A

            shader S {
                var world: mat4
                var albedo: Texture2D
                var linear: Sampler

                [VertexShader]
                [Semantic("SV_Position")]
                func Vertex(position: float3, uv: float2): float4 {
                    // Stated, because a vertex stage has no derivatives — RVN3013 warns about the
                    // other spelling, and this test's subject is the module rather than the warning.
                    val tinted = albedo.SampleLevel(linear, uv, 0f)
                    return world * float4(position * tinted.a, 1)
                }
            }

            """
        );

        Assert.Contains("OpEntryPoint Vertex", unit.Code);
    }
}
