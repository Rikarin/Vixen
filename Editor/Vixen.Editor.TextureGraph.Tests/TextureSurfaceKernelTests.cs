// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.6's surface kernels' structural claims, asserted with <b>no device</b>.</summary>
/// <remarks>
///     <para>
///         <c>TextureKernelTests</c> compiles every embedded kernel in every storable format, so one
///         that does not build is red there by existing. What is here is the agreement between each
///         kernel's uniform block and its builder, the binding order the evaluator relies on
///         positionally, and the one arithmetic claim that would otherwise live only in a comment.
///     </para>
///     <para>
///         ⚠ <b>§ 4.6 lists six nodes and this slice registers five kernels, deliberately.</b>
///         <c>Normal → Height</c> is a Poisson solve on the CPU — the plan document says so, and
///         names <c>ConjugateGradient</c> as what should run it — and
///         <see cref="A_plan_cannot_express_an_operation_that_is_not_a_dispatch" /> is the finding
///         about why a plan cannot hold one, written as a test rather than as a paragraph.
///     </para>
/// </remarks>
public class TextureSurfaceKernelTests {
    public static TheoryData<string> Kernels => [.. TextureSurfaceKernels.All];

    /// <summary>Every name this slice registers is a kernel the folder actually holds.</summary>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void A_surface_kernel_is_embedded_under_its_own_name(string kernel) =>
        Assert.Contains(kernel, TextureKernels.Names);

    /// <summary>
    ///     Every parameter a surface kernel declares is one its builder supplies, and every parameter
    ///     the builder supplies is one the kernel declares.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both directions.</b> A kernel member the op omits is an exception at bake time; an op
    ///     parameter the kernel does not declare is silently dropped and the picture is drawn with a
    ///     default — which for <c>AmbientOcclusion</c>'s <c>height</c> is a flat surface and an answer
    ///     of one everywhere, indistinguishable from a pass that did not run.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void A_builder_supplies_exactly_the_parameters_its_kernel_declares(string kernel) {
        var op = Assert.Single(TextureSurfaces.All, candidate => candidate.Kernel == kernel);
        var data = Compile(kernel);

        var declared = data.Parameters
            .Where(member => member.Set == DescriptorSetSlot.PerMaterial)
            .Select(member => Unqualified(member.Name, data.ShaderName))
            .Where(name => !string.Equals(name, "seed", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var supplied = op.Parameters
            .Select(parameter => parameter.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, supplied);
    }

    /// <summary>Each kernel declares its inputs in the order the evaluator binds an op's images over them.</summary>
    /// <remarks>
    ///     ⚠ <b><c>NormalCombine</c> is the one where the order is the whole node.</b> Reorienting is
    ///     not commutative: the base supplies the frame and the detail is rotated into it, so a kernel
    ///     that declared them the other way round would apply the detail's frame to the base and
    ///     produce a perfectly plausible normal map of a different surface.
    /// </remarks>
    [Theory]
    [InlineData("AmbientOcclusion", "source")]
    [InlineData("Curvature", "source")]
    [InlineData("HeightToNormal", "source")]
    [InlineData("NormalCombine", "baseMap", "detailMap")]
    [InlineData("NormalTransform", "source")]
    public void A_surface_kernel_declares_its_inputs_in_binding_order(string kernel, params string[] inputs) {
        var data = Compile(kernel);

        var textures = data.Bindings
            .Where(binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture })
            .OrderBy(binding => binding.Binding)
            .Select(binding => binding.Name)
            .ToArray();

        Assert.Equal(inputs, textures);
    }

    /// <summary>No surface kernel imports, because none of them can.</summary>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void A_surface_kernel_imports_nothing(string kernel) =>
        Assert.DoesNotContain(
            TextureKernels.Source(kernel).Split('\n'),
            line => line.TrimStart().StartsWith("import", StringComparison.Ordinal)
        );

    /// <summary>Every length in this slice is a length in texels at the plan's base resolution.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 48 § D8, and the three that are lengths are exactly the three that are radii.</b>
    ///     An intensity, an opacity, a rotation and a sample count are not lengths and carrying the
    ///     unit on one of them would scale a number that has no business changing with the resolution.
    ///     ⚠ <c>AmbientOcclusion</c>'s <c>height</c> is the interesting one: it is a *height* and it is
    ///     still not a texel count, because it is a fraction of the image's width — which is what
    ///     makes the same relief the same relief at 4K.
    /// </remarks>
    [Fact]
    public void Every_length_here_is_in_texels_at_the_base_resolution() {
        var scaled = TextureSurfaces.All
            .SelectMany(op => op.Parameters.Select(parameter => (op.Kernel, parameter.Name, parameter.Unit)))
            .Where(entry => entry.Unit == TextureParameterUnit.TexelsAtBase)
            .Select(entry => $"{entry.Kernel}.{entry.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["AmbientOcclusion.radius", "Curvature.radius", "HeightToNormal.width"],
            scaled
        );
    }

    /// <summary>
    ///     ⚠ The finding behind § 4.6's missing sixth kernel: a <see cref="TexturePlan" /> has no op
    ///     that is not a compute dispatch.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48 § 4.6 says <c>Normal → Height</c> "is the one entry here that is <em>not</em>
    ///         a compute kernel: it runs on the CPU, which is a deliberate exception to D3".</b> The
    ///         plan cannot express that. <see cref="TextureOp" /> carries a <c>Kernel</c>, images and
    ///         numbers, and <c>TexturePlanEvaluator.Run</c> puts every one of them through
    ///         <c>VariantFor</c> — which compiles the name as Raven and builds a compute pipeline. An
    ///         op whose <c>Kernel</c> names no <c>.rvn</c> is an <see cref="ArgumentException" />
    ///         naming an embedded resource, which is what this asserts.
    ///     </para>
    ///     <para>
    ///         So the node is owed a change to the plan — a second kind of op, or a solve that
    ///         precedes the plan and arrives as an external image — and not a kernel. Writing a GPU
    ///         Poisson instead would be a different node with a different convergence story, and doc
    ///         48 § 4.6 already named the solver it wants.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_plan_cannot_express_an_operation_that_is_not_a_dispatch() {
        var failure = Assert.Throws<ArgumentException>(
            () => TextureKernels.Variant("NormalToHeight", TextureFormat.Rgba16Float)
        );

        Assert.Contains("NormalToHeight", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalToHeight", TextureKernels.Names);
    }

    static string Unqualified(string name, string shader) =>
        name.Length > shader.Length + 1
        && name.StartsWith(shader, StringComparison.Ordinal)
        && name[shader.Length] == '.'
            ? name[(shader.Length + 1)..]
            : name;

    static EffectData Compile(string kernel) {
        var data = RavenEffectCompiler
            .FromSources([
                (TextureKernels.VariantName(kernel, TextureFormat.Rgba8),
                    TextureKernels.Variant(kernel, TextureFormat.Rgba8))
            ])
            .TryGet(EffectKey.Of(kernel));

        Assert.NotNull(data);

        return data;
    }
}
