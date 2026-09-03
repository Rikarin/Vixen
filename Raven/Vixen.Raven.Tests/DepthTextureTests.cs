// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     The comparison pair — <c>DepthTexture2D</c> and <c>ComparisonSampler</c> — which is what a
///     shadow map is when the type system knows about it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this closes is not "the library cannot sample a shadow map".</b> It can, and
///         does: <c>PunctualShadows.rvn</c> binds the atlas as a plain <c>Texture2D</c> with a plain
///         <c>Sampler</c> and does the compare in the shader. Vulkan is happy with that. What it
///         costs is everything downstream that has to know a binding is a depth binding — a WebGPU
///         bind group layout entry states <c>sampleType: "depth"</c> and its sampler entry states
///         <c>comparison</c>, and both of those come from the reflection. A texture the reflection
///         calls an ordinary sampled one is a layout the browser refuses.
///     </para>
///     <para>
///         ⚠ <b>So the assertion that matters most here is the reflection one</b>, not the emitted
///         instruction. A backend that emitted the wrong opcode would give a wrong picture, which
///         somebody sees; a reflection that reported the wrong descriptor type gives a pipeline
///         that will not create, on a platform this repository does not yet run tests on.
///     </para>
/// </remarks>
public class DepthTextureTests {
    /// <summary>
    ///     A fragment shader that shadows a pixel — the shape every shadowed shading pass has.
    /// </summary>
    const string Shadowed = """
                            package A

                            shader Shadowed {
                                [PerView] var shadowMap: DepthTexture2D

                                [PerView] var shadowSampler: ComparisonSampler

                                [FragmentShader]
                                func Fragment([Semantic("TEXCOORD0")] uv: float2): float4 {
                                    val lit = shadowMap.SampleCompare(shadowSampler, uv, 0.5)
                                    return float4(lit, lit, lit, 1.0)
                                }
                            }

                            """;

    /// <summary>The same lookup from a compute stage, which has no quad to derive a level from.</summary>
    const string Masked = """
                          package A

                          shader Masked {
                              [PerView] var shadowMap: DepthTexture2D

                              [PerView] var shadowSampler: ComparisonSampler

                              var mask: RWBuffer<float>

                              [ComputeShader(8, 8)]
                              func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                  val size = shadowMap.GetDimensions(0)
                                  val uv = float2(float(id.x) / float(size.x), float(id.y) / float(size.y))

                                  val lit = shadowMap.SampleCompareLevelZero(shadowSampler, uv, 0.5)
                                  mask[int(id.y) * size.x + int(id.x)] = lit
                              }
                          }

                          """;

    // --- What the host is told --------------------------------------------

