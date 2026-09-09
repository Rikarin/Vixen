// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.CodeGen;
using Vixen.Raven.CodeGen.Glsl;
using Vixen.Raven.CodeGen.Spirv;
using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Xunit;
using static Tests.LoweringTestBase;
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     <c>[Interpolation("…")]</c>: the author's half of how a varying crosses the rasteriser.
/// </summary>
/// <remarks>
///     <para>
///         doc 07 § Streams's open item. The compiler already applied <c>flat</c> where an integer
///         varying leaves no other legal answer (<c>StageInterface.MustBeFlat</c>); what was missing
///         was any way for a shader to say <c>noperspective</c> on a screen-space float, or to
///         decline interpolation on one, or to ask for centroid sampling — three things that are
///         legal in both targets, mean different pictures, and had no spelling at all.
///     </para>
///     <para>
///         ⚠ <b>The interesting half is the asymmetry, and it is why both backends are asserted
///         here rather than one.</b> SPIR-V decorates the fragment <em>input</em> only, because a
///         stage is its own module and the decoration says how a value is received. GLSL is linked
///         as one program by the GL backend, so both ends have to say the same word or the program
///         does not link. A test that checked one backend would pass with the other silently
///         emitting a link error.
///     </para>
/// </remarks>
public class InterpolationTests {
    /// <summary>
    ///     One shader carrying all four modes, so every assertion below reads the same source and a
    ///     mode that stopped being emitted cannot hide behind a fixture of its own.
    /// </summary>
    const string Source = """
                          package A

                          shader Lit {
                              [Interpolation("noperspective")] stream var screenUv: float2
                              [Interpolation("flat")] stream var faceTint: float3
                              [Interpolation("centroid")] stream var edgeSafe: float4
                              stream var normalWS: float3

                              [VertexShader]
                              func Vertex([Semantic("POSITION")] position: float3): float4 {
                                  screenUv = float2(position.x, position.y)
                                  faceTint = float3(1f, 0f, 0f)
                                  edgeSafe = float4(position.x, position.y, 0f, 1f)
                                  normalWS = float3(0f, 1f, 0f)
                                  return float4(position.x, position.y, position.z, 1f)
                              }

                              [FragmentShader]
                              func Shade(): float4 {
                                  return float4(screenUv.x, faceTint.y, edgeSafe.z, normalWS.y)
                              }
                          }

                          """;

    static string GlslFor(IrModule module, ShaderStage stage) {
        var bag = new DiagnosticBag();
        var generated = new GlslBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return Assert.Single(generated, unit => unit.Stage == stage).Code;
    }

