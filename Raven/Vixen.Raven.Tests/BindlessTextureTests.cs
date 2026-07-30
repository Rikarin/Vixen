// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.LoweringTestBase;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     <c>Texture2D[]</c> — the one unsized array that is not memory, and the shader half of
///     <c>docs/plan/23-bindless-materials.md</c>.
/// </summary>
/// <remarks>
///     <para>
///         An array with no length was <c>RVN2126</c> everywhere but the last member of a storage
///         block, and the reasoning behind that is still right for every case it was written about:
///         both targets need a constant extent to lay an array out, and the host reads the extent
///         back to size what it uploads. A texture array is the case the rule did not have. It is an
///         array of <em>descriptors</em>, which are not laid out at all — no stride, nothing packed,
///         nothing to size — so the length it lacks is not missing information.
///     </para>
///     <para>
///         Three things have to be true together, and each is invisible on its own. The type must be
///         a runtime array with no <c>ArrayStride</c>; the module must declare the capabilities that
///         make it legal; and the index must be marked non-uniform, or a driver may read one
///         descriptor for a whole subgroup. The last is the one that produces a picture rather than
///         an error — a merged draw sampling one material's texture on every fragment looks exactly
///         like a merge that worked.
///     </para>
/// </remarks>
public class BindlessTextureTests {
    /// <summary>A table, an index from the per-draw block, and one sample through it.</summary>
    const string Source = """
                          package A

                          shader S {
                              var textures: Texture2D[]
                              var linear: Sampler
                              [PerDraw] var albedoIndex: int

                              [FragmentShader]
                              func Fragment(uv: float2): float4 {
                                  return textures[albedoIndex].Sample(linear, uv)
                              }
                          }

                          """;

    /// <summary>An unsized texture array binds, where every other unsized array is RVN2126.</summary>
    [Fact]
    public void An_unsized_texture_array_is_a_type() => Assert.Empty(Errors(Source));

    /// <summary>
    ///     And every other element type still is not.
    /// </summary>
    /// <remarks>
    ///     The rule is not "unsized arrays are allowed now". A <c>float[]</c> has a stride the host
    ///     needs and no way to say how many, which is what <c>Buffer&lt;T&gt;</c> is for — and
    ///     <c>RVN2126</c> says so in a message that names the two ways out.
    /// </remarks>
    [Theory]
    [InlineData("float")]
    [InlineData("float4")]
    [InlineData("Sampler")]
    public void An_unsized_array_of_anything_else_still_needs_a_length(string element) {
        var errors = Errors(
            $$"""
              package A

              shader S {
                  var values: {{element}}[]

                  [FragmentShader]
                  func Fragment(): float4 {
                      return float4(1, 1, 1, 1)
                  }
              }

              """
        );

        Assert.Contains(errors, d => d.Id == "RVN2126");
    }

    /// <summary>
    ///     A descriptor array is flat, so a second dimension is still refused.
    /// </summary>
    /// <remarks>
    ///     Both spellings, and the first is the one that would have slipped through: an
    ///     <c>ArrayTypeSymbol</c> reports its <em>element's</em> resource kind, so a
    ///     <c>Texture2D[]</c> reached as the element of an outer rank answers "texture" and would let
    ///     the outer <c>[]</c> through — producing a runtime array of runtime arrays, which is a type
    ///     neither target has.
    /// </remarks>
    [Theory]
    [InlineData("Texture2D[][]")]
    [InlineData("Texture2D[,]")]
    public void A_descriptor_array_has_one_dimension(string type) {
        var errors = Errors(
            $$"""
              package A

              shader S {
                  var textures: {{type}}

                  [FragmentShader]
                  func Fragment(): float4 {
                      return float4(1, 1, 1, 1)
                  }
              }

              """
        );

        Assert.Contains(errors, d => d.Id == "RVN2126");
    }

