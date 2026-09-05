// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Core.Curves;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The § 4.2 and § 4.3 kernels' structural claims and the tables they read, asserted with
///     <b>no device</b>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This file carries the claims a device suite cannot be trusted with, because a device
///         suite skips.</b> On a machine with no Vulkan loader every assertion in
///         <c>TextureColourDeviceTests</c> and <c>TextureSpaceDeviceTests</c> is skipped and the run
///         is green — so the two things that would silently rot, the kernel list and the binding
///         order the evaluator binds positionally over, are written down here where nothing skips.
///     </para>
///     <para>
///         ⚠ <b>And the list itself has an instrument problem worth naming.</b> If
///         <c>TextureColourKernels.All</c> were emptied, every theory driven off it would have no
///         cases and this file would pass having asserted nothing.
///         <see cref="The_folder_holds_these_kernels_and_no_others" /> is the guard: it compares the
///         registered set against what is actually embedded, in both directions.
///     </para>
/// </remarks>
public class TextureColourKernelTests {
    /// <summary>The three kernels doc 48 § M1 shipped, which this slice did not add and does not own.</summary>
    static readonly string[] Existing = ["Blend", "Blur", "Levels"];

    public static TheoryData<string> Kernels => [.. TextureColourKernels.All];

    /// <summary>
    ///     Every kernel this slice registers is embedded, and every kernel embedded is registered or
    ///     is one of the three that came before it.
    /// </summary>
    /// <remarks>
    ///     Both directions, because each catches a different mistake: a name in
    ///     <c>TextureColourKernels</c> with no <c>.rvn</c> behind it is a plan that fails at
    ///     evaluation with a message about an embedded resource, and an <c>.rvn</c> nobody registered
    ///     is § 4.2's commonest defect — a finished thing nothing calls.
    ///     <para>
    ///         ⚠ <b>The registered side is the union of every declaring surface, and it has to be.</b>
    ///         Written against this slice's own list plus the three that came before it, this
    ///         assertion was green on its branch and red the moment § 4.1's six source kernels landed
    ///         in the same tree — the folder is shared and the declarations are not. That is
    ///         cross-branch drift no per-branch test run can see, which is why the union is spelled
    ///         out here rather than left as a literal: a slice that adds a seventh surface has to
    ///         appear in this line, and the failure that follows says exactly which one is missing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_folder_holds_these_kernels_and_no_others() {
        Assert.NotEmpty(TextureColourKernels.All);

        foreach (var kernel in TextureColourKernels.All) {
            Assert.Contains(kernel, TextureKernels.Names);
        }

