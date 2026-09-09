// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Reflection;
using Vixen.Raven.Syntax;
using Xunit;

namespace Tests;

/// <summary>
///     <c>[DynamicOffset]</c>: a shader saying that a block's bytes move per draw.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The engine used to infer this from the set index</b>, and the inference was two
///         claims wearing one number. <c>EffectLoader.KindOf</c> read "a uniform block in the
///         per-draw set, used by a graphics stage" as "bound at a dynamic offset", so <c>[PerDraw]</c>
///         answered both <i>where a binding lives</i> and <i>whether its contents change between
///         draws</i> — and a shader wanting the first without the second had no way to decline. It
///         needed a carve-out for compute, because a dispatch has no draws and a compute shader
///         marking a block <c>[PerDraw]</c> is using the set index for storage; and the next shader
///         wanting a plain per-draw block would have met a refusal it could not fix in the shader.
///     </para>
///     <para>
///         <b>Any marked uniform marks the set's block, and that is not a compromise.</b> A set's
///         loose uniforms are gathered into one binding and bound once, so where its bytes are is a
///         property of the block; there is no per-member answer to give.
///     </para>
/// </remarks>
public class DynamicOffsetTests {
    const string Lit = """
                       package A

                       shader Lit {
                           [PerDraw] [DynamicOffset] var lightCount: int = 0
                           [PerDraw] var probeIndex: int = 0

                           [PerMaterial] var tint: float3

                           [FragmentShader]
                           func Main(): float4 {
                               return float4(tint * float(lightCount + probeIndex), 1f)
                           }
                       }
                       """;

    const string Plain = """
                         package A

                         shader Plain {
                             [PerDraw] var lightCount: int = 0

                             [PerMaterial] var tint: float3

                             [FragmentShader]
                             func Main(): float4 {
                                 return float4(tint * float(lightCount), 1f)
                             }
                         }
                         """;

    /// <summary>The marker survives lowering, on the binding the author wrote it on.</summary>
    [Fact]
    public void TheMarkerReachesTheIr() {
        var module = Lower(Lit);

        Assert.True(FindBinding(module, "lightCount").IsDynamicOffset);

        // And it is not spread over the set's other uniforms: the block-level answer is assembled by
        // the reflection, not by pretending each member said it.
        Assert.False(FindBinding(module, "probeIndex").IsDynamicOffset);
        Assert.False(FindBinding(module, "tint").IsDynamicOffset);
    }

    /// <summary>The reflection reports the set's block as dynamic, and its other sets as not.</summary>
    [Fact]
    public void TheReflectionReportsTheBlock() {
        var reflection = Describe(Lit);

        Assert.True(Block(reflection, 3).IsDynamicOffset);
        Assert.False(Block(reflection, 2).IsDynamicOffset);
    }

    /// <summary>
    ///     ⚠ And an unmarked per-draw block is <i>not</i> dynamic, which is the answer the old
    ///     inference could not give.
    /// </summary>
    /// <remarks>
    ///     The discriminating half. The same shader, the same set, the same stage — the only
    ///     difference is the attribute, and under the rule this replaces the two were identical and
    ///     both came back dynamic.
    /// </remarks>
    [Fact]
    public void AnUnmarkedPerDrawBlockIsNotDynamic() => Assert.False(Block(Describe(Plain), 3).IsDynamicOffset);

    static IrModule Lower(string source) {
        var compilation = Compilation.Create("Test", SyntaxTree.ParseText(source, path: "Test.rvn"));

        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        Assert.True(IrVerifier.Verify(module, bag), string.Join("\n", bag.Select(d => d.ToString())));
        Assert.True(bag.IsEmpty, string.Join("\n", bag.Select(d => d.ToString())));

        return module;
    }

    static RavenReflection Describe(string source) =>
        ReflectionBuilder.Describe(Assert.Single(Lower(source).Shaders));

    /// <summary>The uniform block of one set.</summary>
    static BindingInfo Block(RavenReflection reflection, int set) =>
        Assert.Single(
            reflection.Sets.Single(s => s.Set == set).Bindings,
            binding => binding.Type == DescriptorType.UniformBuffer
        );

    static IrBinding FindBinding(IrModule module, string name) =>
        module.Shaders.SelectMany(shader => shader.Bindings).Single(binding => binding.Name == name);
}