    /// <summary>Every texture type, since a table of cubes is what per-object probes want.</summary>
    [Theory]
    [InlineData("Texture2D")]
    [InlineData("Texture3D")]
    [InlineData("TextureCube")]
    public void Any_texture_makes_a_table(string texture) =>
        Assert.Empty(
            Errors(
                $$"""
                  package A

                  shader S {
                      var textures: {{texture}}[]

                      [FragmentShader]
                      func Fragment(): float4 {
                          return float4(1, 1, 1, 1)
                      }
                  }

                  """
            )
        );

    /// <summary>
    ///     It reaches the reflection as a binding of count zero.
    /// </summary>
    /// <remarks>
    ///     Which is what the RHI reads: <c>DescriptorBinding.Count == 0</c> on a texture is an
    ///     unbounded array, sized from the device. The same zero on a storage buffer means a
    ///     runtime-sized array inside one descriptor, and
    ///     <c>Vixen.Graphics.DescriptorBindingExtensions.IsUnbounded</c> is where the two are told
    ///     apart — so this asserts the number the two readings both start from.
    /// </remarks>
    [Fact]
    public void The_reflection_reports_a_count_of_zero() {
        var bindings = Reflect(Source).Sets.SelectMany(set => set.Bindings).ToArray();
        var textures = Assert.Single(bindings, b => b.Name == "textures");

        Assert.Equal(0, textures.Count);
        Assert.Equal(DescriptorType.SampledTexture, textures.Type);

        // Beside a bounded one, so the zero is visibly the array's and not every texture's.
        Assert.Equal(1, Assert.Single(bindings, b => b.Name == "linear").Count);
    }

    // --- SPIR-V ------------------------------------------------------------