        var registered = TextureColourKernels.All
            .Concat(TextureSources.All.Select(op => op.Kernel))
            .Concat(Existing)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(registered, TextureKernels.Names.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    ///     Each kernel declares its inputs in the order the evaluator binds an op's images over them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nothing in the C# would notice a kernel that declared its two textures the other way
    ///     round.</b> <c>BindingPlan</c> puts the uniform block at binding 0 and the textures in
    ///     declaration order, and <c>TexturePlanEvaluator.Bind</c> walks an op's inputs positionally
    ///     over them — so a <c>Curve</c> that declared its table before its image would read the
    ///     image as a table and the table as an image, and produce a picture. Which one is a picture
    ///     is exactly the question this file exists to close.
    /// </remarks>
    [Theory]
    [InlineData("AutoLevels", "source", "stats")]
    [InlineData("ChannelShuffle", "first", "second")]
    [InlineData("Crop", "source")]
    [InlineData("Curve", "source", "curve")]
    [InlineData("GradientMap", "source", "ramp")]
    [InlineData("Grayscale", "source")]
    [InlineData("Hsl", "source")]
    [InlineData("Invert", "source")]
    [InlineData("MinMaxReduce", "source")]
    [InlineData("Mirror", "source")]
    [InlineData("Resample", "source")]
    [InlineData("Tile", "source")]
    [InlineData("Transform2D", "source")]
    public void A_kernel_declares_its_inputs_in_binding_order(string kernel, params string[] inputs) {
        var data = Compile(kernel);

        var textures = data.Bindings
            .Where(binding => binding is { Set: DescriptorSetSlot.PerMaterial, Kind: DescriptorKind.SampledTexture })
            .OrderBy(binding => binding.Binding)
            .Select(binding => binding.Name)
            .ToArray();

        Assert.Equal(inputs, textures);
    }

    /// <summary>
    ///     No kernel here declares a sampler, which is a constraint of the evaluator rather than a
    ///     style.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>TexturePlanEvaluator.Bind</c> handles a uniform block, sampled textures and one
    ///     storage image, and throws on anything else.</b> A kernel that reached for
    ///     <c>SampleLevel</c> would need a <c>SamplerState</c>, which is a
    ///     <c>DescriptorKind.Sampler</c> binding, and the failure would arrive at evaluation as a
    ///     message about a descriptor kind rather than as "this kernel cannot be run here". It is
    ///     also the reason `Transform2D` computes its own minification instead of asking for a mip.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void No_kernel_asks_for_a_sampler_the_evaluator_cannot_bind(string kernel) {
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
    ///     ⚠ <c>AutoLevels</c> declares no uniform block at all, which is a claim about the evaluator
    ///     and not only about the kernel.
    /// </summary>
    /// <remarks>
    ///     Doc 48 § 4.2 gives Auto Levels no parameters. Nothing before it exercised a descriptor set
    ///     of textures alone — every other kernel in the folder has at least one number — so if
    ///     <c>Bind</c> could not build one, the failure would surface as a Vulkan message about a
    ///     descriptor rather than as a node that cannot exist. It can; this is where that is written
    ///     down, and <c>TextureColourDeviceTests</c> is where it is run.
    /// </remarks>
    [Fact]
    public void Auto_levels_declares_no_uniform_block() {
        var data = Compile("AutoLevels");

        Assert.DoesNotContain(
            data.Bindings,
            binding => binding.Set == DescriptorSetSlot.PerMaterial
                && binding.Kind is DescriptorKind.UniformBuffer or DescriptorKind.DynamicUniformBuffer
        );
    }

    /// <summary>
    ///     The channel-selector numbering in <c>ChannelShuffle.rvn</c> is the one
    ///     <see cref="TextureChannelSource" /> spells.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The kernel cannot raise, so a selector it does not recognise falls through to the
    ///     first input's red — a plausible picture.</b> Two tables, one of them in a shader, is
    ///     exactly the arrangement that drifts; this reads the constants back out of the source.
    /// </remarks>
    [Fact]
    public void The_channel_selector_numbering_is_the_kernels() {
        var source = TextureKernels.Source("ChannelShuffle");

        Assert.Equal(8, (int)TextureChannelSource.Zero);
        Assert.Equal(9, (int)TextureChannelSource.One);
        Assert.Equal(4, (int)TextureChannelSource.SecondRed);

        Assert.Matches(new Regex(@"if \(selector == 8\)\s*\{\s*return 0f"), source);
        Assert.Matches(new Regex(@"if \(selector == 9\)\s*\{\s*return 1f"), source);
        Assert.Matches(new Regex(@"if \(selector >= 4\)"), source);
    }

    /// <summary>
    ///     ⚠ Not one parameter of these thirteen kernels is a length in texels, so § D8's scaling
    ///     never applies to any of them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A finding, written as a test so it cannot quietly stop being true.</b> Doc 48 § D8
    ///         and <c>TextureParameterUnit.TexelsAtBase</c> exist because a radius authored at 1K is
    ///         half as wide at 4K. Every parameter in § 4.2 and § 4.3 is instead a ratio, a fraction
    ///         of the image, a count or a turn — so these kernels are resolution-independent
    ///         <em>by construction</em> rather than by the evaluator's arithmetic, and
    ///         <a href="https://github.com/Rikarin/Vixen/issues/619">#619</a>'s rework of that
    ///         arithmetic cannot change what any of them does.
    ///     </para>
    ///     <para>
    ///         The test is the negative: no kernel here declares a member whose name reads like a
    ///         length. A kernel that grew one would be a kernel whose op has to carry
    ///         <c>TexelsAtBase</c>, and the person adding it should be told so here.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kernels))]
    public void No_kernel_here_takes_a_length_in_texels(string kernel) {
        var data = Compile(kernel);

        foreach (var member in data.Parameters) {
            var name = member.Name.Split('.')[^1];

            Assert.False(
                name is "radius" or "width" or "length" or "sigma" or "distance",
                $"'{kernel}' declares '{name}', which reads as a length in texels. Doc 48 § D8 makes every "
                + "length relative to the plan's base resolution, so its op has to carry "
                + "TextureParameterUnit.TexelsAtBase — and no other kernel in § 4.2 or § 4.3 does."
            );
        }
    }

    /// <summary>An identity spline bakes to a table whose entry <c>k</c> is <c>k</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion every <c>Curve</c> golden rests on, and it is about
    ///     <c>CurveEvaluation</c> rather than about a kernel.</b> If <see cref="TextureRamp.Straight" />
    ///     did not come back as a straight line through the editor's own evaluator — a wrong tangent
    ///     mode, a slope of zero — then "an identity curve is a copy" would be asserting that the
    ///     kernel reproduces whatever curve that happens to be, which is true of any kernel.
    /// </remarks>
    [Fact]
    public void A_straight_curve_bakes_to_an_identity_table() {
        var straight = TextureRamp.Straight();
        var table = TextureRamp.FromCurves(straight, straight, straight, straight);

        Assert.Equal(TextureRamp.Entries * 4, table.Length);

        for (var entry = 0; entry < TextureRamp.Entries; entry++) {
            for (var lane = 0; lane < 4; lane++) {
                Assert.Equal((byte)entry, table[(entry * 4) + lane]);
            }
        }
    }

    /// <summary>Each lane of a curve table is its own channel's spline and nothing else's.</summary>
    [Fact]
    public void A_curve_table_keeps_its_four_channels_apart() {
        var straight = TextureRamp.Straight();

        CurveSample[] flat = [
            new(0f, 0.25f, 0f, 0f, TangentMode.Linear),
            new(1f, 0.25f, 0f, 0f, TangentMode.Linear)
        ];

        var table = TextureRamp.FromCurves(flat, straight, straight, straight);

        for (var entry = 0; entry < TextureRamp.Entries; entry++) {
            Assert.Equal(64, table[entry * 4]);
            Assert.Equal((byte)entry, table[(entry * 4) + 1]);
        }
    }

    /// <summary>A ramp bakes on the same grid, so a table's ends are the gradient's ends.</summary>
    [Fact]
    public void A_ramp_bakes_its_two_ends_onto_the_first_and_last_entry() {
        var table = TextureRamp.FromRamp(
            position => new(position, 1f - position, 0.5f, 1f)
        );

        Assert.Equal(0, table[0]);
        Assert.Equal(255, table[1]);
        Assert.Equal(255, table[((TextureRamp.Entries - 1) * 4)]);
        Assert.Equal(0, table[((TextureRamp.Entries - 1) * 4) + 1]);

        // And the grid is linear in between, which is what the kernel's interpolation assumes.
        Assert.Equal(128, table[(128 * 4)]);
    }

    /// <summary>A null ramp is refused where it is passed rather than as a null reference per entry.</summary>
    [Fact]
    public void A_missing_ramp_is_refused() => Assert.Throws<ArgumentNullException>(() => TextureRamp.FromRamp(null!));

    static EffectData Compile(string kernel) {
        var name = TextureKernels.VariantName(kernel, TextureFormat.Rgba8);
        var source = TextureKernels.Variant(kernel, TextureFormat.Rgba8);
        var data = RavenEffectCompiler.FromSources([(name, source)]).TryGet(EffectKey.Of(kernel));

        Assert.NotNull(data);

        return data;
    }
}
