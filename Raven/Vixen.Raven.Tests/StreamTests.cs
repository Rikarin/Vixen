// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;
using Vixen.Raven.Artefacts;
using Vixen.Raven.CodeGen;
using Vixen.Raven.CodeGen.Glsl;
using Vixen.Raven.CodeGen.Spirv;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;
using Vixen.Core.Syntax.Diagnostics;
using static Tests.LoweringTestBase;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     <c>stream</c>: a value written by one pipeline stage and read by the next, declared once on
///     the shader instead of threaded through every entry point's signature.
/// </summary>
/// <remarks>
///     Two things carry the feature, and both are tested here rather than assumed. The direction is
///     <em>derived</em> — a function anywhere in a stage's call graph can contribute a stream without
///     any signature mentioning it, which is the reason to have streams at all rather than more
///     parameters. And the location is a property of the shader, so the stage that writes a stream and
///     the stage that reads it arrive at the same number without either knowing about the other.
/// </remarks>
public class StreamTests {
    const string Source = """
                          package A

                          shader Lit {
                              stream var normalWS: float3
                              stream var uv: float2

                              var scale: float

                              func WriteNormal(n: float3) {
                                  normalWS = n * scale
                              }

                              [VertexShader]
                              func Vertex([Semantic("POSITION")] position: float3): float4 {
                                  WriteNormal(float3(position.x, position.y, 1f))
                                  uv = float2(position.x, position.y)
                                  return float4(position.x, position.y, position.z, 1f)
                              }

                              [PixelShader]
                              func Shade(): float4 {
                                  val n = normalize(normalWS)
                                  return float4(n.x, n.y, uv.x, 1f)
                              }
                          }

                          """;

    static IrShader LitOf(IrModule module) => FindShader(module, "Lit");

    static IrEntryPoint Stage(IrModule module, ShaderStage stage) =>
        Assert.Single(LitOf(module).EntryPoints, e => e.Stage == stage);

    static string GlslFor(IrModule module, ShaderStage stage) {
        var bag = new DiagnosticBag();
        var generated = new GlslBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return Assert.Single(generated, unit => unit.Stage == stage).Code;
    }

    // --- Direction --------------------------------------------------------

    /// <summary>
    ///     A stream a stage writes is an output; one it reads is an input. Neither is declared — both
    ///     come from what the stage's code does.
    /// </summary>
    [Fact]
    public void DirectionComesFromUse() {
        var module = Lower(Source);

        var vertex = Stage(module, ShaderStage.Vertex);
        Assert.Equal(["normalWS", "uv"], vertex.StreamOutputs.Select(s => s.Name));
        Assert.Empty(vertex.StreamInputs);

        var pixel = Stage(module, ShaderStage.Pixel);
        Assert.Equal(["normalWS", "uv"], pixel.StreamInputs.Select(s => s.Name));
        Assert.Empty(pixel.StreamOutputs);
    }

    /// <summary>
    ///     A stream written by a helper the entry point calls is still the stage's output. This is
    ///     the property that makes a stream worth more than another parameter: nothing between
    ///     <c>WriteNormal</c> and the pipeline mentions <c>normalWS</c>.
    /// </summary>
    [Fact]
    public void AHelperCanContributeAStream() {
        var module = Lower(Source);

        var writer = FindFunction(module, "WriteNormal");
        var vertex = Stage(module, ShaderStage.Vertex);

        // The entry point does not touch normalWS itself — only the helper does.
        Assert.Contains(writer, CallGraph.Calls(vertex.Function.Body));
        Assert.Contains("normalWS", vertex.StreamOutputs.Select(s => s.Name));
    }

