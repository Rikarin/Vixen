// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § 4.1's source kernels, through the real Raven front end, with no device.
/// </summary>
/// <remarks>
///     <para>
///         <c>TextureKernelTests</c> already compiles every embedded kernel in every storable format,
///         so a source kernel that does not build is red there by existing. What is asserted here is
///         what that file's theories cannot reach: the <em>agreement</em> between each kernel's
///         uniform block and the <see cref="TextureSources" /> builder that fills it, and the binding
///         order the evaluator relies on positionally.
///     </para>
///     <para>
///         ⚠ <b>The agreement test is the one that earns its keep.</b>
///         <c>TexturePlanEvaluator.Uniforms</c> throws when an op omits a parameter, so a drifted
///         builder is a bake-time exception — on a device, in whichever test happens to use that
///         kernel, with a message about a uniform rather than about the builder. It also throws
///         nothing at all when an op carries a parameter the kernel does <em>not</em> declare: that
///         one is silently ignored, so renaming <c>scale</c> in a <c>.rvn</c> and forgetting the
///         builder would leave a shape drawn at its default size and no error anywhere.
///     </para>
/// </remarks>
public class TextureSourceKernelTests {
    /// <summary>The six of doc 48 § 4.1 that are implemented, and the two that are not.</summary>
    /// <remarks>
    ///     <c>Text</c> and <c>Svg Path</c> are the two § 4.1 names with no kernel: both rasterise on
    ///     the CPU — <c>Vixen.Ui.Text</c>'s outlines and <c>Core/Vixen.Ui/SvgPath.cs</c> through
    ///     <c>Rendering/PathTessellator.cs</c> — and reach a plan as an external image, which is
    ///     <c>Bitmap</c>'s path and not a kernel of their own.
    /// </remarks>
    public static TheoryData<string> Sources => ["Uniform", "Bitmap", "Gradient", "Shape", "Noise", "Checker"];

    /// <summary>The kernel every <see cref="TextureSources" /> builder emits, so a new builder is walked.</summary>
    public static TheoryData<string> Builders {
        get {
            TheoryData<string> data = [];

            foreach (var op in TextureSources.All) {
                data.Add(op.Kernel);
            }

            return data;
        }
    }

    /// <summary>Each of the six is embedded under the name a <see cref="TextureOp" /> gives.</summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public void A_source_kernel_is_embedded_under_its_own_name(string kernel) =>
        Assert.Contains(kernel, TextureKernels.Names);

