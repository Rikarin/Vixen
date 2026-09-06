// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
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
///     <para>
///         ⚠ <b>The closed form used to exclude the dominant loop, which is the instrument failing
///         rather than the code</b> — <a href="https://github.com/Rikarin/Vixen/issues/871">#871</a>.
///         <c>WeightsEvaluated</c> is incremented only for a covered texel inside a stamp's
///         footprint; the seam dilation scans <c>gutter</c> rounds of that footprint grown by the
///         gutter on every side, which at radius 48 is roughly 45 000 texels against 9 600 weights.
///         So the gate measured the cheaper half and would have stayed green through a dilation that
///         had quietly become quadratic. <c>PaintSession.TexelsScanned</c> counts both loops and the
///         bound below names both terms.
///     </para>
/// </remarks>
public class PaintCostTests(ITestOutputHelper output) {
    const uint Opaque = 0xFF0000FFu;

    /// <summary>An undo entry's <c>Do</c> and <c>Undo</c> read no context, and this says so.</summary>
    static readonly EditorContext NoContext = null!;

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
        const int Gutter = 4;

        var square = (long)((2 * Radius) + 2) * ((2 * Radius) + 2);
        var dilated = (long)((2 * Radius) + 2 + (2 * Gutter)) * ((2 * Radius) + 2 + (2 * Gutter));

        Assert.True(
            session.WeightsEvaluated <= Stamps * square,
            $"{session.WeightsEvaluated} weights for {Stamps} stamps of radius {Radius}; "
            + $"{Stamps * square} is the footprint bound."
        );

        // ⚠ The bound the criterion is actually about — #871. The weight count above omits the
        // dilation scan, which is the larger of a stamp's two loops; this names both terms, and
        // neither mentions the atlas or the stack either.
        var scanned = Stamps * (square + (Gutter * dilated));

        Assert.True(
            session.TexelsScanned <= scanned,
            $"{session.TexelsScanned} texels scanned for {Stamps} stamps of radius {Radius} at gutter "
            + $"{Gutter}; {scanned} is the footprint-plus-dilation bound."
        );

        // ⚠ The instrument, and it is what says the new bound is not the old one renamed. Over
        // `Everywhere` coverage every footprint texel is covered, so the weight count *is* the
        // footprint loop exactly — and the remainder is the dilation scan, entire. It has to be the
        // larger of the two, or the counter that missed it was not missing much.
        var dilationScan = session.TexelsScanned - session.WeightsEvaluated;

        Assert.True(
            dilationScan > session.WeightsEvaluated,
            $"the dilation scanned {dilationScan} texels against {session.WeightsEvaluated} weights. If the "
            + "smaller loop is the bigger number, the gutter is not being walked at all."
        );

        output.WriteLine(
            $"{session.WeightsEvaluated} weights and {dilationScan} dilation-scan texels over {Stamps} stamps "
            + $"— the loop the old closed form did not count is {(double)dilationScan / session.WeightsEvaluated:F1}× "
            + "the one it did."
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

    /// <summary>
    ///     ⚠ Two mirrored paths cost the same however far apart they are, which is what #871 found
    ///     they did not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A closed-form oracle rather than a bound, because the swept parameter must not
    ///         appear in the answer at all.</b> The separation between the two mirrors is the only
    ///         thing that differs between the two runs; a composite that resolved the union of the
    ///         two dirty rectangles pays for the bounding box between them, so the number would grow
    ///         as the square of the separation. A composite that resolves the regions pays for two
    ///         stamps, twice, whatever the geometry — so the two runs must be <em>equal</em>, and
    ///         there is nothing here to re-bless when the brush changes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Symmetry is the worst case and not an edge case, which is why this is worth a
    ///         test of its own.</b> A mirror plane through the middle of a model puts its two hits
    ///         in different UV islands, and an atlas packer has no reason to put those islands near
    ///         each other. Painting down the seam of a symmetric model is the ordinary use of the
    ///         feature.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_mirrored_paths_cost_the_same_however_far_apart_the_atlas_puts_them() {
        var near = Mirrored(separation: 64f);
        var far = Mirrored(separation: 900f);

        Assert.Equal(near.Resolved, far.Resolved);
        Assert.Equal(near.Weights, far.Weights);
        Assert.True(near.Resolved > 0, "nothing was recomposited at all, so this proves nothing.");

        // The instrument: the far pair really is far apart, so a union would have been enormous.
        // 900² is a hundred times two stamps of radius 12, and the assertion above would not have
        // survived it.
        Assert.True(
            far.Resolved < 900 * 900 / 4,
            $"{far.Resolved} texels for two stamps 900 apart on a 1024² atlas — that is the bounding box "
            + "between them rather than the two footprints."
        );
    }

