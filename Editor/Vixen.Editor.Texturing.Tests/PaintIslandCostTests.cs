// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     What a stamp costs once the coverage map is a real one —
///     <a href="https://github.com/Rikarin/Vixen/issues/920">#920</a>'s effect on
///     <c>PaintCostTests</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every number in <c>PaintCostTests</c> was measured over
///         <c>PaintCoverage.Everywhere</c>, which is the easy case by construction.</b>
///         <c>PaintStroke.Dilate</c> breaks out of its round loop as soon as a round finds nothing to
///         fill, and over an atlas every texel of which is surface the first round finds nothing at
///         all — so the dilation runs <em>once</em> however large the gutter is. That was true of
///         every stroke the editor could take, because nothing handed the paint pane a real map.
///     </para>
///     <para>
///         <b>So the closed form is re-derived here against real islands, and the derivation is from
///         the flat run rather than from constants.</b> A stamp scans its footprint once and then
///         scans the footprint grown by the gutter once per round; over <c>Everywhere</c> that is
///         exactly one round, which makes the flat run a measurement of both terms — and the island
///         run must then be the same footprint plus <c>gutter</c> of the same grown rectangle. No
///         number below is written down; each is read off the other run.
///     </para>
///     <para>
///         ⚠ <b>The bound <c>PaintCostTests</c> asserts still holds and is not what moved.</b> It
///         allows <c>footprint + gutter × dilated</c> per stamp, which is the worst case — the island
///         case. What moved is the measurement: the dilation scan is four times what it was, so the
///         run that was inside the bound by a factor of four is now at it.
///     </para>
/// </remarks>
public class PaintIslandCostTests(ITestOutputHelper output) {
    const int Size = 4096;
    const int Radius = 48;
    const int Gutter = 4;
    const uint Opaque = 0xFF0000FFu;

    /// <summary>The dilation runs once over a flat map and <c>gutter</c> times over real islands.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A closed-form oracle rather than a bound</b>, because the two runs differ in exactly
    ///         one thing: the coverage map. The footprint is the same rectangle — <c>FootprintOf</c>
    ///         reads the stamp and the atlas and never the coverage — so the grown rectangle is the
    ///         same too, and the only free variable left is how many rounds ran.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The islands are eight texels wide with eight-texel gaps, which is what makes
    ///         every round find something.</b> Round <c>r</c> fills the texels at distance
    ///         <c>r + 1</c> from coverage; a gap of eight has texels at distances one to four from
    ///         each side, so all four rounds have work. A gap of two would fill in one round and this
    ///         would measure the gap rather than the gutter.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_real_island_map_runs_every_dilation_round_and_a_flat_one_runs_one() {
        var flat = Stamp(PaintCoverage.Everywhere(Size, Size));
        var islands = Stamp(Grid());

        // Over `Everywhere` every footprint texel is covered, so the weight count *is* the footprint
        // scan and the remainder is one round of the grown rectangle, entire.
        var footprint = flat.Weights;
        var round = flat.Scanned - flat.Weights;

        Assert.True(footprint > 0, "the flat stamp evaluated no weights, so it measured nothing.");
        Assert.True(round > footprint, "one dilation round is smaller than the footprint it grew from.");

        // ⚠ The claim, with nothing hard-coded: the same footprint, and `Gutter` rounds of the same
        // grown rectangle instead of one.
        Assert.Equal(footprint + round, flat.Scanned);
        Assert.Equal(footprint + (Gutter * round), islands.Scanned);

        // And the island run evaluates fewer weights, because half its footprint is not surface —
        // which is the half of the change that makes a stamp cheaper rather than dearer.
        Assert.True(
            islands.Weights < flat.Weights,
            $"the island stamp evaluated {islands.Weights} weights against the flat one's {flat.Weights}; "
            + "a coverage map that refuses nothing is not a coverage map."
        );

