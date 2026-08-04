// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     Multiple render targets — docs/plan/07 § F, "an entry point returns one value".
/// </summary>
/// <remarks>
///     A stage interface variable takes one location and so has to be one scalar or vector. A
///     fragment stage that writes four targets therefore returns a struct, and the entry-point
///     wrapper takes it apart. Declaration order is target order, which is the rule
///     <c>StreamPlan</c> already uses for streams: a number both sides derive beats a number one
///     side spells.
/// </remarks>
public class RenderTargetTests {
    const string GBuffer = """
                           package A

                           struct GBufferData {
                               var albedo: float4
                               var normal: float4
                               var material: float4
                           }

                           shader S {
                               var tint: float4

                               [FragmentShader]
                               func Fragment(normalWS: float3): GBufferData {
                                   var g: GBufferData
                                   g.albedo = tint
                                   g.normal = float4(normalWS, 0f)
                                   g.material = float4(0.5f, 0.5f, 0f, 1f)
                                   return g
                               }
                           }

                           """;

    [Fact]
    public void A_returned_struct_becomes_one_output_per_member_in_declaration_order() {
        var module = LoweringTestBase.Lower(GBuffer);
        var entry = Assert.Single(LoweringTestBase.FindShader(module, "S").EntryPoints);

        Assert.Equal(["albedo", "normal", "material"], entry.Outputs.Select(o => o.Name));
        Assert.Equal([0, 1, 2], entry.Outputs.Select(o => o.Member));

        // The semantic is synthesized from the index rather than declared, so the target a member
        // writes cannot disagree with the location the plan gives it.
        Assert.Equal(["SV_Target0", "SV_Target1", "SV_Target2"], entry.Outputs.Select(o => o.Semantic));

        // `Output` means "the one output", and there is not one.
        Assert.Null(entry.Output);
    }