    /// <summary>
    ///     Reading a stream the same stage produced is not an input: the value wanted is the one just
    ///     written, so no vertex attribute appears for it.
    /// </summary>
    /// <remarks>
    ///     The reason the input rule is "read <em>before</em> written" rather than "read at all". A
    ///     read of a stream this stage produces resolves to the output variable, which both targets
    ///     allow — only SPIR-V's <c>Input</c> is read-only.
    /// </remarks>
    [Fact]
    public void ReadingBackWhatTheStageWroteIsNotAnInput() {
        var module = Lower(
            """
            package A

            shader Lit {
                stream var normalWS: float3

                [VertexShader]
                func Vertex([Semantic("POSITION")] position: float3): float4 {
                    normalWS = float3(position.x, position.y, 1f)
                    val n = normalize(normalWS)
                    return float4(n.x, n.y, n.z, 1f)
                }
            }

            """
        );

        var vertex = Stage(module, ShaderStage.Vertex);
        Assert.Single(vertex.StreamOutputs);
        Assert.Empty(vertex.StreamInputs);

        // And the read resolves to the out variable rather than to an attribute nobody binds.
        var glsl = GlslFor(module, ShaderStage.Vertex);
        Assert.DoesNotContain("in vec3 in_normalWS", glsl, StringComparison.Ordinal);
        Assert.Contains("= out_normalWS;", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Writing part of a stream keeps the rest, so it reads before it writes — the stage needs
    ///     both directions.
    /// </summary>
    /// <remarks>
    ///     Which matters in SPIR-V, where the read-modify-write a partial store lowers to has to load
    ///     from the <c>Input</c> variable: an <c>Output</c> is writable but the value being preserved
    ///     came from upstream.
    /// </remarks>
    [Fact]
    public void APartialWriteReadsToo() {
        var module = Lower(
            """
            package A

            shader Lit {
                stream var normalWS: float3

                [VertexShader]
                func Vertex([Semantic("POSITION")] position: float3): float4 {
                    normalWS.x = position.x
                    return float4(position.x, position.y, position.z, 1f)
                }
            }

            """
        );

        var vertex = Stage(module, ShaderStage.Vertex);
        Assert.Single(vertex.StreamInputs);
        Assert.Single(vertex.StreamOutputs);
    }

    // --- Locations --------------------------------------------------------

    /// <summary>
    ///     The writing stage's location for a stream is the reading stage's location for it. This is
    ///     the whole reason a stream's location is planned from the shader rather than from the stage.
    /// </summary>
    [Fact]
    public void TheTwoStagesAgreeOnEveryLocation() {
        var module = Lower(Source);
        var shader = LitOf(module);

        var vertex = GlslFor(module, ShaderStage.Vertex);
        var pixel = GlslFor(module, ShaderStage.Pixel);

        foreach (var planned in StreamPlan.Of(shader)) {
            Assert.Contains(
                $"layout(location = {planned.Location}) out vec{Lanes(planned.Stream)} out_{planned.Stream.Name}",
                vertex,
                StringComparison.Ordinal
            );

            Assert.Contains(
                $"layout(location = {planned.Location}) in vec{Lanes(planned.Stream)} in_{planned.Stream.Name}",
                pixel,
                StringComparison.Ordinal
            );
        }
    }

    static int Lanes(IrStream stream) => ((IrVectorType)stream.Type).Size;

    /// <summary>
    ///     A stage's own parameters are located after the streams, so the two stages can agree.
    /// </summary>
    /// <remarks>
    ///     The stated consequence of the rule: adding a stream renumbers the shader's vertex
    ///     attributes. Reflection is where that has to be visible, because the engine builds its
    ///     vertex layout from it — so this pins both halves at once.
    /// </remarks>
    [Fact]
    public void ParametersAreLocatedAfterTheStreams() {
        var module = Lower(Source);
        var shader = LitOf(module);

        Assert.Equal(2, StreamPlan.ParameterBase(shader));
        Assert.Contains(
            "layout(location = 2) in vec3 in_position",
            GlslFor(module, ShaderStage.Vertex),
            StringComparison.Ordinal
        );

        // A vertex-stage stream read would be an attribute too; here the vertex stage only writes,
        // so the layout is the parameter alone, at its shifted location.
        var reflection = ReflectionBuilder.Describe(shader, []);
        var position = Assert.Single(reflection.VertexInputs);
        Assert.Equal("position", position.Name);
        Assert.Equal(2, position.Location);
    }

    /// <summary>
    ///     A stream the vertex stage reads is a vertex attribute — there is no earlier stage — and the
    ///     engine sees it in the vertex layout alongside the parameters.
    /// </summary>
    [Fact]
    public void AVertexStreamReadIsAVertexAttribute() {
        var module = Lower(
            """
            package A

            shader Lit {
                stream var tangent: float3

                [VertexShader]
                func Vertex([Semantic("POSITION")] position: float3): float4 {
                    return float4(tangent.x, position.y, position.z, 1f)
                }
            }

            """
        );

        var reflection = ReflectionBuilder.Describe(LitOf(module), []);

        Assert.Equal(["tangent", "position"], reflection.VertexInputs.Select(i => i.Name));
        Assert.Equal([0, 1], reflection.VertexInputs.Select(i => i.Location));
    }

    /// <summary>
    ///     A fragment output stays at location 0 whatever the shader streams: it is a render-target
    ///     index, not an interstage location.
    /// </summary>
    [Fact]
    public void AFragmentOutputStaysAtLocationZero() {
        var module = Lower(Source);
        var shader = LitOf(module);

        Assert.Equal(0, StreamPlan.OutputBase(shader, ShaderStage.Pixel));
        Assert.Contains(
            "layout(location = 0) out vec4 out_result",
            GlslFor(module, ShaderStage.Pixel),
            StringComparison.Ordinal
        );
    }

    // --- Both backends ----------------------------------------------------

    /// <summary>
    ///     Both backends read the same plan, so the SPIR-V <c>Location</c> decorations are the GLSL
    ///     <c>layout(location = …)</c> numbers.
    /// </summary>
    [Fact]
    public void SpirvDecoratesTheSameLocations() {
        Assert.SkipUnless(SpirvTestBase.ValidatorAvailable, "spirv-val is not on PATH (brew install spirv-tools).");

        var module = Lower(Source);
        var bag = new DiagnosticBag();
        var generated = new SpirvBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        foreach (var unit in generated) {
            SpirvTestBase.Validate(unit);
        }

        var vertex = Assert.Single(generated, u => u.Stage == ShaderStage.Vertex).Code;
        var pixel = Assert.Single(generated, u => u.Stage == ShaderStage.Pixel).Code;

        // Matched by name, since the two modules number their ids independently.
        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(vertex, "out_normalWS")} Location 0",
            vertex,
            StringComparison.Ordinal
        );

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(pixel, "in_normalWS")} Location 0",
            pixel,
            StringComparison.Ordinal
        );
    }

    /// <summary>A stream is not a binding: nothing about it reaches a descriptor set.</summary>
    [Fact]
    public void AStreamIsNotABinding() {
        var module = Lower(Source);
        var shader = LitOf(module);

        // The one binding is the uniform `scale`, not the two streams.
        Assert.Equal(["scale"], shader.Bindings.Select(b => b.Name));

        var reflection = ReflectionBuilder.Describe(shader, []);
        var members = reflection.Sets.SelectMany(s => s.Bindings).SelectMany(b => b.Members).Select(m => m.Name);
        Assert.DoesNotContain("normalWS", members);
        Assert.Empty(reflection.Parameters.Where(p => p.Name.Contains("normalWS", StringComparison.Ordinal)));
    }

    // --- Diagnostics ------------------------------------------------------

    /// <summary>
    ///     A stream only means something on the type that is the pipeline.
    /// </summary>
    [Fact]
    public void AStreamOutsideAShaderIsReported() {
        var diagnostics = Diagnose(
            """
            package A

            struct Vertex {
                stream var normalWS: float3
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2100");
    }

    /// <summary>
    ///     A stream cannot also be a constant or a slot: none of those has storage to thread between
    ///     stages.
    /// </summary>
    [Theory]
    [InlineData("stream const val x = 1f")]
    [InlineData("[Permutation] stream val x: bool = false")]
    [InlineData("stream compose val x: IFeature")]
    public void AStreamThatIsAlsoAConstantIsReported(string declaration) {
        var diagnostics = Diagnose(
            $$"""
              package A

              protocol IFeature {
                  func F(): float
              }

              shader Lit {
                  {{declaration}}
              }

              """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2101");
    }

    /// <summary>A stream's value comes from the stage that writes it, so an initializer is dead text.</summary>
    [Fact]
    public void AStreamWithAnInitializerIsReported() {
        var diagnostics = Diagnose(
            """
            package A

            shader Lit {
                stream var uv: float2 = float2(0f, 0f)
            }

            """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2102");
    }

    /// <summary>
    ///     A stream must be something a stage interface can carry, and that is said at the declaration
    ///     rather than twice over by the two backends.
    /// </summary>
    [Theory]
    [InlineData("stream var flag: bool")]
    [InlineData("stream var m: mat4")]
    [InlineData("stream var t: Texture2D")]
    public void AStreamOfAnUncarryableTypeIsReported(string declaration) {
        var diagnostics = Diagnose(
            $$"""
              package A

              shader Lit {
                  {{declaration}}
              }

              """
        );

        Assert.Contains(diagnostics, d => d.Id == "RVN2103");
    }

    /// <summary>
    ///     A stream written by a fragment stage has nothing downstream to read it, and the shader
    ///     still compiles — so it is a warning, on the RVN2091 pattern.
    /// </summary>
    [Fact]
    public void AStreamWrittenByTheFragmentStageIsReported() {
        var diagnostics = LoweringDiagnosticsOf(
            """
            package A

            shader Lit {
                stream var normalWS: float3

                [PixelShader]
                func Shade(): float4 {
                    normalWS = float3(1f, 0f, 0f)
                    return float4(1f, 1f, 1f, 1f)
                }
            }

            """
        );

        var warning = Assert.Single(diagnostics, d => d.Id == "RVN3005");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("normalWS", warning.GetMessage(), StringComparison.Ordinal);
    }

    // --- Libraries --------------------------------------------------------

    /// <summary>
    ///     A library refuses to export a body that touches a stream, and says why: a stream's location
    ///     belongs to the shader that declares it, so linking one would mean matching two shaders'
    ///     streams by name.
    /// </summary>
    /// <remarks>
    ///     Distinct from the binding refusal (RVN5001) because the remedy is different. Inside one
    ///     compilation a stream crosses any number of functions freely; it is only the artefact
    ///     boundary it does not cross.
    /// </remarks>
    [Fact]
    public void ALibraryCannotExportABodyThatUsesAStream() {
        var tree = SyntaxTree.ParseText(
            """
            package Features

            shader Surface {
                stream var normalWS: float3

                func Perturb(n: float3) {
                    normalWS = n
                }
            }

            """,
            path: "Features.rvn"
        );

        var compilation = Compilation.Create("Features", tree);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.IsError);

        var bag = new DiagnosticBag();
        var lowered = Lowerer.LowerWithLinks(compilation, bag);
        var library = LibraryBuilder.Build(compilation, lowered, bag);

        var refused = Assert.Single(bag.ToArray(), d => d.Id == "RVN5007");
        Assert.Contains("normalWS", refused.GetMessage(), StringComparison.Ordinal);

        // Refused means not exported: the declaration travels, the body does not.
        var surface = Assert.Single(library.Types, t => t.Name == "Surface");
        Assert.True(Assert.Single(surface.Fields, f => f.Name == "normalWS").IsStream);
        Assert.Null(Assert.Single(surface.Methods).IrFunction);
    }
}