        output.WriteLine(
            $"radius {Radius}, gutter {Gutter}: footprint {footprint} texels, one dilation round {round}. "
            + $"Flat: {flat.Scanned} scanned for {flat.Weights} weights ({(double)(flat.Scanned - flat.Weights) / flat.Weights:F1}× "
            + $"the counted loop). Islands: {islands.Scanned} scanned for {islands.Weights} weights "
            + $"({(double)(islands.Scanned - islands.Weights) / islands.Weights:F1}×). ⚠ The dilation scan is "
            + $"{(double)(islands.Scanned - islands.Weights) / (flat.Scanned - flat.Weights):F1}× what PaintCostTests measured."
        );
    }

    /// <summary>A whole stroke over real islands is still inside the bound that gate asserts.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the instrument check for <c>PaintCostTests</c> rather than a new claim.</b>
    ///     That test's bound — <c>stamps × (footprint + gutter × dilated)</c> — was derived for the
    ///     island case and measured on the flat one, so nothing had ever shown it was reachable. It
    ///     is: the run below sits at the bound rather than a quarter of it, which is what makes the
    ///     bound a bound rather than a number four times too generous.
    /// </remarks>
    [Fact]
    public void A_stroke_over_real_islands_stays_inside_the_bound_the_flat_case_measured() {
        const int Stamps = 64;

        PaintImage layer = new(Size, Size);
        PaintStroke stroke = new(layer, Grid(), PaintStrokeTests.Hard(Radius) with { Spacing = 1f }, Opaque, Gutter);

        stroke.MoveTo(new(1024f, 2048f));

        for (var step = 1; step < Stamps; step++) {
            stroke.MoveTo(new(1024f + (step * Radius), 2048f));
        }

        Assert.Equal(Stamps, stroke.StampCount);

        var square = (long)((2 * Radius) + 2) * ((2 * Radius) + 2);
        var dilated = (long)((2 * Radius) + 2 + (2 * Gutter)) * ((2 * Radius) + 2 + (2 * Gutter));
        var bound = Stamps * (square + (Gutter * dilated));

        Assert.True(
            stroke.TexelsScanned <= bound,
            $"{stroke.TexelsScanned} texels scanned against a bound of {bound}. The bound names the "
            + "island case, so an island stroke passing it is the only thing that tests it."
        );

        // The instrument: the run is not inside the bound by a factor that would hide a regression.
        // Half of it or more is what "the worst case is the case being measured" looks like.
        Assert.True(
            stroke.TexelsScanned * 2 > bound,
            $"{stroke.TexelsScanned} of a {bound} bound is loose enough that the bound proves little."
        );

        output.WriteLine(
            $"64 stamps over real islands: {stroke.TexelsScanned} texels scanned, "
            + $"{stroke.WeightsEvaluated} weights, {stroke.DilatedTexels} texels dilated. The bound is {bound}, "
            + $"and the flat case reaches about a quarter of it."
        );
    }

    /// <summary>One stamp's counters over a coverage map.</summary>
    static (long Scanned, long Weights) Stamp(PaintCoverage coverage) {
        PaintImage layer = new(Size, Size);
        PaintStroke stroke = new(layer, coverage, PaintStrokeTests.Hard(Radius), Opaque, Gutter);

        stroke.MoveTo(new(1024f, 2048f));

        Assert.Equal(1, stroke.StampCount);

        return (stroke.TexelsScanned, stroke.WeightsEvaluated);
    }

    /// <summary>
    ///     Islands eight texels square on a sixteen-texel pitch, over the region one stamp reaches.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Built through <see cref="PaintCoverage.FromTriangles" /> from UV quads rather than
    ///     from a raster</b>, because that is the path a bound mesh takes — <c>LayerStackMesh</c>
    ///     hands the same rasteriser the model's own triangles. A hand-written raster would measure
    ///     the dilation over a map this test invented.
    /// </remarks>
    static PaintCoverage Grid() {
        List<Vector2> coordinates = [];

        // ⚠ The whole of the stroke's path and not only the first stamp's footprint. A grid that
        // stopped short would leave sixty of the sixty-four stamps over bare atlas, where the first
        // dilation round finds nothing and breaks — which is the flat case wearing an island's name.
        for (var y = 1900; y < 2200; y += 16) {
            for (var x = 900; x < Size; x += 16) {
                Quad(coordinates, x, y, 8);
            }
        }

        return PaintCoverage.FromTriangles(Size, Size, coordinates);
    }

    static void Quad(List<Vector2> into, int x, int y, int size) {
        Vector2 a = new(x / (float)Size, y / (float)Size);
        Vector2 b = new((x + size) / (float)Size, y / (float)Size);
        Vector2 c = new((x + size) / (float)Size, (y + size) / (float)Size);
        Vector2 d = new(x / (float)Size, (y + size) / (float)Size);

        into.Add(a);
        into.Add(b);
        into.Add(c);
        into.Add(a);
        into.Add(c);
        into.Add(d);
    }
}
