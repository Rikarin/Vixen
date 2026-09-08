// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Painting;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     The brush alpha, and the rotation that only reaches a texel because there is one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § M9's brush row, the two settings of it that were declared and unreachable —
///         <a href="https://github.com/Rikarin/Vixen/issues/1083">#1083</a>.</b> They are asserted
///         together because they are one feature: <c>PaintBrush.KernelFor</c> picks
///         <c>BrushShape.Circle</c> for a null alpha and a disc turned is a disc, so a rotation
///         asserted over a round brush would be a test that could not tell the feature from its
///         absence.
///     </para>
///     <para>
///         ⚠ <b>Which is why the round brush is in these tests as the instrument rather than only as
///         a case.</b> Each claim about a masked stamp is paired with the same claim about a round
///         one, and the round one is asserted to come out the <em>other</em> way. A test that only
///         said "turning the brush changed the painting" would have passed on the day the rotation
///         was wired to nothing, because a jitter or a spacing drift would satisfy it too.
///     </para>
/// </remarks>
public class PaintAlphaTests(ITestOutputHelper output) {
    const uint Opaque = 0xFF0000FFu;

    /// <summary>An undo entry's <c>Do</c> and <c>Undo</c> read no context, and this says so.</summary>
    static readonly EditorContext NoContext = null!;

    // --- The mask ------------------------------------------------------------

    /// <summary>A mask reads its image, and reads nothing outside the unit square.</summary>
    /// <remarks>
    ///     ⚠ <b>The zero outside is the assertion with a defect behind it.</b> A stamp's footprint
    ///     rectangle is <c>√2</c> radii, so every masked stamp samples a region outside its own
    ///     square on every stamp; a mask that clamped its address instead would smear its border row
    ///     across that region and quietly change the shape of every brush.
    /// </remarks>
    [Fact]
    public void A_mask_reads_its_image_and_is_zero_outside_the_unit_square() {
        PaintImage image = new(2, 2);

        image[0] = PaintImage.Pack(0f, 0f, 0f, 1f);
        image[1] = PaintImage.Pack(0f, 0f, 0f, 0f);
        image[2] = PaintImage.Pack(0f, 0f, 0f, 0f);
        image[3] = PaintImage.Pack(0f, 0f, 0f, 1f);

        PaintImageMask mask = new(image);

        Assert.Equal(1f, mask.Sample(new(0.25f, 0.25f)), 2);
        Assert.Equal(0f, mask.Sample(new(0.75f, 0.25f)), 2);

        // Halfway between all four taps: two of them are one and two are nought.
        Assert.Equal(0.5f, mask.Sample(new(0.5f, 0.5f)), 2);

        foreach (var outside in new[] {
                     new Vector2(-0.01f, 0.5f), new(1.01f, 0.5f), new(0.5f, -0.01f), new(0.5f, 1.01f),
                     new(float.NaN, 0.5f)
                 }) {
            Assert.Equal(0f, mask.Sample(outside));
        }
    }

    /// <summary>Every name the shelf offers resolves, and Round is the absence of a mask.</summary>
    [Fact]
    public void The_shelf_resolves_every_name_it_offers_and_nothing_else() {
        Assert.Contains(PaintAlphas.Round, PaintAlphas.Names);
        Assert.Null(PaintAlphas.Find(PaintAlphas.Round));
        Assert.Null(PaintAlphas.Find("Nothing this build ships"));
        Assert.Null(PaintAlphas.Find(null));

        foreach (var name in PaintAlphas.Names.Where(name => name != PaintAlphas.Round)) {
            var mask = PaintAlphas.Find(name);

            Assert.NotNull(mask);

            // ⚠ Every shipped shape reaches its own corner, and that is not decoration: a mask whose
            // support fitted inside the inscribed disc would be a shape whose rotation an artist
            // could not see, which is the state the whole feature was in.
            Assert.True(
                mask.Sample(new(0.5f, 0.5f)) > 0.5f,
                $"{name} is empty at its own centre, so it is a brush that paints nothing."
            );

            Assert.True(
                Enumerable.Range(0, 64).Any(step => mask.Sample(new(step / 64f, step / 64f)) > 0.5f),
                $"{name} covers none of its own diagonal, so it cannot differ from a disc."
            );
        }
    }

    // --- The stamp -----------------------------------------------------------

