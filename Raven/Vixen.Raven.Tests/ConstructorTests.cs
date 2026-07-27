// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.CodeGenTestBase;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     Constructing a value: a declared <c>init</c>, and the positional form a plain data struct
///     gets for free.
/// </summary>
/// <remarks>
///     <para>
///         A constructor is valid on a GPU because it needs nothing a GPU lacks — no heap, no
///         lifetime, no dispatch. It is a function that builds a value and returns it, which is how
///         MSL and Slang treat one and how this lowers: <c>Ray_init(vec3, vec3)</c> returning a
///         <c>Ray</c>, with <c>self</c> as a local. That is also the line that made removing
///         <c>~init</c> right in docs/plan/07 § J: a destructor needs a lifetime, a constructor
///         needs only a return value.
///     </para>
///     <para>
///         What it cannot do is enforce an invariant. <c>var r: Ray</c> skips the constructor
///         entirely and partial initialisation is silent, both pinned below — the same as HLSL and
///         GLSL, so it is a property of the domain rather than a defect. Constructors here are
///         convenience, not a guarantee.
///     </para>
/// </remarks>
public class ConstructorTests {
    const string Ray = """
                       struct Ray {
                           var origin: float3
                           var direction: float3
                       }
                       """;

    static string Pixel(string types, string body) =>
        $$"""
          package A

          {{types}}

          shader S {
              var colour: float3

              [PixelShader]
              func Pixel(): float4 {
          {{body}}
              }
          }

          """;

    // --- A declared constructor ---------------------------------------------

    /// <summary>
    ///     It becomes an ordinary function returning the value it built — no allocation, and
    ///     nothing that needs a lifetime.
    /// </summary>
    [Fact]
    public void A_declared_constructor_lowers_to_a_function_returning_the_value() {
        var glsl = GenerateOne(
            Pixel(
                """
                struct Ray {
                    var origin: float3

                    init(o: float3) {
                        origin = o
                    }
                }
                """,
                "        val r = Ray(colour)\n        return float4(r.origin, 1)"
            )
        );

        // Named `Ray_init`, not `Ray`: GLSL generates its own positional `Ray(...)` for every
        // struct, and two things of one name is one thing too many.
        Assert.Contains("Ray Ray_init(vec3 o) {", glsl, StringComparison.Ordinal);
        Assert.Contains("Ray self;", glsl, StringComparison.Ordinal);
        Assert.Contains("Ray_init(", glsl, StringComparison.Ordinal);
    }

    // --- The positional form ------------------------------------------------

