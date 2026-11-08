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
    [InlineData("Colour/Blend", false)]
    [InlineData("Colour/Image Ramp", false)]
    [InlineData("Source/Noise", false)]
    [InlineData("Colour/Premultiply", false)]
    [InlineData("Colour/Unpremultiply", false)]
    public void One_image_in_and_one_out_is_an_adjustment_unless_it_cannot_change_a_stack(string path, bool offered) {
        var library = LayerStackCompiler.Library(out _);

        Assert.True(library.TryGet(path, out var type), $"'{path}' is not in the library.");
        Assert.Equal(offered, LayerStackGraph.IsAdjustment(type!));
    }
}
