// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     The interface a <c>compose</c>d shader contributes to its consumer: bindings and streams.
/// </summary>
/// <remarks>
///     <para>
///         <c>ComposeTests</c> covers resolution — which implementation a slot picks and that the call
///         is static. This covers what the implementation *brings with it*, which was missing
///         entirely: a feature's bindings live on its own <see cref="IrShader" />, only the consuming
///         shader's reached the emitted unit, and the GLSL therefore named identifiers it never
///         declared. <c>glslc</c> rejected it and Raven said nothing — the same shape as the
///         inheritance defects in docs/plan/07 § J.
///     </para>
///     <para>
///         So `compose` worked only for a stateless implementation, and every real material feature
///         has parameters. These tests exist because that gap survived a passing test suite: nothing
///         composed an implementation that declared a <c>var</c>.
///     </para>
/// </remarks>
public class ComposeInterfaceTests {
    const string Tinted = """
                          package A

                          protocol IDiffuse {
                              func Diffuse(albedo: float3): float3
                          }

                          shader Tinted : IDiffuse {
                              var tint: float3
                              var albedoMap: Texture2D
                              var albedoSampler: Sampler

                              func Diffuse(albedo: float3): float3 => albedo * tint
                          }

                          shader Lit {
                              compose val diffuse: IDiffuse

                              var baseColor: float3

                              [FragmentShader]
                              [Semantic("SV_Target")]
                              func Fragment(): float4 {
                                  return float4(diffuse.Diffuse(baseColor), 1f)
                              }
                          }

                          """;

    /// <summary>
    ///     A composed feature's bindings become the consumer's, so the emitted unit declares them.
    /// </summary>
    [Fact]
    public void AComposedFeaturesBindingsReachTheConsumer() {
        var shader = FindShader(Lower(Tinted, "diffuse=Tinted"), "Lit");

        Assert.Equal(
            ["baseColor", "Tinted.tint", "Tinted.albedoMap", "Tinted.albedoSampler"],
            shader.Bindings.Select(binding => binding.Name)
        );
    }

    /// <summary>
    ///     Opaque resources come across too, not only the uniform block's members.
    /// </summary>
    /// <remarks>
    ///     Worth separating: a texture and a sampler are their own descriptors rather than block
    ///     members, so they travel a different path through <see cref="BindingPlan" /> and could
    ///     plausibly have been handled for one kind and not the other.
    /// </remarks>
    [Fact]
    public void AComposedFeaturesResourcesAreDescriptorsOfTheConsumer() {
        var shader = FindShader(Lower(Tinted, "diffuse=Tinted"), "Lit");
        var plan = BindingPlan.Of(shader);

        Assert.Contains(plan, planned => planned.Resource?.Kind == IrBindingKind.Texture);
        Assert.Contains(plan, planned => planned.Resource?.Kind == IrBindingKind.Sampler);

        // Every binding gets a distinct (set, binding) pair, which is what the host binds against.
        var pairs = plan.Select(planned => (planned.Set, planned.Binding)).ToArray();
        Assert.Equal(pairs.Length, pairs.Distinct().Count());
    }