    /// <summary>
    ///     The type is a runtime array with no stride, and the module says why that is legal.
    /// </summary>
    /// <remarks>
    ///     The absent <c>ArrayStride</c> is as load-bearing as the two capabilities. A storage
    ///     buffer's runtime array carries one because it is memory the host computes offsets into; a
    ///     descriptor array is a range of the descriptor set, and decorating it with a stride is a
    ///     validation error rather than a harmless extra. Sharing one <c>RuntimeArray</c> helper
    ///     between the two is how that would have happened.
    /// </remarks>
    [Fact]
    public void The_module_declares_a_runtime_array_and_the_capabilities_for_it() {
        var listing = SpirvTestBase.One(Source).Code;

        Assert.Contains("OpCapability RuntimeDescriptorArray", listing, StringComparison.Ordinal);
        Assert.Contains("OpCapability ShaderNonUniform", listing, StringComparison.Ordinal);
        Assert.Contains("OpExtension \"SPV_EXT_descriptor_indexing\"", listing, StringComparison.Ordinal);

        var array = Assert.Single(
            listing.Split('\n'),
            line => line.Contains("OpTypeRuntimeArray", StringComparison.Ordinal)
        );

        // The element is the image type, so this is the descriptor array and not a buffer's contents.
        var id = array.Split('=')[0].Trim();
        Assert.DoesNotContain($"OpDecorate {id} ArrayStride", listing, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Both the index and the pointer it produced are decorated <c>NonUniform</c>.
    /// </summary>
    /// <remarks>
    ///     Two decorations rather than one, and the second is the one that matters to a driver: the
    ///     index says the number varies across the subgroup, the access chain's result says the
    ///     <em>descriptor</em> does. A module with only the first validates and then samples one
    ///     texture for every fragment of a merged draw.
    /// </remarks>
    [Fact]
    public void The_index_and_the_pointer_are_both_non_uniform() {
        var listing = SpirvTestBase.One(Source).Code;
        var decorated = DecoratedNonUniform(listing);

        Assert.Equal(2, decorated.Length);

        // Exactly one of the two is an access chain's result and the other is not — which is the
        // pair. Two chains or two indices would be the same count and the wrong thing.
        var chains = listing.Split('\n')
            .Where(line => line.Contains("OpAccessChain", StringComparison.Ordinal))
            .Select(line => line.Split('=')[0].Trim())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Single(decorated, id => chains.Contains(id));
    }

    /// <summary>The ids something decorated <c>NonUniform</c>, which is not the capability's name.</summary>
    static string[] DecoratedNonUniform(string listing) =>
        [
            .. listing.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("OpDecorate ", StringComparison.Ordinal)
                    && line.EndsWith(" NonUniform", StringComparison.Ordinal))
                .Select(line => line.Split(' ')[1])
        ];

    /// <summary>A shader that declares a table and never indexes it decorates nothing.</summary>
    /// <remarks>
    ///     The capabilities still have to be there — the <em>type</em> is what needs them — but
    ///     nothing is non-uniform, because nothing varies. Asserted because the cheap implementation
    ///     of "decorate the index" is "decorate every index", which would put the decoration on every
    ///     array subscript in the shader library.
    /// </remarks>
    [Fact]
    public void A_table_nobody_indexes_decorates_nothing() {
        var listing = SpirvTestBase.One(
            """
            package A

            shader S {
                var textures: Texture2D[]

                [FragmentShader]
                func Fragment(): float4 {
                    return float4(1, 1, 1, 1)
                }
            }

            """
        ).Code;

        // The capability is still declared — the *type* is what needs it — so the assertion is about
        // the decoration and not about the word, which appears in `OpCapability ShaderNonUniform`.
        Assert.Contains("OpTypeRuntimeArray", listing, StringComparison.Ordinal);
        Assert.Contains("OpCapability ShaderNonUniform", listing, StringComparison.Ordinal);
        Assert.Empty(DecoratedNonUniform(listing));
    }

    /// <summary>An ordinary array's index is left alone.</summary>
    /// <remarks>
    ///     The other half of the same guard, and the one a regression would show up in first: every
    ///     sized array in the shader library is subscripted, and a decoration applied to all of them
    ///     is a module that needs a capability for arithmetic.
    /// </remarks>
    [Fact]
    public void A_sized_array_is_indexed_uniformly() {
        var listing = SpirvTestBase.One(
            """
            package A

            shader S {
                var probes: TextureCube[4]
                var linear: Sampler
                [PerDraw] var probeIndex: int

                [FragmentShader]
                func Fragment(direction: float3): float4 {
                    return probes[probeIndex].Sample(linear, direction)
                }
            }

            """
        ).Code;

        Assert.DoesNotContain("NonUniform", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("SPV_EXT_descriptor_indexing", listing, StringComparison.Ordinal);
    }

    /// <summary><c>spirv-val</c> accepts it, which is the only thing that can say the flags agree.</summary>
    [Fact]
    public void The_module_validates() => SpirvTestBase.Validate(SpirvTestBase.One(Source));

    // --- GLSL --------------------------------------------------------------

    /// <summary>The declaration carries an empty extent, and the subscript carries the qualifier.</summary>
    [Fact]
    public void The_glsl_declares_an_empty_extent_and_qualifies_the_index() {
        var glsl = CodeGenTestBase.GenerateClean(Source).Single().Code;

        Assert.Contains("#extension GL_EXT_nonuniform_qualifier : require", glsl, StringComparison.Ordinal);
        Assert.Contains("uniform texture2D textures[];", glsl, StringComparison.Ordinal);
        Assert.Contains("textures[nonuniformEXT(", glsl, StringComparison.Ordinal);
    }

    /// <summary>The extension is not declared by a shader that does not index a table.</summary>
    /// <remarks>
    ///     Declaring an extension a unit does not use is not harmless — a driver may reject it — which
    ///     is the rule the prologue already follows for <c>GL_EXT_samplerless_texture_functions</c>.
    /// </remarks>
    [Fact]
    public void The_extension_is_only_declared_where_it_is_used() {
        var glsl = CodeGenTestBase.GenerateClean(
            """
            package A

            shader S {
                var textures: Texture2D[]

                [FragmentShader]
                func Fragment(): float4 {
                    return float4(1, 1, 1, 1)
                }
            }

            """
        ).Single().Code;

        Assert.Contains("uniform texture2D textures[];", glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("nonuniform_qualifier", glsl, StringComparison.Ordinal);
    }

    // --- The set a table lives in ------------------------------------------

    /// <summary>A table of its own, in set 4.</summary>
    const string Set4 = """
                        package A

                        shader S {
                            [Bindless] var textures: Texture2D[]
                            [PerFrame] var linear: Sampler
                            [PerDraw] var albedoIndex: int

                            [FragmentShader]
                            func Fragment(uv: float2): float4 {
                                return textures[albedoIndex].Sample(linear, uv)
                            }
                        }

                        """;

    /// <summary><c>[Bindless]</c> is set 4, and the sampler beside it is still set 0.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Why a fifth set exists at all.</strong> Sets 0 to 3 are written per frame from
    ///         a content-addressed allocator, so a set whose write list differs by a byte is a
    ///         different set. A table's descriptors are written once each and there may be thousands;
    ///         a table sharing set 0 would be written out again whenever a uniform block moved, which
    ///         is the whole cost it exists to remove.
    ///     </para>
    ///     <para>
    ///         The sampler is the control. It is an ordinary binding a host fills like any other, so
    ///         it stays where a host already looks for it — <c>[Bindless]</c> is not "everything to do
    ///         with textures", it is "the one binding that cannot be written per frame".
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_bindless_marker_puts_the_table_in_set_four() {
        var bindings = Reflect(Set4).Sets.SelectMany(set => set.Bindings.Select(binding => (set.Set, binding))).ToArray();

        var table = Assert.Single(bindings, pair => pair.binding.Name == "textures");
        Assert.Equal((int)ResourceSet.Bindless, table.Set);
        Assert.Equal(0, table.binding.Count);

        var sampler = Assert.Single(bindings, pair => pair.binding.Name == "linear");
        Assert.Equal((int)ResourceSet.PerFrame, sampler.Set);
    }

    /// <summary>And SPIR-V says set 4, which is the number that actually reaches a driver.</summary>
    /// <remarks>
    ///     The reflection above is what builds the descriptor-set layout and this is what the shader
    ///     reads through. A disagreement between them binds a real set at an index the module does
    ///     not sample, which draws with whatever descriptors were left at set 4 — undefined rather
    ///     than absent, and invisible to every check that only reads the reflection.
    /// </remarks>
    [Fact]
    public void The_module_decorates_the_table_with_set_four() {
        var module = SpirvTestBase.One(Set4);
        var listing = module.Code;

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(listing, "textures")} DescriptorSet 4",
            listing,
            StringComparison.Ordinal
        );

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(listing, "linear")} DescriptorSet 0",
            listing,
            StringComparison.Ordinal
        );

