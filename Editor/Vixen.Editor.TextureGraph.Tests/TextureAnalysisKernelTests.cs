// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § 4.5's analysis kernels and the two op chains they are, asserted with <b>no device</b>.
/// </summary>
/// <remarks>
///     <para>
///         <c>TextureKernelTests</c> already compiles every embedded kernel in every storable format,
///         so one of these that does not build is red there by existing. What is asserted here is what
///         that file's theories cannot reach: the agreement between each kernel's uniform block and
///         the builder that fills it, the binding order the evaluator relies on positionally, the
///         arithmetic of the chain lengths, and the two refusals that stand in for a clamp.
///     </para>
///     <para>
///         ⚠ <b>The chain-length arithmetic is the claim most worth pinning here, because it is the
///         one a device test would confirm for the wrong reason.</b> A jump flood that ran one
///         dispatch too few still produces a distance field — one that is wrong by whatever the last
///         halving would have fixed, which on a small test image is nothing at all. The number is
///         asserted directly.
///     </para>
/// </remarks>
public class TextureAnalysisKernelTests {
    public static TheoryData<string> Kernels => [.. TextureAnalysisKernels.All];

    /// <summary>Every name this slice registers is a kernel the folder actually holds.</summary>
    /// <remarks>
    ///     The other direction — an <c>.rvn</c> nobody registered — is
    ///     <c>TextureColourKernelTests.The_folder_holds_these_kernels_and_no_others</c>, which walks
    ///     every declaring surface in the assembly by reflection and so covers this slice without an
    ///     edit.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void An_analysis_kernel_is_embedded_under_its_own_name(string kernel) =>
        Assert.Contains(kernel, TextureKernels.Names);

    /// <summary>
    ///     Every parameter an analysis kernel declares is one its builder supplies, and every
    ///     parameter the builder supplies is one the kernel declares.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both directions, because they fail differently.</b> A kernel member the op omits is an
    ///     exception at bake time, with a message about a uniform rather than about the builder; an op
    ///     parameter the kernel does not declare is silently dropped and the picture is drawn with a
    ///     default. The second is the one that produces a plausible picture.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void A_builder_supplies_exactly_the_parameters_its_kernel_declares(string kernel) {
        var op = TextureAnalysis.All.First(candidate => candidate.Kernel == kernel);
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

    /// <summary>Each kernel declares its inputs in the order the evaluator binds an op's images over them.</summary>
    /// <remarks>
    ///     ⚠ <b><c>FloodResidual</c> is the one that would produce a picture either way</b>: it reads
    ///     the record before an iteration and the record after it, and comparing them the other way
    ///     round is the same comparison. What the order protects is the *next* reader of this
    ///     convention, and it costs one line to write down.
    /// </remarks>
    [Theory]
    [InlineData("Distance", "flood")]
    [InlineData("EdgeDetect", "source")]
    [InlineData("FloodBounds", "source")]
    [InlineData("FloodFill", "bounds")]
    [InlineData("FloodResidual", "previous", "current")]
    [InlineData("JumpFlood", "source")]
    public void An_analysis_kernel_declares_its_inputs_in_binding_order(string kernel, params string[] inputs) {
        var data = Compile(kernel);

        var textures = data.Bindings
            .Where(binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture })
            .OrderBy(binding => binding.Binding)
            .Select(binding => binding.Name)
            .ToArray();

