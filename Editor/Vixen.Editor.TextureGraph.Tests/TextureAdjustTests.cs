// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>The <c>Auto Levels</c> chain builder, with no device.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/713">#713</a>.</b> The chain's
///         <em>length</em> is a function of the resolution the plan is baked at, which is the property
///         <see cref="TextureOp.EmittedForExtent" /> exists for — and until this builder the chain was
///         written out at call sites, so nothing stamped it and a re-bake was silent. The picture a
///         short chain produces is stretched by the extremes of one block rather than of the image:
///         flatter, and never obviously wrong.
///     </para>
///     <para>
///         ⚠ <b>The assertions are closed forms and one of them is against the kernel's own
///         source.</b> A ladder built for a block the kernel does not loop over is the same defect one
///         level down — the reduction would read the first 8×8 of each block and find a corner's
///         extremes — so the number is read out of <c>MinMaxReduce.rvn</c> rather than restated here.
///     </para>
/// </remarks>
public class TextureAdjustTests {
    /// <summary>How many reduction dispatches an image of each size is.</summary>
    /// <remarks>
    ///     The ladder is the plan's own level arithmetic: an axis that has reached one texel stays
    ///     there. ⚠ 4096×64 is the case that separates it from <c>log8(longer side)</c> — four
    ///     dispatches, driven by the long axis, while the short one is a single texel after two.
    /// </remarks>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(8, 8, 1)]
    [InlineData(9, 9, 2)]
    [InlineData(64, 64, 2)]
    [InlineData(512, 512, 3)]
    [InlineData(1024, 1024, 4)]
    [InlineData(4096, 64, 4)]
    [InlineData(64, 4096, 4)]
    [InlineData(1000, 1000, 4)]
    public void The_reduction_is_one_dispatch_per_block_of_the_longer_axis(int width, int height, int expected) {
        Assert.Equal(expected, TextureAdjust.ReductionDispatches(width, height));
        Assert.Equal(expected, TextureAdjust.ReductionLevels(width, height).Length);
    }

    /// <summary>
    ///     ⚠ No rung of the ladder asks the kernel for a wider block than it loops over.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The property a fixed three-level step does not have, and the reason this builder
    ///         computes its step.</b> A level offset is a <em>floored</em> halving, so a rung's block
    ///         is <c>ceil(parent / (parent >> 3))</c>, which is 9 for a 9-texel axis and 15 for a
    ///         15-texel one. <c>MinMaxReduce.rvn</c> clamps its loop at <c>MaxBlock</c> and reads the
    ///         first eight, so the columns past that are never looked at and the "extremes of the
    ///         image" are the extremes of most of it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing downstream could notice.</b> Every dispatch runs, every image fills, the
    ///         pair is monotone and plausible, and the stretched picture is merely a little flat —
    ///         which is what the whole of #713 is about one level up.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(9, 9)]
    [InlineData(15, 15)]
    [InlineData(1000, 1000)]
    [InlineData(1920, 1080)]
    [InlineData(4095, 7)]
    [InlineData(2048, 2048)]
    public void No_rung_asks_for_a_wider_block_than_the_kernel_loops_over(int width, int height) {
        var parentWidth = width;
        var parentHeight = height;

        foreach (var level in TextureAdjust.ReductionLevels(width, height)) {
            var targetWidth = Math.Max(1, width >> level);
            var targetHeight = Math.Max(1, height >> level);

            var blockWidth = (parentWidth + targetWidth - 1) / targetWidth;
            var blockHeight = (parentHeight + targetHeight - 1) / targetHeight;

            Assert.True(
                blockWidth <= TextureAdjust.ReduceBlock && blockHeight <= TextureAdjust.ReduceBlock,
                $"{width}×{height}: the rung at level {level} reduces {parentWidth}×{parentHeight} to "
                + $"{targetWidth}×{targetHeight}, which is a {blockWidth}×{blockHeight} block against a "
                + $"kernel that loops over {TextureAdjust.ReduceBlock}."
            );

            parentWidth = targetWidth;
            parentHeight = targetHeight;
        }
    }

    /// <summary>The last scratch image of the ladder is one texel, which is what the map reads.</summary>
    /// <remarks>
    ///     ⚠ <b>The property the levels exist for.</b> <c>AutoLevels.rvn</c> loads <c>(0, 0)</c> of
    ///     its stats image and calls that the whole picture's extremes; a ladder whose last rung is
    ///     4×4 makes that sentence false while every dispatch still runs and every image still fills.
    /// </remarks>
    [Theory]
    [InlineData(64, 64)]
    [InlineData(1024, 1024)]
    [InlineData(4096, 64)]
    [InlineData(2048, 512)]
    public void The_last_rung_of_the_ladder_is_a_single_texel(int width, int height) {
        var levels = TextureAdjust.ReductionLevels(width, height);
        var last = levels[^1];

        Assert.Equal(1, Math.Max(1, width >> last));
        Assert.Equal(1, Math.Max(1, height >> last));

        // And the rung before it is not, or the chain is one dispatch longer than it needs to be.
        if (levels.Length > 1) {
            var previous = levels[^2];

            Assert.True(Math.Max(Math.Max(1, width >> previous), Math.Max(1, height >> previous)) > 1);
        }
    }

