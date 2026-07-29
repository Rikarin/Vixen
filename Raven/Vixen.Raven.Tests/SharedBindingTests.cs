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
using static Tests.LoweringTestBase;

namespace Tests;

/// <summary>
///     <c>[Shared]</c> — one resource for the whole compilation, rather than a contribution from
///     each feature that names it.
/// </summary>
/// <remarks>
///     <para>
///         A composed feature's bindings are contributed, and every contribution is qualified by the
///         path it was reached through. That is right for a value: three features that each declare a
///         <c>strength</c> want three, and qualifying is what stops them colliding. It is exactly
///         wrong for the frame's texture table, which two of is two descriptor arrays and two pools —
///         and <c>CompositeSurface</c> chains up to eight features, most of which would want a map.
///     </para>
///     <para>
///         So a binding says for itself. Marked rather than inferred from the declarations matching,
///         because that inference is the wrong default: two features that happened to name a texture
///         <c>noise</c> would silently share one descriptor and neither author would have said
///         anything to that effect.
///     </para>
/// </remarks>
public class SharedBindingTests {
    /// <summary>Two features, each declaring the table, composed into one pass.</summary>
    /// <remarks>
    ///     The shape that found the gap. Both features sample; both name <c>textures</c>; the sampler
    ///     beside it is deliberately <em>not</em> shared, so one source proves both halves — the
    ///     shared name collapses and the ordinary one still qualifies.
    /// </remarks>
    const string Source = """
                          package A

                          protocol ISurface {
                              func Compute(inout value: float4)
                          }

                          shader BaseColor : ISurface {
                              [PerFrame] [Shared] var textures: Texture2D[]
                              var linear: Sampler
                              var index: uint = 0u

                              func Compute(inout value: float4) {
                                  value = textures[int(index)].Sample(linear, float2(0f, 0f))
                              }
                          }

                          shader NormalMap : ISurface {
                              [PerFrame] [Shared] var textures: Texture2D[]
                              var linear: Sampler
                              var index: uint = 0u

                              func Compute(inout value: float4) {
                                  value += textures[int(index)].Sample(linear, float2(0f, 0f))
                              }
                          }

                          shader Composite : ISurface {
                              compose val first: ISurface
                              compose val second: ISurface

                              func Compute(inout value: float4) {
                                  first.Compute(value)
                                  second.Compute(value)
                              }
                          }

                          shader Pass {
                              compose val surface: ISurface

                              [FragmentShader]
                              func Fragment(): float4 {
                                  var value = float4(0f, 0f, 0f, 1f)
                                  surface.Compute(value)
                                  return value
                              }
                          }

                          """;

    /// <summary>
    ///     Two features declaring the table get one binding, under the name they both wrote.
    /// </summary>
    /// <remarks>
    ///     The name is the bare one rather than a path, and that is not cosmetic: the name is how the
    ///     several declarations are recognised as meaning one resource, and it is what a host binds
    ///     against. Qualifying it would make each feature's mention its own binding again.
    /// </remarks>
    [Fact]
    public void Two_features_that_name_one_table_get_one_binding() {
        var plan = Plan(Source);

        var table = Assert.Single(plan, b => b.Kind == IrBindingKind.Texture);

        Assert.Equal("textures", table.Name);
        Assert.Equal(ResourceSet.PerFrame, table.Set);
        Assert.Single(table.Aliases);
    }

    /// <summary>And an unshared binding beside it still gets one per feature, still qualified.</summary>
    /// <remarks>
    ///     The control. A change that collapsed every identically-named contribution would pass the
    ///     test above and break the rule qualification exists for — three features with a
    ///     <c>strength</c> each are three values, and sharing them is a material where moving one
    ///     slider moves three.
    /// </remarks>
    [Fact]
    public void An_unshared_binding_is_still_one_per_feature() {
        var samplers = Plan(Source).Where(b => b.Kind == IrBindingKind.Sampler).ToArray();

        Assert.Equal(2, samplers.Length);
        Assert.Equal(["Composite.BaseColor.linear", "Composite.NormalMap.linear"], samplers.Select(b => b.Name));
        Assert.All(samplers, binding => Assert.Empty(binding.Aliases));
    }

    /// <summary>Both features' variables reach the one declaration, in both backends.</summary>
    /// <remarks>
    ///     <strong>The half that is easy to leave out.</strong> Collapsing the plan is not enough:
    ///     each feature's body was compiled against its <em>own</em> variable, so the second
    ///     feature's sample refers to something the emitter never declared. In SPIR-V that is a
    ///     diagnostic about a variable that is plainly there; in GLSL it is an identifier the unit
    ///     does not contain. Asserted by counting declarations rather than by reading the sample,
    ///     because one declaration used twice is the whole claim.
    /// </remarks>
    [Fact]
    public void Both_features_reach_the_one_declaration() {
        var glsl = Generate(Source, "glsl");

        Assert.Single(
            glsl.Split('\n'),
            line => line.Contains("uniform texture2D", StringComparison.Ordinal)
        );

        // And it is sampled twice, so the second feature did resolve to it rather than being dropped.
        Assert.Equal(2, Occurrences(glsl, "textures[nonuniformEXT("));

        var spirv = Generate(Source, "spirv");

        Assert.Single(
            spirv.Split('\n'),
            line => line.Contains("OpTypeRuntimeArray", StringComparison.Ordinal)
        );

        Assert.Single(
            spirv.Split('\n'),
            line => line.Contains("OpName", StringComparison.Ordinal)
                && line.Contains("\"textures\"", StringComparison.Ordinal)
        );
    }

