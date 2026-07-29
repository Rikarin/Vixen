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
using static Tests.SemanticTestBase;

namespace Tests;

/// <summary>
///     The engine's four-set descriptor convention (docs/plan/05 § "Descriptor model"):
///     set 0 per-frame, 1 per-view, 2 per-material, 3 per-draw.
/// </summary>
/// <remarks>
///     The convention is honoured by <see cref="BindingPlan" /> alone, and both emitters plus
///     the reflection read that plan — so these tests are about the plan being right and the
///     three consumers agreeing, which is what doc 07 § C asks for.
/// </remarks>
public class DescriptorSetTests {
    const string Source = """
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

                          """;

    static IrShader Lower(string source, string name = "S") {
        var tree = SyntaxTree.ParseText(source, path: "Test.rvn");
        Assert.Empty(tree.Diagnostics);

        var compilation = Compilation.Create("Test", tree);
        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);
        IrVerifier.Verify(module, bag);
        Assert.DoesNotContain(bag.ToArray(), d => d.IsError);

        return FindShader(module, name);
    }

    // --- The default --------------------------------------------------------

    /// <summary>
    ///     An unmarked field is a material parameter. Defaulting to set 0 instead would put
    ///     every unannotated shader in the engine's per-frame set, on top of the camera and
    ///     lighting buffers.
    /// </summary>
    [Fact]
    public void An_unmarked_binding_is_per_material() {
        var plan = BindingPlan.Of(Lower(
            """
            package A

            shader S {
                var tint: float4

                [FragmentShader]
                func Fragment(): float4 {
                    return tint
                }
            }

            """
        ));

        Assert.Equal(ResourceSet.PerMaterial, Assert.Single(plan).Set);
    }

    // --- The plan -----------------------------------------------------------

    [Fact]
    public void Each_marker_places_its_binding_in_the_matching_set() {
        var plan = BindingPlan.Of(Lower(Source));

        Assert.Equal(
            [
                (ResourceSet.PerFrame, 0, "SPerFrameUniforms"),
                (ResourceSet.PerView, 0, "SPerViewUniforms"),
                (ResourceSet.PerMaterial, 0, "SPerMaterialUniforms"),
                (ResourceSet.PerMaterial, 1, "albedo"),
                (ResourceSet.PerMaterial, 2, "linear"),
                (ResourceSet.PerDraw, 0, "SPerDrawUniforms")
            ],
            plan.Select(b => (b.Set, b.Binding, b.Name))
        );
    }

    /// <summary>
    ///     A set is one binding namespace, so numbering restarts — and the block comes first,
    ///     so adding a texture never renumbers it.
    /// </summary>
    [Fact]
    public void Sets_are_ordered_and_the_block_precedes_its_resources() {
        var plan = BindingPlan.Of(Lower(Source));

        Assert.Equal(plan.Select(b => (int)b.Set).Order(), plan.Select(b => (int)b.Set));

        foreach (var set in plan.GroupBy(b => b.Set)) {
            Assert.Equal(Enumerable.Range(0, set.Count()), set.Select(b => b.Binding));
            Assert.True(set.First().IsBlock || set.All(b => !b.IsBlock));
        }
    }

    [Fact]
    public void A_set_with_no_uniforms_gets_no_block() {
        var plan = BindingPlan.Of(Lower(
            """
            package A

            shader S {
                [PerFrame] var albedo: Texture2D
                [PerFrame] var linear: Sampler

                [FragmentShader]
                func Fragment(uv: float2): float4 {
                    return albedo.Sample(linear, uv)
                }
            }

            """
        ));

        Assert.DoesNotContain(plan, b => b.IsBlock);
        Assert.Equal([0, 1], plan.Select(b => b.Binding));
    }

    /// <summary>
    ///     An array of textures is one binding with a count, not a member of the uniform block.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         It used to be the block, and that is the failure this exists to keep out: a field's
    ///         resource kind came from its type, an array reported none, and the lowerer's binding
    ///         kind falls through to <c>Uniform</c> for anything that is not a declared resource. So
    ///         <c>var probes: TextureCube[4]</c> compiled — into a uniform block with
    ///         <c>OpTypeImage</c> in it, which no backend can express and both of them emitted.
    ///     </para>
    ///     <para>
    ///         Asserted on the plan rather than on the emitted text because the plan is what both
    ///         backends and the reflection read; the test below holds all three against it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_array_of_textures_is_one_binding_with_a_count() {
        var shader = Lower(ProbeArray);
        var plan = BindingPlan.Of(shader);

        var probes = Assert.Single(plan, b => b.Name == "probes");

        Assert.False(probes.IsBlock);
        Assert.Equal(IrBindingKind.Texture, probes.Kind);

        // And the block that does exist holds the index and nothing else — an array of images in it
        // is what this is here to prevent.
        var block = Assert.Single(plan, b => b.IsBlock);
        Assert.Equal(["probeIndex"], block.Members.Select(m => m.Variable.Name));

        // The count reaches the reflection, which is what a host builds a descriptor set layout from.
        var reflection = ReflectionBuilder.Describe(shader);
        var described = reflection.Sets.SelectMany(s => s.Bindings).Single(b => b.Name == "probes");

        Assert.Equal(DescriptorType.SampledTexture, described.Type);
        Assert.Equal(4, described.Count);
    }

    /// <summary>And what comes out of both backends is something they will accept.</summary>
    /// <remarks>
    ///     The GLSL is the readable half of the same claim: <c>glslc</c> rejects an opaque type
    ///     inside a block with "member of block cannot be or contain a sampler", and the declaration
    ///     carrying its own <c>layout(...)</c> is what says it is no longer in one.
    /// </remarks>
    [Fact]
    public void An_array_of_textures_is_declared_outside_the_block() {
        var glsl = CodeGenTestBase.GenerateClean(ProbeArray, "glsl").Single().Code;

        Assert.Contains(
            "uniform textureCube probes[4]",
            Assert.Single(
                glsl.Split('\n'),
                line => line.StartsWith("layout(", StringComparison.Ordinal)
                    && line.Contains(" probes[", StringComparison.Ordinal)
            ),
            StringComparison.Ordinal
        );
    }

    /// <summary>A shader picking one of several probe cubes by an index the host writes.</summary>
    /// <remarks>
    ///     Which is what per-object reflection probes need: one array bound for the frame and an
    ///     index in the per-object block, rather than a descriptor set per probe bound per draw.
    /// </remarks>
    const string ProbeArray = """
                              package A

                              shader S {
                                  [Permutation] val ProbeCount: int = 4

                                  var probes: TextureCube[ProbeCount]
                                  var probeSampler: Sampler
                                  var probeIndex: int

                                  [FragmentShader]
                                  func Fragment(direction: float3): float4 {
                                      return probes[probeIndex].Sample(probeSampler, direction)
                                  }
                              }

                              """;

    // --- The consumers agree ------------------------------------------------

    /// <summary>
    ///     The requirement in doc 07 § C: "both emitters must agree". They read one plan, so
    ///     this checks the wiring — that neither has kept a numbering scheme of its own.
    /// </summary>
    [Fact]
    public void Both_backends_and_the_reflection_report_the_same_sets_and_bindings() {
        var shader = Lower(Source);
        var plan = BindingPlan.Of(shader);

        // The reflection.
        var reflection = ReflectionBuilder.Describe(shader);
        Assert.Equal(
            plan.Select(b => ((int)b.Set, b.Binding, b.Name)),
            reflection.Sets.SelectMany(s => s.Bindings.Select(b => (s.Set, b.Binding, b.Name)))
        );

        var glsl = CodeGenTestBase.GenerateClean(Source, "glsl").Single().Code;
        var spirv = CodeGenTestBase.GenerateClean(Source, "spirv").Single().Code;

        foreach (var planned in plan) {
            var set = (int)planned.Set;

            // GLSL writes the pair as a layout qualifier on the declaration itself, so the
            // name and its set/binding have to be on one line for this to mean anything.
            Assert.Contains(
                $"set = {set}, binding = {planned.Binding})",
                Assert.Single(
                    glsl.Split('\n'),
                    line => line.StartsWith("layout(", StringComparison.Ordinal)
                        && line.Contains($" {planned.Name}", StringComparison.Ordinal)
                ),
                StringComparison.Ordinal
            );

            // SPIR-V writes it as two decorations on the variable's id. A block's OpName
            // names the struct type, and the emitter names the variable after it with a
            // lower-case initial, so that is the id the decorations are on.
            var named = planned.IsBlock
                ? char.ToLowerInvariant(planned.Name[0]) + planned.Name[1..]
                : planned.Name;

            var id = SpirvTestBase.IdNamed(spirv, named);
            Assert.Contains($"OpDecorate {id} DescriptorSet {set}", spirv, StringComparison.Ordinal);
            Assert.Contains($"OpDecorate {id} Binding {planned.Binding}", spirv, StringComparison.Ordinal);
        }
    }

    // --- Validation ---------------------------------------------------------

    [Fact]
    public void Two_markers_on_one_field_are_rejected() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                """
                package A

                shader S {
                    [PerFrame] [PerDraw] var time: float

                    [FragmentShader]
                    func Fragment(): float4 {
                        return float4(time, 0, 0, 1)
                    }
                }

                """,
                "RVN2090"
            )
        );

        Assert.Contains("PerFrame", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("PerDraw", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A warning, not an error: the shader is still correct, but the author believes
    ///     something about where that value lives that is not true.
    /// </summary>
    [Fact]
    public void A_marker_on_something_that_never_becomes_a_binding_is_a_warning() {
        var diagnostic = Assert.Single(
            AssertDiagnostics(
                """
                package A

                shader S {
                    [PerFrame] const val Ambient = 0.1f

                    [FragmentShader]
                    func Fragment(): float4 {
                        return float4(Ambient, 0, 0, 1)
                    }
                }

                """,
                "RVN2091"
            )
        );

        Assert.False(diagnostic.IsError);
        Assert.Contains("Ambient", diagnostic.GetMessage(), StringComparison.Ordinal);
    }
}