    /// <summary>A square alpha paints corners the disc inside it never reaches.</summary>
    /// <remarks>
    ///     ⚠ <b>The end-to-end half of #1083's arithmetic.</b> Before it,
    ///     <c>TerrainBrush.WeightAt</c> clipped every stamp to its radius whatever the shape, so a
    ///     mask that is one over its whole square painted exactly what a circle painted — and the
    ///     round brush in the second half of this test is what says the assertion can tell them
    ///     apart.
    /// </remarks>
    [Fact]
    public void A_square_alpha_paints_the_corner_that_a_round_brush_cannot_reach() {
        const int Size = 128;
        const float Radius = 20f;

        // Offset (18, 18) from the centre: 25.5 texels away, so outside a disc of radius 20, and 18
        // across the square, so inside one.
        var corner = (78 * Size) + 78;

        var square = Paint(Brush(Radius, PaintAlphas.Square), Size);
        var round = Paint(Brush(Radius, PaintAlphas.Round), Size);

        Assert.Equal(0xFFu, square[corner] >> 24);
        Assert.Equal(0x00u, round[corner] >> 24);

        // And the two are the same brush in the middle, so the difference above is the shape rather
        // than one of them having painted nothing at all.
        Assert.Equal(0xFFu, square[(60 * Size) + 60] >> 24);
        Assert.Equal(0xFFu, round[(60 * Size) + 60] >> 24);
    }

    /// <summary>Turning a chisel moves which texels a stroke lands on. A round brush's does not.</summary>
    [Fact]
    public void Turning_a_masked_stamp_moves_the_painting_and_turning_a_round_one_does_not() {
        const int Size = 128;
        const float Radius = 24f;

        var upright = Painted(Paint(Brush(Radius, PaintAlphas.Chisel), Size));
        var turned = Painted(Paint(Brush(Radius, PaintAlphas.Chisel, MathF.PI / 2f), Size));

        Assert.NotEmpty(upright);

        var moved = upright.Except(turned).Union(turned.Except(upright)).Count();

        Assert.True(
            moved > upright.Count / 2,
            $"a quarter turn moved {moved} of {upright.Count} texels. A rotation that moves a handful "
            + "is a spacing drift, not a turned stamp."
        );

        // ⚠ The instrument. The same two angles over a round brush have to paint the *same* texels,
        // or this test is measuring something other than the stamp's rotation — and "the same" is
        // exact rather than approximate, because a disc turned is the same disc.
        Assert.True(
            Painted(Paint(Brush(Radius, PaintAlphas.Round), Size))
                .SetEquals(Painted(Paint(Brush(Radius, PaintAlphas.Round, MathF.PI / 2f), Size))),
            "turning a round brush moved a texel, so what moved above was not the stamp's shape."
        );
    }

    // --- The drag ------------------------------------------------------------

    /// <summary>Two overlapping masked paths are still one undo entry, exact both ways.</summary>
    /// <remarks>
    ///     ⚠ <b>The hard case for the record rather than the easy one.</b> A mask, an angle and three
    ///     jitters each change which texels a stamp claims, and the drag's <c>PaintOriginal</c> — the
    ///     map that exists because two mirrored paths overlapping in the atlas each record the
    ///     other's paint — is sized from those claims. Two paths eight texels apart overlap almost
    ///     everywhere, which is the arrangement that map exists for; a disc on an empty 512 would
    ///     exercise none of it.
    /// </remarks>
    [Fact]
    public void A_masked_symmetric_drag_is_one_undo_entry_and_restores_every_texel() {
        const int Size = 192;

        PaintImage layer = new(Size, Size);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(Size, Size), new BlankStack(Size), Gutter: 4);

        var brush = Brush(24f, PaintAlphas.Chisel, 0.6458f) with {
            Spacing = 0.2f,
            PositionJitter = 0.4f,
            AngleJitter = 0.5f,
            SizeJitter = 0.3f
        };

        var before = (byte[])layer.Texels.Clone();
        var session = PaintSession.Begin(target, brush, Opaque);

        for (var step = 0; step < 6; step++) {
            Span<Vector2> paths = [new(70f + (step * 9f), 96f), new(78f + (step * 9f), 96f)];

            session.MoveAll(paths);
        }

        var command = session.End("Paint");

        Assert.NotNull(command);
        Assert.Equal(2, session.Strokes);

        var painted = (byte[])layer.Texels.Clone();

        Assert.False(before.AsSpan().SequenceEqual(painted), "the drag painted nothing, so nothing is proved.");

        command.Undo(NoContext);

        Assert.True(
            before.AsSpan().SequenceEqual(layer.Texels),
            "one undo left paint behind, so the drag was more than one entry's worth of record."
        );

        command.Do(NoContext);