    /// <summary>
    ///     The reflection reports both halves as their own descriptor types, which is the whole
    ///     point of the pair existing.
    /// </summary>
    [Fact]
    public void The_reflection_reports_a_depth_texture_and_a_comparison_sampler() {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Shadowed), "Shadowed");
        var descriptors = ReflectionBuilder.Describe(shader).Sets.SelectMany(s => s.Bindings).ToArray();

        var texture = Assert.Single(descriptors, b => b.Name == "shadowMap");
        var sampler = Assert.Single(descriptors, b => b.Name == "shadowSampler");

        Assert.Equal(DescriptorType.DepthTexture, texture.Type);
        Assert.Equal(DescriptorType.ComparisonSampler, sampler.Type);
    }

    /// <summary>
    ///     ⚠ The negative half, and the one a flag would have failed: an ordinary texture and an
    ///     ordinary sampler still report as ordinary. A change that made every sampled texture a
    ///     depth one would pass the test above and break every material in the library.
    /// </summary>
    [Fact]
    public void An_ordinary_texture_and_sampler_are_unaffected() {
        var shader = LoweringTestBase.FindShader(
            LoweringTestBase.Lower(
                """
                package A

                shader Plain {
                    var albedo: Texture2D

                    var albedoSampler: Sampler

                    [FragmentShader]
                    func Fragment([Semantic("TEXCOORD0")] uv: float2): float4 {
                        return albedo.Sample(albedoSampler, uv)
                    }
                }

                """
            ),
            "Plain"
        );

        var descriptors = ReflectionBuilder.Describe(shader).Sets.SelectMany(s => s.Bindings).ToArray();

        Assert.Equal(DescriptorType.SampledTexture, Assert.Single(descriptors, b => b.Name == "albedo").Type);
        Assert.Equal(DescriptorType.Sampler, Assert.Single(descriptors, b => b.Name == "albedoSampler").Type);
    }

    /// <summary>Each half lowers to its own IR type rather than to the filtering one.</summary>
    [Fact]
    public void Each_half_lowers_to_its_own_ir_type() {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Shadowed), "Shadowed");

        Assert.IsType<IrDepthTextureType>(Assert.Single(shader.Bindings, b => b.Name == "shadowMap").Type);
        Assert.IsType<IrComparisonSamplerType>(Assert.Single(shader.Bindings, b => b.Name == "shadowSampler").Type);
    }

    // --- GLSL --------------------------------------------------------------

    /// <summary>
    ///     ⚠ The image is a plain <c>texture2D</c> and only the sampler carries the shadow
    ///     spelling — GL_KHR_vulkan_glsl has no <c>texture2DShadow</c>. The reference is packed into
    ///     the coordinate's third lane, which is the one place GLSL's calling convention differs
    ///     from every other target's.
    /// </summary>
    [Fact]
    public void GLSL_pairs_the_two_into_a_shadow_sampler_and_packs_the_reference() {
        var unit = Assert.Single(GenerateClean(Shadowed));

        Assert.Contains("uniform texture2D shadowMap;", unit.Code, StringComparison.Ordinal);
        Assert.Contains("uniform samplerShadow shadowSampler;", unit.Code, StringComparison.Ordinal);
        Assert.Contains("texture(sampler2DShadow(shadowMap, shadowSampler), vec3(", unit.Code, StringComparison.Ordinal);
    }

    /// <summary>The level-zero form is <c>textureLod</c>, which is what makes it legal in compute.</summary>
    [Fact]
    public void GLSL_states_level_zero_for_the_level_zero_form() {
        var unit = Assert.Single(GenerateClean(Masked));

        Assert.Contains("textureLod(sampler2DShadow(shadowMap, shadowSampler),", unit.Code, StringComparison.Ordinal);
        Assert.Contains("0.0)", unit.Code, StringComparison.Ordinal);

        // GetDimensions on a separate image, which is what the extension is for.
        Assert.Contains("textureSize(shadowMap,", unit.Code, StringComparison.Ordinal);
        Assert.Contains("GL_EXT_samplerless_texture_functions", unit.Code, StringComparison.Ordinal);
    }

    /// <summary>Both shapes survive <c>glslc</c>, which is the only oracle that reads them as GLSL.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_generated_GLSL_compiles(bool fragment) {
        Assert.SkipUnless(ReferenceCompiler.Available, "glslc is not on PATH (brew install glslang shaderc).");

        var unit = Assert.Single(GenerateClean(fragment ? Shadowed : Masked));
        ReferenceCompiler.GlslToSpirv(unit.Code, fragment ? ShaderStage.Fragment : ShaderStage.Compute);
    }

    // --- SPIR-V ------------------------------------------------------------

    /// <summary>
    ///     A depth image and the comparing instruction. ⚠ <c>Depth = 1</c> is the operand SPIRV-Cross
    ///     reads to choose <c>texture_depth_2d</c> in WGSL, so it is not decoration.
    /// </summary>
    [Fact]
    public void SPIR_V_declares_a_depth_image_and_compares_against_it() {
        var listing = ReferenceCompiler.Disassemble(SpirvTestBase.One(Shadowed).Binary!);

        Assert.Contains("OpTypeImage %float 2D 1 0 0 1 Unknown", listing, StringComparison.Ordinal);
        Assert.Contains("OpImageSampleDrefImplicitLod", listing, StringComparison.Ordinal);

        // ⚠ And *not* the non-comparing form, which is what a missed arm in the emitter would
        // leave behind: a module that validates, binds, and returns a texel where a comparison
        // was asked for.
        Assert.DoesNotContain("OpImageSampleImplicitLod", listing, StringComparison.Ordinal);
    }

    /// <summary>Outside a fragment stage the level is stated, exactly as a plain sample's is.</summary>
    [Fact]
    public void SPIR_V_states_the_level_in_a_compute_stage() {
        var listing = ReferenceCompiler.Disassemble(SpirvTestBase.One(Masked).Binary!);

        Assert.Contains("OpTypeImage %float 2D 1 0 0 1 Unknown", listing, StringComparison.Ordinal);
        Assert.Contains("OpImageSampleDrefExplicitLod", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpImageSampleDrefImplicitLod", listing, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ One <c>OpTypeSampler</c>, not two. SPIR-V has a single sampler type and the compare
    ///     lives on the instruction — emitting a second, identical declaration for the comparison
    ///     sampler would be a module saying something SPIR-V cannot mean.
    /// </summary>
    [Fact]
    public void SPIR_V_interns_one_sampler_type_for_both_kinds() {
        var listing = ReferenceCompiler.Disassemble(
            SpirvTestBase.One(
                    """
                    package A

                    shader Both {
                        [PerView] var shadowMap: DepthTexture2D

                        [PerView] var shadowSampler: ComparisonSampler

                        var albedo: Texture2D

                        var albedoSampler: Sampler

                        [FragmentShader]
                        func Fragment([Semantic("TEXCOORD0")] uv: float2): float4 {
                            val lit = shadowMap.SampleCompare(shadowSampler, uv, 0.5)
                            return albedo.Sample(albedoSampler, uv) * lit
                        }
                    }

                    """
                )
                .Binary!
        );

        Assert.Equal(
            1,
            listing.Split('\n').Count(line => line.Contains("= OpTypeSampler", StringComparison.Ordinal))
        );

        // The two images stay two, because a depth image and a colour one are two OpTypeImage.
        Assert.Contains("OpTypeImage %float 2D 1 0 0 1 Unknown", listing, StringComparison.Ordinal);
        Assert.Contains("OpTypeImage %float 2D 0 0 0 1 Unknown", listing, StringComparison.Ordinal);
    }

    // --- What the pairing refuses -----------------------------------------

    /// <summary>
    ///     A filtering sampler in a comparison lookup is a type error, and a comparison sampler in
    ///     an ordinary one is too. That refusal is the reason for two types rather than one with a
    ///     flag on the host side.
    /// </summary>
    [Theory]
    [InlineData("shadowMap.SampleCompare(plain, uv, 0.5)")]
    [InlineData("albedo.Sample(shadowSampler, uv).x")]
    public void The_two_samplers_are_not_interchangeable(string call) {
        var tree = SyntaxTree.ParseText(
            $$"""
              package A

              shader Mixed {
                  [PerView] var shadowMap: DepthTexture2D

                  [PerView] var shadowSampler: ComparisonSampler

                  var albedo: Texture2D

                  var plain: Sampler

                  [FragmentShader]
                  func Fragment([Semantic("TEXCOORD0")] uv: float2): float4 {
                      val lit = {{call}}
                      return float4(lit, lit, lit, 1.0)
                  }
              }

              """,
            path: "Test.rvn"
        );

        Assert.Empty(tree.Diagnostics);
        Assert.Contains(Compilation.Create("Test", tree).GetDiagnostics(), d => d.IsError);
    }

    /// <summary>
    ///     No <c>Sample</c> and no <c>Load</c> on a depth texture: both would be members no target
    ///     can implement, because a shadow lookup returns the comparison and never the texel.
    /// </summary>
    [Fact]
    public void A_depth_texture_has_no_plain_sample_or_load() {
        Assert.DoesNotContain(BuiltInTypes.DepthTexture2D.GetMembers(), m => m.Name is "Sample" or "Load");

        Assert.Contains(
            BuiltInTypes.DepthTexture2D.GetMembers(),
            m => m.Name is "SampleCompare" or "SampleCompareLevelZero" or "GetDimensions"
        );
    }

    /// <summary>
    ///     ⚠ A derivative-implied comparison outside a fragment stage is the same silently-level-zero
    ///     module <c>RVN3013</c> exists to name, and it is easy to add the intrinsic and forget the
    ///     check — nothing else would have said so, since the backends emit a valid module either
    ///     way.
    /// </summary>
    [Fact]
    public void An_implicit_comparison_outside_a_fragment_stage_is_reported() {
        var diagnostics = LoweringTestBase.LoweringDiagnosticsOf(
            """
            package A

            shader Early {
                [PerView] var shadowMap: DepthTexture2D

                [PerView] var shadowSampler: ComparisonSampler

                var mask: RWBuffer<float>

                [ComputeShader(8)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    mask[int(id.x)] = shadowMap.SampleCompare(shadowSampler, float2(0.5, 0.5), 0.5)
                }
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN3013");
    }
}
