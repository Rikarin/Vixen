// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     A modifier that changes nothing where it stands is named (<c>RVN2093</c>)
///     rather than silently ignored, statement attributes are named
///     (<c>RVN2095</c>) because nothing reads them, and a <c>where</c> clause is
///     enforced (<c>RVN2096</c>) rather than stored and forgotten.
/// </summary>
public class ModifierAndConstraintTests {
    [Theory]
    // `override` participates only in method dispatch; on a field it does nothing.
    [InlineData("package A\n\nshader S {\n    override var x: float\n}\n")]
    // `compose` declares a shader-typed slot, which only a field can be.
    [InlineData("package A\n\nshader S {\n    compose func M() {\n    }\n}\n")]
    // No modifier means anything on a type declaration.
    [InlineData("package A\n\nstatic shader S {\n}\n")]
    [InlineData("package A\n\nreadonly struct P {\n    var x: float\n}\n")]
    // Or on an `init`.
    [InlineData("package A\n\nstruct P {\n    var x: float\n\n    static init() {\n        x = 1f\n    }\n}\n")]
    public void A_modifier_with_no_effect_is_a_warning(string source) {
        var diagnostics = Diagnose(source);

        Assert.Contains("RVN2093", diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void A_modifier_in_its_place_is_silent() =>
        AssertNoDiagnostics(
            """
            package A

            shader S {
                const val Taps = 4
                readonly var bound: float
                static func Shared(): int => Taps

                [VertexShader]
                func VS(pos: float4): float4 => pos
            }

            """
        );

    [Fact]
    public void Attributes_on_a_statement_are_a_warning() {
        var diagnostics = Diagnose(
            """
            package A

            shader S {
                func M() {
                    [Unroll]
                    for (i in 0 .. 4) {
                    }
                }
            }

            """
        );

        Assert.Contains("RVN2095", diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void A_type_argument_must_satisfy_the_constraint() {
        var diagnostics = Diagnose(
            """
            package A

            protocol Shaded {
                func Tint(): float4
            }

            struct Box<T> where T : Shaded {
                var item: T
            }

            struct Good : Shaded {
                func Tint(): float4 => float4(1, 1, 1, 1)
            }

            struct Holder {
                var bad: Box<float>
            }

            """
        );

        Assert.Contains("RVN2096", diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void A_satisfying_type_argument_is_silent() =>
        AssertNoDiagnostics(
            """
            package A

            protocol Shaded {
                func Tint(): float4
            }

            struct Box<T> where T : Shaded {
                var item: T
            }

            struct Good : Shaded {
                func Tint(): float4 => float4(1, 1, 1, 1)
            }

            struct Holder {
                var good: Box<Good>
            }

            """
        );

    /// <summary>
    ///     A constraint naming another type parameter is checked against that parameter's
    ///     <em>argument</em>, so the instantiation is accepted.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every concrete instantiation of such a type used to be refused, the identity one
    ///         included.</b> <c>CheckConstraints</c> walked <c>ConstraintTypes</c> as declared and
    ///         asked whether <c>Leaf</c> derives from <c>U</c> — a type parameter, which nothing
    ///         derives from — so <c>Relay&lt;Leaf, Leaf&gt;</c> reported
    ///         <c>RVN2096: Type argument 'A.Leaf' does not satisfy the constraint 'U' …</c>. A
    ///         constraint form the parser and the binder both accept, that no program could use.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two shapes, because one of them is what makes the fix wrong if it is done by
    ///         ordinal.</b> <c>Chain</c> is a chain — <c>where T : U where U : V</c>, three deep —
    ///         and <c>Wrapper</c>'s field constructs a <c>Relay</c> from the <em>containing</em>
    ///         type's parameters, whose ordinals index a different list. Substituting by position
    ///         rather than by identity picks the wrong argument there and says nothing about it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_constraint_naming_another_type_parameter_is_satisfied_by_its_argument() =>
        AssertNoDiagnostics(
            """
            package A

            protocol Shaded {
                func Tint(): float4
            }

            struct Box<T> where T : Shaded {
                var item: T
            }

            struct Leaf : Shaded {
                var value: float4

                func Tint(): float4 => value
            }

            struct Relay<T, U> where U : Shaded where T : U {
                var inner: Box<T>
            }

            struct Chain<T, U, V> where V : Shaded where U : V where T : U {
                var inner: Box<T>
            }

            struct Wrapper<W> where W : Shaded {
                var forwarded: Relay<W, W>
            }

            shader S {
                var identity: Relay<Leaf, Leaf>
                var chained: Chain<Leaf, Leaf, Leaf>
                var wrapped: Wrapper<Leaf>

                [FragmentShader]
                [Semantic("SV_Target")]
                func Fragment(): float4 {
                    val a = identity.inner.item.Tint()
                    val b = chained.inner.item.Tint()
                    val c = wrapped.forwarded.inner.item.Tint()

                    return a * b * c
                }
            }

            """
        );

    /// <summary>
    ///     And the rule still fires: an argument that does not satisfy the argument supplied for the
    ///     parameter its constraint names is <c>RVN2096</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The half that makes the fix a substitution rather than a hole.</b> Substituting and
    ///     then not checking would accept everything, and the test above cannot tell the difference —
    ///     it only asks for silence. <c>Relay&lt;Bare, Leaf&gt;</c> asks whether <c>Bare</c> is a
    ///     <c>Leaf</c>, which it is not, and the message names <c>A.Leaf</c> rather than <c>U</c>:
    ///     the substituted constraint is what the author has to satisfy and so is what the
    ///     diagnostic reports.
    /// </remarks>
    [Fact]
    public void An_argument_that_does_not_satisfy_the_substituted_constraint_is_reported() {
        var diagnostics = Diagnose(
            """
            package A

            protocol Shaded {
                func Tint(): float4
            }

            struct Box<T> where T : Shaded {
                var item: T
            }

            struct Leaf : Shaded {
                var value: float4

                func Tint(): float4 => value
            }

            struct Bare : Shaded {
                var value: float4

                func Tint(): float4 => value
            }

            struct Relay<T, U> where U : Shaded where T : U {
                var inner: Box<T>
            }

            struct Holder {
                var mismatched: Relay<Bare, Leaf>
            }

            """
        );

        var refusal = Assert.Single(diagnostics, d => d.Id == "RVN2096");

        Assert.Contains("A.Bare", refusal.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("A.Leaf", refusal.GetMessage(), StringComparison.Ordinal);
    }
}
