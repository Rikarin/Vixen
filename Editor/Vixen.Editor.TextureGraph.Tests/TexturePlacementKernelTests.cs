// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § 4.7's two placement kernels and § 4.2's sixteen blend modes, asserted with
///     <b>no device</b>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every claim here is one a device suite cannot be trusted with, because a device suite
///         skips.</b> On a machine with no Vulkan loader <c>TexturePlacementDeviceTests</c> is green
///         having run nothing, so what lives here is what would otherwise rot in silence: the binding
///         order the evaluator binds positionally over, the agreement between each builder and its
///         kernel's uniform block, the two loop ceilings a host has to know because Raven's reflection
///         does not carry a <c>const val</c>, and the roll call of blend modes.
///     </para>
///     <para>
///         ⚠ <b>And ask what this file prints on the day one of its lists is emptied.</b> Every theory
///         driven off <see cref="TexturePlacement.All" /> would have no cases and the file would pass
///         having asserted nothing — which is why <see cref="Builders" /> is asserted non-empty and
///         why the folder-versus-registry comparison lives in <c>TextureColourKernelTests</c>, where
///         it walks every declaring surface in the assembly by reflection rather than by a list.
///     </para>
/// </remarks>
public class TexturePlacementKernelTests {
    /// <summary>Doc 48 § 4.7's two, by the names a <see cref="TextureOp" /> gives.</summary>
    public static TheoryData<string> Placement => ["TileSampler", "Splatter"];

    /// <summary>Every op this slice's builders emit, so a new builder is walked by existing.</summary>
    public static TheoryData<string> Builders {
        get {
            TheoryData<string> data = [];

            foreach (var op in TexturePlacement.All.Concat(TextureBlend.All)) {
                data.Add(op.Kernel);
            }

            return data;
        }
    }

    /// <summary>Each of the two is embedded under the name a <see cref="TextureOp" /> gives.</summary>
    [Theory]
    [MemberData(nameof(Placement))]
    public void A_placement_kernel_is_embedded_under_its_own_name(string kernel) =>
        Assert.Contains(kernel, TextureKernels.Names);

    /// <summary>A builder that emits no op would make every theory below vacuous.</summary>
    [Fact]
    public void Every_builder_in_this_slice_emits_an_op() {
        Assert.Equal(2, TexturePlacement.All.Length);
        Assert.NotEmpty(TextureBlend.All);
    }

