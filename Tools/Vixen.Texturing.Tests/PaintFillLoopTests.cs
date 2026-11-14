// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>
///     A loop fill on a tube of quads worked out by hand: the ring round it, or the column up it, by which edge of the
///     quad the click lands nearest.
/// </summary>
public class PaintFillLoopTests {
    const int Side = 128;
    const int Around = 8;
    const int Rings = 3;
    const float Ring = 0.5f;
    const uint Red = 0xFF0000FFu;

    static readonly PaintBrush Hard = PaintBrush.Default with { Radius = 4f, Flow = 1f, Falloff = 0f };

    /// <summary>
    ///     A tube of <see cref="Around" /> by <see cref="Rings" /> quads, each cut along its diagonal, its top closed by
    ///     a fan of triangles. The tube is laid flat over the top of the atlas, its first column twice, once at each
    ///     end of the strip, so a ring crosses a UV seam; the cap lies apart below it. Quad (column, ring) is triangles
    ///     2 × (ring × <see cref="Around" /> + column) and the one after.
    /// </summary>
    static PaintMeshSurface Tube() {
        List<Vector3> positions = [];
        List<Vector2> layout = [];
        List<int> indices = [];

        for (var ring = 0; ring <= Rings; ring++) {
            for (var column = 0; column <= Around; column++) {
                var (sin, cos) = MathF.SinCos(MathF.Tau * column / Around);

                positions.Add(new(cos, ring * Ring, sin));
                layout.Add(new(0.05f + 0.9f * column / Around, 0.05f + 0.6f * ring / Rings));
            }
        }

        for (var ring = 0; ring < Rings; ring++) {
            for (var column = 0; column < Around; column++) {
                var corner = ring * (Around + 1) + column;

                indices.AddRange(
                    [corner, corner + 1, corner + Around + 2, corner, corner + Around + 2, corner + Around + 1]
                );
            }
        }

        var middle = positions.Count;

        positions.Add(new(0f, Rings * Ring, 0f));
        layout.Add(new(0.5f, 0.85f));

        for (var column = 0; column < Around; column++) {
            var (sin, cos) = MathF.SinCos(MathF.Tau * column / Around);

            positions.Add(new(cos, Rings * Ring, sin));
            layout.Add(new(0.5f + 0.1f * cos, 0.85f + 0.1f * sin));
        }

        for (var column = 0; column < Around; column++) {
            indices.AddRange([middle, middle + 1 + (column + 1) % Around, middle + 1 + column]);
        }

        return new(PaintProjection.Over([..positions], [..layout], [..indices]), Side, Side);
    }

    /// <summary>A place on the tube, by column and ring, which may be a fraction of either.</summary>
    static Vector3 At(float column, float ring) {
        var (sin, cos) = MathF.SinCos(MathF.Tau * column / Around);

        return new(cos, ring * Ring, sin);
    }

    static int Quad(int column, int ring) => 2 * (ring * Around + column);

    /// <summary>
    ///     ⚠ A click nearest the edge between two columns runs round the ring, across that edge and the ones opposite,
    ///     closing on itself past the UV seam: every quad of that ring, and none of another. A click nearest the edge
    ///     between two rings runs up the column, and stops at the tube's open bottom and at the fan of triangles that
    ///     caps its top, which are not quads.
    /// </summary>
    [Fact]
    public void A_loop_runs_across_the_edge_nearest_the_click_round_the_ring_or_up_the_column() {
        var surface = Tube();
        List<int> into = [];

        surface.Select(Quad(2, 1), PaintFillMode.Loop, At(2.95f, 1.5f), into);

        Assert.Equal(
            Enumerable.Range(0, Around)
                .SelectMany(static column => new[] { Quad(column, 1), Quad(column, 1) + 1 })
                .Order(),
            into.Order()
        );
        Assert.Equal(Quad(2, 1), into[0]);

        surface.Select(Quad(2, 1), PaintFillMode.Loop, At(2.5f, 1.95f), into);

        Assert.Equal(
            Enumerable.Range(0, Rings).SelectMany(static ring => new[] { Quad(2, ring), Quad(2, ring) + 1 }).Order(),
            into.Order()
        );

        // The other half of the same quad leads the same way.
        surface.Select(Quad(2, 1) + 1, PaintFillMode.Loop, At(2.05f, 1.5f), into);

        Assert.Equal(2 * Around, into.Count);
        Assert.All(into, static triangle => Assert.InRange(triangle, Quad(0, 1), Quad(0, 2) - 1));
    }

    /// <summary>A triangle that is not half of a quad is a loop of itself: the cap's fan takes one triangle a click.</summary>
    [Fact]
    public void A_triangle_that_is_no_quads_half_is_a_loop_alone() {
        var surface = Tube();
        List<int> into = [];
        var fan = 2 * Around * Rings + 3;

        surface.Select(fan, PaintFillMode.Loop, At(3.5f, Rings), into);

        Assert.Equal([fan], into);
    }

    /// <summary>
    ///     Clicked in the atlas, a loop fill paints the whole ring it runs round, both ends of the strip the UV seam cuts
    ///     it into among it, and nothing of the rings beside it or of the cap.
    /// </summary>
    [Fact]
    public void A_loop_fill_in_the_atlas_paints_the_ring_on_both_sides_of_the_seam() {
        var surface = Tube();
        PaintImage image = new(Side, Side);

        // The middle ring is 0.25 to 0.45 of the atlas down; this is near the right edge of its third quad.
        var click = new Vector2((0.05f + 0.9f * 2.95f / Around) * Side, 0.35f * Side);

        new PaintStroke(image, surface.Coverage, Hard, Red, 0, placement: surface.Fill(PaintFillMode.Loop)).Dab(click);

        var row = (int)(0.35f * Side);

        Assert.Equal(Red, image.At((int)(0.07f * Side), row));
        Assert.Equal(Red, image.At((int)(0.93f * Side), row));
        Assert.Equal(0u, image.At(Side / 2, (int)(0.15f * Side)));
        Assert.Equal(0u, image.At(Side / 2, (int)(0.55f * Side)));
        Assert.Equal(0u, image.At(Side / 2, (int)(0.85f * Side)));
    }

    /// <summary>A loop is found from where a click lands, so it is refused an outline, and a selection with no place.</summary>
    [Fact]
    public void A_loop_takes_no_outline_and_needs_to_know_where_it_was_clicked() {
        var surface = Tube();
        var box = PaintFillOutline.Rectangle(Vector2.Zero, new(Side, Side));

        Assert.Throws<ArgumentException>(() => surface.Fill(PaintFillMode.Loop, outline: box));
        Assert.Throws<ArgumentException>(() => surface.Select(0, PaintFillMode.Loop, []));
    }
}
