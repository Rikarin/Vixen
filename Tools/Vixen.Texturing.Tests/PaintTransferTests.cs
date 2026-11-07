// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Texturing.Painting;
using Xunit;

namespace Tests;

/// <summary>Paint stays on the surface when the layout under it changes.</summary>
public class PaintTransferTests {
    const int Side = 64;
    const uint Red = 0xff0000ffu;

    static readonly Vector3[] Square = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f)];

    static PaintProjection Plane(params Vector2[] layout) => PaintProjection.Over(Square, layout, [0, 1, 2, 0, 2, 3]);

    [Fact]
    public void A_layout_is_the_same_in_any_triangle_order_from_any_corner() {
        Vector2[] layout = [new(0f, 0f), new(0.5f, 0f), new(0.5f, 0.5f), new(0f, 0.5f)];
        var from = PaintProjection.Over(Square, layout, [0, 1, 2, 0, 2, 3]);

        Assert.True(PaintTransfer.SameLayout(from, PaintProjection.Over(Square, layout, [2, 3, 0, 1, 2, 0])));
    }

    [Fact]
    public void A_sculpted_mesh_on_the_same_islands_has_the_same_layout() {
        Vector2[] layout = [new(0f, 0f), new(0.5f, 0f), new(0.5f, 0.5f), new(0f, 0.5f)];
        Vector3[] sculpted = [new(0f, 0f, 0.2f), new(1f, 0f, 0f), new(1.1f, 1f, 0f), new(0f, 1f, 0.4f)];

        Assert.True(PaintTransfer.SameLayout(Plane(layout), PaintProjection.Over(sculpted, layout, [0, 1, 2, 0, 2, 3])));
    }

    [Fact]
    public void A_moved_island_is_another_layout() {
        var from = Plane(new(0f, 0f), new(0.5f, 0f), new(0.5f, 0.5f), new(0f, 0.5f));
        var to = Plane(new(0.5f, 0.5f), new(1f, 0.5f), new(1f, 1f), new(0.5f, 1f));

        Assert.False(PaintTransfer.SameLayout(from, to));
    }

    [Fact]
    public void A_flipped_island_is_another_layout() {
        var from = Plane(new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        var to = Plane(new(1f, 0f), new(0f, 0f), new(0f, 1f), new(1f, 1f));

        Assert.False(PaintTransfer.SameLayout(from, to));
    }

    [Fact]
    public void Another_triangle_count_is_another_layout() {
        Vector2[] layout = [new(0f, 0f), new(0.5f, 0f), new(0.5f, 0.5f), new(0f, 0.5f)];

        Assert.False(PaintTransfer.SameLayout(Plane(layout), PaintProjection.Over(Square, layout, [0, 1, 2])));
    }

    [Fact]
    public void A_repacked_island_carries_its_paint_with_it() {
        var from = Plane(new(0f, 0f), new(0.5f, 0f), new(0.5f, 0.5f), new(0f, 0.5f));
        var to = Plane(new(0.5f, 0.5f), new(1f, 0.5f), new(1f, 1f), new(0.5f, 1f));
        PaintCanvas canvas = new(Side, Side);
        canvas.Channel("baseColor").Put(8, 8, Red);
        canvas.Channel("baseColor").Put(9, 8, Red);
        canvas.Channel("baseColor").Put(8, 9, Red);
        canvas.Channel("baseColor").Put(9, 9, Red);

        var moved = PaintTransfer.Move(canvas, from, to).Channel("baseColor");

        Assert.Equal(Red, moved.At(40, 40));
        Assert.Equal(0u, moved.At(8, 8));
        Assert.Equal(0u, moved.At(60, 40) & 0xff000000u);
    }

    [Fact]
    public void A_flipped_island_keeps_each_mark_where_it_was_on_the_surface() {
        var from = Plane(new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        var to = Plane(new(1f, 0f), new(0f, 0f), new(0f, 1f), new(1f, 1f));
        PaintCanvas canvas = new(Side, Side);
        for (var x = 8; x < 16; x++) {
            for (var y = 30; y < 34; y++) {
                canvas.Channel("mask").Put(x, y, Red);
            }
        }

        var moved = PaintTransfer.Move(canvas, from, to).Channel("mask");

        Assert.Equal(Red, moved.At(Side - 12, 32));
        Assert.Equal(0u, moved.At(12, 32));
    }

    [Fact]
    public void Another_topology_still_reads_the_same_surface() {
        var from = Plane(new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        List<Vector3> points = [];
        List<Vector2> layout = [];
        List<int> indices = [];
        const int Cells = 8;
        for (var y = 0; y <= Cells; y++) {
            for (var x = 0; x <= Cells; x++) {
                points.Add(new(x / (float)Cells, y / (float)Cells, 0f));
                layout.Add(new(y / (float)Cells, x / (float)Cells));
            }
        }

        for (var y = 0; y < Cells; y++) {
            for (var x = 0; x < Cells; x++) {
                var at = y * (Cells + 1) + x;
                indices.AddRange([at, at + 1, at + Cells + 2, at, at + Cells + 2, at + Cells + 1]);
            }
        }

        var to = PaintProjection.Over([..points], [..layout], [..indices]);
        PaintCanvas canvas = new(Side, Side);
        for (var x = 40; x < 50; x++) {
            for (var y = 10; y < 20; y++) {
                canvas.Channel("baseColor").Put(x, y, Red);
            }
        }

        var moved = PaintTransfer.Move(canvas, from, to).Channel("baseColor");

        Assert.Equal(Red, moved.At(15, 45));
        Assert.Equal(0u, moved.At(45, 15));
    }

    [Fact]
    public void Surface_the_old_mesh_never_had_stays_clear() {
        var from = Plane(new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f));
        Vector3[] far = [new(0f, 0f, 5f), new(1f, 0f, 5f), new(1f, 1f, 5f), new(0f, 1f, 5f)];
        var to = PaintProjection.Over(far, [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)], [0, 1, 2, 0, 2, 3]);
        PaintCanvas canvas = new(Side, Side);
        for (var x = 0; x < Side; x++) {
            for (var y = 0; y < Side; y++) {
                canvas.Channel("baseColor").Put(x, y, Red);
            }
        }

        Assert.Equal(0u, PaintTransfer.Move(canvas, from, to).Channel("baseColor").At(32, 32));
    }
}
