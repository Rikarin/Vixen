// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     Storage images — docs/plan/07's "a writable <em>texture</em>, which a compute post-process
///     pass writes into".
/// </summary>
/// <remarks>
///     <para>
///         <c>RWBuffer&lt;T&gt;</c> unblocked everything that reads a number back; this is the other
///         half — a pass that produces a render target rather than a number. The two things that
///         make it more than "a buffer with two indices" are both here: a storage image is
///         <em>not sampled</em>, so it has no sampler, no filtering and no mips; and its texel
///         format is part of its declaration, because it is part of the type in SPIR-V.
///     </para>
///     <para>
///         Only the read-write form exists. A texture a shader merely reads is a
///         <c>Texture2D</c> — a sampled image, which filters and mips — so there is no
///         read-only storage image to choose wrongly between.
///     </para>
/// </remarks>
public class StorageImageTests {
    const string Post = """
                        package A

                        shader Post {
                            var source: Texture2D
                            var linear: Sampler

                            [Format("rgba16f")] var target: RWTexture2D<float4>

                            [ComputeShader(8, 8, 1)]
                            func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                val size = target.GetDimensions()
                                val coord = int2(int(id.x), int(id.y))
                                val previous = target.Load(coord)
                                val uv = float2(float(coord.x) / float(size.x), float(coord.y) / float(size.y))
                                target.Store(coord, source.SampleLevel(linear, uv, 0f) + previous * 0.5f)
                            }
                        }

                        """;