    /// <summary>The module a real compiler and a real validator accept.</summary>
    [Fact]
    public void The_module_validates() {
        // Compiled through this fixture's own path rather than SpirvTestBase.One, which supplies no
        // compose bindings — and a pass with three unfilled slots does not compile at all.
        SpirvTestBase.Validate(Compile(Source, "spirv").Single());

        if (ReferenceCompiler.Glslc is not null) {
            var unit = Compile(Source, "glsl").Single();
            Assert.NotEmpty(ReferenceCompiler.GlslToSpirv(unit.Code, unit.Stage));
        }
    }

    /// <summary>The reflection reports one binding, which is what the host binds against.</summary>
    [Fact]
    public void The_reflection_reports_one() {
        var (compilation, module) = Lower(Source);
        var reflected = ReflectionBuilder.Describe(FindShader(module, "Pass"), compilation.UsedPermutationKeys);
        var bindings = reflected.Sets.SelectMany(set => set.Bindings).ToArray();

        var table = Assert.Single(bindings, b => b.Name == "textures");
        Assert.Equal(0, table.Count);
        Assert.Equal(2, bindings.Count(b => b.Type == DescriptorType.Sampler));
    }

    /// <summary>
    ///     Two shared declarations of one name that are not the same resource are refused.
    /// </summary>
    /// <remarks>
    ///     One of the two authors is wrong and nothing can say which, so collapsing to whichever came
    ///     first would compile a feature against a resource it did not declare. The set counts as
    ///     well as the kind: one <c>[PerFrame]</c> and one <c>[PerMaterial]</c> are two descriptor
    ///     sets and cannot be one binding however identical the type is.
    /// </remarks>
    [Theory]
    [InlineData("[PerFrame] [Shared] var thing: Sampler", "[PerFrame] [Shared] var thing: Texture2D")]
    [InlineData("[PerFrame] [Shared] var thing: Texture2D", "[PerMaterial] [Shared] var thing: Texture2D")]
    public void Declarations_that_disagree_are_refused(string first, string second) {
        var source = $$"""
                       package A

                       protocol ISurface {
                           func Compute(inout value: float4)
                       }

                       shader First : ISurface {
                           {{first}}

                           func Compute(inout value: float4) {
                               value += float4(1f, 1f, 1f, 1f)
                           }
                       }

                       shader Second : ISurface {
                           {{second}}

                           func Compute(inout value: float4) {
                               value += float4(1f, 1f, 1f, 1f)
                           }
                       }

                       shader Composite : ISurface {
                           compose val a: ISurface
                           compose val b: ISurface

                           func Compute(inout value: float4) {
                               a.Compute(value)
                               b.Compute(value)
                           }
                       }

                       shader Pass {
                           compose val surface: ISurface

                           [FragmentShader]
                           func Fragment(): float4 {
                               var value = float4(0f, 0f, 0f, 1f)
                               surface.Compute(value)
                               return value
                           }
                       }

                       """;

        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", PermutationValues.Empty, Bindings(), [tree]);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);

        Assert.Contains(bag.ToArray(), d => d.Id == "RVN3011" && d.IsError);
    }

    // --- The fixture -------------------------------------------------------

    static ComposeBindings Bindings() =>
        ComposeBindings.Create(
            [new("Pass.surface", "Composite"), new("Composite.a", "First"), new("Composite.b", "Second")]
        );

    static ComposeBindings SurfaceBindings() =>
        ComposeBindings.Create(
            [
                new("Pass.surface", "Composite"),
                new("Composite.first", "BaseColor"),
                new("Composite.second", "NormalMap")
            ]
        );

    static (Compilation Compilation, IrModule Module) Lower(string source) {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", PermutationValues.Empty, SurfaceBindings(), [tree]);
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

    static IReadOnlyList<PlannedBinding> Plan(string source) => BindingPlan.Of(FindShader(Lower(source).Module, "Pass"));

    static IReadOnlyList<GeneratedSource> Compile(string source, string target) {
        var backend = TargetBackends.Create(target);
        Assert.NotNull(backend);

        var bag = new DiagnosticBag();
        var generated = backend!.Generate(Lower(source).Module, bag);

        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);
        return generated;
    }

    static string Generate(string source, string target) => Compile(source, target).Single().Code;

    static int Occurrences(string text, string needle) {
        var count = 0;

        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) {
            count++;
        }

        return count;
    }
}