    /// <summary>⚠ The block the ladder is built for is the block the kernel loops over.</summary>
    /// <remarks>
    ///     <b>Read out of the source rather than restated</b>, because two numbers that have to agree
    ///     and are written in two files are the defect this whole batch keeps finding. A ladder for a
    ///     16-wide block over a kernel that loops 8 would reduce 4096 in three dispatches and take
    ///     the extremes of the first 8×8 of every 16×16 block — a plausible pair, from a quarter of
    ///     the image.
    /// </remarks>
    [Fact]
    public void The_ladder_and_the_kernel_agree_about_the_block() {
        var source = TextureKernels.Source("MinMaxReduce");
        var declared = Regex.Match(source, @"const val MaxBlock: int = (\d+)");

        Assert.True(declared.Success, "MinMaxReduce.rvn no longer declares MaxBlock as a constant.");
        Assert.Equal(TextureAdjust.ReduceBlock, int.Parse(declared.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>The chain is the reduction, in order, and then the map.</summary>
    [Fact]
    public void The_chain_reduces_into_itself_and_the_map_reads_the_last_rung() {
        var ops = TextureAdjust.AutoLevels(3, 0, [1, 2], 64, 64);

        Assert.Equal(3, ops.Length);

        Assert.Equal("MinMaxReduce", ops[0].Kernel);
        Assert.Equal([0], ops[0].Inputs);
        Assert.Equal(1, ops[0].Output);
        Assert.Equal(1f, ops[0].Find("first")!.Value.Value);

        Assert.Equal("MinMaxReduce", ops[1].Kernel);
        Assert.Equal([1], ops[1].Inputs);
        Assert.Equal(2, ops[1].Output);

        // ⚠ Zero rather than one, and it is not a flag about the plan: a tap at this level is
        // already a (min, max) pair, and reading it as a grey loses the maximum and blackens the
        // picture.
        Assert.Equal(0f, ops[1].Find("first")!.Value.Value);

        Assert.Equal("AutoLevels", ops[2].Kernel);
        Assert.Equal([0, 2], ops[2].Inputs);
        Assert.Equal(3, ops[2].Output);
        Assert.Empty(ops[2].Parameters);
    }

    /// <summary>A scratch list of the wrong length is refused, with both numbers in the message.</summary>
    [Fact]
    public void A_ladder_of_the_wrong_length_is_refused() {
        var refusal = Assert.Throws<ArgumentException>(() => TextureAdjust.AutoLevels(2, 0, [1], 1024, 1024));

        Assert.Contains("4 reduction dispatch", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("1 scratch image", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The plan the builder writes is sound at the bake it was emitted for.</summary>
    [Fact]
    public void The_chain_passes_the_plans_own_check() {
        Assert.Empty(Stretch(0).Check());
    }

    /// <summary>
    ///     ⚠ The same op list under a different <c>BakeLevelOffset</c> is refused rather than run.
    /// </summary>
    /// <remarks>
    ///     <b>The whole of #713.</b> Re-baking is expressed by copying <see cref="TexturePlan.Ops" />
    ///     into a plan with another <see cref="TexturePlan.BakeLevelOffset" />, because the plan is a
    ///     class and the field is <c>init</c>-only. A 64² chain is two dispatches; the same list at
    ///     four times the resolution needs three, and the two it has stop the reduction at 4×4. Every
    ///     image still fills and every dispatch still runs, so the only thing that can say so is the
    ///     stamp: deleting <see cref="TextureOp.EmittedForExtent" /> from the builder turns this
    ///     green.
    /// </remarks>
    [Fact]
    public void The_same_chain_at_another_bake_is_refused() {
        var refusals = Stretch(-2).Check();

        Assert.NotEmpty(refusals);

        Assert.Contains(
            refusals,
            problem => problem.Severity == TextureProblemSeverity.Error
                && problem.Message.Contains("emitted for an image", StringComparison.Ordinal)
        );
    }

    /// <summary>A 64² auto-levels plan, at whatever bake the caller asks for.</summary>
    /// <remarks>
    ///     ⚠ The images come from <see cref="TextureAdjust.ReductionLevels" /> and the ops from
    ///     <see cref="TextureAdjust.AutoLevels" />, so the ladder in the table and the ladder in the
    ///     op list cannot be two different ladders. That is what a call site writing both out by hand
    ///     could not promise.
    /// </remarks>
    static TexturePlan Stretch(int bake) {
        const int Side = 64;

        var levels = TextureAdjust.ReductionLevels(Side, Side);
        var images = ImmutableArray.CreateBuilder<TextureImage>();

        images.Add(new(TextureFormat.Rgba8, External: true));

        foreach (var level in levels) {
            images.Add(new(TextureFormat.Rgba16Float, level));
        }

        images.Add(new(TextureFormat.Rgba8));

        var scratch = ImmutableArray.CreateRange(Enumerable.Range(1, levels.Length));

        return new() {
            BaseWidth = Side,
            BaseHeight = Side,
            BakeLevelOffset = bake,
            Images = images.ToImmutable(),
            Ops = TextureAdjust.AutoLevels(levels.Length + 1, 0, scratch, Side, Side),
            Outputs = [levels.Length + 1]
        };
    }
}