        Assert.Equal(inputs, textures);
    }

    /// <summary>No analysis kernel imports, because none of them can.</summary>
    /// <remarks>
    ///     The rule <c>TextureSourceKernelTests.A_standalone_kernel_cannot_reach_the_shader_library</c>
    ///     guards. ⚠ <c>FloodFill</c> is the one that wants to: its random value is
    ///     <c>Raven/Library/Core/Random.rvn</c>'s hash, transcribed for the same reported reason
    ///     <c>Noise</c> carries a copy of it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void An_analysis_kernel_imports_nothing(string kernel) =>
        Assert.DoesNotContain(
            TextureKernels.Source(kernel).Split('\n'),
            line => line.TrimStart().StartsWith("import", StringComparison.Ordinal)
        );

    /// <summary>A jump flood is one dispatch per halving of the image's longer side, and no more.</summary>
    /// <remarks>
    ///     ⚠ <b>The seeding pass is also the widest jump</b>, which is what makes this <c>log2(n)</c>
    ///     and not <c>log2(n) + 1</c>. A non-power-of-two extent rounds up, because the first jump has
    ///     to be able to reach across the whole image.
    /// </remarks>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(64, 64, 6)]
    [InlineData(64, 16, 6)]
    [InlineData(33, 4, 6)]
    [InlineData(2048, 2048, 11)]
    [InlineData(4096, 4096, 12)]
    public void A_jump_flood_is_one_dispatch_per_halving(int width, int height, int expected) =>
        Assert.Equal(expected, TextureAnalysis.FloodDispatches(width, height));

    /// <summary>The chain the builder emits is the chain the arithmetic promised, in order.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The step sequence is what no device test in this assembly can see going wrong, and
    ///         that was measured rather than assumed.</b> Emitting the steps <em>ascending</em> — 1, 2,
    ///         4, … 32 — was tried against the whole device suite and left every one of its assertions
    ///         green, including the exact single-seed distance field.
    ///     </para>
    ///     <para>
    ///         <b>And it is green for a real reason, not by luck.</b> A hop at step <c>2^k</c> moves an
    ///         offset by <c>±2^k</c> or nothing, independently in each axis, so with <em>one</em> seed
    ///         both orders can reach every offset in the image — the binary expansion of the distance
    ///         is the path. The orders part only where <em>several</em> seeds compete for a texel,
    ///         which is what a jump flood's descending sequence is actually for and which no closed
    ///         form in this repository pins. So this assertion is the instrument for it, and a device
    ///         test would not have been.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_distance_chain_halves_its_step_from_the_image_down_to_one() {
        var ops = TextureAnalysis.Distance(0, 1, [2, 3, 4, 5, 6, 7], 64, 64);

        Assert.Equal(7, ops.Length);

        var steps = ops
            .Where(op => op.Kernel == TextureAnalysisKernels.JumpFlood)
            .Select(op => op.Find("step")!.Value.Value)
            .ToArray();

        Assert.Equal([32f, 16f, 8f, 4f, 2f, 1f], steps);

        // Only the first dispatch reads the mask; every one after it reads the record before it.
        Assert.Equal(1f, ops[0].Find("first")!.Value.Value);
        Assert.Equal(1, ops[0].Inputs[0]);

        for (var pass = 1; pass < 6; pass++) {
            Assert.Equal(0f, ops[pass].Find("first")!.Value.Value);
            Assert.Equal(ops[pass - 1].Output, ops[pass].Inputs[0]);
        }

        Assert.Equal(TextureAnalysisKernels.Distance, ops[^1].Kernel);
        Assert.Equal(ops[^2].Output, ops[^1].Inputs[0]);
        Assert.Equal(0, ops[^1].Output);
    }

    /// <summary>A scratch list that is not the chain's length is refused where the plan is built.</summary>
    [Fact]
    public void A_distance_chain_refuses_a_scratch_list_that_does_not_match_it() {
        var failure = Assert.Throws<ArgumentException>(
            () => TextureAnalysis.Distance(0, 1, [2, 3], 64, 64)
        );

        Assert.Contains("6 jump-flood dispatches", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ A maximum distance past what a half-float names exactly is refused rather than quantised.
    /// </summary>
    /// <remarks>
    ///     <b>The finding, executably.</b> Doc 48 § D5's format list has no 32-bit float in it, and a
    ///     jump flood's record is a position. A half is exact on the integers only to
    ///     <see cref="TextureAnalysis.ExactExtent" />, so a field measured further than that would
    ///     come back quantised to even texels — a distance that is wrong by up to a texel everywhere,
    ///     with nothing anywhere saying so. The refusal names both numbers, which is what a clamp
    ///     inside the kernel could not have done.
    /// </remarks>
    [Fact]
    public void A_distance_further_than_a_half_float_names_exactly_is_refused() {
        // 4096 texels of a 4096 image, which is exactly the case a 4K bake reaches.
        var failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => TextureAnalysis.Distance(0, 1, [.. Enumerable.Range(2, 12)], 4096, 4096, maxDistance: 1f)
        );

        Assert.Contains("2048", failure.Message, StringComparison.Ordinal);

        // And the same bake with a distance inside the ceiling is fine, so the refusal is about the
        // record and not about the resolution.
        Assert.Equal(13, TextureAnalysis.Distance(0, 1, [.. Enumerable.Range(2, 12)], 4096, 4096, maxDistance: 0.4f).Length);
    }

    /// <summary>An image whose coordinates a half-float cannot name exactly is refused too.</summary>
    /// <remarks>
    ///     ⚠ <b>The flood fill's ceiling is the image and not the distance</b>, because its record is
    ///     a pair of absolute coordinates rather than an offset. Two islands two texels apart would be
    ///     given the same identity, and "flood fill to random" would colour them alike — a picture
    ///     that looks like a mask with two touching islands in it.
    /// </remarks>
    [Fact]
    public void A_flood_fill_larger_than_a_half_float_names_exactly_is_refused() {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(
            () => TextureAnalysis.FloodFill(0, 1, [2, 3], 4096, 4096)
        );

        Assert.Contains("2048", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A flood fill is one propagation dispatch per iteration of its budget, then the read.</summary>
    [Fact]
    public void A_flood_chain_is_its_budget_and_then_a_read() {
        var ops = TextureAnalysis.FloodFill(0, 1, [2, 3, 4], 64, 64, TextureFloodOutput.Size);

        Assert.Equal(4, ops.Length);
        Assert.Equal(1f, ops[0].Find("first")!.Value.Value);
        Assert.Equal(0f, ops[1].Find("first")!.Value.Value);
        Assert.Equal(0f, ops[2].Find("first")!.Value.Value);
        Assert.Equal(TextureAnalysisKernels.FloodFill, ops[^1].Kernel);
        Assert.Equal((float)TextureFloodOutput.Size, ops[^1].Find("kind")!.Value.Value);
    }

    /// <summary>
    ///     ⚠ The residual op carries no parameters, because its kernel declares none — the second
    ///     kernel in the assembly with no uniform block.
    /// </summary>
    /// <remarks>
    ///     A descriptor set of textures alone, which <c>AutoLevels</c> is the only other user of. A
    ///     <c>Bind</c> that fell over on it would fail with a message about a descriptor rather than
    ///     about a node, so it is written down where nothing skips.
    /// </remarks>
    [Fact]
    public void The_residual_kernel_takes_no_parameters() {
        var data = Compile(TextureAnalysisKernels.FloodResidual);

        Assert.DoesNotContain(data.Parameters, member => member.Set == DescriptorSetSlot.PerMaterial);
        Assert.Empty(TextureAnalysis.Residual(0, 1, 2).Parameters);
    }

    /// <summary>
    ///     ⚠ A jump flood's <c>step</c> and <c>maxDistance</c> are deliberately <em>not</em>
    ///     <c>TexelsAtBase</c>, and every other length in the slice is.
    /// </summary>
    /// <remarks>
    ///     <b>The one place doc 48 § D8's rule is departed from, so it is asserted rather than
    ///     assumed.</b> A jump flood's op count is already <c>log2</c> of the <em>baked</em> extent, so
    ///     its chain is emitted per bake and its numbers are the baked image's; scaling them again
    ///     through <c>TexturePlan.Resolve</c> would apply the bake's factor twice. Everything else —
    ///     an edge detect's width — is a length in texels at the authoring resolution and is scaled
    ///     exactly once.
    /// </remarks>
    [Fact]
    public void Only_the_lengths_that_are_authored_carry_the_texels_at_base_unit() {
        var scaled = TextureAnalysis.All
            .SelectMany(op => op.Parameters.Select(parameter => (op.Kernel, parameter.Name, parameter.Unit)))
            .Where(entry => entry.Unit == TextureParameterUnit.TexelsAtBase)
            .Select(entry => $"{entry.Kernel}.{entry.Name}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["EdgeDetect.width"], scaled);
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
                (TextureKernels.VariantName(kernel, TextureFormat.Rgba16Float),
                    TextureKernels.Variant(kernel, TextureFormat.Rgba16Float))
            ])
            .TryGet(EffectKey.Of(kernel));

        Assert.NotNull(data);

        return data;
    }
}