    /// <summary>
    ///     Every parameter a kernel declares is one its builder supplies, and every parameter the
    ///     builder supplies is one the kernel declares.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both directions, because they fail differently.</b> A kernel member the op omits is an
    ///     <c>ArgumentException</c> at bake time, on a device, with a message about a uniform. An op
    ///     parameter the kernel does <em>not</em> declare is silently dropped — so renaming
    ///     <c>scale</c> in a <c>.rvn</c> and forgetting the builder leaves every instance drawn at its
    ///     default size and nothing anywhere says so. The second is the plausible picture, and only
    ///     this direction of the comparison catches it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Builders))]
    public void A_builder_supplies_exactly_the_parameters_its_kernel_declares(string kernel) {
        var op = Assert.Single(
            TexturePlacement.All.Concat(TextureBlend.All),
            candidate => candidate.Kernel == kernel
        );

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
    ///     A placement kernel declares its textures in the order the evaluator binds an op's inputs.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nothing in the C# would notice these being declared in another order.</b> The
    ///     evaluator binds an op's <c>Inputs</c> positionally over the sampled textures sorted by
    ///     binding number, and <c>BindingPlan</c> numbers them in declaration order — so a
    ///     <c>TileSampler</c> that declared its size map before its mask would cull instances by the
    ///     size map and shrink them by the mask, which is a perfectly plausible field of stamps.
    /// </remarks>
    [Theory]
    [InlineData("TileSampler", "pattern", "mask", "sizeMap", "rotationMap")]
    [InlineData("Splatter", "pattern", "mask", "sizeMap", "rotationMap", "placement")]
    public void A_placement_kernel_declares_its_inputs_in_the_order_the_evaluator_binds_them(
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
    ///     ⚠ Not one parameter of either kernel is a length in texels, so doc 48 § D8's scaling never
    ///     touches them.
    /// </summary>
    /// <remarks>
    ///     A grid is a count, a scale is a fraction of a cell or of the image, and a jitter is a
    ///     fraction of a turn or of itself — every one resolution-independent <em>by construction</em>
    ///     rather than by <c>TexturePlan.Resolve</c>'s arithmetic. That is the same claim § 4.1's
    ///     sources make, and it is worth an assertion for the same reason: the day somebody adds a
    ///     radius in texels to one of these, § D8's rule has to be applied to it and this test is what
    ///     says so.
    /// </remarks>
    [Fact]
    public void No_placement_parameter_is_a_length_in_texels() {
        foreach (var op in TexturePlacement.All) {
            foreach (var parameter in op.Parameters) {
                Assert.Equal(TextureParameterUnit.Scalar, parameter.Unit);
            }
        }
    }

    /// <summary>
    ///     The two loop ceilings the C# reasons about are the ones the kernels actually declare.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Raven's reflection carries a uniform and not a <c>const val</c></b>, so a host that has
    ///     to know a kernel's bound has to hold a second copy of it — the same trade
    ///     <c>TexturePlanEvaluator.GroupSize</c> makes with <c>[ComputeShader(8, 8, 1)]</c>. This is
    ///     what stops the two copies drifting: a <c>MaxSearch</c> raised in the <c>.rvn</c> and not
    ///     here would leave <c>TexturePlacement.TileSampler</c> refusing a scale the kernel handles,
    ///     and lowered in the <c>.rvn</c> it would let through a scale the kernel silently cuts off
    ///     along a cell boundary.
    /// </remarks>
    [Theory]
    [InlineData("TileSampler", "MaxSearch", TexturePlacement.MaxSearch)]
    [InlineData("Splatter", "MaxInstances", TexturePlacement.MaxInstances)]
    public void A_kernel_ceiling_is_the_number_the_builder_refuses_against(string kernel, string name, int expected) =>
        Assert.Contains(
            $"const val {name}: int = {expected}",
            TextureKernels.Source(kernel),
            StringComparison.Ordinal
        );

    /// <summary>
    ///     A tile sampler whose instance would reach past the search is refused where the op is built.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion that keeps the kernel's own <c>clamp</c> from being the
    ///     answer.</b> <a href="https://github.com/Rikarin/Vixen/issues/678">#678</a> is a kernel that
    ///     clamped a size to its own constant and was therefore right at the resolution it was tuned
    ///     at and quietly wrong at a large bake. The clamp stays — a loop bound is a correctness
    ///     property and a NaN in <c>scale</c> must not be a dispatch that never ends — but it is
    ///     unreachable through a plan built here, and the message names the number.
    /// </remarks>
    [Fact]
    public void A_tile_sampler_that_outreaches_the_search_is_refused_by_the_builder() {
        var failure = Assert.Throws<ArgumentException>(
            () => TexturePlacement.TileSampler(0, 1, scale: 6f)
        );

        Assert.Contains(TexturePlacement.MaxSearch.ToString(), failure.Message, StringComparison.Ordinal);

        // And the largest scale the search does cover is built without complaint, so the refusal is a
        // boundary and not a blanket.
        Assert.NotNull(TexturePlacement.TileSampler(0, 1, scale: 4f));
    }

    /// <summary>A splatter past the instance ceiling is refused rather than quietly truncated.</summary>
    /// <remarks>
    ///     A plan given 400 instances would draw 256 of them and say nothing — a picture right in
    ///     every respect but the count, which is the one parameter the artist was turning.
    /// </remarks>
    [Fact]
    public void A_splatter_past_the_instance_ceiling_is_refused_by_the_builder() {
        var failure = Assert.Throws<ArgumentException>(
            () => TexturePlacement.Splatter(0, 1, count: TexturePlacement.MaxInstances + 1)
        );

        Assert.Contains(TexturePlacement.MaxInstances.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.NotNull(TexturePlacement.Splatter(0, 1, count: TexturePlacement.MaxInstances));
    }

    /// <summary>The short overloads bind the pattern into every map slot, and read none of them.</summary>
    /// <remarks>
    ///     ⚠ <b>The evaluator refuses an op whose input count is not the kernel's</b>, so an optional
    ///     input is not a shape it has. Binding one view several times is free; what makes it harmless
    ///     is that the amounts are zero, and that is what is asserted here rather than assumed.
    /// </remarks>
    [Fact]
    public void The_short_overloads_bind_the_pattern_into_every_map_slot() {
        var tile = TexturePlacement.TileSampler(3, 7);

        Assert.Equal([7, 7, 7, 7], tile.Inputs);
        Assert.Equal(0f, tile.Find("sizeMapAmount")?.Value);
        Assert.Equal(0f, tile.Find("rotationMapAmount")?.Value);
        Assert.Equal(0f, tile.Find("maskThreshold")?.Value);

        var splatter = TexturePlacement.Splatter(3, 7);

        Assert.Equal([7, 7, 7, 7, 7], splatter.Inputs);
        Assert.Equal(0f, splatter.Find("placementAmount")?.Value);
        Assert.Equal(0f, splatter.Find("maskThreshold")?.Value);
    }

    /// <summary>Every mode <see cref="TextureBlendMode" /> names has a case in the kernel.</summary>
    /// <remarks>
    ///     ⚠ <b>A blend mode with a C# name and no case in the <c>.rvn</c> is the failure this
    ///     catalogue is most exposed to, and it is invisible.</b> <c>Combine</c> falls through to the
    ///     foreground, so an unimplemented mode is a `Copy` — a picture, with the layer on top of the
    ///     one below it, which is exactly what somebody looking at a blend expects to see. Doc 48
    ///     § 4.2 names sixteen; this counts them on both sides.
    /// </remarks>
    [Fact]
    public void Every_blend_mode_named_in_C_sharp_has_a_case_in_the_kernel() {
        var modes = Enum.GetValues<TextureBlendMode>();
        var source = TextureKernels.Source("Blend");

        Assert.Equal(16, modes.Length);

        foreach (var mode in modes) {
            if (mode == TextureBlendMode.Copy) {
                // Copy is the fall-through and has no `if` of its own — which is also why an
                // unimplemented mode looks like one.
                Assert.DoesNotContain($"mode == {(int)mode})", source, StringComparison.Ordinal);

                continue;
            }

            Assert.Contains($"mode == {(int)mode})", source, StringComparison.Ordinal);
        }

        // And no case for a number no name covers, which would be a mode reachable only by hand.
        Assert.Equal(15, Regex.Count(source, @"mode == \d+\)"));
    }

    /// <summary>Neither placement kernel imports, because neither can.</summary>
    /// <remarks>
    ///     <c>TextureSourceKernelTests.A_standalone_kernel_cannot_reach_the_shader_library</c> is the
    ///     tripwire that proves the <em>why</em>; this is the rule it guards, applied to the two files
    ///     that carry `Random`'s hash for that reason.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Placement))]
    public void A_placement_kernel_imports_nothing(string kernel) {
        var imports = TextureKernels
            .Source(kernel)
            .Split('\n')
            .Where(line => line.TrimStart().StartsWith("import", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(imports);
    }

    /// <summary>
    ///     ⚠ Stamping a small pattern over a large output is what these two are <em>for</em>, and it
    ///     raised a caution an artist could read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <a href="https://github.com/Rikarin/Vixen/issues/867">#867</a>. Both kernels take every
    ///         input through the <em>source's</em> own extent — <c>Stamp</c> divides by
    ///         <c>pattern.GetDimensions(0)</c>, <c>At</c> by the map's — so a 64² pattern under a 256²
    ///         output is the ordinary case and not the smeared corner #801's caution describes. #801
    ///         landed the flag, the guard and the reader that puts a caution under the layer stack's
    ///         pane, and marked eight ops; these two were not among them.
    ///     </para>
    ///     <para>
    ///         <b>The instrument first:</b> the same op with the declaration taken off is asserted to
    ///         caution, on the same plan and the same sizes. Without that half this test would pass on
    ///         a <c>Check</c> that had stopped looking at extents at all, which is the failure the
    ///         whole batch is about.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Placement))]
    public void A_placement_op_stamping_a_smaller_pattern_is_not_cautioned(string kernel) {
        var placed = kernel == "Splatter"
            ? TexturePlacement.Splatter(1, 0)
            : TexturePlacement.TileSampler(1, 0);

        TexturePlan plan = new() {
            BaseWidth = 256,
            BaseHeight = 256,

            // Level 2 is a quarter of the base on each axis, so the pattern is 64² and the output 256².
            Images = [new(TextureFormat.Rgba16Float, LevelOffset: 2), new(TextureFormat.Rgba8)],
            Ops = [new() { Kernel = "Uniform", Output = 0 }, placed],
            Outputs = [1]
        };

        Assert.Equal(64, plan.SizeOf(0).X);
        Assert.Equal(256, plan.SizeOf(1).X);
        Assert.True(placed.ReadsOtherExtents);
        Assert.Empty(plan.Check());

        // And the guard is awake: the identical plan whose op does not declare it is cautioned, once
        // for every input bound to the small pattern.
        TexturePlan undeclared = new() {
            BaseWidth = plan.BaseWidth,
            BaseHeight = plan.BaseHeight,
            Images = plan.Images,
            Ops = [plan.Ops[0], placed with { ReadsOtherExtents = false }],
            Outputs = plan.Outputs
        };

        Assert.All(
            undeclared.Check(),
            problem => Assert.Equal(TextureProblemSeverity.Warning, problem.Severity)
        );

        Assert.Equal(placed.Inputs.Length, undeclared.Check().Length);
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