    [Fact]
    public void GLSL_declares_one_out_per_target_and_destructures_once() {
        var unit = Assert.Single(GenerateClean(GBuffer));

        Assert.Contains("layout(location = 0) out vec4 out_albedo;", unit.Code, StringComparison.Ordinal);
        Assert.Contains("layout(location = 1) out vec4 out_normal;", unit.Code, StringComparison.Ordinal);
        Assert.Contains("layout(location = 2) out vec4 out_material;", unit.Code, StringComparison.Ordinal);

        // One call, not one per target: calling per target would run the shader body three times.
        Assert.Single(unit.Code.Split("Fragment(in_normalWS)")[1..]);
        Assert.Contains("out_material = _targets.material;", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void SPIR_V_extracts_each_member_from_one_call() {
        if (!SpirvTestBase.ValidatorAvailable) {
            return;
        }

        var listing = ReferenceCompiler.Disassemble(SpirvTestBase.One(GBuffer).Binary!);

        Assert.Equal(3, listing.Split("OpCompositeExtract").Length - 1);
        Assert.Single(listing.Split("OpFunctionCall")[1..]);

        foreach (var location in (string[])["Location 0", "Location 1", "Location 2"]) {
            Assert.Contains(location, listing, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_reflection_reports_one_colour_attachment_per_target() {
        var reflection = ReflectionOf(GBuffer);

        Assert.Equal([0, 1, 2], reflection.Outputs.Select(o => o.Location));
        Assert.Equal(["albedo", "normal", "material"], reflection.Outputs.Select(o => o.Name));
    }

    [Fact]
    public void A_single_value_return_is_unchanged() {
        const string One = """
                           package A

                           shader S {
                               [FragmentShader]
                               [Semantic("SV_Target")]
                               func Fragment(): float4 => float4(1, 0, 0, 1)
                           }

                           """;

        var entry = Assert.Single(LoweringTestBase.FindShader(LoweringTestBase.Lower(One), "S").EntryPoints);
        var output = Assert.Single(entry.Outputs);

        // No member index: the output *is* the returned value, so nothing is extracted.
        Assert.Null(output.Member);
        Assert.Equal("result", output.Name);
        Assert.Equal("SV_Target", output.Semantic);

        Assert.Contains("out_result = Fragment();", Assert.Single(GenerateClean(One)).Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Fragment stages only, and deliberately. A vertex stage's several outputs are
    ///     <c>stream</c>s — a mechanism where the location is a property of the shader, so the
    ///     writing and reading stages agree without either declaring the other's struct.
    /// </summary>
    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void An_aggregate_return_from_a_vertex_stage_is_still_refused(string target) {
        Generate(
            """
            package A

            struct Varyings {
                var position: float4
                var uv: float2
            }

            shader S {
                [VertexShader]
                func Vertex(p: float3): Varyings {
                    var v: Varyings
                    v.position = float4(p, 1)
                    v.uv = float2(0, 0)
                    return v
                }
            }

            """,
            out var reported,
            target
        );

        Assert.Contains(reported, d => d.Id == "RVN4001" && d.IsError);
    }

    /// <summary>
    ///     The per-member check is the same one a single output gets: a member a location cannot
    ///     carry is refused where it is declared, not silently emitted.
    /// </summary>
    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void A_target_of_a_type_no_location_can_carry_is_refused(string target) {
        Generate(
            """
            package A

            struct Targets {
                var colour: float4
                var visible: bool
            }

            shader S {
                [FragmentShader]
                func Fragment(): Targets {
                    var t: Targets
                    t.colour = float4(1, 1, 1, 1)
                    t.visible = true
                    return t
                }
            }

            """,
            out var reported,
            target
        );

        Assert.Contains(reported, d => d.Id == "RVN4001" && d.IsError);
    }

    /// <summary>
    ///     A struct member only one permutation stores — the shape of <c>ForwardPlus.SplitOutputs</c>,
    ///     where one shader is both halves of an MRT split.
    /// </summary>
    const string Gated = """
                         package A

                         struct SplitTargets {
                             var color: float4
                             var normal: float4
                         }

                         shader S {
                             [Permutation] val Split: bool = false

                             var tint: float4

                             [FragmentShader]
                             func Fragment(normalWS: float3): SplitTargets {
                                 var t: SplitTargets
                                 t.color = tint

                                 if (Split) {
                                     t.normal = float4(normalWS, 1f)
                                 }

                                 return t
                             }
                         }

                         """;

    /// <summary>
    ///     A target nothing writes is not an output — the streams' rule, applied to the other end
    ///     of the fragment stage. With the permutation folded off, the member it guarded was never
    ///     stored, so the pipeline is owed one attachment rather than two.
    /// </summary>
    [Fact]
    public void A_member_stored_only_under_a_false_permutation_is_not_an_output() {
        var module = LoweringTestBase.Lower(Gated);
        var entry = Assert.Single(LoweringTestBase.FindShader(module, "S").EntryPoints);

        var output = Assert.Single(entry.Outputs);
        Assert.Equal("color", output.Name);
        Assert.Equal(0, output.Member);
    }

    [Fact]
    public void The_same_member_is_an_output_when_the_permutation_is_on() {
        var tree = SyntaxTree.ParseText(Gated, path: "Test.rvn");
        var values = PermutationValues.Create([new KeyValuePair<string, object>("Split", true)]);
        var compilation = Compilation.Create("Test", values, [tree]);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        Assert.Empty(bag.ToArray());

        var entry = Assert.Single(LoweringTestBase.FindShader(module, "S").EntryPoints);

        Assert.Equal(["color", "normal"], entry.Outputs.Select(o => o.Name));
        Assert.Equal([0, 1], entry.Outputs.Select(o => o.Member));
    }

    /// <summary>
    ///     The GLSL for the folded-off variant declares one <c>out</c> and extracts one member —
    ///     so a host that binds a single attachment gets a shader that writes a single attachment.
    /// </summary>
    [Fact]
    public void GLSL_for_the_folded_variant_declares_only_the_written_target() {
        var unit = Assert.Single(GenerateClean(Gated));

        Assert.Contains("layout(location = 0) out vec4 out_color;", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("out_normal", unit.Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The survivors keep the location their member index gives them — dropping a target leaves
    ///     a hole rather than renumbering its neighbours, exactly as pruned vertex inputs do.
    /// </summary>
    [Fact]
    public void A_pruned_target_leaves_a_hole_rather_than_renumbering() {
        const string Middle = """
                              package A

                              struct Holes {
                                  var color: float4
                                  var extra: float4
                                  var normal: float4
                              }

                              shader S {
                                  [Permutation] val Extra: bool = false

                                  [FragmentShader]
                                  func Fragment(normalWS: float3): Holes {
                                      var t: Holes
                                      t.color = float4(1f, 0f, 0f, 1f)

                                      if (Extra) {
                                          t.extra = float4(0f, 1f, 0f, 1f)
                                      }

                                      t.normal = float4(normalWS, 1f)
                                      return t
                                  }
                              }

                              """;

        var unit = Assert.Single(GenerateClean(Middle));

        Assert.Contains("layout(location = 0) out vec4 out_color;", unit.Code, StringComparison.Ordinal);
        Assert.Contains("layout(location = 2) out vec4 out_normal;", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("out_extra", unit.Code, StringComparison.Ordinal);

        var reflection = ReflectionOf(Middle);
        Assert.Equal([0, 2], reflection.Outputs.Select(o => o.Location));
    }

    /// <summary>
    ///     A whole-value store hides what each member carries, so nothing is pruned — keeping a
    ///     spare target is recoverable, dropping a written one is a black attachment.
    /// </summary>
    [Fact]
    public void A_wholesale_copy_keeps_every_member() {
        const string Copied = """
                              package A

                              struct Pair {
                                  var color: float4
                                  var normal: float4
                              }

                              shader S {
                                  func Make(): Pair {
                                      var p: Pair
                                      p.color = float4(1f, 1f, 1f, 1f)
                                      return p
                                  }

                                  [FragmentShader]
                                  func Fragment(): Pair {
                                      var t = Make()
                                      return t
                                  }
                              }

                              """;

        var module = LoweringTestBase.Lower(Copied);
        var entry = Assert.Single(LoweringTestBase.FindShader(module, "S").EntryPoints);

        Assert.Equal(2, entry.Outputs.Count);
    }

    static RavenReflection ReflectionOf(string source) =>
        ReflectionBuilder.Describe(LoweringTestBase.FindShader(LoweringTestBase.Lower(source), "S"), []);
}
