// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Rendering;
using Vixen.Terrain;
using Xunit;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>The brush footprint, as geometry — [docs/plan/31 § T3].</summary>
/// <remarks>
///     ⚠ <b>Every assertion here is about where a vertex <i>is</i>, not about how many there are.</b>
///     A count says a ring was emitted; it says nothing about a ring at the wrong scale, at the wrong
///     place, or lying flat through a hillside — which are the three ways this can be wrong and still
///     look busy in a viewport.
/// </remarks>
public class TerrainCursorTests {
    /// <summary>The middle of <see cref="Ground.Shape" />, which is 62 m square.</summary>
    static readonly Vector2 Middle = new(31f, 31f);

    /// <summary>A terrain that rises along X at a fixed grade.</summary>
    /// <param name="grade">Metres of height per metre of X.</param>
    /// <remarks>
    ///     Written into <see cref="TerrainMap.Base" /> rather than stroked, because a stroke is a
    ///     brush and a brush is what is under test — a slope made by the thing being measured would
    ///     agree with it however wrong both were.
    /// </remarks>
    static TerrainMap Sloped(float grade = 0.5f) {
        var terrain = new TerrainMap(Ground.Shape);
        var description = terrain.Description;

        terrain.AddLayer("Sculpt");

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                terrain.Base[x, z] = description.StoreHeight(x * description.MetresPerQuad * grade);
            }
        }

        terrain.InvalidateAll();
        terrain.Resolve();

        return terrain;
    }

    static (List<LineVertex> Lines, GizmoDraw Draw) Into() {
        List<LineVertex> lines = [];

        return (lines, new GizmoDraw(lines));
    }

    /// <summary>How far a vertex is from the brush's centre, horizontally.</summary>
    static float Reach(LineVertex vertex, Vector3 origin) =>
        new Vector2(vertex.Position.X - origin.X - Middle.X, vertex.Position.Z - origin.Z - Middle.Y).Length();

    // ── The shape of one ring ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_hard_brush_draws_one_circle_of_the_brush_radius() {
        var terrain = Ground.Terrain();
        var (lines, draw) = Into();

        // Falloff 0 is a hard disc: the plateau *is* the radius, so a second ring would be the first
        // one drawn twice.
        TerrainCursor.Draw(draw, terrain, Vector3.Zero, Middle, 8f, 0f);

        Assert.NotEmpty(lines);
        Assert.All(lines, vertex => Assert.Equal(8f, Reach(vertex, Vector3.Zero), 3));
    }

    [Fact]
    public void A_soft_brush_draws_the_plateau_as_well_as_the_reach() {
        var terrain = Ground.Terrain();
        var (lines, draw) = Into();

        TerrainCursor.Draw(draw, terrain, Vector3.Zero, Middle, 8f, 0.25f);

        // 8 m of reach and a plateau at 8 × (1 − 0.25) — TerrainBrush.WeightAt's own boundary, which
        // is the number that decides what a stroke looks like.
        Assert.Contains(lines, vertex => MathF.Abs(Reach(vertex, Vector3.Zero) - 8f) < 1e-3f);
        Assert.Contains(lines, vertex => MathF.Abs(Reach(vertex, Vector3.Zero) - 6f) < 1e-3f);

        // And nothing between them: two rings, not an annulus.
        Assert.All(
            lines,
            vertex => Assert.True(
                MathF.Abs(Reach(vertex, Vector3.Zero) - 8f) < 1e-3f
                || MathF.Abs(Reach(vertex, Vector3.Zero) - 6f) < 1e-3f
            )
        );
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.01f)]
    [InlineData(0.995f)]
    [InlineData(1f)]
    public void A_plateau_that_is_the_whole_brush_or_none_of_it_is_not_a_second_ring(float falloff) {
        var terrain = Ground.Terrain();
        var (lines, draw) = Into();
        var (single, singleDraw) = Into();

        TerrainCursor.Draw(draw, terrain, Vector3.Zero, Middle, 8f, falloff);
        TerrainCursor.Draw(singleDraw, terrain, Vector3.Zero, Middle, 8f, 0f);

        // A ring that is 2% of the reach is a dot and one that is 98% of it is the outer ring drawn
        // again; both read as "the cursor got brighter" and neither says anything.
        Assert.Equal(single.Count, lines.Count);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_radius_that_is_not_a_radius_draws_nothing(float radius) {
        var terrain = Ground.Terrain();
        var (lines, draw) = Into();

        TerrainCursor.Draw(draw, terrain, Vector3.Zero, Middle, radius, 0.5f);

        Assert.Empty(lines);
    }

    [Fact]
    public void Every_ring_is_closed() {
        var terrain = Sloped();
        var (lines, draw) = Into();

        TerrainCursor.Draw(draw, terrain, Vector3.Zero, Middle, 8f, 0f);

        // A polyline of N segments is 2N vertices, and the last one is the first one — a ring with a
        // gap in it is what an off-by-one in the loop bound produces, and it is invisible in a count.
        //
        // ⚠ Within a millimetre rather than exactly: the closing vertex is cos(τ) where the first is
        // cos(0), which is the same point and not the same float.
        Assert.Equal(0, lines.Count % 2);
        Assert.True((lines[0].Position - lines[^1].Position).Length() < 1e-3f, "the ring does not close");

        for (var i = 1; i < lines.Count - 1; i += 2) {
            Assert.Equal(lines[i].Position, lines[i + 1].Position);
        }
    }

    // ── The ground ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_vertex_sits_on_the_ground_the_pick_would_report() {
        var terrain = Sloped();
        var (lines, draw) = Into();

        TerrainCursor.Draw(draw, terrain, Vector3.Zero, Middle, 8f, 0.5f);

        Assert.NotEmpty(lines);

        // The bilinear surface, sample for sample: the ring lands where the stamp will, because it
        // is the same function the pick that placed it used.
        Assert.All(
            lines,
            vertex => Assert.Equal(
                TerrainPick.HeightAt(terrain, vertex.Position.X, vertex.Position.Z),
                vertex.Position.Y,
                3
            )
        );
    }

    [Fact]
    public void A_ring_on_a_hillside_is_not_flat() {
        var terrain = Sloped();
        var (lines, draw) = Into();

        TerrainCursor.Draw(draw, terrain, Vector3.Zero, Middle, 8f, 0f);

        var lowest = lines.Min(vertex => vertex.Position.Y);
        var highest = lines.Max(vertex => vertex.Position.Y);

        // ⚠ The assertion the whole feature turns on. A disc drawn flat at the hit's height spans
        // zero; a conformed one spans the grade across its diameter — 0.5 × 16 m here. Sabotaging
        // `On` to return the centre's height fails exactly this and nothing else.
        Assert.Equal(8f, highest - lowest, 1);
    }

    [Fact]
    public void The_terrain_origin_moves_the_ring_and_nothing_else() {
        var terrain = Sloped();
        var origin = new Vector3(100f, 5f, -20f);

        var (here, hereDraw) = Into();
        var (there, thereDraw) = Into();

        TerrainCursor.Draw(hereDraw, terrain, Vector3.Zero, Middle, 8f, 0.5f);
        TerrainCursor.Draw(thereDraw, terrain, origin, Middle, 8f, 0.5f);

        Assert.Equal(here.Count, there.Count);

        for (var i = 0; i < here.Count; i++) {
            Assert.Equal(here[i].Position + origin, there[i].Position);
        }
    }

    // ── The sampling rate ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_bigger_brush_is_sampled_more_finely_so_the_ring_keeps_following_the_ground() {
        var terrain = Ground.Terrain();

        // A fixed segment count would give the same number at every size, and the chords of a big
        // one would fly over the valleys between their ends.
        Assert.True(
            TerrainCursor.SegmentsFor(terrain, 200f) > TerrainCursor.SegmentsFor(terrain, 20f),
            "a larger radius must be sampled with more segments"
        );

        // About twice a quad: a 20 m radius on a 1 m grid is τ × 20 × 2 ≈ 251 samples.
        Assert.Equal(251, TerrainCursor.SegmentsFor(terrain, 20f));
    }

    [Theory]
    [InlineData(0.001f)]
    [InlineData(1f)]
    [InlineData(4_000f)]
    [InlineData(float.MaxValue)]
    public void The_segment_count_is_clamped_at_both_ends(float radius) {
        var terrain = Ground.Terrain();

        Assert.InRange(
            TerrainCursor.SegmentsFor(terrain, radius),
            TerrainCursor.MinimumSegments,
            TerrainCursor.MaximumSegments
        );
    }

    // ── The colours ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_two_rings_are_told_apart_by_alpha_rather_than_by_hue() {
        var terrain = Ground.Terrain();
        var (lines, draw) = Into();

        TerrainCursor.Draw(draw, terrain, Vector3.Zero, Middle, 8f, 0.5f);

        var outer = lines.Where(vertex => Reach(vertex, Vector3.Zero) > 7f).ToList();
        var inner = lines.Where(vertex => Reach(vertex, Vector3.Zero) < 5f).ToList();

        Assert.NotEmpty(outer);
        Assert.NotEmpty(inner);

        Assert.All(outer, vertex => Assert.Equal(TerrainCursor.Outer, vertex.Colour));
        Assert.All(inner, vertex => Assert.Equal(TerrainCursor.Inner, vertex.Colour));

        // The same brush said quieter, not a second thing: one hue, two weights.
        Assert.Equal(TerrainCursor.Outer.R, TerrainCursor.Inner.R);
        Assert.Equal(TerrainCursor.Outer.G, TerrainCursor.Inner.G);
        Assert.Equal(TerrainCursor.Outer.B, TerrainCursor.Inner.B);
        Assert.True(TerrainCursor.Inner.A < TerrainCursor.Outer.A);
    }
}
