// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Xunit;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     What <c>shader Derived : Base</c> does and does not do.
/// </summary>
/// <remarks>
///     <para>
///         The symbol layer models inheritance properly — member lookup walks the base chain,
///         nearest first, which <c>SymbolTests</c> and <c>BindingTests</c> cover. Lowering does not
///         flatten it: a type contributes only its declared members, so a base's fields never reach
///         the derived layout and a base's body is lowered once against its own type.
///     </para>
///     <para>
///         Left alone, that produced three silent miscompilations, recorded in docs/plan/07 § J.
///         Each is now an error, and the checks are narrow on purpose: inheritance used only to
///         supply a member to a <c>compose</c> slot lowers correctly and keeps working.
///     </para>
/// </remarks>
public class InheritanceTests {
    /// <summary>
    ///     An inherited uniform used to emit GLSL naming an identifier the unit never declared —
    ///     <c>glslc</c> rejected it with "undeclared identifier" while Raven reported nothing. The
    ///     SPIR-V backend was the only one that noticed, as <c>RVN4002</c>.
    /// </summary>
    [Fact]
    public void An_inherited_uniform_is_reported_rather_than_emitted_undeclared() {
        var diagnostics = LoweringDiagnosticsOf(
            """
            package A

            shader Base {
                var tint: float4

                func Shade(): float4 => tint
            }

            shader Derived : Base {
                [PixelShader]
                func Pixel(): float4 {
                    return Shade() * tint
                }
            }

            """
        );

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "RVN3002" && d.GetMessage().Contains("tint"));
        Assert.Contains("not flattened", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The worst of the three: field access lowers to an index, and a derived struct's indices
    ///     are its own — so reading the inherited <c>a</c> emitted a read of <c>b</c>. Type-correct,
    ///     accepted by <c>glslc</c>, and wrong.
    /// </summary>
    [Fact]
    public void An_inherited_struct_field_is_reported_rather_than_reading_the_wrong_one() {
        var diagnostics = LoweringDiagnosticsOf(
            """
            package A

            struct Base {
                var a: float
            }

            struct Derived : Base {
                var b: float
            }

            shader S {
                [PixelShader]
                func Pixel(): float4 {
                    var d: Derived
                    d.b = 1f
                    return float4(d.a, d.b, 0, 1)
                }
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN3002" && d.GetMessage().Contains('a'));
    }

    /// <summary>
    ///     An <c>override</c> was dropped: the base's own call was bound to the base's method and
    ///     its body lowered once, so <c>Compute()</c> kept returning the base's value. This is the
    ///     semantics Stride's mixin resolver exists to provide, and providing it means flattening.
    /// </summary>
    [Fact]
    public void An_override_is_reported_rather_than_silently_ignored() {
        var diagnostics = LoweringDiagnosticsOf(
            """
            package A

            shader Base {
                func Shade(): float3 => float3(1, 0, 0)

                func Compute(): float3 {
                    return Shade()
                }
            }

            shader Derived : Base {
                override func Shade(): float3 => float3(0, 1, 0)

                [PixelShader]
                func Pixel(): float4 {
                    return float4(Compute(), 1)
                }
            }

            """
        );

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "RVN3002" && d.GetMessage().Contains("overriding"));
        Assert.Contains("still reach the base's method", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The case the checks must not catch. A stateless base supplying a method that satisfies a
    ///     protocol lowers correctly — <c>compose</c> resolves through the base chain and calls the
    ///     base's function directly — so composition keeps working while the broken cases are
    ///     rejected. <c>ComposeTests</c> covers the resolution itself; this pins that lowering stays
    ///     quiet about it.
    /// </summary>
    [Fact]
    public void Inheriting_only_a_method_for_a_compose_slot_stays_legal() {
        var diagnostics = LoweringDiagnosticsOf(
            """
            package A

            protocol IDiffuse {
                func Diffuse(albedo: float4): float4
            }

            shader BaseModel {
                func Diffuse(albedo: float4): float4 => albedo * 0.75f
            }

            shader Derived : BaseModel, IDiffuse {
            }

            shader Lit {
                compose val diffuse: IDiffuse

                var tint: float4

                [PixelShader]
                func Pixel(): float4 {
                    return diffuse.Diffuse(tint)
                }
            }

            """,
            ComposeBindings.Parse(["diffuse=Derived"])
        );

        Assert.DoesNotContain(diagnostics, d => d.Id == "RVN3002");
    }
}
