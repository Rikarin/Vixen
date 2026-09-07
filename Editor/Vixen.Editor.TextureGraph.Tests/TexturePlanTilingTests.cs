// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>Which of this assembly's chains a tiled evaluation would compute wrongly, and why.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/636">#636</a>, and doc 48 § 4.2 said
///         of <c>Auto Levels</c> that the plan runner has to know.</b> It did not. Every op in the
///         catalogue before that node had a bounded neighbourhood, so nothing had ever needed to ask,
///         and the three chains that are global carried the knowledge in prose beside their kernels.
///     </para>
///     <para>
///         ⚠ <b>Nothing evaluates in tiles today, and that is the argument for writing this now
///         rather than later.</b> The failure a tiled evaluator produces is a picture and not an
///         error — every tile stretched by its own extremes looks correct, and the seams between them
///         are a different contrast, which reads as a lighting problem in whatever consumes the map.
///         The classification can only be made by whoever wrote the chain, so a tiled evaluator
///         written after the fact has nothing to ask.
///     </para>
///     <para>
///         <b>The whole-image bake of every plan here is unaffected</b>, which is why this is separate
///         from <see cref="TexturePlan.Check" />: nothing below is wrong with the plan, and each of
///         these bakes correctly as long as it is baked whole.
///     </para>
/// </remarks>
public class TexturePlanTilingTests {
    /// <summary>An ordinary pointwise plan — one dispatch over one source.</summary>
    static TexturePlan Pointwise() =>
        new() {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
            Ops = [
                new() {
                    Kernel = "Invert",
                    Output = 1,
                    Inputs = [0],
                    Parameters = [new("invertR", 1f), new("invertG", 0f), new("invertB", 0f), new("invertA", 0f)]
                }
            ],
            Outputs = [1]
        };

    /// <summary>A plan whose one chain is doc 48 § 4.5's jump flood over a mask.</summary>
    static TexturePlan Distance() {
        var scratch = TextureAnalysis.FloodDispatches(64, 64);

        return new() {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.R16Float),
                .. Enumerable.Repeat(new TextureImage(TextureFormat.Rgba16Float), scratch)
            ],
            Ops = TextureAnalysis.Distance(1, 0, [.. Enumerable.Range(2, scratch)], 64, 64),
            Outputs = [1]
        };
    }

    /// <summary>A plan whose one chain is doc 48 § 4.2's Auto Levels over a filled image.</summary>
    static TexturePlan AutoLevels() {
        var levels = TextureAdjust.ReductionLevels(64, 64);
        var images = ImmutableArray.CreateBuilder<TextureImage>();
        var scratch = ImmutableArray.CreateBuilder<int>(levels.Length);

        images.Add(new(TextureFormat.Rgba16Float));

        foreach (var offset in levels) {
            scratch.Add(images.Count);
            images.Add(new(TextureFormat.Rgba16Float, LevelOffset: offset));
        }

        var written = images.Count;

        images.Add(new(TextureFormat.Rgba16Float));

        return new() {
            BaseWidth = 64,
            BaseHeight = 64,
            Images = images.ToImmutable(),
            Ops = [
                new() { Kernel = "Uniform", Output = 0 },
                .. TextureAdjust.AutoLevels(written, 0, scratch.ToImmutable(), 64, 64)
            ],
            Outputs = [written]
        };
    }

    /// <summary>An ordinary plan can be tiled, and says nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument, and it is not decoration.</b> A refusal list that named every op would
    ///     pass every assertion below it and mean nothing — "this plan cannot be tiled" is only worth
    ///     reading if some plan can.
    /// </remarks>
    [Fact]
    public void A_plan_of_bounded_ops_names_nothing() {
        Assert.Empty(Pointwise().TilingRefusals());
        Assert.Empty(Pointwise().Check());
    }

    /// <summary>⚠ Every dispatch of a jump flood is global, and the read at the end of it is not.</summary>
    /// <remarks>
    ///     A jump flood's first pass steps half the image at once, so a tile evaluated alone finds
    ///     only the seeds inside it and the field is discontinuous across every seam. The final
    ///     <c>Distance</c> op is a pointwise read of the settled record and could be tiled — which is
    ///     the distinction that makes this a property of the <em>op</em> rather than of the chain.
    /// </remarks>
    [Fact]
    public void A_jump_flood_names_its_propagation_and_not_its_read() {
        var plan = Distance();
        var refusals = plan.TilingRefusals();

        // The instrument: there are more ops than refusals, so "everything is refused" is not what
        // is being read.
        Assert.Equal(plan.Ops.Length - 1, refusals.Length);
        Assert.All(refusals, message => Assert.Contains("JumpFlood", message, StringComparison.Ordinal));
        Assert.False(plan.Ops[^1].DependsOnEveryTexel);

        // And it bakes: a global op is not a defect in the plan, it is a constraint on how the plan
        // may be run.
        Assert.Empty(plan.Validate());
    }

    /// <summary>⚠ And the Auto Levels map is global although it is one dispatch at any resolution.</summary>
    /// <remarks>
    ///     <b>The case that separates this property from <see cref="TextureOp.EmittedForExtent" />.</b>
    ///     The reduction rungs carry both — their count is a function of the bake and each answers
    ///     from a whole level. The map carries only this one: it is one dispatch whatever the
    ///     resolution, and every texel it writes is scaled by the 1×1 the reduction ended on, so a
    ///     tile stretched alone comes out by its own extremes. Reading tileability off the extent
    ///     stamp would have missed exactly this op.
    /// </remarks>
    [Fact]
    public void The_auto_levels_map_is_global_and_carries_no_extent_stamp() {
        var plan = AutoLevels();
        var map = plan.Ops[^1];

        Assert.Equal("AutoLevels", map.Kernel);
        Assert.True(map.DependsOnEveryTexel);
        Assert.Null(map.EmittedForExtent);

        // Every op of the chain but the `Uniform` that fills the source is named.
        Assert.Equal(plan.Ops.Length - 1, plan.TilingRefusals().Length);
        Assert.Contains(plan.TilingRefusals(), message => message.Contains("AutoLevels", StringComparison.Ordinal));
        Assert.Contains(plan.TilingRefusals(), message => message.Contains("MinMaxReduce", StringComparison.Ordinal));
        Assert.Empty(plan.Validate());
    }

    /// <summary>The message names the op and says what a tile would answer instead.</summary>
    /// <remarks>
    ///     "Somewhere in this plan an op is global" is not something a caller can act on: what it has
    ///     to decide is whether to fall back to a whole dispatch, and the op that forced that is the
    ///     one an author would have to change.
    /// </remarks>
    [Fact]
    public void A_refusal_names_the_op_and_the_picture_a_tile_would_draw() {
        var message = Assert.Single(
            Distance().TilingRefusals(),
            candidate => candidate.Contains("Op 0", StringComparison.Ordinal)
        );

        Assert.Contains("JumpFlood", message, StringComparison.Ordinal);
        Assert.Contains("every texel", message, StringComparison.Ordinal);
        Assert.Contains("seams", message, StringComparison.Ordinal);
    }
}
