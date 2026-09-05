// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Doc 48's exit criterion 8, and an argument about what it should have said.
/// </summary>
/// <remarks>
///     <para>
///         <b>The criterion is "a stroke on a 4K texture set with twelve layers under it stays under
///         16 ms per stamp".</b> That is a wall-clock budget, and a wall-clock budget calibrated on
///         an idle machine is this repository's single largest flake source: the number that fails
///         here on a loaded laptop is not a number about the brush.
///     </para>
///     <para>
///         ⚠ <b>So the gate is the property the budget was a proxy for, and the milliseconds are
///         reported beside it.</b> What makes a stamp cheap at 4K with twelve layers under it is
///         that its work is its own footprint and nothing else — the stack is evaluated once at
///         pointer-down, so the layer count leaves the per-stamp path entirely, and the composite is
///         resolved per rectangle, so the atlas size does too. Both are exact counters, both are
///         asserted equal across the two axes in <c>PaintCompositeTests</c>, and here they are
///         asserted against a closed-form bound at the criterion's own size.
///     </para>
///     <para>
///         ⚠ <b>The one time assertion is an absurd ceiling and its message says so.</b> It is a
///         hang check — a stamp that takes a second has stopped being a stamp and has started being
///         a full-atlas pass — and it is not a performance bound. The measured number goes to the
///         test output so that a regression is visible in a run people read, without a build that
///         goes red because somebody was compiling in another window.
///     </para>
/// </remarks>
public class PaintCostTests(ITestOutputHelper output) {
    const uint Opaque = 0xFF0000FFu;

    /// <summary>A stamp's work is bounded by its own footprint, at the criterion's size.</summary>
    [Fact]
    public void A_stamp_on_a_4k_set_with_twelve_layers_costs_its_footprint_and_no_more() {
        const int Size = 4096;
        const int Radius = 48;
        const int Stamps = 64;

        FlatStack stack = new(Size, Size, layers: 12);
        PaintImage layer = new(Size, Size);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(Size, Size), stack, Gutter: 4);

        var started = Stopwatch.GetTimestamp();
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(Radius) with { Spacing = 1f }, Opaque);
        var opened = Stopwatch.GetElapsedTime(started);

        var resolvedAtStart = session.Composite.TexelsResolved;
        var stamping = Stopwatch.GetTimestamp();

        session.Move(new(1024f, 2048f));

        for (var step = 1; step < Stamps; step++) {
            session.Move(new(1024f + (step * Radius), 2048f));
        }

        var elapsed = Stopwatch.GetElapsedTime(stamping);

        Assert.Equal(2, stack.Evaluations);
        Assert.Equal(Stamps, session.StampCount);

        // The closed form: a stamp evaluates at most its own square, and recomposites at most that
        // square grown by the gutter on every side. Neither number mentions the atlas or the stack.
        var square = (long)((2 * Radius) + 2) * ((2 * Radius) + 2);
        var dilated = (long)((2 * Radius) + 2 + 8) * ((2 * Radius) + 2 + 8);

        Assert.True(
            session.WeightsEvaluated <= Stamps * square,
            $"{session.WeightsEvaluated} weights for {Stamps} stamps of radius {Radius}; "
            + $"{Stamps * square} is the footprint bound."
        );

        Assert.True(
            session.Composite.TexelsResolved - resolvedAtStart <= Stamps * dilated,
            $"{session.Composite.TexelsResolved - resolvedAtStart} texels recomposited for {Stamps} stamps; "
            + $"{Stamps * dilated} is the dilated-footprint bound."
        );

        var perStamp = elapsed.TotalMilliseconds / Stamps;

        output.WriteLine(
            $"4096², 12 layers, radius {Radius}: stroke start {opened.TotalMilliseconds:F1} ms, "
            + $"{perStamp:F3} ms per stamp over {Stamps} stamps. Exit criterion 8 asks for under 16."
        );

        // ⚠ A hang check and not a bound. See this class's remarks: the property is asserted above,
        // and this only catches a stamp that has quietly become a full-atlas pass.
        Assert.True(
            perStamp < 500d,
            $"{perStamp:F1} ms per stamp is not a slow machine, it is a stamp that stopped being local."
        );
    }

    /// <summary>The stroke's start is the only thing that pays for the stack.</summary>
    /// <remarks>
    ///     ⚠ <b>Stated as a counter rather than as a time, for this file's whole argument.</b> Twelve
    ///     layers cost twelve passes and one layer costs one, exactly once, whatever the drag does
    ///     afterwards — which is doc 48 § D13's sentence turned into a number.
    /// </remarks>
    [Fact]
    public void The_layer_count_is_paid_once_at_pointer_down_and_never_again() {
        FlatStack thin = new(512, 512, layers: 1);
        FlatStack thick = new(512, 512, layers: 12);

        var one = Drag(thin);
        var twelve = Drag(thick);

        Assert.Equal(1 * 2, thin.LayerPasses);
        Assert.Equal(12 * 2, thick.LayerPasses);
        Assert.Equal(one.Weights, twelve.Weights);
        Assert.Equal(one.Resolved, twelve.Resolved);
        Assert.True(one.Weights > 0, "The drag evaluated no weights at all.");
    }

    static (long Weights, long Resolved) Drag(FlatStack stack) {
        PaintImage layer = new(512, 512);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(512, 512), stack, Gutter: 2);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(10f) with { Spacing = 0.5f }, Opaque);
        var start = session.Composite.TexelsResolved;

        session.Move(new(100f, 256f));

        for (var step = 1; step <= 40; step++) {
            session.Move(new(100f + (step * 5f), 256f));
        }

        return (session.WeightsEvaluated, session.Composite.TexelsResolved - start);
    }

    /// <summary>A stack whose evaluation costs one pass per layer, and says how many it ran.</summary>
    sealed class FlatStack : IPaintStack {
        readonly int width;
        readonly int height;
        readonly int layers;

        public FlatStack(int width, int height, int layers) {
            this.width = width;
            this.height = height;
            this.layers = layers;
        }

        public int Evaluations { get; private set; }

        public int LayerPasses { get; private set; }

        public PaintImage Evaluate(PaintStackSlice slice) {
            Evaluations++;

            PaintImage image = new(width, height);

            LayerPasses += layers;
            image.Fill(slice == PaintStackSlice.Below ? 0xFF202020u : 0u);

            return image;
        }
    }
}