    /// <summary>
    ///     Both backends emit something the reference tools accept — the claim that failed before.
    /// </summary>
    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void AComposedFeatureReachesBothBackends(string target) {
        var generated = Generate(Tinted, "diffuse=Tinted", target);

        if (target == "spirv") {
            Assert.All(generated, SpirvTestBase.Validate);
        } else {
            // The identifier the GLSL declares, whatever it renamed it to, has to be there — an
            // undeclared one is what glslc caught and Raven did not.
            var code = Assert.Single(generated).Code;
            Assert.Contains("tint", code, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The reflection reports a contributed binding under a qualified name.
    /// </summary>
    /// <remarks>
    ///     Features are authored independently and collide — three of the features in
    ///     <c>Material/MaterialSurface.rvn</c> declare a <c>strength</c>. Two reflection entries with
    ///     one name is a host writing the wrong offset, and nothing would have said so.
    /// </remarks>
    [Fact]
    public void ACollidingNameIsQualifiedInTheReflection() {
        const string Collides = """
                                package A

                                protocol IDiffuse {
                                    func Diffuse(albedo: float3): float3
                                }

                                shader Tinted : IDiffuse {
                                    var tint: float3

                                    func Diffuse(albedo: float3): float3 => albedo * tint
                                }

                                shader Lit {
                                    compose val diffuse: IDiffuse

                                    var tint: float3

                                    [FragmentShader]
                                    [Semantic("SV_Target")]
                                    func Fragment(): float4 {
                                        return float4(diffuse.Diffuse(tint), 1f)
                                    }
                                }

                                """;

        var module = Lower(Collides, "diffuse=Tinted");
        var reflection = ReflectionBuilder.Describe(FindShader(module, "Lit"));

        Assert.Equal(["tint", "Tinted.tint"], reflection.Parameters.Select(parameter => parameter.Name));

        // Distinct offsets, which is the thing a host would have got wrong.
        Assert.Equal(2, reflection.Parameters.Select(parameter => parameter.Offset).Distinct().Count());

        // And the GLSL still compiles: its identifiers are uniquified separately from these names.
        AssertGlslCompiles(Generate(Collides, "diffuse=Tinted", "glsl"));
    }

    /// <summary>
    ///     A feature's own <c>compose</c> slot contributes too, with the whole path in the name.
    /// </summary>
    /// <remarks>
    ///     Transitive composition — a layered material whose coat feature fills a slot of its own —
    ///     did not work at all before this, and for an unrelated reason: validation ran as a side
    ///     effect of resolution, so resolving the middle shader's base list reached the outer
    ///     shader's compose check, which asked the middle one for interfaces it had not finished
    ///     resolving. It answered with the empty list and reported RVN2076 on correct source. See
    ///     <c>SourceNamedTypeSymbol.EnsureValidated</c>.
    /// </remarks>
    [Fact]
    public void ATransitivelyComposedFeatureContributesUnderItsWholePath() {
        const string Layered = """
                               package A

                               protocol INdf {
                                   func D(x: float): float
                               }

                               protocol IDiffuse {
                                   func Diffuse(albedo: float3): float3
                               }

                               shader Ggx : INdf {
                                   var alpha: float

                                   func D(x: float): float => alpha * x
                               }

                               shader Layered : IDiffuse {
                                   compose val ndf: INdf

                                   var strength: float

                                   func Diffuse(albedo: float3): float3 => albedo * strength * ndf.D(0.5f)
                               }

                               shader Lit {
                                   compose val diffuse: IDiffuse

                                   var baseColor: float3

                                   [FragmentShader]
                                   [Semantic("SV_Target")]
                                   func Fragment(): float4 {
                                       return float4(diffuse.Diffuse(baseColor), 1f)
                                   }
                               }

                               """;

        var module = Lower(Layered, "diffuse=Layered", "ndf=Ggx");
        var shader = FindShader(module, "Lit");

        Assert.Equal(
            ["baseColor", "Layered.strength", "Layered.Ggx.alpha"],
            shader.Bindings.Select(binding => binding.Name)
        );

        AssertGlslCompiles(Generate(Layered, "glsl", ["diffuse=Layered", "ndf=Ggx"]));
    }

    /// <summary>
    ///     A shader that both implements a protocol and declares a slot of its own is accepted.
    /// </summary>
    /// <remarks>
    ///     The narrow regression test for the resolution-order defect above, kept separate from the
    ///     transitive test so a failure says which of the two broke.
    /// </remarks>
    [Fact]
    public void AnImplementationMayDeclareItsOwnSlot() {
        const string Source = """
                              package A

                              protocol INdf {
                                  func D(x: float): float
                              }

                              protocol IDiffuse {
                                  func Diffuse(albedo: float3): float3
                              }

                              shader Ggx : INdf {
                                  func D(x: float): float => x
                              }

                              shader Layered : IDiffuse {
                                  compose val ndf: INdf

                                  func Diffuse(albedo: float3): float3 => albedo
                              }

                              shader Lit {
                                  compose val diffuse: IDiffuse

                                  [FragmentShader]
                                  [Semantic("SV_Target")]
                                  func Fragment(): float4 {
                                      return float4(diffuse.Diffuse(float3(1f)), 1f)
                                  }
                              }

                              """;

        var diagnostics = Compile(Source, "diffuse=Layered", "ndf=Ggx").GetDiagnostics();

        Assert.DoesNotContain(diagnostics, d => d.Id == "RVN2076");
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(d => d.ToString())));
    }

    /// <summary>
    ///     Two slots filled with the same implementation contribute its bindings once.
    /// </summary>
    [Fact]
    public void OneImplementationInTwoSlotsContributesOnce() {
        const string Twice = """
                             package A

                             protocol IDiffuse {
                                 func Diffuse(albedo: float3): float3
                             }

                             shader Tinted : IDiffuse {
                                 var tint: float3

                                 func Diffuse(albedo: float3): float3 => albedo * tint
                             }

                             shader Lit {
                                 compose val a: IDiffuse
                                 compose val b: IDiffuse

                                 [FragmentShader]
                                 [Semantic("SV_Target")]
                                 func Fragment(): float4 {
                                     return float4(a.Diffuse(float3(1f)) + b.Diffuse(float3(2f)), 1f)
                                 }
                             }

                             """;

        var shader = FindShader(Lower(Twice, "a=Tinted", "b=Tinted"), "Lit");

        // One entry, not two: the implementation is one shader with one set of storage, so both
        // slots read the same parameter. Per-slot parameters would need the implementation
        // instantiated per slot, which is not what `compose` does.
        Assert.Equal(["Tinted.tint"], shader.Bindings.Select(binding => binding.Name));
    }

    /// <summary>
    ///     A shader that composes nothing is unchanged, bindings and names alike.
    /// </summary>
    /// <remarks>
    ///     The merge appends and qualifies only what it contributes, so the overwhelmingly common
    ///     case — a shader with no slots — must keep bare names. A regression here would rename every
    ///     existing parameter a host binds against.
    /// </remarks>
    [Fact]
    public void AShaderWithoutSlotsIsUnchanged() {
        const string Plain = """
                             package A

                             shader Lit {
                                 var baseColor: float3
                                 var roughness: float

                                 [FragmentShader]
                                 [Semantic("SV_Target")]
                                 func Fragment(): float4 {
                                     return float4(baseColor, roughness)
                                 }
                             }

                             """;

        var shader = FindShader(Lower(Plain), "Lit");
        Assert.Equal(["baseColor", "roughness"], shader.Bindings.Select(binding => binding.Name));
    }

    // --- Plumbing ---------------------------------------------------------

    static Compilation Compile(string source, params string[] bindings) =>
        Compilation.Create(
            "Test",
            PermutationValues.Empty,
            ComposeBindings.Parse(bindings),
            [SyntaxTree.ParseText(source, path: "Test.rvn")]
        );

    static IrModule Lower(string source, params string[] bindings) {
        var compilation = Compile(source, bindings);

        var diagnostics = compilation.GetDiagnostics();
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(d => d.ToString())));

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        Assert.True(IrVerifier.Verify(module, bag), string.Join("\n", bag.Select(d => d.ToString())));
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));
        return module;
    }

    static IReadOnlyList<GeneratedSource> Generate(string source, string binding, string target) =>
        Generate(source, target, [binding]);

    static IReadOnlyList<GeneratedSource> Generate(string source, string target, string[] bindings) {
        var module = Lower(source, bindings);
        var bag = new DiagnosticBag();
        var generated = TargetBackends.Create(target)!.Generate(module, bag);

        var errors = bag.ToArray().Where(d => d.IsError).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(d => d.ToString())));

        return generated;
    }

    /// <summary>
    ///     Runs the emitted GLSL through <c>glslc</c>, which is the tool that caught this defect when
    ///     Raven did not.
    /// </summary>
    static void AssertGlslCompiles(IReadOnlyList<GeneratedSource> generated) {
        Assert.SkipUnless(ReferenceCompiler.Available, "glslc is not on PATH (brew install shaderc).");

        foreach (var unit in generated) {
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }
    }

    static IrShader FindShader(IrModule module, string name) =>
        module.Shaders.Single(shader => shader.Name == name);
}
