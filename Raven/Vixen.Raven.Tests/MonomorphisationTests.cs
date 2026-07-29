// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.CodeGenTestBase;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     Generics reach both backends — docs/plan/07 § I, "generics do not lower at all".
/// </summary>
/// <remarks>
///     <para>
///         Monomorphisation is the only way a generic reaches a GPU: SPIR-V's types are fully
///         concrete and GLSL has no templates, so <c>Box&lt;T&gt;</c> is never emitted and
///         <c>Box&lt;float4&gt;</c> is — as an ordinary struct called <c>Box_float4</c>. The open
///         definition costs nothing when nobody instantiates it, which is what makes a generic
///         library affordable.
///     </para>
///     <para>
///         The bodies are bound once, against the open definition, and lowered once per
///         instantiation through a substitution. Binding each instantiation separately would
///         type-check the same code twice and give the same answer.
///     </para>
/// </remarks>
public class MonomorphisationTests {
    const string TwoBoxes = """
                            package A

                            struct Box<T> {
                                var value: T

                                func Get(): T {
                                    return value
                                }
                            }

                            shader S {
                                var tint: float4

                                [FragmentShader]
                                [Semantic("SV_Target")]
                                func Fragment(): float4 {
                                    var a: Box<float4>
                                    a.value = tint
                                    var b: Box<float>
                                    b.value = 0.5f
                                    return a.Get() * b.Get()
                                }
                            }

                            """;

    [Fact]
    public void One_struct_and_one_function_per_instantiation() {
        var module = Lower(TwoBoxes);

        Assert.Equal(["Box_float4", "Box_float"], module.Structs.Select(s => s.Name));
        Assert.Equal(
            ["vec<f32,4>", "f32"],
            module.Structs.Select(s => Assert.Single(s.Fields).Type.Name)
        );

        // The open definition is emitted nowhere: there is no `Box` and no `Get`.
        Assert.DoesNotContain(module.Structs, s => s.Name == "Box");
        Assert.Equal(["Box_float4_Get", "Box_float_Get"], module.Functions.Select(f => f.Name));
    }

