// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
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

                              [FragmentShader]
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

        var fragment = Stage(module, ShaderStage.Fragment);
        Assert.Equal(["normalWS", "uv"], fragment.StreamInputs.Select(s => s.Name));
        Assert.Empty(fragment.StreamOutputs);
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
        var fragment = GlslFor(module, ShaderStage.Fragment);

        foreach (var planned in StreamPlan.Of(shader)) {
            Assert.Contains(
                $"layout(location = {planned.Location}) out vec{Lanes(planned.Stream)} out_{planned.Stream.Name}",
                vertex,
                StringComparison.Ordinal
            );

            Assert.Contains(
                $"layout(location = {planned.Location}) in vec{Lanes(planned.Stream)} in_{planned.Stream.Name}",
                fragment,
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

        Assert.Equal(0, StreamPlan.OutputBase(shader, ShaderStage.Fragment));
        Assert.Contains(
            "layout(location = 0) out vec4 out_result",
            GlslFor(module, ShaderStage.Fragment),
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
        var fragment = Assert.Single(generated, u => u.Stage == ShaderStage.Fragment).Code;

        // Matched by name, since the two modules number their ids independently.
        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(vertex, "out_normalWS")} Location 0",
            vertex,
            StringComparison.Ordinal
        );

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(fragment, "in_normalWS")} Location 0",
            fragment,
            StringComparison.Ordinal
        );
    }

    // --- Flat interpolation -----------------------------------------------

    const string Indexed = """
                           package A

                           shader Lit {
                               stream var objectIndex: int
                               stream var uv: float2

                               [VertexShader]
                               func Vertex([Semantic("POSITION")] position: float3, [Semantic("SV_InstanceID")] instance: int): float4 {
                                   objectIndex = instance
                                   uv = float2(position.x, position.y)
                                   return float4(position.x, position.y, position.z, 1f)
                               }

                               [FragmentShader]
                               func Shade(): float4 {
                                   return float4(uv.x, uv.y, float(objectIndex), 1f)
                               }
                           }

                           """;

    /// <summary>
    ///     An integer stream arrives at the fragment stage flat, and a float one does not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>A rule rather than a preference, and the module is invalid without it.</strong>
    ///         The rasteriser weights a varying by barycentric coordinates, which produces a fraction
    ///         — so there is no interpolation an integer could take, and SPIR-V requires <c>Flat</c>
    ///         on a fragment input of integer type.
    ///     </para>
    ///     <para>
    ///         ⚠ The float stream is asserted too, and not for symmetry: decorating everything flat
    ///         would satisfy the validator and quietly kill interpolation, which is a picture that
    ///         looks like faceted shading rather than an error.
    ///     </para>
    ///     <para>
    ///         And only the <em>input</em>. The decoration says how a value is received; the vertex
    ///         stage that wrote it has no interpolation to describe, and decorating its output would
    ///         be a claim about the wrong end of the wire.
    ///     </para>
    ///     <para>
    ///         ⚠ That last paragraph is true of SPIR-V and <em>only</em> of SPIR-V, because a stage
    ///         is its own module here and nothing links the two. It was applied to GLSL as well and
    ///         was wrong there — see
    ///         <see cref="GlslQualifiesTheSameStreamFlatAtBothEnds" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnIntegerStreamIsFlat() {
        Assert.SkipUnless(SpirvTestBase.ValidatorAvailable, "spirv-val is not on PATH (brew install spirv-tools).");

        var module = Lower(Indexed);
        var bag = new DiagnosticBag();
        var generated = new SpirvBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        foreach (var unit in generated) {
            SpirvTestBase.Validate(unit);
        }

        var vertex = Assert.Single(generated, u => u.Stage == ShaderStage.Vertex).Code;
        var fragment = Assert.Single(generated, u => u.Stage == ShaderStage.Fragment).Code;

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(fragment, "in_objectIndex")} Flat",
            fragment,
            StringComparison.Ordinal
        );

        Assert.DoesNotContain(
            $"OpDecorate {SpirvTestBase.IdNamed(fragment, "in_uv")} Flat",
            fragment,
            StringComparison.Ordinal
        );

        Assert.DoesNotContain(
            $"OpDecorate {SpirvTestBase.IdNamed(vertex, "out_objectIndex")} Flat",
            vertex,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     And GLSL says the same thing in its own words — <b>at both ends</b>, which is where it
    ///     stops agreeing with SPIR-V.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         GLSL rejects the declaration without the qualifier, so this is the same rule and not
    ///         a second one — which is why both backends ask <c>StageInterface.MustBeFlat</c> rather
    ///         than each deciding. Two copies of a rule is how they come to differ.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This test asserted <c>flat out</c>'s absence, and that was the bug.</b> The
    ///         reasoning it inherited — the decoration describes how a value is <em>received</em>,
    ///         so only the fragment input carries it — is SPIR-V's, where a stage is its own module
    ///         and the vertex output genuinely must not be decorated (asserted above, and still
    ///         true). GLSL is not modules: <c>Platform/Vixen.Graphics.OpenGL/GlslTranslator</c>
    ///         hands these two strings to <c>glShaderSource</c> and links them into one program, and
    ///         a linked program whose halves disagree about an interpolation qualifier does not
    ///         link. An integer vertex output is separately required to be <c>flat</c> in GLSL ES.
    ///     </para>
    ///     <para>
    ///         It is reachable rather than theoretical: <c>vixen content build --shader-target glsl</c>
    ///         is the GLES and WebGL2 path, and two shipped library shaders carry an integer stream
    ///         across the boundary — <c>Pipeline/ForwardPlus.rvn</c>'s <c>objectIndex</c> and
    ///         <c>Pipeline/ClusterRaster.rvn</c>'s <c>identity</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public void GlslQualifiesTheSameStreamFlatAtBothEnds() {
        var module = Lower(Indexed);
        var fragment = GlslFor(module, ShaderStage.Fragment);
        var vertex = GlslFor(module, ShaderStage.Vertex);

        Assert.Contains("flat in int in_objectIndex;", fragment, StringComparison.Ordinal);
        Assert.Contains("flat out int out_objectIndex;", vertex, StringComparison.Ordinal);

        // The float stream at both ends too, for the reason the SPIR-V test gives: qualifying
        // everything flat links perfectly and quietly kills interpolation.
        Assert.DoesNotContain("flat in vec2", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("flat out vec2", vertex, StringComparison.Ordinal);
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

                [FragmentShader]
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

    // --- A stream nothing reads -------------------------------------------

    /// <summary>
    ///     A prepass whose only read of its stream sits inside a permutation — the shape all three
    ///     library shaders that spent a varying on nothing turned out to have.
    /// </summary>
    const string Gated = """
                         package A

                         shader Prepass {
                             [Permutation] val AlphaTested: bool = false

                             stream var uv: float2

                             [VertexShader]
                             [Semantic("SV_Position")]
                             func Vertex(position: float3, texcoord: float2): float4 {
                                 uv = texcoord
                                 return float4(position, 1f)
                             }

                             [FragmentShader]
                             [Semantic("SV_Target")]
                             func Fragment(): float4 {
                                 if (AlphaTested) {
                                     return float4(uv.x, uv.y, 0f, 1f)
                                 }

                                 return float4(1f, 1f, 1f, 1f)
                             }
                         }

                         """;

    /// <summary>
    ///     One pipeline written as two <c>shader</c> declarations, which is how <c>Ui.rvn</c> and
    ///     <c>Line.rvn</c> are written: the vertex stage's consumer is not in its own shader.
    /// </summary>
    const string Split = """
                         package A

                         shader Blit {
                             stream var colour: float4

                             [VertexShader]
                             [Semantic("SV_Position")]
                             func Vertex(position: float3, vertexColour: float4): float4 {
                                 colour = vertexColour
                                 return float4(position, 1f)
                             }
                         }

                         shader Tint {
                             stream var colour: float4

                             [FragmentShader]
                             [Semantic("SV_Target")]
                             func Fragment(): float4 {
                                 return colour
                             }
                         }

                         """;

    static IrModule LowerWith(string source, PermutationValues values) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", values, [tree]);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.Empty(bag.ToArray());

        return module;
    }

    /// <summary>
    ///     A stream the folded variant's fragment stage never reads costs the vertex stage no
    ///     varying: it is written to a private global instead of an <c>out</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The interesting half is that this is decided per variant.</b> The read is right
    ///         there in the source; what makes the varying dead is a permutation value, so nothing
    ///         about the <c>.rvn</c> could say it, and the answer differs between two compilations
    ///         of the same file. GLSL ES 3.0 guarantees 16 vec4 of varyings and a written-but-unread
    ///         vertex output still counts against them, so the slot is a real budget.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AStreamTheFoldedVariantNeverReadsTakesNoVaryingSlot() {
        var module = LowerWith(Gated, PermutationValues.Empty);
        var shader = FindShader(module, "Prepass");
        var vertex = Assert.Single(shader.EntryPoints, e => e.Stage == ShaderStage.Vertex);
        var fragment = Assert.Single(shader.EntryPoints, e => e.Stage == ShaderStage.Fragment);

        Assert.Empty(vertex.StreamOutputs);
        Assert.Equal(["uv"], vertex.PrivateStreams.Select(s => s.Name));
        Assert.Empty(fragment.StreamInputs);

        var bag = new DiagnosticBag();
        var generated = new GlslBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        var code = Assert.Single(generated, unit => unit.Stage == ShaderStage.Vertex).Code;

        // The store survives — it is the declaration's qualifier that goes.
        Assert.Contains("vec2 out_uv;", code, StringComparison.Ordinal);
        Assert.Contains("out_uv = ", code, StringComparison.Ordinal);
        Assert.DoesNotContain("out vec2 out_uv", code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     And the other value of the same permutation keeps it: this is a variant's answer, not the
    ///     shader's.
    /// </summary>
    [Fact]
    public void TheVariantThatReadsTheStreamStillGetsAVarying() {
        var module = LowerWith(Gated, PermutationValues.Create([new("AlphaTested", true)]));
        var shader = FindShader(module, "Prepass");
        var vertex = Assert.Single(shader.EntryPoints, e => e.Stage == ShaderStage.Vertex);

        Assert.Equal(["uv"], vertex.StreamOutputs.Select(s => s.Name));
        Assert.Empty(vertex.PrivateStreams);

        var bag = new DiagnosticBag();
        var generated = new GlslBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        Assert.Contains(
            "layout(location = 0) out vec2 out_uv;",
            Assert.Single(generated, unit => unit.Stage == ShaderStage.Vertex).Code,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     ⚠ A shader with no stage after the writer keeps every stream it writes, because its
    ///     reader is in another <c>shader</c> declaration and this compilation cannot see it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The engine's two most-shipped modules are this shape</b>, and they are the reason
    ///         the rule is not simply "nothing in this shader reads it".
    ///         <c>Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn</c> declares <c>UiVertex</c> beside seven
    ///         fragment shaders and <c>Editor/Vixen.Editor.Host/Shaders/Line.rvn</c> splits
    ///         <c>LineVertex</c> from <c>LineFragment</c>: one pipeline, two declarations, matching
    ///         locations only because <c>StreamPlan</c> numbers by declaration order in each. Drop a
    ///         vertex-only shader's streams and the fragment module reads locations nothing writes —
    ///         which is a link failure, not a wasted slot.
    ///     </para>
    ///     <para>
    ///         Both of those modules are committed, so <c>CheckShaders</c> caught it — but only
    ///         after the fact and only for the two files that happen to have a committed
    ///         <c>.spv</c>. This is the assertion that does not depend on that.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AVertexOnlyShaderKeepsEveryStreamItWrites() {
        var module = LowerWith(Split, PermutationValues.Empty);
        var vertex = Assert.Single(FindShader(module, "Blit").EntryPoints);

        Assert.Equal(["colour"], vertex.StreamOutputs.Select(s => s.Name));
        Assert.Empty(vertex.PrivateStreams);

        var bag = new DiagnosticBag();
        var generated = new GlslBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        // The two halves have to agree on location 0 or the host's program does not link.
        Assert.Contains(
            "layout(location = 0) out vec4 out_colour;",
            Assert.Single(generated, unit => unit.Name.StartsWith("Blit.", StringComparison.Ordinal)).Code,
            StringComparison.Ordinal
        );

        Assert.Contains(
            "layout(location = 0) in vec4 in_colour;",
            Assert.Single(generated, unit => unit.Name.StartsWith("Tint.", StringComparison.Ordinal)).Code,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     And in SPIR-V it is a <c>Private</c> variable: no <c>Location</c>, and absent from the
    ///     entry point's interface, which before SPIR-V 1.4 may list only <c>Input</c> and
    ///     <c>Output</c>.
    /// </summary>
    /// <remarks>
    ///     The validator is the point of this one. A private stream that kept the stream maps'
    ///     Input/Output storage class would build its access chain through a pointer type that does
    ///     not match the variable, and an interface list naming it would be rejected outright —
    ///     both are <c>spirv-val</c> failures rather than anything a string assertion would notice.
    /// </remarks>
    [Fact]
    public void SpirvGivesAnUnreadStreamPrivateStorageAndNoLocation() {
        Assert.SkipUnless(SpirvTestBase.ValidatorAvailable, "spirv-val is not on PATH (brew install spirv-tools).");

        var module = LowerWith(Gated, PermutationValues.Empty);
        var bag = new DiagnosticBag();
        var generated = new SpirvBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        foreach (var unit in generated) {
            SpirvTestBase.Validate(unit);
        }

        var vertex = Assert.Single(generated, u => u.Stage == ShaderStage.Vertex).Code;
        var id = SpirvTestBase.IdNamed(vertex, "out_uv");

        Assert.DoesNotContain($"OpDecorate {id} Location", vertex, StringComparison.Ordinal);
        Assert.DoesNotContain("out_uv", SpirvInterface.Read(vertex).Locations.Keys);

        var entryPoint = Assert.Single(
            vertex.Split('\n'),
            line => line.StartsWith("OpEntryPoint ", StringComparison.Ordinal)
        );

        Assert.DoesNotContain(id, entryPoint.Split(' '));
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