    /// <summary>
    ///     Every parameter a source kernel declares is one its builder supplies, and every parameter
    ///     the builder supplies is one the kernel declares.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both directions, because they fail differently.</b> A kernel member the op omits is an
    ///     exception at bake time; an op parameter the kernel does not declare is silently dropped and
    ///     the picture is drawn with a default. The second is the one that produces a plausible
    ///     picture, and it is the one only this direction of the comparison catches.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Builders))]
    public void A_builder_supplies_exactly_the_parameters_its_kernel_declares(string kernel) {
        var op = Assert.Single(TextureSources.All, candidate => candidate.Kernel == kernel);
        var data = Compile(kernel);

        var declared = data.Parameters
            .Where(member => member.Set == DescriptorSetSlot.PerMaterial)
            .Select(member => Unqualified(member.Name, data.ShaderName))
            // `seed` is the one member the evaluator fills itself, from `TexturePlan.SeedFor`.
            .Where(name => !string.Equals(name, "seed", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var supplied = op.Parameters
            .Select(parameter => parameter.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, supplied);
    }

    /// <summary>
    ///     A source kernel's textures are declared in the order the evaluator binds an op's inputs.
    /// </summary>
    /// <remarks>
    ///     ⚠ Four of the six read nothing at all — they are <em>sources</em> — and that is worth
    ///     asserting rather than assuming: a stray <c>Texture2D</c> in one of them would make the
    ///     evaluator refuse every op that runs it, with a message about input counts.
    /// </remarks>
    [Theory]
    [InlineData("Uniform")]
    [InlineData("Shape")]
    [InlineData("Checker")]
    [InlineData("Noise")]
    [InlineData("Bitmap", "source")]
    [InlineData("Gradient", "ramp")]
    public void A_source_kernel_declares_its_inputs_in_the_order_the_evaluator_binds_them(
        string kernel,
        params string[] inputs
    ) {
        var data = Compile(kernel);

        var textures = data.Bindings
            .Where(binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture })
            .OrderBy(binding => binding.Binding)
            .Select(binding => binding.Name)
            .ToArray();

        Assert.Equal(inputs, textures);
    }

    /// <summary>
    ///     ⚠ A texture-graph kernel cannot reach the shader library, which is why <c>Noise</c> carries
    ///     a copy of <c>Random</c>'s hash and <c>Checker</c> a copy of <c>ComputeColor</c>'s two lines.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The finding, executably.</b> <c>Raven/Library/Core/Random.rvn</c> already holds the
    ///         PCG hash, and <c>Raven/Library/Material/ComputeColor.rvn</c> already holds value noise,
    ///         fractal noise and a checkerboard — for the shader graph. None of them is reachable from
    ///         here: <c>TexturePlanEvaluator.VariantFor</c> hands
    ///         <c>RavenEffectCompiler.FromSources</c> exactly one tree and no <c>.rvnlib</c>
    ///         references, and a package's declarations are visible to another file only within one
    ///         compilation. <c>build/Build.Shaders.cs</c> says the same thing from the other end: a
    ///         shader that imports has to be compiled as its whole import closure, which is what
    ///         <c>raven --source</c> is for.
    ///     </para>
    ///     <para>
    ///         <b>This is a tripwire and it is meant to go red.</b> The day the evaluator gains a way
    ///         to bind the library — a reference path, or the closure compiled in — this test fails,
    ///         and the right response is to delete it and make <c>Noise</c> import
    ///         <c>Vixen.Shaders.Core</c> instead of carrying the constants.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_standalone_kernel_cannot_reach_the_shader_library() {
        const string Source = """
            package Vixen.Editor.TextureGraph.Shaders

            import Vixen.Shaders.Core

            shader Reach {
                [Format("rgba16f")] var target: RWTexture2D<float4>

                [ComputeShader(8, 8, 1)]
                func Main([Semantic("SV_DispatchThreadID")] id: uint3) {
                    val value = Random.Float01(id.x)

                    target.Store(int2(int(id.x), int(id.y)), float4(value, value, value, 1f))
                }
            }
            """;

        var failure = Record.Exception(
            () => RavenEffectCompiler.FromSources([("Reach.rvn", Source)]).TryGet(EffectKey.Of("Reach"))
        );

        Assert.NotNull(failure);

        // Not the message, which is the compiler's to word — only that the library did not resolve.
        Assert.Contains("Random", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>No source kernel imports, because none of them can. The rule the tripwire above guards.</summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public void A_source_kernel_imports_nothing(string kernel) {
        var imports = TextureKernels
            .Source(kernel)
            .Split('\n')
            .Where(line => line.TrimStart().StartsWith("import", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(imports);
    }

    static string Unqualified(string name, string shader) =>
        name.Length > shader.Length + 1
        && name.StartsWith(shader, StringComparison.Ordinal)
        && name[shader.Length] == '.'
            ? name[(shader.Length + 1)..]
            : name;

    static EffectData Compile(string kernel) {
        var data = RavenEffectCompiler
            .FromSources([(TextureKernels.VariantName(kernel, TextureFormat.Rgba8),
                TextureKernels.Variant(kernel, TextureFormat.Rgba8))])
            .TryGet(EffectKey.Of(kernel));

        Assert.NotNull(data);

        return data;
    }
}