    static string SpirvFor(IrModule module, ShaderStage stage) {
        var bag = new DiagnosticBag();
        var generated = new SpirvBackend().Generate(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        var unit = Assert.Single(generated, u => u.Stage == stage);
        SpirvTestBase.Validate(unit);

        return unit.Code;
    }

    // --- The mode reaches the IR ------------------------------------------

    /// <summary>
    ///     The declaration's word arrives on the stream, and a declaration with no attribute is
    ///     <see cref="InterpolationMode.Smooth" />.
    /// </summary>
    /// <remarks>
    ///     Asserted before either backend, because a mode that never left the binder would make
    ///     every emitter assertion below fail as "the word is missing" rather than as "the emitter
    ///     drops it", and those want different fixes.
    /// </remarks>
    [Fact]
    public void TheDeclaredModeReachesTheIr() {
        var streams = FindShader(Lower(Source), "Lit").Streams.ToDictionary(s => s.Name, s => s.Interpolation);

        Assert.Equal(InterpolationMode.NoPerspective, streams["screenUv"]);
        Assert.Equal(InterpolationMode.Flat, streams["faceTint"]);
        Assert.Equal(InterpolationMode.Centroid, streams["edgeSafe"]);
        Assert.Equal(InterpolationMode.Smooth, streams["normalWS"]);
    }

    // --- GLSL: both ends ---------------------------------------------------

    /// <summary>
    ///     GLSL qualifies the consuming and the producing declaration with the same word.
    /// </summary>
    /// <remarks>
    ///     The rule <c>StreamTests.GlslQualifiesTheSameStreamFlatAtBothEnds</c> pins for the
    ///     compiler's own <c>flat</c>, applied to the author's three. A linked program whose halves
    ///     disagree about an interpolation qualifier does not link, and
    ///     <c>vixen content build --shader-target glsl</c> is the path that links them.
    /// </remarks>
    [Fact]
    public void GlslQualifiesBothEndsWithTheDeclaredWord() {
        var module = Lower(Source);
        var fragment = GlslFor(module, ShaderStage.Fragment);
        var vertex = GlslFor(module, ShaderStage.Vertex);

        Assert.Contains("noperspective in vec2 in_screenUv;", fragment, StringComparison.Ordinal);
        Assert.Contains("noperspective out vec2 out_screenUv;", vertex, StringComparison.Ordinal);

        Assert.Contains("flat in vec3 in_faceTint;", fragment, StringComparison.Ordinal);
        Assert.Contains("flat out vec3 out_faceTint;", vertex, StringComparison.Ordinal);

        Assert.Contains("centroid in vec4 in_edgeSafe;", fragment, StringComparison.Ordinal);
        Assert.Contains("centroid out vec4 out_edgeSafe;", vertex, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A stream that asked for nothing takes no qualifier at either end.
    /// </summary>
    /// <remarks>
    ///     The half that makes the assertions above mean something: an emitter that qualified every
    ///     varying would satisfy all six and would have killed interpolation on every shader in the
    ///     library. <c>smooth</c> is GLSL's default at every version, so the word is not written.
    /// </remarks>
    [Fact]
    public void AStreamThatAskedForNothingTakesNoQualifier() {
        var module = Lower(Source);

        Assert.Contains("in vec3 in_normalWS;", GlslFor(module, ShaderStage.Fragment), StringComparison.Ordinal);
        Assert.DoesNotContain("smooth", GlslFor(module, ShaderStage.Fragment), StringComparison.Ordinal);
        Assert.DoesNotContain("flat in vec3 in_normalWS", GlslFor(module, ShaderStage.Fragment), StringComparison.Ordinal);
        Assert.DoesNotContain("flat out vec3 out_normalWS", GlslFor(module, ShaderStage.Vertex), StringComparison.Ordinal);
    }

    // --- SPIR-V: the receiving end only ------------------------------------

    /// <summary>
    ///     SPIR-V decorates the fragment input and leaves the vertex output bare.
    /// </summary>
    /// <remarks>
    ///     The asymmetry is deliberate and is the reason this file asserts both backends: a
    ///     decoration says how a value is <em>received</em>, and a vertex output is not received.
    ///     <c>spirv-val</c> runs over both modules through <see cref="SpirvFor" />, so a decoration
    ///     applied to the wrong side fails as an invalid module rather than as a missing string.
    /// </remarks>
    [Fact]
    public void SpirvDecoratesOnlyTheFragmentInput() {
        var module = Lower(Source);
        var fragment = SpirvFor(module, ShaderStage.Fragment);
        var vertex = SpirvFor(module, ShaderStage.Vertex);

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(fragment, "in_screenUv")} NoPerspective",
            fragment,
            StringComparison.Ordinal
        );

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(fragment, "in_faceTint")} Flat",
            fragment,
            StringComparison.Ordinal
        );

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(fragment, "in_edgeSafe")} Centroid",
            fragment,
            StringComparison.Ordinal
        );

        // The stream that asked for nothing takes a Location and nothing else — named one mode at a
        // time rather than as "no decoration", since every located variable carries a Location.
        var bare = SpirvTestBase.IdNamed(fragment, "in_normalWS");

        foreach (var mode in new[] { "NoPerspective", "Flat", "Centroid" }) {
            Assert.DoesNotContain($"OpDecorate {bare} {mode}", fragment, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("NoPerspective", vertex, StringComparison.Ordinal);
        Assert.DoesNotContain("Centroid", vertex, StringComparison.Ordinal);
    }

    // --- The other declaration a varying can be ----------------------------

    /// <summary>
    ///     A fragment entry point's own parameter carries the attribute too.
    /// </summary>
    /// <remarks>
    ///     ⚠ The hole this closes is the one <c>StageInterface.MustBeFlat</c> itself once had: it
    ///     was applied in <c>DeclareStream</c> alone, so a varying that arrived as a parameter went
    ///     undecorated and the module was invalid. An attribute only a <c>stream</c> could carry
    ///     would be the same shape of gap one level up — legal source, silently ignored.
    /// </remarks>
    [Fact]
    public void AFragmentParameterCarriesItToo() {
        const string source = """
                              package A

                              shader Lit {
                                  [VertexShader]
                                  func Vertex([Semantic("POSITION")] position: float3): float4 {
                                      return float4(position.x, position.y, position.z, 1f)
                                  }

                                  [FragmentShader]
                                  func Shade([Interpolation("noperspective")] uv: float2): float4 {
                                      return float4(uv.x, uv.y, 0f, 1f)
                                  }
                              }

                              """;

        var module = Lower(source);

        Assert.Contains(
            "noperspective in vec2 in_uv;",
            GlslFor(module, ShaderStage.Fragment),
            StringComparison.Ordinal
        );

        var fragment = SpirvFor(module, ShaderStage.Fragment);

        Assert.Contains(
            $"OpDecorate {SpirvTestBase.IdNamed(fragment, "in_uv")} NoPerspective",
            fragment,
            StringComparison.Ordinal
        );
    }

    // --- Diagnostics -------------------------------------------------------

    /// <summary>
    ///     A word that is not one of the four is <c>RVN2143</c> rather than a fallback to smooth.
    /// </summary>
    /// <remarks>
    ///     A fallback would hand a typo exactly the default the attribute was written to decline,
    ///     and a perspective-divided screen-space value is a wrong picture rather than a missing
    ///     one — nothing downstream can see it.
    /// </remarks>
    [Fact]
    public void AnUnrecognisedWordIsReported() {
        var reported = Assert.Single(
            Diagnose(Fixture("""[Interpolation("nopersective")] stream var screenUv: float2""")),
            d => d.Id == "RVN2143"
        );

        Assert.True(reported.IsError);
        Assert.Contains("nopersective", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("noperspective", reported.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     An interpolation other than <c>flat</c> on an integer varying is <c>RVN2144</c>.
    /// </summary>
    /// <remarks>
    ///     The two predicates agreeing, asserted from outside: the binder refuses the declaration
    ///     because the type has no interpolation to take, and it is the same question
    ///     <c>StageInterface.MustBeFlat</c> answers of the lowered type. Obeying the author instead
    ///     emits a module <c>spirv-val</c> refuses (<c>VUID-StandaloneSpirv-Flat-04744</c>); ignoring
    ///     the author is the silent override this language does not do.
    /// </remarks>
    [Theory]
    [InlineData("int")]
    [InlineData("uint")]
    [InlineData("int2")]
    public void AnInterpolationOnAnIntegerVaryingIsRefused(string type) {
        var reported = Assert.Single(
            Diagnose(Fixture($$"""[Interpolation("noperspective")] stream var id: {{type}}""")),
            d => d.Id == "RVN2144"
        );

        Assert.True(reported.IsError);
        Assert.Contains(type, reported.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>flat</c> on an integer varying is accepted: it says what the compiler was going to do.
    /// </summary>
    [Fact]
    public void FlatOnAnIntegerVaryingIsAccepted() =>
        Assert.DoesNotContain(
            Diagnose(Fixture("""[Interpolation("flat")] stream var id: int""")),
            d => d.Id == "RVN2144"
        );

    /// <summary>
    ///     The attribute on a declaration nothing interpolates is <c>RVN2145</c>.
    /// </summary>
    /// <remarks>
    ///     A warning rather than an error, on the policy <c>RVN2091</c> sets for a marker that
    ///     cannot mean anything where it stands: the shader compiles to exactly what it says and
    ///     what is wrong is that the author believes something is happening. ⚠ A vertex stage's
    ///     parameter is in this list and is the one worth naming — it is a vertex attribute read
    ///     from a buffer, and the thing the author wants qualified is the stream that stage writes.
    /// </remarks>
    [Theory]
    [InlineData("""[Interpolation("flat")] var scale: float""", "not a stream")]
    [InlineData("""[Interpolation("flat")] groupshared var tile: float""", "not a stream")]
    [InlineData("""[Interpolation("flat")] const val Tap: int = 4""", "not a stream")]
    public void TheAttributeOnSomethingThatIsNotAVaryingIsReported(string declaration, string because) {
        var reported = Assert.Single(Diagnose(Fixture(declaration)), d => d.Id == "RVN2145");

        Assert.False(reported.IsError);
        Assert.Contains(because, reported.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     And on a vertex stage's parameter, which is the one an author is most likely to reach
    ///     for.
    /// </summary>
    [Fact]
    public void TheAttributeOnAVertexParameterIsReported() {
        const string source = """
                              package A

                              shader Lit {
                                  [VertexShader]
                                  func Vertex([Interpolation("flat")] [Semantic("POSITION")] position: float3): float4 {
                                      return float4(position.x, position.y, position.z, 1f)
                                  }
                              }

                              """;

        var reported = Assert.Single(Diagnose(source), d => d.Id == "RVN2145");

        Assert.False(reported.IsError);
        Assert.Contains("vertex attributes", reported.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>A shader carrying one declaration, so a fixture is one line.</summary>
    /// <param name="declaration">The member to put in it.</param>
    /// <returns>The source.</returns>
    static string Fixture(string declaration) =>
        $$"""
          package A

          shader Lit {
              {{declaration}}

              [VertexShader]
              func Vertex([Semantic("POSITION")] position: float3): float4 {
                  return float4(position.x, position.y, position.z, 1f)
              }
          }

          """;
}
