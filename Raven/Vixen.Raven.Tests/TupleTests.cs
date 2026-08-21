// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Reflection;
using Xunit;
using static Tests.CodeGenTestBase;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     Tuples, which used to bind and then be rejected by lowering as having no GPU representation.
/// </summary>
/// <remarks>
///     <para>
///         They earned their keep on one fact: a tuple is the <em>only</em> way a Raven function
///         returns two values. There are no <c>out</c> parameters and the IR has no by-reference
///         arguments, so removing tuples would have removed the capability rather than the sugar.
///     </para>
///     <para>
///         Each distinct shape becomes one struct, named after its element types so the name is
///         stable across compilations rather than depending on the order types happened to be
///         lowered in. Element names come from the symbol, which already gives an unnamed element
///         <c>Item1</c>, <c>Item2</c>, … — so access needs nothing special and both backends see an
///         ordinary struct. See docs/plan/07 § J.
///     </para>
/// </remarks>
public class TupleTests {
    const string Split = """
                         package A

                         shader S {
                             var tint: float4

                             func Split(v: float4): (rgb: float3, a: float) {
                                 return (float3(v.x, v.y, v.z), v.w)
                             }

                             [FragmentShader]
                             func Fragment(): float4 {
                                 val parts = Split(tint)
                                 return float4(parts.rgb * parts.a, 1)
                             }
                         }

                         """;

    /// <summary>The capability that justifies the feature.</summary>
    [Fact]
    public void A_function_can_return_two_values() {
        var glsl = GenerateOne(Split);

        Assert.Contains("struct Tuple_vec_f32_3_f32 {", glsl, StringComparison.Ordinal);
        Assert.Contains("Tuple_vec_f32_3_f32 Split(vec4 v)", glsl, StringComparison.Ordinal);
    }

    /// <summary>Named elements keep their names, so the struct reads as the author wrote it.</summary>
    [Fact]
    public void A_named_element_keeps_its_name() {
        var glsl = GenerateOne(Split);

        Assert.Contains("vec3 rgb;", glsl, StringComparison.Ordinal);
        Assert.Contains("float a;", glsl, StringComparison.Ordinal);
    }

    /// <summary>And an unnamed one is still reachable positionally.</summary>
    [Fact]
    public void An_unnamed_element_is_reachable_as_ItemN() {
        var glsl = GenerateOne(
            """
            package A

            shader S {
                var tint: float4

                func Pair(): (float, float) {
                    return (tint.x, tint.y)
                }

                [FragmentShader]
                func Fragment(): float4 {
                    val p = Pair()
                    return float4(p.Item1, p.Item2, 0, 1)
                }
            }

            """
        );

        Assert.Contains("float Item1;", glsl, StringComparison.Ordinal);
        Assert.Contains("float Item2;", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One struct per shape, so two tuples of the same element types share it rather than
    ///     generating a near-duplicate.
    /// </summary>
    [Fact]
    public void Two_tuples_of_the_same_shape_share_one_struct() {
        var glsl = GenerateOne(
            """
            package A

            shader S {
                var tint: float4

                func First(): (float, float) => (tint.x, tint.y)

                func Second(): (float, float) => (tint.z, tint.w)

                [FragmentShader]
                func Fragment(): float4 {
                    val a = First()
                    val b = Second()
                    return float4(a.Item1, a.Item2, b.Item1, b.Item2)
                }
            }

            """
        );

        Assert.Equal(1, Occurrences(glsl, "struct Tuple_f32_f32 {"));
    }

    /// <summary>
    ///     A tuple in a uniform block lays out and reflects like any other struct, so the host can
    ///     write its elements by generated offset.
    /// </summary>
    [Fact]
    public void A_tuple_binding_reflects_its_elements_with_offsets() {
        var shader = FindShader(
            Lower(
                """
                package A

                shader S {
                    var pair: (x: float, y: float)

                    [FragmentShader]
                    func Fragment(): float4 {
                        return float4(pair.x, pair.y, 0, 1)
                    }
                }

                """
            ),
            "S"
        );

        var members = Assert.Single(ReflectionBuilder.Describe(shader).Sets).Bindings[0].Members;

        Assert.Equal(["pair", "pair.x", "pair.y"], members.Select(m => m.Name));
        Assert.Equal([0, 0, 4], members.Select(m => m.Offset));
    }

    /// <summary>Both backends emit it, and both reference tools accept the result.</summary>
    [Fact]
    public void Both_backends_emit_a_tuple() {
        Assert.NotEmpty(SpirvTestBase.One(Split).Code);

        Assert.SkipUnless(ReferenceCompiler.Glslc is not null, ReferenceCompiler.HowToInstall);

        var unit = Assert.Single(GenerateClean(Split));
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
