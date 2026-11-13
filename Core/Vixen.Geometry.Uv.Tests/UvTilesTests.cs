// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>Which UDIM tiles a layout a reader handed over reaches.</summary>
public class UvTilesTests {
    /// <summary>A triangle in the square whose corner is at <paramref name="u" />, <paramref name="v" />, as a file says it.</summary>
    /// <remarks>A reader flips v, so a file's <c>v</c> is <c>1 − v</c> here.</remarks>
    static Vector2[] In(float u, float v) =>
        [new(u + 0.1f, 1f - (v + 0.1f)), new(u + 0.9f, 1f - (v + 0.1f)), new(u + 0.1f, 1f - (v + 0.9f))];

    [Fact]
    public void A_layout_inside_the_unit_square_is_tile_1001_alone() {
        var tiles = UvTiles.Reached(In(0f, 0f));

        Assert.Equal([new UvTile(0, 0)], tiles);
        Assert.Equal(1001, tiles[0].Udim);
    }

    [Fact]
    public void Across_is_one_more_and_up_is_ten_more() {
        Vector2[] corners = [..In(1f, 0f), ..In(0f, 1f), ..In(0f, 0f)];

        var tiles = UvTiles.Reached(corners);

        Assert.Equal(["1001", "1002", "1011"], tiles.Select(static tile => tile.ToString()));
    }

    [Fact]
    public void A_corner_a_hair_over_the_edge_is_still_the_first_tile() {
        Vector2[] corners = [new(0.5f, 0.5f), new(1.0001f, 0.5f), new(0.5f, -0.0001f)];

        Assert.Equal([new UvTile(0, 0)], UvTiles.Reached(corners));
    }

    [Fact]
    public void A_square_udim_has_no_number_for_is_named_by_its_counts() {
        var tile = UvTiles.Reached(In(-1f, 0f))[0];

        Assert.Null(tile.Udim);
        Assert.Equal("(-1, 0)", tile.ToString());
    }

    [Fact]
    public void A_layout_on_one_tile_names_it_and_a_layout_on_two_names_none() {
        Assert.Equal(new UvTile(1, 1), UvTiles.Only([..In(1f, 1f), ..In(1f, 1f)]));
        Assert.Null(UvTiles.Only([..In(0f, 0f), ..In(1f, 0f)]));
        Assert.Null(UvTiles.Only([]));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 0f)]
    [InlineData(3f, 2f)]
    public void Home_moves_a_tile_onto_the_first_and_keeps_its_place_in_it(float u, float v) {
        var corners = In(u, v);
        var tile = UvTiles.Only(corners)!.Value;

        Vector2[] home = [..corners.Select(tile.Home)];

        Assert.Equal(new UvTile(0, 0), UvTiles.Only(home));
        for (var at = 0; at < home.Length; at++) {
            Assert.Equal(In(0f, 0f)[at].X, home[at].X, 5);
            Assert.Equal(In(0f, 0f)[at].Y, home[at].Y, 5);
        }
    }

    [Fact]
    public void Coordinates_that_are_not_whole_triangles_are_refused() =>
        Assert.Throws<ArgumentException>(static () => UvTiles.Reached(new Vector2[4]));
}