    /// <summary>
    ///     ⚠ Undo and redo of two mirrored paths cost the same however far apart they are, which is
    ///     the half #871 did not reach.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The same oracle as the test above, driven through the undo entry instead of
    ///         through the pointer —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/891">#891</a>.</b>
    ///         <c>PaintStrokeCommand</c> rebuilt exactly the bounding-box union that #871 removed
    ///         from <c>PaintSession.MoveAll</c>: <c>rect.Union(stroke.Undo())</c> over every stroke
    ///         and one resolve on the result. So an artist who painted a symmetric model and pressed
    ///         Ctrl+Z paid the whole-atlas cost the drag itself had just stopped paying.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Per stroke and deliberately not per stamp.</b> The rectangles a stroke's stamps
    ///         earned are handed to the caller as they happen and kept by nobody;
    ///         <c>PaintStroke.Undo</c> and <c>PaintStrokeRedo.Redo</c> return the stroke's own
    ///         rectangle, which already exists. Recording every stamp's rectangle for the life of an
    ///         undo entry would add a list per stroke to a record whose size is already #850's
    ///         complaint, to save re-compositing inside one path's own swept area — and undo runs
    ///         once where a stamp runs hundreds of times. Symmetry is the case where the union is
    ///         unbounded, and symmetry is exactly what the plural is for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Undoing_two_mirrored_paths_costs_the_same_however_far_apart_the_atlas_puts_them() {
        var near = MirroredUndo(separation: 64f);
        var far = MirroredUndo(separation: 900f);

        Assert.Equal(near.Undone, far.Undone);
        Assert.Equal(near.Redone, far.Redone);
        Assert.True(near.Undone > 0, "nothing was recomposited on undo at all, so this proves nothing.");
        Assert.True(near.Redone > 0, "nothing was recomposited on redo at all, so this proves nothing.");

        // The instrument, as above: the far pair really is far apart, so a union would have been
        // enormous and the equality could not have survived it.
        Assert.True(
            far.Undone < 900 * 900 / 4,
            $"{far.Undone} texels to undo two strokes 900 apart on a 1024² atlas — that is the bounding "
            + "box between them rather than the two rectangles."
        );
    }

    /// <summary>⚠ Pointer-down does not composite the atlas, which is #853's 1.9 seconds.</summary>
    /// <remarks>
    ///     <b>A counter, for this file's whole argument.</b> The measurement in #853 is a wall clock
    ///     and the property under it is that starting a stroke touches no texel of the result:
    ///     everything the composite has at pointer-down is the two slices, which the stack owes it
    ///     whatever this type does. The milliseconds are reported beside it as evidence.
    /// </remarks>
    [Fact]
    public void Beginning_a_stroke_composites_no_texels_at_all() {
        const int Size = 4096;

        FlatStack stack = new(Size, Size, layers: 12);
        PaintImage layer = new(Size, Size);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(Size, Size), stack, Gutter: 4);

        var started = Stopwatch.GetTimestamp();
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(48f), Opaque);
        var opened = Stopwatch.GetElapsedTime(started);

        Assert.Equal(2, stack.Evaluations);
        Assert.Equal(0L, session.Composite.TexelsResolved);

        // ⚠ The instrument, and it is the half that could not be false on its own. `ResolveAll` is
        // the pass the constructor used to make, so a build in which the composite has quietly
        // stopped compositing at all would satisfy the assertion above and fail this one. Timed, so
        // the number the constructor no longer pays is in the same output as the number it does.
        var all = Stopwatch.GetTimestamp();

        Assert.Equal((long)Size * Size, session.Composite.ResolveAll().Area);

        var resolving = Stopwatch.GetElapsedTime(all);

        Assert.Equal((long)Size * Size, session.Composite.TexelsResolved);

        output.WriteLine(
            $"4096², 12 layers: stroke start {opened.TotalMilliseconds:F1} ms, "
            + $"{session.Composite.TexelsResolved - ((long)Size * Size)} texels composited by it. The "
            + $"whole-atlas pass it used to make costs {resolving.TotalMilliseconds:F1} ms; what is left is "
            + "the two slice evaluations, which are the stack's own cost and #849's."
        );
    }

    /// <summary>What one undo and the redo after it recomposite, for a drag with one mirror.</summary>
    static (long Undone, long Redone) MirroredUndo(float separation) {
        const int Size = 1024;

        FlatStack stack = new(Size, Size, layers: 2);
        PaintImage layer = new(Size, Size);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(Size, Size), stack, Gutter: 2);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(12f), Opaque);

        Span<Vector2> both = [new(60f, 512f), new(60f + separation, 512f)];

        session.MoveAll(both);

        var command = session.End("Paint");

        Assert.NotNull(command);

        var painted = session.Composite.TexelsResolved;

        command.Undo(NoContext);

        var undone = session.Composite.TexelsResolved - painted;

        command.Do(NoContext);

        return (undone, session.Composite.TexelsResolved - painted - undone);
    }

    static (long Weights, long Resolved) Mirrored(float separation) {
        const int Size = 1024;

        FlatStack stack = new(Size, Size, layers: 2);
        PaintImage layer = new(Size, Size);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(Size, Size), stack, Gutter: 2);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(12f), Opaque);
        var start = session.Composite.TexelsResolved;

        Span<Vector2> both = [new(60f, 512f), new(60f + separation, 512f)];

        session.MoveAll(both);

        return (session.WeightsEvaluated, session.Composite.TexelsResolved - start);
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
