// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;
using Xunit;
using static Tests.CodeGenTestBase;

namespace Tests;

/// <summary>
///     <c>SampleGrad</c>: a sample whose level of detail comes from gradients the caller computed.
/// </summary>
/// <remarks>
///     <para>
///         The third sampling form, and the one docs/plan/22-virtualized-geometry.md § B3 is blocked on.
///         <c>Sample</c> takes its gradients from the fragment quad, which is meaningless in a
///         visibility-buffer resolve — the pixel next door may be a different triangle of a
///         different material, so the quad's difference is between two unrelated surfaces.
///         <c>SampleLevel</c> states one number, and one number has no anisotropy in it, which is
///         visible as blur on every floor at a grazing angle.
///     </para>
///     <para>
///         So what is tested here is that the gradients survive as <em>operands</em> all the way to
///         both targets — SPIR-V's <c>Grad</c> image operand and GLSL's <c>textureGrad</c> — rather
///         than being collapsed into a level somewhere on the way.
///     </para>
/// </remarks>
public class SampleGradTests {
    const string Resolve = """
                           package A

                           shader S {
                               var albedo: Texture2D
                               var linear: Sampler

                               [FragmentShader]
                               func Fragment(uv: float2, dx: float2, dy: float2): float4 {
                                   return albedo.SampleGrad(linear, uv, dx, dy)
                               }
                           }

                           """;

    /// <summary>
    ///     A compute stage, because that is where the resolve actually runs — and where neither of
    ///     the other two forms would do: there are no derivatives to take, and a stated level throws
    ///     away what the pass computed the gradients for.
    /// </summary>
    const string Compute = """
                           package A

                           shader S {
                               var albedo: Texture2D
                               var linear: Sampler
                               var output: RWBuffer<float4>

                               [ComputeShader(64, 1, 1)]
                               func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                                   val uv = float2(float(id.x), float(id.y)) * 0.01f
                                   output[int(id.x)] = albedo.SampleGrad(linear, uv, float2(0.01f, 0f), float2(0f, 0.01f))
                               }
                           }

                           """;

    [Theory]
    [InlineData("Texture2D", "float2")]
    [InlineData("Texture3D", "float3")]
    [InlineData("TextureCube", "float3")]
    public void Every_texture_type_carries_SampleGrad(string type, string gradient) {
        var texture = Assert.IsType<BuiltInNamedTypeSymbol>(BuiltInTypes.Lookup(type));
        var method = Assert.Single(texture.GetMembers().OfType<MethodSymbol>(), m => m.Name == "SampleGrad");

        // Sampler, coordinate, and one gradient per direction.
        Assert.Equal(4, method.Parameters.Count);
        Assert.Equal("float4", method.ReturnType.ToDisplayString());

        // The gradients are in the *coordinate's* space, not the pixel's: a cube is sampled by a
        // direction, so its gradients have three lanes even though the screen has two axes.
        Assert.Equal(gradient, method.Parameters[2].Type.ToDisplayString());
        Assert.Equal(gradient, method.Parameters[3].Type.ToDisplayString());
    }

    [Fact]
    public void Gradients_reach_GLSL_as_textureGrad() {
        var unit = Assert.Single(GenerateClean(Resolve));

        Assert.Contains("textureGrad(sampler2D(albedo, linear)", unit.Code, StringComparison.Ordinal);

        // Neither of the other two forms: an implicit sample would take the quad's derivatives and
        // an explicit level would have thrown the anisotropy away.
        Assert.DoesNotContain("textureLod(", unit.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("texture(sampler2D", unit.Code, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The gradients are two operands in x-then-y order, and both are the values the shader
    ///     passed rather than anything the backend derived.
    /// </summary>
    [Fact]
    public void Both_gradients_reach_GLSL_in_order() {
        var unit = Assert.Single(GenerateClean(Resolve));

        // `uv`, `dx`, `dy` are loaded in declaration order, so the three ids in the call say which
        // argument went where — an x/y swap would read `_4, _3` and mip a floor along the wrong axis.
        Assert.Contains(
            "textureGrad(sampler2D(albedo, linear), _2, _3, _4)",
            unit.Code,
            StringComparison.Ordinal
        );

        Assert.Contains("vec4 Fragment(vec2 uv, vec2 dx, vec2 dy)", unit.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Gradients_reach_SPIR_V_as_the_Grad_image_operand() {
        if (!SpirvTestBase.ValidatorAvailable) {
            return;
        }

        var listing = ReferenceCompiler.Disassemble(SpirvTestBase.One(Resolve).Binary!);

        // Grad is image-operands bit 2, and it is mutually exclusive with Lod — which is the whole
        // reason this is a third instruction shape rather than a fourth operand on SampleLevel.
        Assert.Contains("OpImageSampleExplicitLod", listing, StringComparison.Ordinal);
        Assert.Contains("Grad", listing, StringComparison.Ordinal);
        Assert.DoesNotContain(" Lod ", listing, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The point of the feature: a stage with no fragment quad can still sample at the right
    ///     mip. <c>spirv-val</c> is the verdict — an implicit-lod sample here would be rejected
    ///     outright.
    /// </summary>
    [Fact]
    public void A_compute_stage_may_sample_with_stated_gradients() {
        if (!SpirvTestBase.ValidatorAvailable) {
            return;
        }

        var listing = ReferenceCompiler.Disassemble(SpirvTestBase.One(Compute).Binary!);

        Assert.Contains("OpImageSampleExplicitLod", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("OpImageSampleImplicitLod", listing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("glsl")]
    [InlineData("spirv")]
    public void Sampling_a_cube_and_a_volume_by_gradient_generates(string target) {
        GenerateClean(
            """
            package A

            shader S {
                var volume: Texture3D
                val sky: TextureCube
                var linear: Sampler

                [FragmentShader]
                func Fragment(uvw: float3, dx: float3, dy: float3): float4 {
                    return volume.SampleGrad(linear, uvw, dx, dy) + sky.SampleGrad(linear, uvw, dx, dy)
                }
            }

            """,
            target
        );
    }
}