        Assert.True(painted.AsSpan().SequenceEqual(layer.Texels), "the redo did not reproduce the drag.");
    }

    // --- The cost ------------------------------------------------------------

    /// <summary>A masked stamp on the criterion's own set costs its square, which is twice a disc's.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48's exit criterion 8 with the shape that costs the most.</b>
    ///         <c>PaintCostTests</c> measures a disc, whose footprint rectangle is <c>2r</c> a side;
    ///         a masked stamp's is <c>2√2·r</c>, because the bound has to hold whichever way the
    ///         square is turned. So the closed form here is the disc's doubled, and both terms still
    ///         mention neither the atlas nor the stack.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the doubling is asserted rather than only allowed for</b>, because a bound
    ///         twice the size of the thing it bounds is a bound that would stay green through a stamp
    ///         that had stopped growing with its own shape.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_masked_stamp_on_a_4k_set_costs_its_square_and_the_square_is_twice_a_discs() {
        const int Size = 4096;
        const int Radius = 48;
        const int Stamps = 32;

        // ⚠ The round one first, and the order is the measurement's. Whichever runs first pays the
        // jit, and a masked stamp measured first read four times a round one where it reads a little
        // over one — a difference entirely in the warm-up. The counters below are unaffected either
        // way, which is exactly why they and not the milliseconds are what is asserted.
        var round = Cost(Size, Radius, Stamps, PaintAlphas.Round);
        var masked = Cost(Size, Radius, Stamps, PaintAlphas.Chisel);

        // The closed form: a masked stamp evaluates at most the square that holds its own square
        // however it is turned, √2 radii to a side, plus a texel of rounding at each edge.
        var side = (2d * MathF.Sqrt(2f) * Radius) + 2d;
        var bound = (long)(Stamps * side * side);

        Assert.True(
            masked.Weights <= bound,
            $"{masked.Weights} weights for {Stamps} masked stamps of radius {Radius}; {bound} is the "
            + "√2 footprint bound."
        );

        // ⚠ The instrument: the masked bound is not the round one renamed. A disc's footprint is
        // 2r a side and a mask's is 2√2·r, so the ratio is 2 — and if it came out at 1 the stamp
        // would be being sized off the brush's shape at one place and the stamp's at another, which
        // is #1064 all over again.
        Assert.InRange(masked.Weights / (double)round.Weights, 1.8d, 2.2d);

        // ⚠ The milliseconds are evidence beside the counter and not a second gate — `PaintCostTests`'
        // own rule — and here they are worth even less than usual: this test allocates two 4K atlases
        // and their composites, so what the clock mostly measures is the collector. Read the weights.
        output.WriteLine(
            $"radius {Radius}: {masked.Weights / Stamps} weights a masked stamp against "
            + $"{round.Weights / Stamps} round, and {masked.Elapsed.TotalMilliseconds / Stamps:0.00} ms a "
            + $"stamp against {round.Elapsed.TotalMilliseconds / Stamps:0.00} — debug, and dominated by "
            + "allocation."
        );

        // A hang check and not a performance bound — `PaintCostTests`' own remark. A stamp that takes
        // a second has stopped being a stamp and has become a full-atlas pass.
        Assert.True(
            masked.Elapsed.TotalSeconds < Stamps,
            $"{masked.Elapsed.TotalSeconds:0.0} s for {Stamps} stamps is not a stamp cost at all."
        );
    }

    // --- Fixtures ------------------------------------------------------------

    static PaintBrush Brush(float radius, string alpha, float angle = 0f) =>
        PaintStrokeTests.Hard(radius) with {
            Alpha = PaintAlphas.Find(alpha),
            Rotation = BrushRotation.Fixed,
            Angle = angle
        };

    /// <summary>One stamp in the middle of an empty square image.</summary>
    static PaintImage Paint(PaintBrush brush, int size) {
        PaintImage image = new(size, size);
        PaintStroke stroke = new(image, PaintCoverage.Everywhere(size, size), brush, Opaque, gutter: 0);

        stroke.MoveTo(new(size / 2f, size / 2f));

        return image;
    }

    /// <summary>Which texels an image has paint on.</summary>
    static HashSet<int> Painted(PaintImage image) {
        HashSet<int> texels = [];

        for (var index = 0; index < image.Width * image.Height; index++) {
            if (image[index] >> 24 != 0u) {
                texels.Add(index);
            }
        }

        return texels;
    }

    static (long Weights, TimeSpan Elapsed) Cost(int size, float radius, int stamps, string alpha) {
        PaintImage layer = new(size, size);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(size, size), new BlankStack(size), Gutter: 4);

        var session = PaintSession.Begin(target, Brush(radius, alpha, 0.5f) with { Spacing = 1f }, Opaque);

        // ⚠ Two hundred megabytes of atlas per call, so whichever of the two ran second used to be
        // measured while the other's was still uncollected — which moved the number by four times
        // and had nothing to do with the brush.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var started = Stopwatch.GetTimestamp();

        for (var step = 0; step < stamps; step++) {
            session.Move(new(1024f + (step * radius), 2048f));
        }

        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal(stamps, session.StampCount);

        return (session.WeightsEvaluated, elapsed);
    }

    /// <summary>A stack with nothing in it, which is what a cost measured per stamp wants.</summary>
    sealed class BlankStack(int size) : IPaintStack {
        public PaintImage Evaluate(PaintStackSlice slice) => new(size, size);
    }
}
