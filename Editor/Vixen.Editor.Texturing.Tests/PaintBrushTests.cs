// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The brush, and the claim that it is doc 31's brush rather than a second one.</summary>
public class PaintBrushTests {
    /// <summary>
    ///     ⚠ The claim <c>Vixen.Editor.Texturing.csproj</c>'s new reference makes, asserted rather
    ///     than argued.
    /// </summary>
    /// <remarks>
    ///     If <see cref="PaintBrush" /> ever grows its own falloff — a copied switch, an
    ///     "improvement", a rounding difference — this goes red. That is the whole reason it exists:
    ///     a soft edge sculpted at 0.3 and a soft edge painted at 0.3 being different shapes is a
    ///     defect nobody would ever notice by looking, and it is what <c>TerrainBrush</c>'s "one
    ///     service, three consumers" remark exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void The_paint_brush_evaluates_through_the_terrain_brush_and_not_a_copy_of_it(float falloff) {
        PaintBrush paint = new() {
            Radius = 20f,
            Flow = 1f,
            Opacity = 1f,
            Spacing = 0.2f,
            Falloff = falloff,
            Curve = BrushFalloffKind.Smooth
        };

        TerrainBrush terrain = new() {
            Radius = 20f,
            Strength = 1f,
            Falloff = falloff,
            Curve = BrushFalloffKind.Smooth,
            Shape = BrushShape.Circle,
            Spacing = 0.2f,
            Rotation = BrushRotation.Fixed,
            PatternScale = 1f
        };

        PaintStamp stamp = new(new(50f, 50f), 0f, 20f, 1f);
        BrushStamp equivalent = new(new(50f, 50f), 0f, 1f);
        var differed = false;

        for (var distance = 0; distance <= 24; distance++) {
            Vector2 sample = new(50f + distance, 50f);

            Assert.Equal(terrain.WeightAt(sample, equivalent), paint.WeightAt(sample, stamp), 6);

            differed |= terrain.WeightAt(sample, equivalent) != terrain.WeightAt(new(50f, 50f), equivalent);
        }

        // The instrument. A brush whose weight never varied would make every equality above a
        // comparison of one number with itself, which is true of a falloff that returns a constant
        // and true of one that returns zero.
        Assert.True(differed, "The weights never varied, so the comparison proved nothing.");
    }

    /// <summary>Falloff is the fraction that falls off, and zero is a hard disc.</summary>
    [Fact]
    public void A_brush_with_no_falloff_is_flat_to_its_edge_and_nothing_past_it() {
        PaintBrush brush = new() { Radius = 10f, Flow = 1f, Opacity = 1f, Spacing = 0.2f, Falloff = 0f };
        PaintStamp stamp = new(new(0f, 0f), 0f, 10f, 1f);

        Assert.Equal(1f, brush.WeightAt(new(0f, 0f), stamp), 5);
        Assert.Equal(1f, brush.WeightAt(new(9.5f, 0f), stamp), 5);
        Assert.Equal(0f, brush.WeightAt(new(10f, 0f), stamp), 5);
    }

    /// <summary>Flow is on the stamp, so a partial stamp deposits less.</summary>
    [Fact]
    public void Flow_scales_the_whole_stamp() {
        PaintBrush brush = new() { Radius = 10f, Flow = 1f, Opacity = 1f, Spacing = 0.2f, Falloff = 0f };

        Assert.Equal(0.25f, brush.WeightAt(new(0f, 0f), new(new(0f, 0f), 0f, 10f, 0.25f)), 5);
    }

    /// <summary>A footprint is conservative, clipped, and never smaller than the disc.</summary>
    [Fact]
    public void A_footprint_covers_the_disc_and_is_clipped_to_the_atlas() {
        PaintBrush brush = PaintBrush.Default with { Radius = 8f };
        var inside = brush.FootprintOf(new(new(20f, 20f), 0f, 8f, 1f), 64, 64);

        Assert.True(inside.X <= 12 && inside.Y <= 12, "The footprint did not reach the disc's low corner.");
        Assert.True(inside.EndX >= 28 && inside.EndY >= 28, "The footprint did not reach the disc's high corner.");

        var edge = brush.FootprintOf(new(new(2f, 2f), 0f, 8f, 1f), 64, 64);

        Assert.Equal(0, edge.X);
        Assert.Equal(0, edge.Y);
        Assert.True(edge.Area > 0, "A stamp overlapping the atlas edge produced no rectangle at all.");
    }

    /// <summary>A brush alpha is <c>IBrushMask</c>, which is the seam terrain already has.</summary>
    [Fact]
    public void A_brush_alpha_shapes_the_stamp_through_the_mask_terrain_already_defines() {
        HalfMask mask = new();

        PaintBrush brush = new() {
            Radius = 10f,
            Flow = 1f,
            Opacity = 1f,
            Spacing = 0.2f,
            Falloff = 0f,
            Alpha = mask
        };

        PaintStamp stamp = new(new(0f, 0f), 0f, 10f, 1f);

        // The mask is one on the right half of its unit square and zero on the left, and the stamp is
        // unrotated, so the sign of the offset picks the half.
        Assert.Equal(1f, brush.WeightAt(new(5f, 0f), stamp), 5);
        Assert.Equal(0f, brush.WeightAt(new(-5f, 0f), stamp), 5);
    }

    sealed class HalfMask : IBrushMask {
        public float Sample(Vector2 uv) => uv.X >= 0.5f ? 1f : 0f;
    }
}
