// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.4's eleven filters, asserted with **no device**.</summary>
/// <remarks>
///     <para>
///         <b>What can be proved without a GPU turns out to be most of what goes wrong.</b> Every
///         kernel compiles in every format a plan can ask for; every parameter a builder emits is one
///         the kernel actually declares and the other way round; every length carries doc 48 § D8's
///         unit and nothing else does; and a plan whose radius runs off the end of a kernel's loop is
///         reported rather than silently clamped. None of that needs a picture, and none of it skips
///         on a machine with no adapter — which is where <c>TextureFilterDeviceTests</c> goes quiet.
///     </para>
///     <para>
///         ⚠ <b>Ask what this file prints on the day it does not run.</b> Every theory here is driven
///         off <see cref="TextureFilters.All" />; if that list were emptied, each would have no cases
///         and the suite would pass having asserted nothing.
///         <see cref="Every_filter_this_slice_declares_is_embedded" /> is the guard, and it runs both
///         directions.
///     </para>
/// </remarks>
public class TextureFilterKernelTests {
    /// <summary>Which of this slice's parameters is a length, and therefore doc 48 § D8's business.</summary>
    /// <remarks>
    ///     ⚠ <b>Written out rather than derived, because both mistakes it catches are silent.</b> A
    ///     length that forgot <see cref="TextureParameterUnit.TexelsAtBase" /> is half as wide at 4K
    ///     — § D8's bug with the long fuse. A ratio that <em>acquired</em> it is the mirror image: a
    ///     sharpen's <c>amount</c> multiplied by four at a 4× bake, which is not a wider filter but a
    ///     different one. Deriving the set from the code would assert that the code agrees with
    ///     itself.
    /// </remarks>
    static readonly ImmutableHashSet<string> Lengths = [
        "sigma", "length", "maxRadius", "radius", "intensity"
    ];

    /// <summary>Every parameter of this slice that is a plain number despite reading like a length.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Emboss</c>'s <c>intensity</c> is the exception and it is the interesting one.</b>
    ///     It multiplies a slope taken <em>per unit of image width</em> rather than per texel, which
    ///     is what makes an emboss the same picture at 1K and 4K without any scaling — so scaling it
    ///     as well would be applying § D8's correction twice. The three warps' <c>intensity</c> is a
    ///     genuine displacement in texels and is not on this list.
    /// </remarks>
    static readonly ImmutableHashSet<string> NotLengths = ["Emboss.intensity"];

    public static TheoryData<string> Kernels => [.. TextureFilters.All];

    /// <summary>Every op this slice can build, so a theory over them cannot forget one.</summary>
    /// <remarks>
    ///     ⚠ <b>A theory with an <c>InlineData</c> per builder passes silently when a twelfth builder
    ///     is added and not listed.</b> This is the list the parameter-agreement test walks, and a
    ///     builder reaches it by existing rather than by being remembered — the same move
    ///     <c>TextureSources.All</c> makes for § 4.1.
    /// </remarks>
    public static ImmutableArray<TextureOp> Builders { get; } = [
        TextureFilters.BlurHqOp(1, 0, 2f),
        TextureFilters.DirectionalBlurOp(1, 0, 0.5f, 4f),
        TextureFilters.RadialBlurOp(1, 0, 0.2f),
        TextureFilters.NonUniformBlurOp(1, 0, 2, 4f),
        TextureFilters.SharpenOp(1, 0, 1f),
        TextureFilters.EmbossOp(1, 0, 0f, 0.5f, 0.1f),
        TextureFilters.WarpOp(1, 0, 2, 4f),
        TextureFilters.DirectionalWarpOp(1, 0, 2, 0f, 4f),
        TextureFilters.VectorWarpOp(1, 0, 2, 4f),
        TextureFilters.SlopeBlurOp(1, 0, 2, 4f)
    ];

    public static TheoryData<int> BuilderIndices {
        get {
            TheoryData<int> data = [];

            for (var index = 0; index < Builders.Length; index++) {
                data.Add(index);
            }

            return data;
        }
    }

