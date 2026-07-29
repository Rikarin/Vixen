// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.IR;
using Vixen.Raven.Reflection;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     Push constants — docs/plan/07 § C and § D, "no syntax, so <c>PushConstants</c> is always
///     empty".
/// </summary>
/// <remarks>
///     <c>[PushConstant]</c> sits beside the <c>[PerFrame]</c>…<c>[PerDraw]</c> markers and is
///     deliberately not a fifth one: a push constant is not in a descriptor set at all, which is
///     the entire reason to reach for it. One block per shader, because that is what a Vulkan
///     pipeline layout takes.
/// </remarks>
public class PushConstantTests {
    const string Sprite = """
                          package A

                          shader S {
                              [PushConstant] var offset: float2
                              [PushConstant] var scale: float2

                              var tint: float4

                              [VertexShader]
                              [Semantic("SV_Position")]
                              func Vertex(position: float2): float4 {
                                  val p = position * scale + offset
                                  return float4(p.x, p.y, 0f, 1f)
                              }

                              [FragmentShader]
                              [Semantic("SV_Target")]
                              func Fragment(): float4 => tint
                          }

                          """;

    [Fact]
    public void A_marked_field_becomes_a_push_constant_rather_than_a_descriptor() {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Sprite), "S");

        Assert.Equal(["offset", "scale"], BindingPlan.PushConstants(shader).Select(c => c.Name));

        // And is absent from the descriptor plan: it has no (set, binding) for a host to bind to.
        Assert.DoesNotContain(
            BindingPlan.Of(shader),
            planned => planned.Kind == IrBindingKind.PushConstant
                || planned.Members.Any(m => m.Kind == IrBindingKind.PushConstant)
        );

        // The uniform block is still there, and still starts at binding 0 — adding a push constant
        // renumbers nothing.
        var block = Assert.Single(BindingPlan.Of(shader));
        Assert.Equal(0, block.Binding);
        Assert.Equal(["tint"], block.Members.Select(m => m.Name));
    }

    [Fact]
    public void GLSL_declares_one_block_with_no_set_or_binding() {
        foreach (var unit in GenerateClean(Sprite)) {
            Assert.Contains(
                "layout(push_constant, std430) uniform SPushConstants {",
                unit.Code,
                StringComparison.Ordinal
            );
        }

        // The vertex stage reads them as ordinary globals, exactly as it reads a uniform.
        var vertex = Assert.Single(GenerateClean(Sprite), u => u.Name.EndsWith(".vert", StringComparison.Ordinal));
        Assert.Contains("vec2 _1 = scale;", vertex.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void SPIR_V_uses_the_push_constant_storage_class_and_no_descriptor_decorations() {
        if (!SpirvTestBase.ValidatorAvailable) {
            return;
        }

        var listing = ReferenceCompiler.Disassemble(
            Assert.Single(GenerateClean(Sprite, "spirv"), u => u.Name.EndsWith(".vert", StringComparison.Ordinal))
                .Binary!
        );

        Assert.Contains("OpVariable %_ptr_PushConstant_SPushConstants PushConstant", listing, StringComparison.Ordinal);
        Assert.Contains("OpDecorate %SPushConstants Block", listing, StringComparison.Ordinal);

        // std430, so the second vec2 sits at 8 rather than at std140's 16.
        Assert.Contains("OpMemberDecorate %SPushConstants 1 Offset 8", listing, StringComparison.Ordinal);

        // A DescriptorSet or Binding on a push constant is a module spirv-val rejects; the only
        // decorated variables here are the ordinary uniform block and the stage interface.
        Assert.DoesNotContain("%sPushConstants DescriptorSet", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("%sPushConstants Binding", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reflection_reports_one_range_with_std430_offsets() {
        var shader = LoweringTestBase.FindShader(LoweringTestBase.Lower(Sprite), "S");
        var range = Assert.Single(ReflectionBuilder.Describe(shader).PushConstants);

        Assert.Equal("SPushConstants", range.Name);
        Assert.Equal(0, range.Offset);
        Assert.Equal(16, range.Size);
        Assert.Equal([("offset", 0), ("scale", 8)], range.Members.Select(m => (m.Name, m.Offset)));
    }

    /// <summary>
    ///     A descriptor is a handle the driver resolves, not bytes the host can push — so both
    ///     targets reject a push-constant block containing one, and the declaration is what has
    ///     to change.
    /// </summary>
    [Theory]
    [InlineData("Texture2D")]
    [InlineData("Sampler")]
    [InlineData("Buffer<float>")]
    public void A_descriptor_cannot_be_pushed(string type) {
        Assert.Contains(
            SemanticTestBase.Diagnose(
                $"package A\n\nshader S {{\n    [PushConstant] var thing: {type}\n}}\n"
            ),
            d => d.Id == "RVN2120" && d.IsError
        );
    }

    [Fact]
    public void A_set_marker_on_a_push_constant_says_something_untrue() {
        var diagnostics = SemanticTestBase.Diagnose(
            """
            package A

            shader S {
                [PushConstant] [PerFrame] var offset: float2
            }

            """
        );

        var warning = Assert.Single(diagnostics, d => d.Id == "RVN2121");
        Assert.False(warning.IsError);
        Assert.Contains("PerFrame", warning.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_push_constant_that_is_not_a_binding_at_all_is_reported_like_any_misplaced_marker() {
        Assert.Contains(
            SemanticTestBase.Diagnose(
                """
                package A

                shader S {
                    [PushConstant] const val Bias = 0.5
                }

                """
            ),
            d => d.Id == "RVN2091"
        );
    }

    /// <summary>
    ///     128 bytes is what a Vulkan implementation is <em>required</em> to offer. A shader over
    ///     it may still be right for its target, so this is a warning — but it is the kind of
    ///     limit that is invisible until a device refuses the pipeline.
    /// </summary>
    [Fact]
    public void A_block_over_the_guaranteed_size_warns_with_the_number() {
        var source = "package A\n\nshader S {\n"
            + string.Concat(Enumerable.Range(0, 9).Select(i => $"    [PushConstant] var m{i}: mat4\n"))
            + "}\n";

        LoweringTestBase.LowerWithDiagnostics(source, out var diagnostics);

        var warning = Assert.Single(diagnostics, d => d.Id == "RVN3007");
        Assert.False(warning.IsError);
        Assert.Contains("576 bytes", warning.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_at_the_guaranteed_size_does_not_warn() {
        // Two mat4s is exactly 128.
        LoweringTestBase.Lower(
            """
            package A

            shader S {
                [PushConstant] var world: mat4
                [PushConstant] var view: mat4

                [FragmentShader]
                func Fragment(): float4 => (world * view)[0]
            }

            """
        );
    }
}
