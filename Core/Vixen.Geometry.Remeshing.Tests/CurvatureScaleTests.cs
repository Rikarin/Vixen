// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>What a curvature map is scaled by: a percentile of the model, not its sharpest texel (#1533).</summary>
/// <remarks>
///     ⚠ <b>The sharpest texel scaled the whole map</b>, so on a machined housing whose corner read 380 the
///     rounded edges at 8 to 23 were stored at 0.51 to 0.53, and every generator thresholding the map drew
///     nothing. The figures in the first case are that housing's shape, scaled down to a table.
/// </remarks>
public class CurvatureScaleTests {
    static bool[] Covered(int count) => [.. Enumerable.Repeat(true, count)];

    /// <summary>Flats, a band of soft edges, and a few sharp corners: the corners clip and the edges read.</summary>
    [Fact]
    public void A_few_sharp_corners_do_not_set_the_scale_for_the_soft_edges() {
        // 9 000 flat texels, 980 on soft edges at 10, and 20 sharp corners at 380.
        var curvature = new float[10_000];

        Array.Fill(curvature, 10f, 9_000, 980);
        Array.Fill(curvature, 380f, 9_980, 20);

        var scale = MapBaker.CurvatureScale(curvature, Covered(curvature.Length), 380f);

        Assert.Equal(10f, scale);

        // Stored as 0.5 + 0.5·v/range, a soft edge now reaches the end of the range; scaled by the corner,
        // it read 0.513, which is grey.
        Assert.Equal(1f, 0.5f + (0.5f * 10f / scale));
    }

    /// <summary>Texels no chart covers say nothing about the model, however curved they read.</summary>
    [Fact]
    public void Texels_no_chart_covers_are_not_counted() {
        var curvature = new float[200];
        var coverage = new bool[200];

        Array.Fill(curvature, 4f, 0, 100);
        Array.Fill(coverage, true, 0, 100);
        Array.Fill(curvature, 900f, 100, 100);

        Assert.Equal(4f, MapBaker.CurvatureScale(curvature, coverage, 4f));
    }

    /// <summary>Curvature on under one texel in a hundred: the floor keeps the flats' rounding error flat.</summary>
    [Fact]
    public void Curvature_on_under_one_texel_in_a_hundred_is_scaled_by_the_floor() {
        var curvature = new float[10_000];

        Array.Fill(curvature, 1e-5f, 0, 9_995);
        Array.Fill(curvature, 380f, 9_995, 5);

        var scale = MapBaker.CurvatureScale(curvature, Covered(curvature.Length), 380f);

        Assert.Equal(380f * MapBaker.CurvatureFloor, scale);
    }

    /// <summary>A model curved the same everywhere scales by about its largest, as it did.</summary>
    [Fact]
    public void A_model_curved_the_same_everywhere_scales_by_its_curvature() {
        var curvature = new float[1_000];

        Array.Fill(curvature, -5f);

        Assert.Equal(5f, MapBaker.CurvatureScale(curvature, Covered(curvature.Length), 5f));
        Assert.Equal(0f, MapBaker.CurvatureScale(new float[10], Covered(10), 0f));
    }

    /// <summary>A bake of a sphere with one dent in it is scaled by the sphere, and the dent clips.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The bake's own range, so this fails if the bake goes back to its sharpest texel</b> even
    ///         with <see cref="MapBaker.CurvatureScale" /> correct: a finished thing nothing calls.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A fine sphere, a dent and a cage just outside it, and each is needed.</b> At four
    ///         subdivisions a raised spike's ring was two percent of the atlas and was the percentile; with
    ///         the cage a fifth of a unit out, over one texel in a hundred read 13 on a sphere of radius one,
    ///         at either resolution. Measured, not explained: this fixture is about the scale.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_bake_is_scaled_by_its_percentile_and_not_its_sharpest_texel() {
        var source = MeshMapTests.Icosphere(6, 1f);
        var top = 0;

        for (var position = 1; position < source.PositionCount; position++) {
            if (source.Positions[position].Z > source.Positions[top].Z) {
                top = position;
            }
        }

        source.MovePosition(top, source.Positions[top] * 0.9f);

        var target = MeshMapTests.Unwrapped(MeshMapTests.Cap(MeshMapTests.Icosphere(3, 1.01f), 0f), 0f);
        var maps = MapBaker.Bake(
            source,
            target,
            new() { Resolution = 128, Gutter = 2, Space = BakeSpace.Object, Maps = MeshMaps.Curvature }
        );
        var curvature = Assert.IsAssignableFrom<IReadOnlyList<float>>(maps.Curvature);
        var sharpest = 0f;

        for (var index = 0; index < curvature.Count; index++) {
            if (maps.Coverage[index]) {
                sharpest = MathF.Max(sharpest, MathF.Abs(curvature[index]));
            }
        }

        Assert.True(sharpest > 4f, $"The dent reads {sharpest}, so this fixture has no outlier in it.");
        Assert.True(
            maps.CurvatureRange < sharpest / 2f,
            $"The bake is scaled by {maps.CurvatureRange}, and its sharpest texel is {sharpest}."
        );

        // The sphere's own curvature, one over its radius, within the dent's reach into the percentile.
        Assert.InRange(maps.CurvatureRange, 0.9f, 2f);
    }
}