    /// <summary>The declared set is the embedded set, both ways.</summary>
    /// <remarks>
    ///     A name in <see cref="TextureFilters" /> with no <c>.rvn</c> behind it is a plan that fails
    ///     at evaluation with a message about an embedded resource; an <c>.rvn</c> nobody registered
    ///     is this catalogue's commonest defect — a finished thing nothing calls. The second half is
    ///     restricted to the eleven § 4.4 names, because the folder is shared with three other slices
    ///     and <c>TextureColourKernelTests</c> is where the union is taken.
    /// </remarks>
    [Fact]
    public void Every_filter_this_slice_declares_is_embedded() {
        Assert.Equal(11, TextureFilters.All.Count);

        foreach (var kernel in TextureFilters.All) {
            Assert.Contains(kernel, TextureKernels.Names);
        }

        Assert.Equal(TextureFilters.All.Count, TextureFilters.All.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    ///     Each kernel declares its inputs in the order the evaluator binds an op's images over them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nothing in the C# would notice a kernel that declared its two textures the other way
    ///     round.</b> <c>BindingPlan</c> puts the uniform block at binding 0 and the textures in
    ///     declaration order, and <c>TexturePlanEvaluator.Bind</c> walks an op's inputs positionally
    ///     over them — so a <c>NonUniformBlur</c> that declared its radius map first would blur the
    ///     radius map by the picture and produce something entirely plausible. This is where the
    ///     order is written down, and the builders above are what have to agree with it.
    /// </remarks>
    [Theory]
    [InlineData("BlurHq", "source")]
    [InlineData("DirectionalBlur", "source")]
    [InlineData("DirectionalWarp", "source", "warp")]
    [InlineData("Emboss", "source")]
    [InlineData("NonUniformBlur", "source", "radiusMap")]
    [InlineData("RadialBlur", "source")]
    [InlineData("Sharpen", "source")]
    [InlineData("SlopeBlur", "source", "slope")]
    [InlineData("VectorWarp", "source", "vectors")]
    [InlineData("Warp", "source", "warp")]
    public void A_filter_declares_its_inputs_in_binding_order(string kernel, params string[] inputs) {
        var data = Compile(kernel);

        var textures = data.Bindings
            .Where(binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture })
            .OrderBy(binding => binding.Binding)
            .Select(binding => binding.Name)
            .ToArray();

        Assert.Equal(inputs, textures);
    }

    /// <summary>No filter asks for a sampler, because the evaluator cannot bind one.</summary>
    /// <remarks>
    ///     ⚠ <b><c>TexturePlanEvaluator.Bind</c> handles a uniform block, sampled textures and one
    ///     storage image, and throws on anything else.</b> Every bilinear tap in this slice is
    ///     therefore four <c>Load</c>s and three <c>lerp</c>s, written out per kernel — which is also
    ///     why a warp cannot ask for a mip and why the blurs are separable rather than mip-assisted.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void No_filter_asks_for_a_sampler(string kernel) {
        var data = Compile(kernel);

        Assert.DoesNotContain(
            data.Bindings,
            binding => binding.Set == DescriptorSetSlot.PerMaterial && binding.Kind == DescriptorKind.Sampler
        );

        Assert.Single(
            data.Bindings,
            binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.StorageTexture }
        );
    }

    /// <summary>
    ///     ⚠ Every builder emits exactly the parameters its kernel declares — no more, and none
    ///     missing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the guard against the defect batch 2's review found: a declared parameter
    ///         with nothing that would notice its absence.</b> One half is enforced at run time —
    ///         <c>TexturePlanEvaluator.Uniforms</c> refuses an op that leaves a member out, because
    ///         zero is a valid-looking number for almost every one of them. The other half is not
    ///         enforced anywhere: a builder emitting <c>ammount</c> for <c>amount</c> would hand the
    ///         kernel one parameter it wanted and one it had never heard of, and the evaluator would
    ///         raise about the missing one at bake time and never about the spurious one.
    ///     </para>
    ///     <para>
    ///         Reading the members out of the compiled reflection is what makes it a claim about the
    ///         kernel rather than about a second list of its parameters.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(BuilderIndices))]
    public void A_builder_emits_exactly_the_parameters_its_kernel_declares(int index) {
        var op = Builders[index];
        var data = Compile(op.Kernel);

        var declared = data.Parameters
            .Where(member => member.Set == DescriptorSetSlot.PerMaterial)
            .Select(member => member.Name.Split('.')[^1])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var emitted = op.Parameters
            .Select(parameter => parameter.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, emitted);
    }

    /// <summary>
    ///     ⚠ Every length carries doc 48 § D8's unit, and nothing that is not a length carries it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both directions, because both failures are silent and they are mirror images.</b> A
    ///         radius left as a <see cref="TextureParameterUnit.Scalar" /> is half as wide at 4K —
    ///         the same graph, a different material, and nobody associates the change with the
    ///         resolution field. A ratio that acquired <c>TexelsAtBase</c> is multiplied by four at
    ///         the same bake, which turns a sharpen of amount 1 into a sharpen of amount 4.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Emboss</c>'s <c>intensity</c> is the deliberate exception</b>, and it is on
    ///         <see cref="NotLengths" /> with the reason: the slope it multiplies is already per unit
    ///         of image width, so scaling it would apply § D8's correction a second time.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(BuilderIndices))]
    public void A_length_carries_the_texels_at_base_unit_and_only_a_length_does(int index) {
        var op = Builders[index];

        foreach (var parameter in op.Parameters) {
            var isLength = Lengths.Contains(parameter.Name) && !NotLengths.Contains($"{op.Kernel}.{parameter.Name}");

            Assert.Equal(
                isLength ? TextureParameterUnit.TexelsAtBase : TextureParameterUnit.Scalar,
                parameter.Unit
            );
        }
    }

    /// <summary>The slope-blur mode numbering in the kernel is the one the enum spells.</summary>
    /// <remarks>
    ///     ⚠ <b>The kernel cannot raise, so a mode it does not recognise falls through to the
    ///     blend.</b> A min where a max was meant is an erosion where a dilation was meant, which is
    ///     a picture — so the two tables are pinned against each other here and each mode's actual
    ///     behaviour is pinned on a device.
    /// </remarks>
    [Fact]
    public void The_slope_blur_mode_numbering_is_the_kernels() {
        var source = TextureKernels.Source("SlopeBlur");

        Assert.Equal(0, (int)TextureSlopeMode.Blend);
        Assert.Equal(1, (int)TextureSlopeMode.Min);
        Assert.Equal(2, (int)TextureSlopeMode.Max);

        Assert.Matches(new Regex(@"if \(mode == 1\)\s*\{\s*target\.Store\(coord, lowest\)"), source);
        Assert.Matches(new Regex(@"if \(mode == 2\)\s*\{\s*target\.Store\(coord, highest\)"), source);
    }

    /// <summary>Every kernel's ceiling is the constant its <c>.rvn</c> actually loops to.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two tables, one of them in a shader.</b> <see cref="TextureFilters.Verify" /> is
    ///         only worth running if the numbers it checks against are the numbers the kernels clamp
    ///         with; a ceiling raised in one place and not the other would produce a walk that
    ///         reports a plan nothing clamps, or worse, stays quiet about one that is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Blur</c> is not listed, and this file must not list it.</b> Its constant is a
    ///         budget on taps rather than a ceiling on the width —
    ///         <see href="https://github.com/Rikarin/Vixen/issues/678">#678</see>'s answer, landed by
    ///         another slice of this batch — so nothing about its radius is clipped and there is
    ///         nothing here to agree with. An assertion naming its constant would also be a test in
    ///         one branch reading a kernel another branch owns, which is the cross-branch drift no
    ///         per-branch run can see.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("BlurHq", "MaxRadius", 64)]
    [InlineData("DirectionalBlur", "MaxSteps", 64)]
    [InlineData("NonUniformBlur", "MaxRadius", 12)]
    [InlineData("Sharpen", "MaxRadius", 8)]
    [InlineData("RadialBlur", "MaxSamples", 32)]
    [InlineData("SlopeBlur", "MaxSamples", 32)]
    public void A_kernels_ceiling_is_the_constant_it_loops_to(string kernel, string name, int value) {
        Assert.Matches(
            new Regex($@"const val {name}: int = {value}\b"),
            TextureKernels.Source(kernel)
        );
    }

    /// <summary>A plan whose radii fit the kernels' loops has nothing reported against it.</summary>
    /// <remarks>
    ///     ⚠ <b>Verify the instrument first: ask what <see cref="TextureFilters.Verify" /> says on the
    ///     day nothing is wrong.</b> A walk that reported every op, or that reported none because it
    ///     never matched a kernel name, would be indistinguishable from a working one in the test
    ///     below it — which only ever asks for a non-empty answer.
    /// </remarks>
    [Fact]
    public void A_plan_inside_every_ceiling_is_reported_clean() {
        var plan = Plan(0, TextureFilters.NonUniformBlurOp(2, 0, 1, 8f), TextureFilters.SharpenOp(3, 2, 1f, 4f));

        Assert.Empty(plan.Validate());
        Assert.Empty(TextureFilters.Verify(plan));
    }

    /// <summary>
    ///     ⚠ The same plan, baked four times larger, runs off two kernels' loops — and that is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/678">#678</a>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The number that matters is the resolved one, and nothing else in this assembly
    ///         computes it against a ceiling.</b> A non-uniform blur of 8 texels at a 1K base is 32
    ///         texels in a 4K bake, and the kernel loops to 12 — so what the artist gets is a
    ///         12-texel blur, silently, and the graph is a different material at the resolution they
    ///         shipped it at. <c>TexturePlan.Validate</c> cannot say so: it knows nothing about what
    ///         a kernel's loop bound is. The kernel cannot say so either: a shader has nowhere to
    ///         raise.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the plan is <em>valid</em>.</b> Both halves are asserted here, because a
    ///         reader who found only the second would reasonably assume <c>Validate</c> had it
    ///         covered.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_same_plan_baked_four_times_larger_runs_off_two_kernels_loops() {
        var plan = Plan(-2, TextureFilters.NonUniformBlurOp(2, 0, 1, 8f), TextureFilters.SharpenOp(3, 2, 1f, 4f));

        Assert.Empty(plan.Validate());

        var problems = TextureFilters.Verify(plan);

        Assert.Equal(2, problems.Length);
        Assert.Contains(problems, line => line.Contains("NonUniformBlur", StringComparison.Ordinal) && line.Contains("32", StringComparison.Ordinal));
        Assert.Contains(problems, line => line.Contains("Sharpen", StringComparison.Ordinal) && line.Contains("16", StringComparison.Ordinal));
    }

    /// <summary>A scalar parameter is never mistaken for a radius, whatever the bake.</summary>
    /// <remarks>
    ///     ⚠ <b>A <c>SlopeBlur</c> with 30 samples is not a radius of 30.</b> The walk asserts against
    ///     its own ceiling of 32, and an implementation of <see cref="TextureFilters.Verify" /> that
    ///     matched on parameter name alone would report it — or, at a 4× bake, would report it as 120.
    ///     Nothing else here would notice.
    /// </remarks>
    [Fact]
    public void A_count_is_not_a_radius_and_is_never_scaled() {
        var plan = Plan(-2, TextureFilters.SlopeBlurOp(2, 0, 1, 2f, 30), TextureFilters.SharpenOp(3, 2, 1f, 1f));

        Assert.Empty(TextureFilters.Verify(plan));
    }

    static TexturePlan Plan(int bake, params TextureOp[] ops) =>
        new() {
            BaseWidth = 1024,
            BaseHeight = 1024,
            BakeLevelOffset = bake,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.R8, External: true),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba8)
            ],
            Ops = [.. ops],
            Outputs = [3]
        };

    static EffectData Compile(string kernel) {
        var name = TextureKernels.VariantName(kernel, TextureFormat.Rgba8);
        var source = TextureKernels.Variant(kernel, TextureFormat.Rgba8);
        var data = RavenEffectCompiler.FromSources([(name, source)]).TryGet(EffectKey.Of(kernel));

        Assert.NotNull(data);

        return data;
    }
}