    [Fact]
    public void A_members_body_is_lowered_through_the_instantiations_types() {
        var module = Lower(TwoBoxes);

        // The body says `return value`, which was bound against `Box<T>.value`. Each copy resolves
        // it to its own struct's member 0, at its own type.
        Assert.Equal(
            """
            func Box_float4_Get($self : Box_float4) : vec<f32,4>
              %0 = load $self.0 : vec<f32,4>
              return %0
            end

            """,
            PrintFunction(module, "Box_float4_Get")
        );

        Assert.Contains("load $self.0 : f32", PrintFunction(module, "Box_float_Get"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_uninstantiated_generic_costs_nothing() {
        var module = Lower(
            """
            package A

            struct Unused<T> {
                var value: T
            }

            shader S {
                [FragmentShader]
                func Fragment(): float4 => float4(1, 1, 1, 1)
            }

            """
        );

        Assert.Empty(module.Structs);
    }

    [Fact]
    public void Two_uses_of_one_instantiation_share_one_struct() {
        var module = Lower(
            """
            package A

            struct Box<T> {
                var value: T
            }

            shader S {
                func First(b: Box<float>): float => b.value
                func Second(b: Box<float>): float => b.value

                [FragmentShader]
                func Fragment(): float4 {
                    var b: Box<float>
                    b.value = 1f
                    return float4(First(b), Second(b), 0, 1)
                }
            }

            """
        );

        Assert.Single(module.Structs);
    }

    [Fact]
    public void An_instantiation_named_only_by_another_instantiation_is_still_emitted() {
        var module = Lower(
            """
            package A

            struct Pair<T> {
                var first: T
                var second: T
            }

            struct Holder<T> {
                var pair: Pair<T>
            }

            shader S {
                [FragmentShader]
                func Fragment(): float4 {
                    var h: Holder<float>
                    h.pair.first = 1f
                    return float4(h.pair.first, h.pair.second, 0, 1)
                }
            }

            """
        );

        // `Pair<float>` appears nowhere in the shader; it is reached only by reading `Holder<T>`'s
        // members through its map, which is why discovery is a worklist rather than one pass.
        Assert.Equal(["Holder_float", "Pair_float"], module.Structs.Select(s => s.Name).Order());
        Assert.Equal(
            "Pair_float",
            Assert.Single(module.Structs.Single(s => s.Name == "Holder_float").Fields).Type.Name
        );
    }

    // --- Generic methods ---------------------------------------------------

    const string Pick = """
                        package A

                        struct Util {
                            static func Pick<T>(useFirst: bool, a: T, b: T): T {
                                if (useFirst) {
                                    return a
                                }

                                return b
                            }
                        }

                        shader S {
                            var tint: float4
                            var flag: int

                            [FragmentShader]
                            [Semantic("SV_Target")]
                            func Fragment(): float4 {
                                val c = Util.Pick<float4>(flag > 0, tint, float4(1, 0, 0, 1))
                                val s = Util.Pick<float>(flag > 1, 0.5f, 1f)
                                val t = Util.Pick<float4>(flag > 2, c, tint)
                                return c * s + t
                            }
                        }

                        """;

    [Fact]
    public void A_generic_method_becomes_one_function_per_argument_list() {
        var module = Lower(Pick);

        Assert.Equal(["Pick_float4", "Pick_float"], module.Functions.Select(f => f.Name));

        // Two call sites, one function: a constructed method symbol is built fresh at each use, so
        // without canonicalisation this would be `Pick_float4` twice.
        Assert.Equal(2, PrintFunction(module, "Fragment").Split("call Pick_float4").Length - 1);
    }

    [Fact]
    public void The_open_definition_of_a_generic_method_is_never_emitted() {
        var module = Lower(Pick);

        Assert.DoesNotContain(module.Functions, f => f.Name == "Pick");
        Assert.Empty(Assert.Single(module.Structs).Fields);
    }

    // --- Through the backends ----------------------------------------------

    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void Both_backends_take_the_instantiations(string target) {
        GenerateClean(TwoBoxes, target);
        GenerateClean(Pick, target);
    }

    [Fact]
    public void A_generic_over_a_generic_argument_reaches_both_backends() {
        const string Nested = """
                              package A

                              struct Pair<T> {
                                  var first: T
                                  var second: T
                              }

                              struct Box<T> {
                                  var value: T

                                  func Get(): T => value
                              }

                              shader S {
                                  [FragmentShader]
                                  [Semantic("SV_Target")]
                                  func Fragment(): float4 {
                                      var b: Box<Pair<float>>
                                      b.value.first = 1f
                                      b.value.second = 0f
                                      val p = b.Get()
                                      return float4(p.first, p.second, 0, 1)
                                  }
                              }

                              """;

        // A nested argument recurses through the same rule rather than having its punctuation
        // beaten out, so the name still reads.
        var module = Lower(Nested);
        Assert.Contains(module.Structs, s => s.Name == "Box_Pair_float");
        Assert.Contains(module.Structs, s => s.Name == "Pair_float");

        GenerateClean(Nested);
        GenerateClean(Nested, "spirv");
    }

    /// <summary>
    ///     The boundary, stated rather than discovered: an instantiation with a type argument that
    ///     is still open cannot be emitted, because there is nothing concrete to emit.
    /// </summary>
    /// <remarks>
    ///     Only reachable from inside another generic that the compilation never instantiates — and
    ///     then nothing reaches it either, so it is silently absent rather than an error. This pins
    ///     that a shader that <em>does</em> instantiate the outer one gets both.
    /// </remarks>
    [Fact]
    public void An_instantiation_reached_only_through_an_open_argument_follows_its_outer_one() {
        var module = Lower(
            """
            package A

            struct Inner<T> {
                var value: T
            }

            struct Outer<T> {
                var inner: Inner<T>
            }

            shader S {
                [FragmentShader]
                func Fragment(): float4 {
                    var o: Outer<float4>
                    o.inner.value = float4(1, 1, 1, 1)
                    return o.inner.value
                }
            }

            """
        );

        Assert.Equal(["Inner_float4", "Outer_float4"], module.Structs.Select(s => s.Name).Order());
    }
}