    /// <summary>
    ///     A struct with no <c>init</c> is still constructible from its fields, which is what every
    ///     target already does — GLSL generates the constructor, HLSL and WGSL spell it as an
    ///     aggregate initialiser. Raven used to be stricter than all three.
    /// </summary>
    [Fact]
    public void A_struct_with_no_constructor_is_built_from_its_fields() {
        var glsl = GenerateOne(Pixel(Ray, "        val r = Ray(colour, colour)\n        return float4(r.origin, 1)"));

        // Straight through to GLSL's own constructor — no generated function in between.
        Assert.Contains("Ray(", glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("Ray_init", glsl, StringComparison.Ordinal);
    }

    [Fact]
    public void The_positional_form_converts_each_argument_to_its_field_type() {
        var glsl = GenerateOne(
            Pixel(
                "struct Weights {\n    var scale: float\n    var count: float\n}",
                "        val w = Weights(1, 2)\n        return float4(w.scale, w.count, 0, 1)"
            )
        );

        // `1` and `2` are int literals widened to the fields' float.
        Assert.Contains("Weights(1.0, 2.0)", glsl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_argument_build_still_zero_initializes() =>
        Assert.NotEmpty(GenerateOne(Pixel(Ray, "        val r = Ray()\n        return float4(r.origin, 1)")));

    [Theory]
    // One argument per field, so a short or long list is an arity error naming the count.
    [InlineData("        val r = Ray(colour)\n        return float4(r.origin, 1)", "RVN2033")]
    [InlineData("        val r = Ray(colour, colour, colour)\n        return float4(r.origin, 1)", "RVN2033")]
    // A field's type still has to accept its argument. A scalar into a `float3` field is not
    // an example: that is the ordinary splat conversion, so `Ray(colour, 1f)` builds a
    // direction of (1, 1, 1) exactly as `val v: float3 = 1f` would.
    [InlineData("        val c = Counted(colour, 1)\n        return float4(1, 1, 1, 1)", "RVN2020")]
    public void The_positional_form_is_checked(string body, string id) {
        var diagnostics = Diagnose(Pixel(Ray + "\n\nstruct Counted {\n    var n: int\n    var scale: float\n}", body));

        Assert.Contains(id, diagnostics.Select(d => d.Id));
    }

    /// <summary>
    ///     A declared <c>init</c> replaces the positional form rather than adding to it, so a type
    ///     that wants both spellings has to say so. Offering both silently would make the field
    ///     order part of the public surface of every struct.
    /// </summary>
    [Fact]
    public void A_declared_constructor_takes_over_from_the_positional_form() =>
        AssertDiagnostics(
            Pixel(
                """
                struct Ray {
                    var origin: float3
                    var direction: float3

                    init(o: float3) {
                        origin = o
                    }
                }
                """,
                "        val r = Ray(colour, colour)\n        return float4(1, 1, 1, 1)"
            ),
            "RVN2034"
        );

    // --- What a constructor cannot do ---------------------------------------

    /// <summary>
    ///     Declaring a value skips its constructor — and reading it without filling it is now
    ///     <c>RVN2127</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The skip itself is not a defect and is not fixable: a value type with no heap behaves
    ///         this way in HLSL and GLSL too, so an <c>init</c> is convenience rather than a
    ///         guarantee and cannot be relied on to have run.
    ///     </para>
    ///     <para>
    ///         What <em>was</em> a defect is that the resulting read compiled. Both targets hand
    ///         back whatever the register held, so this shader ran differently on different drivers
    ///         and said nothing. Definite assignment closes it — from the other end than a
    ///         constructor would, which is why the skip staying legal costs nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Declaring_a_value_and_reading_it_unfilled_is_refused() {
        var error = Assert.Single(
            SemanticTestBase.Diagnose(
                Pixel(
                    """
                    struct Ray {
                        var origin: float3

                        init(o: float3) {
                            origin = o
                        }
                    }
                    """,
                    "        var r: Ray\n        return float4(r.origin, 1)"
                )
            ),
            d => d.Id == "RVN2127"
        );

        Assert.True(error.IsError);
    }

    /// <summary>Filling it by field is the way through, and stays the way through.</summary>
    [Fact]
    public void Declaring_a_value_and_filling_it_by_field_compiles() =>
        Assert.NotEmpty(
            GenerateOne(
                Pixel(
                    """
                    struct Ray {
                        var origin: float3

                        init(o: float3) {
                            origin = o
                        }
                    }
                    """,
                    "        var r: Ray\n        r.origin = colour\n        return float4(r.origin, 1)"
                )
            )
        );

    // --- A shader is not a value --------------------------------------------

    /// <summary>
    ///     A shader is the pipeline, not something anything constructs, so an <c>init</c> on one
    ///     used to be lowered and then dropped — silently, while reading as though it initialised
    ///     the bindings. A binding default does that honestly.
    /// </summary>
    [Fact]
    public void A_shader_cannot_declare_a_constructor() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                """
                package A

                shader S {
                    var tint: float4

                    init(t: float4) {
                        val ignored = t
                    }

                    [PixelShader]
                    func Pixel(): float4 {
                        return tint
                    }
                }

                """,
                "RVN2092"
            )
        );

        Assert.Contains("never constructed", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>The honest alternative it points at.</summary>
    [Fact]
    public void A_binding_default_is_the_way_to_give_a_uniform_a_value() {
        var glsl = GenerateOne(
            """
            package A

            shader S {
                var tint: float4 = float4(1, 1, 1, 1)

                [PixelShader]
                func Pixel(): float4 {
                    return tint
                }
            }

            """
        );

        Assert.Contains("Binding defaults are host-side data", glsl, StringComparison.Ordinal);
    }
}
