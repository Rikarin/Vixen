// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
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

                                [FragmentShader]
                                func Fragment(): float4 {
                                    return tint
                                }
                            }

                            """;

    // --- Descriptor sets and offsets ----------------------------------------

    [Fact]
    public void Uniforms_are_gathered_into_one_block_and_resources_get_their_own_bindings() {
        var reflection = Describe(Material);
        var set = Assert.Single(reflection.Sets);

        // Unmarked fields are material parameters, which is set 2 in the engine's
        // convention — not set 0, which is the per-frame camera and lighting state.
        Assert.Equal((int)ResourceSet.PerMaterial, set.Set);
        Assert.Equal(2, set.Bindings.Length);

        Assert.Equal(DescriptorType.UniformBuffer, set.Bindings[0].Type);
        Assert.Equal("SPerMaterialUniforms", set.Bindings[0].Name);

        Assert.Equal(DescriptorType.SampledTexture, set.Bindings[1].Type);
        Assert.Equal("albedo", set.Bindings[1].Name);
        Assert.Empty(set.Bindings[1].Members);
    }

    /// <summary>
    ///     The four-set convention of docs/plan/05: a marker names the update frequency and the
    ///     set index follows from it, so a shader never spells a set number.
    /// </summary>
    [Fact]
    public void A_marker_places_a_binding_in_its_set_and_bindings_restart_within_each_set() {
        var reflection = Describe(
            """
            package A

            shader S {
                [PerFrame] var time: float
                [PerView] var viewProjection: mat4
                var tint: float4
                var albedo: Texture2D
                var linear: Sampler
                [PerDraw] var world: mat4

                [FragmentShader]
                func Fragment(uv: float2): float4 {
                    return albedo.Sample(linear, uv) * tint * time + viewProjection * world * tint
                }
            }

            """
        );

        Assert.Equal([0, 1, 2, 3], reflection.Sets.Select(s => s.Set));

        Assert.Equal(["SPerFrameUniforms"], reflection.Sets[0].Bindings.Select(b => b.Name));
        Assert.Equal(["SPerViewUniforms"], reflection.Sets[1].Bindings.Select(b => b.Name));
        Assert.Equal(["SPerDrawUniforms"], reflection.Sets[3].Bindings.Select(b => b.Name));

        // Each set is its own binding namespace, so every set starts again at 0.
        Assert.Equal([0], reflection.Sets[0].Bindings.Select(b => b.Binding));
        Assert.Equal([0, 1, 2], reflection.Sets[2].Bindings.Select(b => b.Binding));
        Assert.Equal(
            ["SPerMaterialUniforms", "albedo", "linear"],
            reflection.Sets[2].Bindings.Select(b => b.Name)
        );
    }

    [Fact]
    public void A_parameter_reports_the_set_its_marker_put_it_in() {
        var parameters = Describe(
                """
                package A

                shader S {
                    [PerFrame] var time: float
                    var tint: float4

                    [FragmentShader]
                    func Fragment(): float4 {
                        return tint * time
                    }
                }

                """
            )
            .Parameters;

        Assert.Equal((int)ResourceSet.PerFrame, Assert.Single(parameters, p => p.Name == "time").Set);
        Assert.Equal((int)ResourceSet.PerMaterial, Assert.Single(parameters, p => p.Name == "tint").Set);
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

                        [FragmentShader]
                        func Fragment(): float4 {
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
                    return float4(position.x, position.y, uv.x + uv.y, 1.0f)
                }
            }

            """
        );

        Assert.Equal([0, 1], reflection.VertexInputs.Select(i => i.Location));
        Assert.Equal(["position", "uv"], reflection.VertexInputs.Select(i => i.Name));
        Assert.Equal(["POSITION", "TEXCOORD0"], reflection.VertexInputs.Select(i => i.Semantic));
        Assert.Equal([3, 2], reflection.VertexInputs.Select(i => i.Type.Rows));
    }

    /// <summary>
    ///     ⚠ This list is the vertex layout a host binds against, so an input the module does not
    ///     declare must not be in it — a described attribute at a location the pipeline has no
    ///     variable at is a pipeline that fails to create, which is the whole reason
    ///     <c>IrEntryPoint.InputsRead</c> exists.
    /// </summary>
    /// <remarks>
    ///     The surviving inputs keep the locations they had. Numbering is by declaration index, so
    ///     dropping <c>uv</c> leaves location 1 empty rather than moving <c>colour</c> down into it
    ///     — otherwise adding a permutation that stops reading an attribute would silently renumber
    ///     every attribute after it.
    /// </remarks>
    [Fact]
    public void An_input_the_stage_never_reads_is_not_in_the_layout_and_does_not_renumber_the_rest() {
        var reflection = Describe(
            """
            package A

            shader S {
                [VertexShader]
                func Vertex(position: float3, uv: float2, colour: float4): float4 {
                    return float4(position.x, position.y, colour.z, 1.0f)
                }
            }

            """
        );

        Assert.Equal(["position", "colour"], reflection.VertexInputs.Select(i => i.Name));
        Assert.Equal([0, 2], reflection.VertexInputs.Select(i => i.Location));
    }

    /// <summary>
    ///     The same shader, one permutation apart: the attribute is in the layout of the variant
    ///     that reads it and absent from the variant that does not.
    /// </summary>
    /// <remarks>
    ///     <c>ShadowCaster</c> is the case this was written for. Its vertex stage takes bone indices
    ///     and weights because a parameter cannot appear inside a permutation, and with
    ///     <c>Skinned</c> off the branch that reads them folds away — leaving two attributes a
    ///     static-mesh vertex format has no data for and no layout could satisfy.
    /// </remarks>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void A_permutation_that_folds_away_the_only_read_takes_the_attribute_with_it(
        bool skinned,
        int expected
    ) {
        var reflection = Describe(
            """
            package A

            shader S {
                [Permutation] val Skinned: bool = false

                [VertexShader]
                func Vertex(position: float3, weights: float4): float4 {
                    var offset = 0f

                    if (Skinned) {
                        offset = weights.x
                    }

                    return float4(position.x, position.y, offset, 1.0f)
                }
            }

            """,
            PermutationValues.Parse([$"Skinned={(skinned ? "true" : "false")}"])
        );

        Assert.Equal(expected, reflection.VertexInputs.Length);
    }

    [Fact]
    public void Bindings_record_which_stages_reference_them() {
        var reflection = Describe(Material);
        var block = Assert.Single(reflection.Sets).Bindings[0];

        Assert.Equal(ShaderStages.Fragment, block.Stages);
        Assert.Equal([ShaderStage.Fragment], reflection.Stages);
    }

    // --- The flattened parameter list ---------------------------------------

    [Fact]
    public void Parameters_carry_the_set_binding_and_offset_needed_to_write_them() {
        var parameters = Describe(Material).Parameters;

        var direction = Assert.Single(parameters, p => p.Name == "direction");
        Assert.Equal((int)ResourceSet.PerMaterial, direction.Set);
        Assert.Equal(0, direction.Binding);
        Assert.Equal(32, direction.Offset);
        Assert.Equal(12, direction.Size);
    }

    [Fact]
    public void An_opaque_resource_contributes_no_parameters() =>
        Assert.DoesNotContain("albedo", Describe(Material).Parameters.Select(p => p.Name));

    /// <summary>
    ///     A uniform's initialiser is reported, because it is the <em>host's</em> default.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A uniform's initialiser never runs anywhere: the block arrives already filled. So
    ///         <c>var exposure: float = 1f</c> is a statement about what a host should put there when
    ///         it has nothing of its own to say — and until this was carried, a buffer writer filling
    ///         only the parameters somebody set gave it zero, which is a black frame produced by a
    ///         parameter nobody touched.
    ///     </para>
    ///     <para>
    ///         Text in the invariant spelling, matching how a permutation's default is reported, so a
    ///         generator that cannot reference this assembly reads one format for both.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_uniforms_initialiser_is_reported_as_its_default() {
        var parameters = Describe(
            """
            package A

            shader S {
                var exposure: float = 2.5f
                var samples: int = 8
                var enabled: uint = 1u
                var tint: float4

                [FragmentShader]
                func Fragment(): float4 {
                    return tint * exposure
                }
            }

            """
        ).Parameters;

        Assert.Equal("2.5", Assert.Single(parameters, p => p.Name == "exposure").DefaultValue);
        Assert.Equal("8", Assert.Single(parameters, p => p.Name == "samples").DefaultValue);
        // A uint rather than the bool this used to be: a binding cannot hold a boolean (RVN2137),
        // so the "true"/"false" spelling is a permutation default's business and is asserted there.
        Assert.Equal("1", Assert.Single(parameters, p => p.Name == "enabled").DefaultValue);

        // Nothing written is nothing reported, rather than a zero that looks authored.
        Assert.Equal(string.Empty, Assert.Single(parameters, p => p.Name == "tint").DefaultValue);
    }

    /// <summary>
    ///     A default belongs to the member the author wrote, not to a struct's fields.
    /// </summary>
    /// <remarks>
    ///     The same struct used in two blocks would otherwise report two answers for one field, and
    ///     which one it reported would depend on the order they were described in.
    /// </remarks>
    [Fact]
    public void A_structs_fields_do_not_inherit_the_blocks_default() {
        var parameters = Describe(
            """
            package A

            struct Fog {
                var density: float
                var height: float
            }

            shader S {
                var fog: Fog
                var scale: float = 3f

                [FragmentShader]
                func Fragment(): float4 {
                    return float4(fog.density * scale, 0f, 0f, 1f)
                }
            }

            """
        ).Parameters;

        Assert.Equal("3", Assert.Single(parameters, p => p.Name == "scale").DefaultValue);

        Assert.All(
            parameters.Where(p => p.Name.StartsWith("fog", StringComparison.Ordinal)),
            p => Assert.Equal(string.Empty, p.DefaultValue)
        );
    }

    /// <summary>
    ///     A struct array in a uniform block reports its element's layout once, under
    ///     <c>name[].field</c>, with the element stride on every leaf.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A light list is a struct array in a uniform block, and so is every per-instance table
    ///         — so for as long as the element was opaque, a shader's most important parameter was
    ///         the one thing the reflection could not describe. Found by the C# binding generator,
    ///         which had nothing to generate a writer from.
    ///     </para>
    ///     <para>
    ///         One entry per field rather than per element: 64 lights would be 512 entries saying the
    ///         same eight things at a fixed spacing, and the spacing is the stride. Element
    ///         <em>i</em>'s field is <c>Offset + i * ArrayStride</c>, which is why a leaf reports the
    ///         enclosing element's stride rather than its own zero.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_struct_array_reports_its_element_layout_once_with_the_stride_on_each_leaf() {
        var parameters = Describe(
            """
            package A

            struct Light {
                var position: float3
                var range: float
                var color: float3
                var intensity: float
            }

            shader S {
                var lights: Light[4]

                [FragmentShader]
                func Fragment(): float4 {
                    return float4(lights[0].color, lights[1].range)
                }
            }

            """
        ).Parameters;

        Assert.Equal(
            ["lights[].position", "lights[].range", "lights[].color", "lights[].intensity"],
            parameters.Select(p => p.Name)
        );

        var color = Assert.Single(parameters, p => p.Name == "lights[].color");
        Assert.Equal(16, color.Offset);
        Assert.Equal(12, color.Size);

        // 32 is the element's std140 stride, so `lights[2].color` is at 16 + 2 * 32.
        Assert.All(parameters, p => Assert.Equal(32, p.ArrayStride));

        // The aggregate itself is not writable through this list — it has no scalar type to write —
        // so it appears in the block's members and not here.
        Assert.DoesNotContain("lights", parameters.Select(p => p.Name));
    }

    // --- Capabilities and permutation keys ----------------------------------

    [Fact]
    public void Capabilities_and_used_keys_travel_with_the_reflection() {
        const string Source = """
                              package A

                              shader S {
                                  [Permutation] val Precise: bool = false

                                  var tint: float4
                                  var volume: Texture3D

                                  [FragmentShader]
                                  func Fragment(): float4 {
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

                                  [FragmentShader]
                                  func Fragment(): float4 {
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

    // --- What the shader can be varied by -----------------------------------

    const string Variants = """
                            package A

                            shader S<val TapCount: int> {
                                [Permutation] val UseDetail: bool = false
                                [Permutation] val CascadeCount: int = 4
                                [Permutation] val Unread: uint = 7u

                                var tint: float4

                                [FragmentShader]
                                func Fragment(): float4 {
                                    if (UseDetail) {
                                        return tint * float(CascadeCount) * float(TapCount)
                                    }

                                    return tint
                                }
                            }

                            """;

    static RavenReflection DescribeVariants(PermutationValues? values = null) =>
        Describe(Variants, values ?? PermutationValues.Parse(["TapCount=8"]));

    /// <summary>
    ///     The declared keys, with their types and defaults — what a host varies and what the C#
    ///     key generator turns into a <c>PermutationKey</c>.
    /// </summary>
    [Fact]
    public void Declared_permutation_keys_are_reported_with_their_type_and_default() {
        var permutations = DescribeVariants().Permutations;

        Assert.Equal(["UseDetail", "CascadeCount", "Unread"], permutations.Select(p => p.Name));
        Assert.Equal(["false", "4", "7"], permutations.Select(p => p.DefaultValue));
        Assert.Equal(
            [IrTypeKind.Bool, IrTypeKind.Int, IrTypeKind.UInt],
            permutations.Select(p => p.Type.Scalar)
        );
    }

    /// <summary>
    ///     The distinction that makes this worth having: <c>Unread</c> is declared but never read,
    ///     so it is absent from the cache key and present here. A generator using the cache key
    ///     would emit an API that changed shape with the variant.
    /// </summary>
    [Fact]
    public void A_declared_key_is_reported_even_when_this_variant_never_read_it() {
        var reflection = DescribeVariants();

        Assert.Contains("Unread", reflection.Permutations.Select(p => p.Name));
        Assert.DoesNotContain("Unread", reflection.UsedPermutationKeys);
    }

    /// <summary>
    ///     And the shape is stable across variants, which is the property a generated C# API
    ///     depends on — even when folding makes the variants read wildly different key sets.
    /// </summary>
    [Fact]
    public void The_declared_keys_are_the_same_for_every_variant() {
        var off = DescribeVariants(PermutationValues.Parse(["TapCount=8", "UseDetail=false"]));
        var on = DescribeVariants(PermutationValues.Parse(["TapCount=8", "UseDetail=true"]));

        Assert.Equal(off.Permutations, on.Permutations);
        Assert.Equal(off.ValueParameters, on.ValueParameters);

        // The read set does differ, which is exactly why the two are separate.
        Assert.NotEqual(off.UsedPermutationKeys, on.UsedPermutationKeys);
    }

    /// <summary>
    ///     Describing a shader must not change what it compiled to. Reading a permutation key is
    ///     what records a use, so a reflection pass that read values the body never touched would
    ///     silently add cache entries — the exact waste the used-key economy exists to avoid.
    /// </summary>
    [Fact]
    public void Describing_a_shader_does_not_add_to_the_used_keys() {
        var (compilation, module) = Compile(Variants, PermutationValues.Parse(["TapCount=8"]));
        var before = compilation.UsedPermutationKeys.ToArray();

        var reflection = ReflectionBuilder.Describe(FindShader(module, "S"), compilation.UsedPermutationKeys);

        Assert.NotEmpty(reflection.Permutations);
        Assert.Equal(before, compilation.UsedPermutationKeys);
    }

    /// <summary>
    ///     A value parameter is reported without a default, because it has none: a host must supply
    ///     one, so a generator emits it as a required argument rather than a key with a fallback.
    /// </summary>
    [Fact]
    public void A_value_parameter_is_reported_as_required_rather_than_defaulted() {
        var parameter = Assert.Single(DescribeVariants().ValueParameters);

        Assert.Equal("TapCount", parameter.Name);
        Assert.Equal(IrTypeKind.Int, parameter.Type.Scalar);

        // It is not mixed in with the defaulted keys.
        Assert.DoesNotContain("TapCount", DescribeVariants().Permutations.Select(p => p.Name));
    }

    [Fact]
    public void A_shader_with_nothing_to_vary_reports_neither() {
        var reflection = Describe(Material);

        Assert.Empty(reflection.Permutations);
        Assert.Empty(reflection.ValueParameters);
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
                [FragmentShader]
                func Fragment(): float4 {
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

                [FragmentShader]
                func Fragment(): float4 {
                    return a
                }
            }

            shader Second {
                var b: float4

                [FragmentShader]
                func Fragment(): float4 {
                    return b
                }
            }

            """
        );

        var all = ReflectionBuilder.Describe(module, compilation.UsedPermutationKeys);

        Assert.Equal(["First", "Second"], all.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("FirstPerMaterialUniforms", Assert.Single(all["First"].Sets).Bindings[0].Name);
    }

    // --- Declared defaults --------------------------------------------------

    /// <summary>
    ///     A vector's declared default is reported, not only a scalar's.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The asymmetry this exists to prevent: a scalar's default is a <em>literal</em> and
    ///         a vector's is a <em>construction</em>.</b> <c>SourceFieldSymbol.DeclaredValue</c> read
    ///         the first and answered null for the second, so a shader declaring
    ///         <c>float4(1f, 1f, 1f, 1f)</c> reported no default at all — and everything downstream
    ///         treats "no default" as zero.
    ///     </para>
    ///     <para>
    ///         What that cost was a picture: <c>ParticleSprite.tint</c> arrived at the GPU as
    ///         <c>(0, 0, 0, 0)</c>, which is black at zero alpha, which under an additive blend is
    ///         invisible. Reported as an emitter that had stopped working, with every counter saying
    ///         the effects were running and the draws were issued.
    ///     </para>
    ///     <para>
    ///         The lanes are comma separated because the reflection is JSON read by a source generator
    ///         that has no Raven types in it — see <c>ReflectionBuilder.Format</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_vectors_declared_default_is_reported_as_its_lanes() {
        var reflection = Describe(
            """
            package A

            shader S {
                var tint: float4 = float4(1f, 0.5f, 0.25f, 1f)
                var scale: float2 = float2(0.001f, 0.002f)
                var emissive: float = 2f

                [FragmentShader]
                func Fragment(): float4 {
                    return tint * emissive * float4(scale, 0f, 0f)
                }
            }

            """
        );

        var members = Assert.Single(Assert.Single(reflection.Sets).Bindings).Members;

        Assert.Equal("1, 0.5, 0.25, 1", Member(members, "tint").DefaultValue);
        Assert.Equal("0.001, 0.002", Member(members, "scale").DefaultValue);

        // And the scalar path is untouched, which is what says this widened the answer rather than
        // replacing it.
        Assert.Equal("2", Member(members, "emissive").DefaultValue);
    }

    /// <summary>
    ///     A one-argument vector default is every lane, not one.
    /// </summary>
    /// <remarks>
    ///     <c>float3(0.5f)</c> broadcasts across the lanes, and the binder does not expand it — the
    ///     construction it produces has a single argument. A fold that walked the arguments would
    ///     answer with one number for a three-lane vector, and a host writing that would fill the
    ///     first lane and leave two at zero: a default that is wrong in a way nobody would look for.
    /// </remarks>
    [Fact]
    public void A_broadcast_default_fills_every_lane() {
        var reflection = Describe(
            """
            package A

            shader S {
                var grey: float3 = float3(0.5f)

                [FragmentShader]
                func Fragment(): float4 {
                    return float4(grey, 1f)
                }
            }

            """
        );

        var members = Assert.Single(Assert.Single(reflection.Sets).Bindings).Members;

        Assert.Equal("0.5, 0.5, 0.5", Member(members, "grey").DefaultValue);
    }

    /// <summary>A block member by name, so a failure names the parameter rather than an index.</summary>
    static MemberInfo Member(System.Collections.Immutable.ImmutableArray<MemberInfo> members, string name) =>
        Assert.Single(members, member => member.Name == name);
}
