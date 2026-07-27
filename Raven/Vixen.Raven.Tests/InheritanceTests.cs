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
///         The symbol layer always modelled inheritance properly — member lookup walks the base
///         chain, nearest first, which <c>SymbolTests</c> and <c>BindingTests</c> cover. Lowering
///         did not flatten it: a type contributed only its declared members, so a base's fields
///         never reached the derived layout and a base's body was lowered once against its own type.
///         The three tests below were the three silent miscompilations that came out of that, and
///         each now pins the behaviour rather than the refusal.
///     </para>
///     <para>
///         Flattening is monomorphisation over a different axis: a derived type is a context in
///         which <c>self</c> has the derived layout and a call to an overridden member reaches the
///         override, so a copy of each inherited body is emitted per derived type. That is what
///         makes <c>override</c> mean something in a language with no dynamic dispatch.
///     </para>
/// </remarks>
public class InheritanceTests {
    /// <summary>
    ///     An inherited uniform is a binding of the derived shader.
    /// </summary>
    /// <remarks>
    ///     It used to emit GLSL naming an identifier the unit never declared — <c>glslc</c> rejected
    ///     it with "undeclared identifier" while Raven reported nothing, and the SPIR-V backend was
    ///     the only one that noticed, as <c>RVN4002</c>. A shader's storage is module-scope globals,
    ///     so the derived shader lists the same variable the base declared: the merge <c>compose</c>
    ///     already used, for the same reason.
    /// </remarks>
    [Fact]
    public void An_inherited_uniform_becomes_a_binding_of_the_derived_shader() {
        const string Source = """
                              package A

                              shader Base {
                                  var tint: float4

                                  func Shade(): float4 => tint
                              }

                              shader Derived : Base {
                                  var extra: float

                                  [PixelShader]
                                  [Semantic("SV_Target")]
                                  func Pixel(): float4 {
                                      return Shade() * tint * extra
                                  }
                              }

                              """;

        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Source), "Derived");

        // The shader's own first, then what it pulls in — the same rule `compose` follows, so a
        // shader's own layout does not move when a base gains a field.
        Assert.Equal(["extra", "tint"], shader.Bindings.Select(b => b.Name));

        CodeGenTestBase.GenerateClean(Source);
        CodeGenTestBase.GenerateClean(Source, "spirv");
    }

    /// <summary>
    ///     An inherited struct field is in the derived layout, at the derived layout's index.
    /// </summary>
    /// <remarks>
    ///     The worst of the three: field access lowers to an index, and a derived struct's indices
    ///     are its own — so reading the inherited <c>a</c> emitted a read of <c>b</c>. Type-correct,
    ///     accepted by <c>glslc</c>, and wrong. Base-first ordering is what makes a derived value's
    ///     prefix the base's layout.
    /// </remarks>
    [Fact]
    public void An_inherited_struct_field_is_in_the_derived_layout() {
        const string Source = """
                              package A

                              struct Base {
                                  var a: float
                              }

                              struct Derived : Base {
                                  var b: float
                              }

                              shader S {
                                  [PixelShader]
                                  [Semantic("SV_Target")]
                                  func Pixel(): float4 {
                                      var d: Derived
                                      d.a = 2f
                                      d.b = 1f
                                      return float4(d.a, d.b, 0, 1)
                                  }
                              }

                              """;

        var module = LoweringTestBase.Lower(Source);
        var derived = Assert.Single(module.Structs, s => s.Name == "Derived");

        Assert.Equal(["a", "b"], derived.Fields.Select(f => f.Name));

        // The read of `a` is index 0 of the derived struct, which is what used to be `b`'s.
        Assert.Contains("load !d.0 : f32", LoweringTestBase.PrintFunction(module, "Pixel"), StringComparison.Ordinal);

        CodeGenTestBase.GenerateClean(Source);
        CodeGenTestBase.GenerateClean(Source, "spirv");
    }

    /// <summary>
    ///     An <c>override</c> replaces the base's member, including in the base's own calls.
    /// </summary>
    /// <remarks>
    ///     It used to be dropped: the base's own call was bound to the base's method and its body
    ///     lowered once, so <c>Compute()</c> kept returning the base's value. This is the semantics
    ///     Stride's mixin resolver exists to provide, and providing it means flattening — the
    ///     derived type gets its own copy of <c>Compute</c>, in which <c>Shade</c> resolves to the
    ///     override.
    /// </remarks>
    [Fact]
    public void An_override_replaces_the_base_member_in_the_bases_own_calls() {
        const string Source = """
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
                                  [Semantic("SV_Target")]
                                  func Pixel(): float4 {
                                      return float4(Compute(), 1)
                                  }
                              }

                              """;

        var module = LoweringTestBase.Lower(Source);
        var derived = LoweringTestBase.FindShader(module, "Derived");

        // Derived's copy of Compute calls Derived's Shade, not the base's.
        var compute = Assert.Single(derived.Functions, f => f.Name == "Derived_Compute");
        Assert.Contains(
            "call Derived_Shade",
            Vixen.Raven.IR.IrPrinter.Print(compute),
            StringComparison.Ordinal
        );

        // And the base's own copy is untouched — it is still a shader of its own.
        var @base = LoweringTestBase.FindShader(module, "Base");
        Assert.Contains(
            "call Shade",
            Vixen.Raven.IR.IrPrinter.Print(Assert.Single(@base.Functions, f => f.Name == "Compute")),
            StringComparison.Ordinal
        );

        CodeGenTestBase.GenerateClean(Source);
        CodeGenTestBase.GenerateClean(Source, "spirv");
    }

    /// <summary>
    ///     A chain of three flattens once per type, nearest declaration winning.
    /// </summary>
    [Fact]
    public void A_chain_flattens_at_every_level() {
        const string Source = """
                              package A

                              struct A1 {
                                  var a: float

                                  func Value(): float => a
                                  func Twice(): float => Value() * 2f
                              }

                              struct B1 : A1 {
                                  var b: float

                                  override func Value(): float => a + b
                              }

                              struct C1 : B1 {
                                  var c: float

                                  override func Value(): float => a + b + c
                              }

                              shader S {
                                  [PixelShader]
                                  [Semantic("SV_Target")]
                                  func Pixel(): float4 {
                                      var v: C1
                                      v.a = 1f
                                      v.b = 2f
                                      v.c = 3f
                                      return float4(v.Twice(), 0, 0, 1)
                                  }
                              }

                              """;

        var module = LoweringTestBase.Lower(Source);

        // Base-to-derived, so a value's prefix is its base's layout.
        Assert.Equal(["a", "b", "c"], Assert.Single(module.Structs, t => t.Name == "C1").Fields.Select(f => f.Name));

        // `Twice` is inherited twice over and copied once per type, and C1's copy reaches C1's
        // override — two levels down from where the call is written.
        Assert.Contains(
            "call C1_Value",
            Vixen.Raven.IR.IrPrinter.Print(LoweringTestBase.FindFunction(module, "C1_Twice")),
            StringComparison.Ordinal
        );

        Assert.Contains(
            "call B1_Value",
            Vixen.Raven.IR.IrPrinter.Print(LoweringTestBase.FindFunction(module, "B1_Twice")),
            StringComparison.Ordinal
        );

        CodeGenTestBase.GenerateClean(Source);
        CodeGenTestBase.GenerateClean(Source, "spirv");
    }

    /// <summary>
    ///     A derived shader inherits its base's streams as well as its bindings, and its entry
    ///     point can read one the base declared.
    /// </summary>
    [Fact]
    public void A_derived_shader_inherits_streams() {
        const string Source = """
                              package A

                              shader Base {
                                  stream var uv: float2

                                  var albedo: Texture2D
                                  var linear: Sampler

                                  func Sample(): float4 => albedo.Sample(linear, uv)
                              }

                              shader Derived : Base {
                                  var tint: float4

                                  [VertexShader]
                                  [Semantic("SV_Position")]
                                  func Vertex(position: float2, texcoord: float2): float4 {
                                      uv = texcoord
                                      return float4(position.x, position.y, 0f, 1f)
                                  }

                                  [PixelShader]
                                  [Semantic("SV_Target")]
                                  func Pixel(): float4 => Sample() * tint
                              }

                              """;

        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Source), "Derived");

        Assert.Equal(["uv"], shader.Streams.Select(stream => stream.Name));
        Assert.Contains(shader.Bindings, b => b.Name == "albedo");

        CodeGenTestBase.GenerateClean(Source);
        CodeGenTestBase.GenerateClean(Source, "spirv");
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
