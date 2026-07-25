// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax.Diagnostics;
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     The interface the engine binds against: descriptor sets, explicit offsets, the flattened
///     parameter list, capabilities and the permutation keys that mattered.
/// </summary>
public class ReflectionTests {
    static (Compilation Compilation, IrModule Module) Compile(string source, PermutationValues? values = null) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", values ?? PermutationValues.Empty, [tree]);
        var semantic = compilation.GetDiagnostics();
        Assert.True(
            semantic.Count == 0,
            "Expected no semantic diagnostics, got:\n" + string.Join("\n", semantic.Select(d => d.ToString()))
        );

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return (compilation, module);
    }

    static RavenReflection Describe(string source, PermutationValues? values = null, string shader = "S") {
        var (compilation, module) = Compile(source, values);
        return ReflectionBuilder.Describe(FindShader(module, shader), compilation.UsedPermutationKeys);
    }

    const string Material = """
                            package A

                            shader S {
                                var tint: float4
                                var roughness: float
                                var direction: float3
                                var albedo: Texture2D

                                [PixelShader]
                                func Pixel(): float4 {
                                    return tint
                                }
                            }

                            """;

    // --- Descriptor sets and offsets ----------------------------------------

    [Fact]
    public void Uniforms_are_gathered_into_one_block_and_resources_get_their_own_bindings() {
        var reflection = Describe(Material);
        var set = Assert.Single(reflection.Sets);

        Assert.Equal(0, set.Set);
        Assert.Equal(2, set.Bindings.Length);

        Assert.Equal(DescriptorType.UniformBuffer, set.Bindings[0].Type);
        Assert.Equal("SUniforms", set.Bindings[0].Name);

        Assert.Equal(DescriptorType.SampledTexture, set.Bindings[1].Type);
        Assert.Equal("albedo", set.Bindings[1].Name);
        Assert.Empty(set.Bindings[1].Members);
    }

    /// <summary>
    ///     The requirement doc 07 § D calls out: explicit offsets on every member, so the engine
    ///     writes by generated offset rather than looking anything up at draw time.
    /// </summary>
    [Fact]
    public void Every_block_member_carries_an_explicit_offset_and_size() {
        var block = Assert.Single(Describe(Material).Sets).Bindings[0];

        Assert.Equal(["tint", "roughness", "direction"], block.Members.Select(m => m.Name));

        // float4 at 0 (16 bytes), float at 16 (4), float3 aligns to 32 — not 20.
        Assert.Equal([0, 16, 32], block.Members.Select(m => m.Offset));
        Assert.Equal([16, 4, 12], block.Members.Select(m => m.Size));
        Assert.Equal(48, block.Size);
    }

    [Fact]
    public void A_matrix_member_reports_its_stride_and_a_scalar_reports_none() {
        var block = Assert.Single(
                Describe(
                    """
                    package A

                    shader S {
                        var transform: mat4
                        var scale: float

                        [PixelShader]
                        func Pixel(): float4 {
                            return float4(scale, scale, scale, scale)
                        }
                    }

                    """
                )
                .Sets
            )
            .Bindings[0];

        var transform = Assert.Single(block.Members, m => m.Name == "transform");
        Assert.Equal(16, transform.MatrixStride);
        Assert.Equal(64, transform.Size);
        Assert.True(transform.Type.IsMatrix);

        var scale = Assert.Single(block.Members, m => m.Name == "scale");
        Assert.Equal(0, scale.MatrixStride);
        Assert.Equal(0, scale.ArrayStride);
    }

    /// <summary>
    ///     The numbers reflection reports have to be the numbers the backend decorated, or the
    ///     host writes into the wrong place on one backend and not the other. They come from one
    ///     <see cref="ShaderLayout" />, so this checks the wiring rather than the arithmetic.
    /// </summary>
    [Fact]
    public void Reported_offsets_match_what_the_layout_engine_gives_the_backend() {
        var (_, module) = Compile(Material);
        var shader = FindShader(module, "S");
        var block = Assert.Single(ReflectionBuilder.Describe(shader).Sets).Bindings[0];

        var uniformTypes = shader.Bindings
            .Where(b => b.Kind == IrBindingKind.Uniform)
            .Select(b => b.Type)
            .ToArray();

        var (offsets, size) = ShaderLayout.Members(uniformTypes);

        Assert.Equal(offsets, block.Members.Where(m => !m.Name.Contains('.', StringComparison.Ordinal))
            .Select(m => m.Offset));
        Assert.Equal(size, block.Size);
    }

    // --- Stage interface ----------------------------------------------------

    [Fact]
    public void A_pixel_shader_reports_its_output_and_no_vertex_inputs() {
        var reflection = Describe(Material);

        Assert.Empty(reflection.VertexInputs);
        var output = Assert.Single(reflection.Outputs);
        Assert.Equal(0, output.Location);
        Assert.Equal(4, output.Type.Rows);
    }

    [Fact]
    public void A_vertex_shader_reports_its_inputs_with_locations_and_semantics() {
        var reflection = Describe(
            """
            package A

            shader S {
                [VertexShader]
                func Vertex([Semantic("POSITION")] position: float3, [Semantic("TEXCOORD0")] uv: float2): float4 {
                    return float4(position.x, position.y, position.z, 1.0f)
                }
            }

            """
        );

        Assert.Equal([0, 1], reflection.VertexInputs.Select(i => i.Location));
        Assert.Equal(["position", "uv"], reflection.VertexInputs.Select(i => i.Name));
        Assert.Equal(["POSITION", "TEXCOORD0"], reflection.VertexInputs.Select(i => i.Semantic));
        Assert.Equal([3, 2], reflection.VertexInputs.Select(i => i.Type.Rows));
    }

    [Fact]
    public void Bindings_record_which_stages_reference_them() {
        var reflection = Describe(Material);
        var block = Assert.Single(reflection.Sets).Bindings[0];

        Assert.Equal(ShaderStages.Pixel, block.Stages);
        Assert.Equal([ShaderStage.Pixel], reflection.Stages);
    }

    // --- The flattened parameter list ---------------------------------------

    [Fact]
    public void Parameters_carry_the_set_binding_and_offset_needed_to_write_them() {
        var parameters = Describe(Material).Parameters;

        var direction = Assert.Single(parameters, p => p.Name == "direction");
        Assert.Equal(0, direction.Set);
        Assert.Equal(0, direction.Binding);
        Assert.Equal(32, direction.Offset);
        Assert.Equal(12, direction.Size);
    }

    [Fact]
    public void An_opaque_resource_contributes_no_parameters() =>
        Assert.DoesNotContain("albedo", Describe(Material).Parameters.Select(p => p.Name));

    // --- Capabilities and permutation keys ----------------------------------

    [Fact]
    public void Capabilities_and_used_keys_travel_with_the_reflection() {
        const string Source = """
                              package A

                              shader S {
                                  [Permutation] val Precise: bool = false

                                  var tint: float4
                                  var volume: Texture3D

                                  [PixelShader]
                                  func Pixel(): float4 {
                                      if (Precise) {
                                          var wide = 2.0
                                          return tint * float(wide)
                                      }

                                      return tint
                                  }
                              }

                              """;

        var off = Describe(Source);
        Assert.Equal([IrCapability.Texture3D], off.RequiredCapabilities);
        Assert.Equal(["Precise"], off.UsedPermutationKeys);

        var on = Describe(Source, PermutationValues.Parse(["Precise=true"]));
        Assert.Equal([IrCapability.Float64, IrCapability.Texture3D], on.RequiredCapabilities);
    }

    /// <summary>
    ///     A value behind a false permutation is gone by the time the IR is read, so the
    ///     reported interface is the one this variant actually has — not the union of every
    ///     variant's.
    /// </summary>
    [Fact]
    public void The_reported_interface_is_the_variants_own() {
        const string Source = """
                              package A

                              shader S {
                                  [Permutation] val UseDetail: bool = false

                                  var tint: float4

                                  [PixelShader]
                                  func Pixel(): float4 {
                                      if (UseDetail) {
                                          return tint * 2.0f
                                      }

                                      return tint
                                  }
                              }

                              """;

        var off = Describe(Source);
        var on = Describe(Source, PermutationValues.Parse(["UseDetail=true"]));

        // Same interface here, but the keys that mattered are recorded either way, which is
        // what the effect cache hashes.
        Assert.Equal(["UseDetail"], off.UsedPermutationKeys);
        Assert.Equal(["UseDetail"], on.UsedPermutationKeys);
    }

    [Fact]
    public void Push_and_spec_constants_are_reported_as_absent_rather_than_guessed() {
        var reflection = Describe(Material);

        Assert.Empty(reflection.PushConstants);
        Assert.Empty(reflection.SpecConstants);
    }

    [Fact]
    public void A_shader_with_no_state_reports_no_sets() {
        var reflection = Describe(
            """
            package A

            shader S {
                [PixelShader]
                func Pixel(): float4 {
                    return float4(1.0f, 1.0f, 1.0f, 1.0f)
                }
            }

            """
        );

        Assert.Empty(reflection.Sets);
        Assert.Empty(reflection.Parameters);
    }

    [Fact]
    public void Every_shader_in_a_module_can_be_described_at_once() {
        var (compilation, module) = Compile(
            """
            package A

            shader First {
                var a: float4

                [PixelShader]
                func Pixel(): float4 {
                    return a
                }
            }

            shader Second {
                var b: float4

                [PixelShader]
                func Pixel(): float4 {
                    return b
                }
            }

            """
        );

        var all = ReflectionBuilder.Describe(module, compilation.UsedPermutationKeys);

        Assert.Equal(["First", "Second"], all.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("FirstUniforms", Assert.Single(all["First"].Sets).Bindings[0].Name);
    }
}