    [Fact]
    public void An_image_binds_as_its_own_descriptor_type_carrying_its_format() {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Post), "Post");
        var binding = Assert.Single(shader.Bindings, b => b.Kind == IrBindingKind.StorageImage);

        var image = Assert.IsType<IrStorageImageType>(binding.Type);
        Assert.Equal("rgba16f", image.Format);
        Assert.Equal(IrTextureDimension.Texture2D, image.Dimension);
        Assert.True(binding.IsWritable);

        // A sampled texture and a storage image are two descriptor types, because the host has to
        // create the view with different usage for each.
        var descriptors = ReflectionBuilder.Describe(shader).Sets.SelectMany(s => s.Bindings);
        Assert.Contains(descriptors, b => b.Type == DescriptorType.StorageImage && b.Name == "target");
    }

    [Fact]
    public void The_capability_is_reported_so_a_host_can_gate_the_pipeline() {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Post), "Post");
        Assert.Contains(IrCapability.StorageImage, IrCapabilities.Of(shader));
    }

    [Fact]
    public void GLSL_declares_the_format_in_the_layout_and_stores_as_a_statement() {
        var unit = Assert.Single(GenerateClean(Post));

        Assert.Contains("layout(rgba16f, set = 2, binding = 2) uniform image2D target;", unit.Code, StringComparison.Ordinal);
        Assert.Contains("imageLoad(target,", unit.Code, StringComparison.Ordinal);
        Assert.Contains("imageSize(target)", unit.Code, StringComparison.Ordinal);

        // A store produces nothing, so it is a statement rather than an assignment — a `vec4 _n =
        // imageStore(...)` would not be GLSL at all.
        Assert.Contains("    imageStore(target,", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("= imageStore", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void SPIR_V_declares_a_storage_image_rather_than_a_sampled_one() {
        Assert.SkipUnless(SpirvTestBase.ValidatorAvailable, "spirv-val is not on PATH (brew install spirv-tools).");

        var listing = ReferenceCompiler.Disassemble(Assert.Single(GenerateClean(Post, "spirv")).Binary!);

        // Sampled = 2 says "read and written directly"; a stated format is what keeps the module
        // from needing StorageImageReadWithoutFormat.
        Assert.Contains("OpTypeImage %float 2D 0 0 0 2 Rgba16f", listing, StringComparison.Ordinal);

        // And the sampled texture in the same shader is still Sampled = 1 with no format.
        Assert.Contains("OpTypeImage %float 2D 0 0 0 1 Unknown", listing, StringComparison.Ordinal);

        Assert.Contains("OpImageRead", listing, StringComparison.Ordinal);
        Assert.Contains("OpImageWrite", listing, StringComparison.Ordinal);
        Assert.Contains("OpImageQuerySize ", listing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void A_volume_image_and_the_integer_formats_generate(string target) {
        GenerateClean(
            """
            package A

            shader S {
                [Format("rgba32f")] var volume: RWTexture3D<float4>
                [Format("rgba8ui")] var mask: RWTexture2D<uint4>
                [Format("r32i")] var counter: RWTexture2D<int4>

                [ComputeShader(4)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    val xy = int2(int(id.x), int(id.y))
                    volume.Store(int3(xy.x, xy.y, 0), float4(1, 0, 0, 1))
                    mask.Store(xy, uint4(1, 2, 3, 4))
                    counter.Store(xy, counter.Load(xy) + int4(1, 0, 0, 0))
                }
            }

            """,
            target
        );
    }

    // --- What the declaration has to say ----------------------------------

    [Fact]
    public void An_image_needs_a_format() {
        var error = Assert.Single(
            SemanticTestBase.Diagnose("package A\n\nshader S {\n    var target: RWTexture2D<float4>\n}\n"),
            d => d.Id == "RVN2123"
        );

        Assert.True(error.IsError);

        // The message lists what it will accept, because there is no way to guess otherwise.
        Assert.Contains("rgba16f", error.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rgba16", "is not a format Raven admits")]
    [InlineData("rgba32i", "is read as 'int4'")]
    public void A_format_has_to_be_recognised_and_to_match_the_element(string format, string because) {
        var error = Assert.Single(
            SemanticTestBase.Diagnose(
                $"package A\n\nshader S {{\n    [Format(\"{format}\")] var target: RWTexture2D<float4>\n}}\n"
            ),
            d => d.Id == "RVN2124"
        );

        Assert.True(error.IsError);
        Assert.Contains(because, error.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Four components whatever the format stores, because that is what both targets do: an
    ///     <c>r32f</c> image reads as <c>(r, 0, 0, 1)</c>. A narrower declared type would be a
    ///     shape neither target has.
    /// </summary>
    [Theory]
    [InlineData("float")]
    [InlineData("float2")]
    [InlineData("Texture2D")]
    public void The_element_has_to_be_a_four_lane_texel(string element) {
        Assert.Contains(
            SemanticTestBase.Diagnose(
                $"package A\n\nshader S {{\n    [Format(\"rgba16f\")] var target: RWTexture2D<{element}>\n}}\n"
            ),
            d => d.Id == "RVN2122" && d.IsError
        );
    }

    [Fact]
    public void A_format_on_something_with_no_texels_says_nothing() {
        var warning = Assert.Single(
            SemanticTestBase.Diagnose(
                "package A\n\nshader S {\n    [Format(\"rgba16f\")] var albedo: Texture2D\n}\n"
            ),
            d => d.Id == "RVN2125"
        );

        Assert.False(warning.IsError);
    }

    /// <summary>
    ///     The format is part of the type in SPIR-V, so a parameter says which one it takes —
    ///     otherwise a function taking an <c>rgba16f</c> image could be handed an <c>rgba8</c> one
    ///     and the module would not type-check.
    /// </summary>
    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void An_image_can_be_passed_to_a_function_when_the_parameter_names_the_same_format(string target) {
        GenerateClean(
            """
            package A

            shader S {
                [Format("rgba16f")] var target: RWTexture2D<float4>

                func Clear([Format("rgba16f")] image: RWTexture2D<float4>, at: int2) {
                    image.Store(at, float4(0, 0, 0, 1))
                }

                [ComputeShader(8, 8)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    Clear(target, int2(int(id.x), int(id.y)))
                }
            }

            """,
            target
        );
    }

    /// <summary>
    ///     ⚠ And the SPIR-V it emits for that is a module <c>spirv-val</c> accepts.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The case above proved nothing about the module.</b> <c>GenerateClean</c> asks the
    ///         backend for diagnostics and stops there; a binary format gives no other signal, and a
    ///         listing that reads plausibly can still be a module no driver would load. So the
    ///         <c>spirv</c> half of it was green over an <em>illegal</em> module for as long as it
    ///         existed.
    ///     </para>
    ///     <para>
    ///         What was wrong: <c>SpirvEmitter</c>'s opaque-parameter arm listed <c>IrTextureType</c>,
    ///         <c>IrSamplerType</c>, <c>IrAccelerationStructureType</c> and the two comparison types
    ///         and <em>not</em> <c>IrStorageImageType</c>, so a storage-image parameter fell through
    ///         to the ordinary path and was given an <c>OpVariable</c> in <c>Function</c> storage —
    ///         which is what the arm's own comment says cannot be done. ⚠ A missing arm in a type
    ///         pattern does not fail to build; it silently never matches, and this one had a
    ///         same-shaped list in <c>GlslEmitter</c> two directories away that did include it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_image_passed_to_a_function_emits_a_module_the_validator_accepts() =>
        SpirvTestBase.One(
            """
            package A

            shader S {
                [Format("rgba16f")] var target: RWTexture2D<float4>

                func Clear([Format("rgba16f")] image: RWTexture2D<float4>, at: int2) {
                    image.Store(at, float4(0, 0, 0, 1))
                }

                [ComputeShader(8, 8)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    Clear(target, int2(int(id.x), int(id.y)))
                }
            }

            """
        );

    [Fact]
    public void An_image_is_still_not_assignable_as_a_whole() {
        Assert.Contains(
            SemanticTestBase.Diagnose(
                """
                package A

                shader S {
                    [Format("rgba16f")] var a: RWTexture2D<float4>
                    [Format("rgba16f")] var b: RWTexture2D<float4>

                    [ComputeShader(1)]
                    func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                        a = b
                    }
                }

                """
            ),
            d => d.Id == "RVN2119" && d.IsError
        );
    }

    // --- Access qualifiers --------------------------------------------------

    /// <summary>A shader with one image it only stores into and one it only loads.</summary>
    static string Access(string body) =>
        $$"""
          package A

          shader S {
              [Format("rgba16f")] var destination: RWTexture2D<float4>
              [Format("rgba16f")] var origin: RWTexture2D<float4>

              [ComputeShader(8, 8)]
              func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                  val at = int2(int(id.x), int(id.y))
          {{body}}
              }
          }

          """;

    const string WriteOneReadTheOther = "        destination.Store(at, origin.Load(at) * 2f)";

    /// <summary>
    ///     An image a stage only stores into is <c>writeonly</c>, one it only loads is
    ///     <c>readonly</c>, and one it does both to is neither.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not a comment for a reader — GLSL ES rejects the declaration without it.</b> ES
    ///         lets an image be both read and written only at <c>r32f</c>, <c>r32i</c> or
    ///         <c>r32ui</c>; anything else must say which way it goes. Raven's own GLSL is what the
    ///         GL backend compiles, so nothing downstream can supply this for it.
    ///     </para>
    ///     <para>
    ///         ⚠ And the third case is the one that keeps this honest. An image genuinely read and
    ///         written must get <em>no</em> qualifier — a <c>writeonly</c> there is a compile error
    ///         at the load, and its SPIR-V twin (<c>NonReadable</c> on an image something reads) is
    ///         worse, because <c>spirv-val</c> accepts it and a driver may act on it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Glsl_qualifies_an_image_by_what_the_stage_does_to_it() {
        var oneWay = GenerateOne(Access(WriteOneReadTheOther));

        Assert.Contains("writeonly uniform image2D destination", oneWay, StringComparison.Ordinal);
        Assert.Contains("readonly uniform image2D origin", oneWay, StringComparison.Ordinal);

        var both = GenerateOne(Access("        destination.Store(at, destination.Load(at) * 2f)"));

        Assert.DoesNotContain("writeonly uniform image2D destination", both, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly uniform image2D destination", both, StringComparison.Ordinal);
    }

    /// <summary>And the same answer as a SPIR-V decoration, which is where a driver reads it.</summary>
    [Fact]
    public void Spirv_decorates_an_image_by_what_the_stage_does_to_it() {
        var unit = Assert.Single(GenerateClean(Access(WriteOneReadTheOther), "spirv"));

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(unit.Code, "destination")} NonReadable",
            unit.Code,
            StringComparison.Ordinal
        );

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(unit.Code, "origin")} NonWritable",
            unit.Code,
            StringComparison.Ordinal
        );

        SpirvTestBase.Validate(unit);
    }

    /// <summary>
    ///     ⚠ An image handed to a helper is decorated in neither direction, because the walk cannot
    ///     say which way it goes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The instrument's own check, and the reason it is worth a test of its own.</b> A
    ///         storage image reaches an operation as an ordinary load of its declaration, and the
    ///         analysis follows exactly that; a parameter arrives as a value with no declaration
    ///         behind it. Guessing there would put <c>writeonly</c> on an image a caller reads — a
    ///         module <c>spirv-val</c> accepts and a driver may act on — so the answer is no
    ///         decoration at all, for every image in that stage.
    ///     </para>
    ///     <para>
    ///         Nothing in <c>Raven/Library</c> passes a storage image to a function today, so this
    ///         is a floor rather than something the engine's shaders stand on.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_image_passed_to_a_function_is_qualified_in_neither_direction() {
        const string Source = """
                              package A

                              shader S {
                                  [Format("rgba16f")] var target: RWTexture2D<float4>

                                  func Clear([Format("rgba16f")] image: RWTexture2D<float4>, at: int2) {
                                      image.Store(at, float4(0, 0, 0, 1))
                                  }

                                  [ComputeShader(8, 8)]
                                  func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                      Clear(target, int2(int(id.x), int(id.y)))
                                  }
                              }

                              """;

        var glsl = GenerateOne(Source);

        Assert.Contains("uniform image2D target", glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("writeonly", glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly", glsl, StringComparison.Ordinal);

        var unit = Assert.Single(GenerateClean(Source, "spirv"));

        Assert.DoesNotContain("NonReadable", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("NonWritable", unit.Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ A stage that stores into an image directly <em>and</em> hands the same image to a
    ///     helper that loads it is not write-only, and must not be decorated.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the case that makes the untraced flag more than decoration, and it did not
    ///         exist until a sabotage showed the flag could be deleted with every test still
    ///         green.</b> The test above passes an image to a helper and nothing else, so the
    ///         binding is written by nobody the walk can see and gets no qualifier for a reason that
    ///         has nothing to do with tracing.
    ///     </para>
    ///     <para>
    ///         Here the direct store puts the binding in the written set. If the helper's load
    ///         through the <em>parameter</em> were recorded as a load of the binding, this would be
    ///         correct by accident; if it were recorded as nothing at all and the walk stayed
    ///         confident, the module would carry <c>NonReadable</c> on an image it reads — which
    ///         <c>spirv-val</c> accepts and a driver may act on, so it is a miscompilation rather
    ///         than a build failure. Neither: the receiver is not a global, so the stage declines to
    ///         decorate anything.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_store_here_and_a_load_through_a_helper_is_not_write_only() {
        const string Source = """
                              package A

                              shader S {
                                  [Format("rgba16f")] var target: RWTexture2D<float4>

                                  func Peek([Format("rgba16f")] image: RWTexture2D<float4>, at: int2): float4 {
                                      return image.Load(at)
                                  }

                                  [ComputeShader(8, 8)]
                                  func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                      val at = int2(int(id.x), int(id.y))
                                      target.Store(at, Peek(target, at) * 2f)
                                  }
                              }

                              """;

        Assert.DoesNotContain("writeonly", GenerateOne(Source), StringComparison.Ordinal);

        var unit = Assert.Single(GenerateClean(Source, "spirv"));

        Assert.DoesNotContain("NonReadable", unit.Code, StringComparison.Ordinal);
        SpirvTestBase.Validate(unit);
    }

    /// <summary>
    ///     A real front end takes the qualified declaration at ES 3.10 and refuses the same
    ///     declaration without it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both halves, because only the second says the qualifier is load-bearing.</b> The
    ///         string assertions above would be satisfied by a qualifier nothing needed; this is
    ///         glslc reporting <c>'rgba16f' : format requires readonly or writeonly memory
    ///         qualifier</c> on Raven's own output the moment the two words are taken back out —
    ///         the exact error #476 quotes, reproduced on a shader written for this test rather
    ///         than found in the library.
    ///     </para>
    ///     <para>
    ///         ⚠ The <c>precision</c> lines are supplied here, and their absence is a different gap
    ///         with a different owner: Raven emits Vulkan GLSL, and it is
    ///         <c>Platform/Vixen.Graphics.OpenGL/GlslTranslator</c> that adds
    ///         <c>precision highp …</c> for an ES profile. Injecting them is what isolates this
    ///         assertion to the access qualifier — without them the ES compile fails for a reason
    ///         that has nothing to do with what is being tested, which is how a test ends up
    ///         asserting the wrong refusal.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Es_310_takes_the_qualified_image_and_refuses_the_unqualified_one() {
        Assert.SkipUnless(ReferenceCompiler.Available, "glslc is not on PATH (brew install shaderc).");

        var unit = Assert.Single(GenerateClean(Access(WriteOneReadTheOther)));

        // Desktop first: a qualifier contradicting a use is an error at 450 too, so this is the
        // cheap half of "the two words agree with the body".
        Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));

        var es = unit.Code.Replace(
            "#version 450",
            "#version 310 es\n\nprecision highp float;\nprecision highp int;\nprecision highp image2D;",
            StringComparison.Ordinal
        );

        Assert.Contains("310 es", es, StringComparison.Ordinal);
        Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(es, unit.Stage));

        var stripped = es
            .Replace("writeonly uniform", "uniform", StringComparison.Ordinal)
            .Replace("readonly uniform", "uniform", StringComparison.Ordinal);

        Assert.NotEqual(es, stripped);

        var refusal = Record.Exception(() => ReferenceCompiler.GlslToSpirv(stripped, unit.Stage));

        Assert.NotNull(refusal);
        Assert.Contains("readonly or writeonly", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_type_displays_as_it_was_written() {
        Assert.Equal(
            "RWTexture3D<float4>",
            new StorageImageTypeSymbol(BuiltInTypes.Float4, true).ToDisplayString()
        );
    }
}
