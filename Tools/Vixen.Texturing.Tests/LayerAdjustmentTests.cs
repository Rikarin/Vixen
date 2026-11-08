// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>Which node types a host may offer as a filter layer or a mask step.</summary>
public sealed class LayerAdjustmentTests {
    [Theory]
    [InlineData("Colour/Levels", true)]
    [InlineData("Colour/Clamp", true)]
    [InlineData("Filters/Median", true)]
    [InlineData("Utility/Glow", true)]
    [InlineData("Colour/Histogram Equalise", true)]
    [InlineData("Filters/Warp", true)]
    [InlineData("Filters/Slope Blur", true)]
    [InlineData("Colour/Image Ramp", true)]
    [InlineData("Adjust/Colour Correct", true)]
    [InlineData("Adjust/Drop Shadow", true)]
    [InlineData("Adjust/PBR Validate", true)]
    [InlineData("Finishes/Brushed", true)]
    [InlineData("Colour/Blend", false)]
    [InlineData("Colour/Mix", false)]
    [InlineData("Colour/Dissolve", false)]
    [InlineData("Colour/Select Input", false)]
    [InlineData("Placement/Tile Sampler", false)]
    [InlineData("Placement/Splatter", false)]
    [InlineData("Source/Noise", false)]
    [InlineData("Colour/Premultiply", false)]
    [InlineData("Colour/Unpremultiply", false)]
    public void An_image_in_and_one_out_is_an_adjustment_unless_the_node_or_the_stack_says_not(string path, bool offered) {
        var library = LayerStackCompiler.Library(out _);

        Assert.True(library.TryGet(path, out var type), $"'{path}' is not in the library.");
        Assert.Equal(offered, LayerStackGraph.IsAdjustment(type!));
    }

    /// <summary>
    ///     A filter whose outputs are named after channels writes those channels of a layer, and a mask is one picture
    ///     in no channel, so it is no mask step. One that writes a picture is both.
    /// </summary>
    [Theory]
    [InlineData("Adjust/PBR Validate", false)]
    [InlineData("Finishes/Brushed", false)]
    [InlineData("Adjust/Drop Shadow", true)]
    [InlineData("Filters/Warp", true)]
    public void A_filter_that_writes_channels_is_no_mask_step(string path, bool step) {
        var library = LayerStackCompiler.Library(out _);

        Assert.True(library.TryGet(path, out var type), $"'{path}' is not in the library.");
        Assert.True(LayerStackGraph.IsAdjustment(type!));
        Assert.Equal(step, LayerStackGraph.IsMaskStep(type!));
    }

    /// <summary>
    ///     Patina #220: a node whose output measures its input — a histogram, a chart of one, a table of running sums —
    ///     gives a layer or a mask nothing of the surface back, and is offered as neither.
    /// </summary>
    [Theory]
    [InlineData("Analysis/Histogram")]
    [InlineData("Analysis/Histogram Render")]
    [InlineData("Analysis/Summed Area Table")]
    public void A_statistic_is_neither_a_filter_nor_a_mask_step(string path) {
        var library = LayerStackCompiler.Library(out _);

        Assert.True(library.TryGet(path, out var type), $"'{path}' is not in the library.");
        Assert.False(type!.Adjustment, $"'{path}' does not say it is no adjustment.");
        Assert.False(LayerStackGraph.IsAdjustment(type));
        Assert.False(LayerStackGraph.IsMaskStep(type));
    }

    /// <summary>
    ///     ⚠ The whole library, so that the next statistics node cannot be offered by forgetting to say so: every type
    ///     a host is offered either declares itself an adjustment or is offered for a reason the stack gives.
    /// </summary>
    [Fact]
    public void Nothing_that_says_it_is_no_adjustment_is_offered() {
        var library = LayerStackCompiler.Library(out _);

        Assert.All(
            library.Types.Where(static type => !type.Adjustment),
            static type => Assert.False(LayerStackGraph.IsAdjustment(type), type.Path)
        );
        Assert.Contains(library.Types, static type => !type.Adjustment);
    }
}
