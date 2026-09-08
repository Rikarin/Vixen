// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The cached composite, the single undo entry, and symmetry.</summary>
public class PaintCompositeTests {
    static readonly EditorContext NoContext = null!;

    const uint Opaque = 0xFF0000FFu;

    /// <summary>
    ///     ⚠ Doc 48 § D13's latency answer, asserted by counting rather than by looking.
    /// </summary>
    /// <remarks>
    ///     A composite that re-evaluated the stack per stamp would produce exactly the same picture,
    ///     so the picture cannot be the test — which is why this counts the calls and a golden would
    ///     have been useless.
    /// </remarks>
    [Fact]
    public void The_stack_is_evaluated_once_per_stroke_however_many_stamps_the_drag_lays_down() {
        CountingStack stack = new(256, 256, layers: 12);
        PaintTarget target = new(new(256, 256), PaintCoverage.Everywhere(256, 256), stack);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(6f) with { Spacing = 0.05f }, Opaque);

        session.Move(new(20f, 128f));

        for (var step = 1; step <= 200; step++) {
            session.Move(new(20f + step, 128f));
        }

        Assert.Equal(2, stack.Evaluations);
        Assert.Equal(2, session.Composite.Evaluations);
    }

    /// <summary>A stamp recomposites its own rectangle and not the atlas.</summary>
    [Fact]
    public void A_stamp_recomposites_its_footprint_and_nothing_else() {
        CountingStack stack = new(512, 512, layers: 3);
        PaintTarget target = new(new(512, 512), PaintCoverage.Everywhere(512, 512), stack);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(16f), Opaque);

        // ⚠ Zero at pointer-down since #853 — the constructor's whole-atlas pass is gone — but still
        // read rather than assumed, because what this test is about is the *delta* one stamp makes
        // and a baseline it took on faith would be the one thing it could not see change.
        var start = session.Composite.TexelsResolved;

        session.Move(new(256f, 256f));

        var stamp = session.Composite.TexelsResolved - start;

        Assert.True(stamp > 0, "The stamp recomposited nothing, so the composite is not being kept up to date.");
        Assert.True(
            stamp < 512 * 512 / 10,
            $"One 16-texel stamp recomposited {stamp} texels of a 512² atlas."
        );
    }

    /// <summary>
    ///     ⚠ The exit criterion's real shape: a stamp's cost does not know how many layers are under
    ///     it.
    /// </summary>
    [Fact]
    public void A_stamp_costs_the_same_with_twelve_layers_under_it_as_with_one() {
        var (thin, thinResolved) = Stamped(layers: 1);
        var (thick, thickResolved) = Stamped(layers: 12);

        Assert.Equal(thin, thick);
        Assert.Equal(thinResolved, thickResolved);
        Assert.True(thin > 0, "No weights were evaluated at all.");
    }

    /// <summary>And it does not know how big the atlas is either.</summary>
    [Fact]
    public void A_stamp_costs_the_same_on_a_large_atlas_as_on_a_small_one() {
        var (small, smallResolved) = Stamped(layers: 2, size: 256);
        var (large, largeResolved) = Stamped(layers: 2, size: 2048);

        Assert.Equal(small, large);
        Assert.Equal(smallResolved, largeResolved);
    }

    /// <summary>A drag is exactly one undo entry, and it restores the layer to the byte.</summary>
    [Fact]
    public void A_drag_is_one_undo_entry() {
        CountingStack stack = new(128, 128, layers: 2);
        PaintImage layer = new(128, 128);

        layer.Fill(0x0A0B0C0Du);

        var original = (byte[])layer.Texels.Clone();
        PaintTarget target = new(layer, PaintStrokeTests.Islands(128, 128), stack);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(10f) with { Spacing = 0.2f }, Opaque);

        session.Move(new(20f, 64f));

        for (var step = 1; step <= 60; step++) {
            session.Move(new(20f + step, 64f));
        }

        var painted = (byte[])layer.Texels.Clone();
        var command = session.End("Paint");

        Assert.NotNull(command);
        Assert.NotEqual(original, painted);

        command.Undo(NoContext);

        Assert.Equal(original, layer.Texels);

        command.Do(NoContext);

        Assert.Equal(painted, layer.Texels);

        // ⚠ And it never merges. Two strokes are two undos, which is what an artist means.
        Assert.False(command.TryMergeWith(command, out _));
    }

    /// <summary>A click that painted nothing is not an undo entry.</summary>
    [Fact]
    public void A_drag_that_painted_nothing_makes_no_command() {
        CountingStack stack = new(64, 64, layers: 1);
        var raster = new bool[64 * 64];
        PaintTarget target = new(new(64, 64), PaintCoverage.FromRaster(64, 64, raster), stack);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(4f), Opaque);

        session.Move(new(32f, 32f));

        Assert.True(session.IsEmpty);
        Assert.Null(session.End("Paint"));
    }

    /// <summary>Symmetry is a second stroke, and the pair is still one undo entry.</summary>
    /// <remarks>
    ///     ⚠ <b>The mirrored position comes from the caller and this is why.</b> A plane mirrors a
    ///     point in object space and the mirrored point lands in a different UV island, so there is no
    ///     transform of the atlas that performs it. The surface holds the mesh and does the pick; the
    ///     session takes both hits and paints both paths.
    /// </remarks>
    [Fact]
    public void Symmetry_is_a_second_path_and_one_undo_entry() {
        CountingStack stack = new(128, 128, layers: 1);
        PaintImage layer = new(128, 128);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(128, 128), stack, Gutter: 0);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(6f), Opaque);

        for (var step = 0; step <= 20; step++) {
            Span<Vector2> both = [new(20f + step, 40f), new(20f + step, 90f)];

            session.MoveAll(both);
        }

        Assert.Equal(2, session.Strokes);
        Assert.Equal(0xFFu, layer.At(30, 40) >> 24);
        Assert.Equal(0xFFu, layer.At(30, 90) >> 24);

        var command = session.End("Paint");

        Assert.NotNull(command);

        command.Undo(NoContext);

        Assert.Equal(0x00u, layer.At(30, 40) >> 24);
        Assert.Equal(0x00u, layer.At(30, 90) >> 24);
    }

    /// <summary>Turning symmetry on halfway through a drag is refused rather than half-recorded.</summary>
    [Fact]
    public void A_drag_cannot_change_how_many_paths_it_has() {
        CountingStack stack = new(64, 64, layers: 1);
        PaintTarget target = new(new(64, 64), PaintCoverage.Everywhere(64, 64), stack);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(4f), Opaque);

        session.Move(new(20f, 20f));

        Assert.Throws<ArgumentException>(() => {
            Span<Vector2> two = [new(21f, 20f), new(21f, 40f)];

            session.MoveAll(two);
        });
    }

    static (long Weights, long Resolved) Stamped(int layers, int size = 512) {
        CountingStack stack = new(size, size, layers);
        PaintImage layer = new(size, size);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(size, size), stack, Gutter: 0);
        var session = PaintSession.Begin(target, PaintStrokeTests.Hard(12f), Opaque);
        var start = session.Composite.TexelsResolved;

        session.Move(new(size / 2f, size / 2f));

        return (session.WeightsEvaluated, session.Composite.TexelsResolved - start);
    }

    /// <summary>
    ///     ⚠ The live composite's operator list and the bake's cannot drift apart without this going
    ///     red — <a href="https://github.com/Rikarin/Vixen/issues/849">#849</a>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The divergence is deliberate and it is the <em>silent</em> half that is the
    ///         hazard.</b> A seventeenth operator appended to <c>LayerBlendMode</c> — whose two
    ///         directions against <c>TextureBlendMode</c> are already roll-called, so the bake's own
    ///         lists cannot drift — would widen the gap between the pane and the bake by one, in a
    ///         file nobody adding an operator has any reason to open.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A partition rather than two counts.</b> Counting sixteen would pass a list that
    ///         named <c>Copy</c> twice and left an operator out; asserting each enum member appears
    ///         in exactly one of the two is the shape that cannot.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_blend_operator_of_a_compiled_stack_is_declared_reproduced_or_diverged_exactly_once() {
        foreach (var mode in Enum.GetValues<LayerBlendMode>()) {
            var reproduced = PaintComposite.Reproduces.Contains(mode);
            var diverged = PaintComposite.Diverges.Contains(mode);

            Assert.True(
                reproduced != diverged,
                $"LayerBlendMode.{mode} is in {(reproduced ? "both" : "neither")} of PaintComposite.Reproduces "
                + "and PaintComposite.Diverges. The live paint composite reproduces one of the bake's "
                + "operators and diverges from the rest; an operator in neither list is a divergence "
                + "nobody has decided about, and this project's README says otherwise."
            );
        }

        Assert.Equal(
            Enum.GetValues<LayerBlendMode>().Length,
            PaintComposite.Reproduces.Count + PaintComposite.Diverges.Count
        );
    }

    /// <summary>The one operator the pane reproduces is <c>Copy</c>, asserted rather than said.</summary>
    /// <remarks>
    ///     ⚠ <b>Copy's definition is what <c>PaintComposite.Over</c> does at full foreground alpha:
    ///     the foreground, whatever the backdrop was.</b> A composite that grew a second operator
    ///     without moving it out of <c>Diverges</c> would leave the partition green, so the partition
    ///     alone is not enough — this pins which side of it the arithmetic is on.
    /// </remarks>
    [Fact]
    public void Source_over_with_an_opaque_foreground_is_the_foreground_which_is_what_Copy_means() {
        Assert.Equal([LayerBlendMode.Copy], PaintComposite.Reproduces);

        uint[] backdrops = [0x00000000u, 0xFF000000u, 0xFF7F3F1Fu, 0xFFFFFFFFu, 0x80102030u];

        foreach (var backdrop in backdrops) {
            Assert.Equal(0xFF204060u, PaintComposite.Over(backdrop, 0xFF204060u));
        }
    }

    /// <summary>
    ///     ⚠ Why the fifteen are not implemented here: over two transparent halves they would change
    ///     nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The measurement behind <c>PaintComposite.Diverges</c>' remarks, as a test rather
    ///         than as a claim.</b> Every production caller supplies <c>PaintStackImages.Empty</c>,
    ///         and every separable blend operator degenerates to the foreground over a fully
    ///         transparent backdrop — so sixteen operators over these halves are one operator, and
    ///         implementing them would move zero texels for any stack anybody can currently open.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is the halves that make this true and not the join</b> — so the claim rests
    ///         on <em>what production supplies</em>, which this case does not read: it builds its
    ///         own <c>PaintStackImages.Empty</c>, so <c>PaintSurface.Target</c> could start handing
    ///         out real slices and this would stay green for ever.
    ///         <c>PaintSurfaceTests.The_surface_supplies_two_blank_halves_which_is_what_makes_the_degeneracy_a_claim</c>
    ///         is the assertion about the supply, and it is the one that goes red on the day the
    ///         fifteen stop being unobservable.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Over_two_empty_halves_the_join_is_the_painted_layer_so_no_operator_could_change_a_texel() {
        PaintImage layer = new(8, 8);

        // ⚠ Partial alphas included, not only opaque texels. `Over` divides the mixed colour by the
        // resulting alpha, so a half-covered texel is the one that would expose a rounding error in
        // the round trip through the packed bytes — and it is the case a real stroke's edge is made
        // of.
        for (var index = 0; index < layer.Width * layer.Height; index++) {
            layer[index] = (uint)(((index * 4) % 256) << 24) | (uint)(index * 0x00030507);
        }

        PaintComposite composite = new(PaintStackImages.Empty(8, 8), layer);

        composite.ResolveAll();

        for (var index = 0; index < layer.Width * layer.Height; index++) {
            Assert.Equal(layer[index], composite.Result[index]);
        }
    }

    /// <summary>A stack that says how much work it was asked for.</summary>
    sealed class CountingStack : IPaintStack {
        readonly int width;
        readonly int height;
        readonly int layers;

        public CountingStack(int width, int height, int layers) {
            this.width = width;
            this.height = height;
            this.layers = layers;
        }

        public int Evaluations { get; private set; }

        public PaintImage Evaluate(PaintStackSlice slice) {
            Evaluations++;

            PaintImage image = new(width, height);

            // Every layer costs a pass, which is what makes "twelve layers under it" a number that
            // would show up in a measurement if the composite were evaluated per stamp.
            for (var layer = 0; layer < layers; layer++) {
                image.Fill(0x20000000u | (uint)layer);
            }

            return image;
        }
    }
}