        SpirvTestBase.Validate(module);
    }

    /// <summary>The GLSL says so too.</summary>
    [Fact]
    public void The_glsl_declares_the_table_at_set_four() =>
        Assert.Contains(
            "set = 4",
            CodeGenTestBase.GenerateClean(Set4).Single().Code,
            StringComparison.Ordinal
        );

    // --- The fixture -------------------------------------------------------

    /// <summary>The errors a source reports, which for most of these should be none.</summary>
    static IReadOnlyList<Diagnostic> Errors(string source) =>
        [.. Diagnose(source).Where(d => d.IsError)];

    /// <summary>What the reflection says about a shader that compiled.</summary>
    static RavenReflection Reflect(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return ReflectionBuilder.Describe(FindShader(module, "S"), compilation.UsedPermutationKeys);
    }

    /// <summary>And a real compiler accepts what comes out.</summary>
    /// <remarks>
    ///     The half of the gate <c>spirv-val</c> cannot give: <c>glslc</c> reads the declaration and
    ///     the qualifier as a human wrote them, so a shader that validates as SPIR-V and is
    ///     unspellable as GLSL fails here rather than on whichever backend gets there first.
    /// </remarks>
    [Fact]
    public void A_reference_compiler_accepts_the_glsl() {
        if (ReferenceCompiler.Glslc is null) {
            return;
        }

        var unit = CodeGenTestBase.GenerateClean(Source).Single();
        Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
    }
}
