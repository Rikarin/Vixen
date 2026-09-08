// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Doc 48's exit criterion 8, measured on the hard case through the 3D pane rather than around
///     it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The criterion is "a stroke on a 4K texture set with twelve layers under it stays under
///         16 ms per stamp", and <c>PaintCostTests</c> already gates the atlas half of it.</b> What a
///         3D view adds to the per-stamp path is the <em>picture</em>: the pointer is over a render
///         of the model, and every stamp has to put its texels back on that render. A pane that
///         rasterised the mesh again per pointer move would satisfy every counter that suite has and
///         would still miss the criterion by an order of magnitude, because the model's triangle
///         count would be back in the inner loop.
///     </para>
///     <para>
///         ⚠ <b>Three counters and a clock, in that order of authority.</b> The stack is evaluated
///         twice for the whole drag; the mesh is rasterised once; the pane pixels shaded per stamp
///         are bounded by the brush's own disc rather than by the pane. All three are exact and none
///         of them is a wall clock — the milliseconds are reported beside them as evidence, with an
///         absurd ceiling that is a hang check and says so, which is this repository's rule for a
///         budget calibrated on an idle machine.
///     </para>
///     <para>
///         ⚠ <b>And the bound is in pane pixels on purpose, because that is the quantity that has to
///         stop depending on the two axes.</b> A stamp's shaded count here is a function of the
///         brush's radius on screen and of nothing else: not of the twelve layers, not of the 4096
///         texels, not of the model's triangle count. Asserting it against a fraction of the pane
///         would be a weaker claim that a full-pane scan could still satisfy at these sizes.
///     </para>
/// </remarks>
public class PaintMeshCostTests(ITestOutputHelper output) {
    const uint Opaque = 0xFF0000FFu;

    /// <summary>A projected drag over a 4K set with twelve layers pays for neither the stack nor the mesh.</summary>
    [Fact]
    public void A_projected_stamp_on_a_4k_set_with_twelve_layers_redraws_neither_the_stack_nor_the_mesh() {
        const int Size = 4096;
        const int Moves = 64;
        const int Wide = 1280;
        const int Tall = 720;
        // ⚠ Five *screen* pixels, and the number is derived rather than chosen. `PaintCostTests`
        // measures the criterion at a 48-texel brush; on this framing the quad's two world units
        // cover the 4096-texel atlas and about 409 pane pixels, so one pane pixel is roughly ten
        // texels — and a 48-pixel brush would be a 480-texel one, which is a tenth of the atlas and
        // measures a gesture no artist makes. The assertion below states the resulting footprint so
        // that a change to the framing cannot silently move what is being measured.
        const float Radius = 5f;

        var mesh = Quad();
        PaintCamera camera = new();

        camera.Frame(mesh.Bounds);

        PaintMeshRaster raster = new();

        raster.Draw(mesh, camera, Wide, Tall);

        FlatStack stack = new(Size, Size, layers: 12);
        PaintImage layer = new(Size, Size);
        PaintTarget target = new(layer, PaintCoverage.Everywhere(Size, Size), stack, Gutter: 4);

        raster.Texture(layer);

        Assert.True(raster.Covered > 100_000, $"{raster.Covered} pane pixels covered — the model is not on screen.");

        PaintProjector projector = new(mesh, Size, Size);

        // Straight down the middle of the pane, which on this fixture is the middle of the atlas.
        var eye = camera.Eye(Tall);

        Assert.True(
            projector.Begin(eye, camera.Ray(Wide * 0.5f, Tall * 0.5f, Wide, Tall), Radius, out var footprint),
            "the first ray missed the model."
        );

        Assert.True(footprint.IsMeasurable, "the footprint is not a brush.");
        Assert.InRange(footprint.Radius, 30f, 90f);

        var session = PaintSession.Begin(
            target,
            PaintStrokeTests.Hard(footprint.Radius) with {
                Spacing = 1f,
                Aspect = footprint.Aspect,
                AspectAngle = footprint.Angle
            },
            Opaque
        );

        var drawn = raster.Renders;
        var before = raster.Shaded;
        List<PaintRect> dirtied = [];
        var started = Stopwatch.GetTimestamp();

        for (var step = 0; step < Moves; step++) {
            // One brush radius per move, which is what makes a move a stamp — the same relation
            // `PaintCostTests` uses, in pane pixels rather than in texels.
            var ray = camera.Ray((Wide * 0.5f) - 160f + (step * Radius), Tall * 0.5f, Wide, Tall);

            session.MoveAll(projector.Resolve(ray), dirtied);

            foreach (var rect in dirtied) {
                raster.Retexture(session.Composite.Result, rect);
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var shaded = raster.Shaded - before;

        // ⚠ The property the 16 ms was a proxy for, on the atlas side — the stack is evaluated in the
        // session's constructor and never again, so the layer count is not in the per-stamp path.
        Assert.Equal(2, session.Composite.Evaluations);
        Assert.Equal(2, stack.Evaluations);

        // ⚠ And on the pane side: sixty-four stamps drew no geometry, so the model's triangle count
        // is not in the per-stamp path. ⚠ This is an assertion about `Retexture` and not about the
        // pane, and the difference matters — the loop above calls `Retexture` directly, so a view
        // that rendered on every pointer move would leave it green. That half is asserted where it
        // can fail, in `PaintMeshViewTests`, by counting whole-picture uploads across a real drag.
        Assert.Equal(drawn, raster.Renders);

        // The instrument: a drag that laid one stamp, or a projector that missed the mesh, would
        // satisfy every bound below for reasons that have nothing to do with either cache.
        Assert.True(
            session.StampCount >= Moves - 1,
            $"{session.StampCount} stamps from {Moves} projected moves — the rays are not reaching the mesh."
        );

        Assert.True(shaded > 0L, "no pane pixel was reshaded, so the model never showed the stroke.");

        // The closed form, in pane pixels and in nothing else: a stamp covers a disc of the brush's
        // screen radius, whose bounding square is (2r + 2)². Twice that is the slack for the atlas
        // rectangle being a square around an ellipse and for the seam gutter around it.
        var square = (long)((2f * Radius) + 2f) * (long)((2f * Radius) + 2f);

        Assert.True(
            shaded <= session.StampCount * square * 2L,
            $"{shaded} pane pixels shaded for {session.StampCount} stamps; {session.StampCount * square * 2L} is "
            + $"the brush-disc bound. The pane is {Wide * Tall} pixels and the atlas is {Size}²: a number "
            + "near either of those is a scan rather than a lookup."
        );

        var perStamp = elapsed.TotalMilliseconds / session.StampCount;

        output.WriteLine(
            $"4096², 12 layers, {Wide}×{Tall} pane, projected: {perStamp:F3} ms per stamp over "
            + $"{session.StampCount} stamps at radius {footprint.Radius:F1} texels from {Radius} px, "
            + $"{shaded} pane pixels reshaded of {raster.Covered} covered. Exit criterion 8 asks for under 16."
        );

        // A hang check and not a bound — `PaintCostTests`' whole argument, applied here.
        Assert.True(
            perStamp < 500d,
            $"{perStamp:F1} ms per projected stamp is not a slow machine, it is a stamp that stopped being local."
        );
    }

    /// <summary>Turning the camera costs one geometry pass and painting costs none.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half of the same counter, and without it the assertion above could be met
    ///     by a rasteriser that had stopped drawing.</b> <c>Renders</c> not moving during a drag is
    ///     only evidence if it moves when the camera does — a <c>Draw</c> that early-returned would
    ///     make the stroke assertion pass and this one fail.
    /// </remarks>
    [Fact]
    public void The_geometry_pass_runs_when_the_camera_moves_and_only_then() {
        var mesh = Quad();
        PaintCamera camera = new();

        camera.Frame(mesh.Bounds);

        PaintMeshRaster raster = new();

        raster.Draw(mesh, camera, 128, 128);

        PaintImage atlas = new(256, 256, 0xFF808080u);

        raster.Texture(atlas);

        Assert.Equal(1, raster.Renders);

        camera.Orbit(40f, 10f, 128);
        raster.Draw(mesh, camera, 128, 128);

        Assert.Equal(2, raster.Renders);

        raster.Retexture(atlas, new(0, 0, 16, 16));
        raster.Retexture(atlas, new(64, 64, 16, 16));

        Assert.Equal(2, raster.Renders);
    }

    /// <summary>What one frame of an orbit costs, at a docked pane's size and at a maximised 4K one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1107">#1107</a>: the stamp path
    ///         was measured and the <em>camera</em> path was not.</b> An orbit calls
    ///         <c>PaintMeshRaster.Draw</c> once per pointer move at whatever size the pane is, on one
    ///         thread — and the case above measures 1280×720, which says nothing at all about the
    ///         8.3 million pixels of a maximised pane on a 4K display. The issue asks for the
    ///         measurement <em>first</em>, because a cap, a coarser draw while dragging and a
    ///         parallel raster are three different answers and only a number chooses between them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both sizes in one case, on the same machine in the same second.</b> A budget for
    ///         "a 4K draw" calibrated on an idle laptop is this repository's largest flake source; a
    ///         <em>ratio</em> between two draws taken back to back is not, because the load that
    ///         would inflate one inflates the other. What is asserted is that the cost tracks the
    ///         pane's area rather than sitting at some fixed number — which is the property that
    ///         makes an uncapped pane a problem and the capped one below the fix.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mesh is sized so the triangles are not the story.</b> Eighteen thousand
    ///         triangles over a pane that is 8.3 million pixels means the per-pixel work dominates,
    ///         which is what the issue claims and what the cap addresses; a fixture of forty
    ///         triangles would measure the same milliseconds at both sizes for the wrong reason.
    ///     </para>
    /// </remarks>
    [Fact]
    public void One_frame_of_an_orbit_costs_what_the_pane_is_wide_at_both_sizes() {
        const int Repeats = 3;

        var mesh = Grid(96);

        Assert.True(mesh.Triangles > 10_000, $"{mesh.Triangles} triangles is not a model.");

        PaintImage atlas = new(2048, 2048, 0xFF808080u);

        var docked = Cost(mesh, atlas, 1280, 720, Repeats, out var small);
        var maximised = Cost(mesh, atlas, 3840, 2160, Repeats, out var large);

        // The instrument: a draw that covered nothing would be fast for a reason that has nothing to
        // do with the pane's size.
        Assert.True(small > 100_000, $"{small} pixels covered at 1280×720 — the model is not on screen.");
        Assert.True(large > 900_000, $"{large} pixels covered at 3840×2160 — the model is not on screen.");

        output.WriteLine(
            $"{mesh.Triangles} triangles, best of {Repeats}: 1280×720 draw {docked:F1} ms "
            + $"({small} covered, {PaintMeshRaster.BandCount(1280, 720)} bands), 3840×2160 draw "
            + $"{maximised:F1} ms ({large} covered, {PaintMeshRaster.BandCount(3840, 2160)} bands) — "
            + $"{maximised / Math.Max(docked, 1e-3d):F1}× for 9× the pixels, on "
            + $"{Environment.ProcessorCount} processors. An orbit pays this per pointer move."
        );

        // ⚠ The claim as work and not as a clock: what makes a 4K pane expensive is that the pass
        // shades nine times the pixels, and *that* is what is asserted. Three rather than nine
        // because the model does not cover either pane entirely. An earlier draft asserted the
        // milliseconds instead — `maximised > docked * 3` — which would have gone red for the fix:
        // #1107's two remaining answers both help the larger pass more than the smaller, so making
        // the pane cheap would have broken the case that exists to say it is expensive.
        Assert.True(
            large > small * 3L,
            $"{large} pixels covered at 3840×2160 against {small} at 1280×720, for nine times the pane, so "
            + "this case is no longer measuring the per-pixel cost it was written for."
        );

        // A hang check and not a bound, in this file's established shape.
        Assert.True(maximised < 5_000d, $"{maximised:F0} ms for one geometry pass is a hang, not a slow machine.");
    }

    /// <summary>What the row bands buy, as a differential taken on one machine in one second.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1107">#1107</a>'s second answer,
    ///         reported rather than asserted.</b> The two draws are the same mesh, the same camera
    ///         and the same pane, differing only in the band count — so the load that would inflate
    ///         one inflates the other, which is the only honest way to put a wall clock near a
    ///         parallel raster on a developer machine running four other agents.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing here asserts a speed-up, and that is deliberate.</b> A ratio of "at least
    ///         two" would be this repository's largest flake source measured on a box whose cores are
    ///         already spoken for, and it would go red on a single-core runner where
    ///         <c>BandCount</c> correctly answers one. What is asserted is the property the bands
    ///         exist to preserve — that the two pictures are the same bytes — which is exact, and the
    ///         milliseconds are printed beside it as evidence.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_banded_orbit_frame_costs_less_than_a_serial_one_and_draws_the_same_picture() {
        const int Repeats = 3;
        const int Wide = 1600;
        const int Tall = 900;

        var mesh = Grid(96);
        PaintImage atlas = new(2048, 2048, 0xFF808080u);

        var bands = PaintMeshRaster.BandCount(Wide, Tall);

        var serial = Split(mesh, atlas, Wide, Tall, Repeats, 1, out var one);
        var banded = Split(mesh, atlas, Wide, Tall, Repeats, bands, out var many);

        output.WriteLine(
            $"{mesh.Triangles} triangles at {Wide}×{Tall}, best of {Repeats} on "
            + $"{Environment.ProcessorCount} processors: 1 band {serial:F1} ms, {bands} bands "
            + $"{banded:F1} ms — {serial / Math.Max(banded, 1e-3d):F2}× . This is the cap "
            + $"{PaintMeshView.RasterLimit} puts a maximised pane at, paid per pointer move."
        );

        // The instrument: two draws of an empty pane would agree for a reason that is not the bands.
        Assert.True(one.Length > 0 && many.Length == one.Length, "the two draws are not the same pane.");

        Assert.Equal(one, many);
    }

    /// <summary>The best of several combined draws at one size and one band count.</summary>
    /// <param name="mesh">The model.</param>
    /// <param name="atlas">What it wears.</param>
    /// <param name="width">How wide the pane is.</param>
    /// <param name="height">How tall.</param>
    /// <param name="repeats">How many times to draw it.</param>
    /// <param name="bands">How many row bands to split the fill across.</param>
    /// <param name="picture">The last picture drawn, copied.</param>
    /// <returns>The fastest of the passes, in milliseconds.</returns>
    static double Split(
        PaintProjection mesh,
        PaintImage atlas,
        int width,
        int height,
        int repeats,
        int bands,
        out uint[] picture
    ) {
        PaintMeshRaster raster = new();
        PaintCamera camera = new();

        camera.Frame(mesh.Bounds);
        raster.Draw(mesh, camera, width, height, atlas, bands);

        var best = double.MaxValue;

        for (var pass = 0; pass < repeats; pass++) {
            camera.Orbit(3f, 1f, height);

            var started = Stopwatch.GetTimestamp();

            raster.Draw(mesh, camera, width, height, atlas, bands);

            best = Math.Min(best, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var drawn = raster.Picture!;

        picture = new uint[drawn.Width * drawn.Height];

        for (var index = 0; index < picture.Length; index++) {
            picture[index] = drawn[index];
        }

        return best;
    }

    /// <summary>The best of several whole-pane draws at one size, in milliseconds.</summary>
    /// <param name="mesh">The model.</param>
    /// <param name="atlas">What it wears.</param>
    /// <param name="width">How wide the pane is.</param>
    /// <param name="height">How tall.</param>
    /// <param name="repeats">How many times to draw it.</param>
    /// <param name="covered">How many pane pixels showed the model.</param>
    /// <returns>The fastest of the passes, in milliseconds.</returns>
    /// <remarks>
    ///     ⚠ <b>The best and not the mean, and the buffers are allocated outside the timing.</b> A
    ///     mean over three passes on a machine running four other agents measures the machine; the
    ///     minimum is the closest thing to the work itself that a wall clock can report. The first
    ///     draw at a size also allocates five buffers, a picture and the fragment list, which an
    ///     orbit's second frame never pays — so it is drawn once before the clock starts.
    ///     <para>
    ///         ⚠ <b>One combined pass, because that is what <c>PaintMeshView.Render</c> calls.</b> A
    ///         <c>Draw</c> followed by a <c>Texture</c> measures a full-pane shade that
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1115">#1115</a> removed, so a helper
    ///         that kept the two-call shape would go on reporting the cost of a route production no
    ///         longer takes — the commonest way a cost case stops measuring anything.
    ///     </para>
    /// </remarks>
    static double Cost(PaintProjection mesh, PaintImage atlas, int width, int height, int repeats, out int covered) {
        PaintMeshRaster raster = new();
        PaintCamera camera = new();

        camera.Frame(mesh.Bounds);
        raster.Draw(mesh, camera, width, height, atlas);

        var best = double.MaxValue;

        for (var pass = 0; pass < repeats; pass++) {
            // A different angle each time, so nothing can be cached on the camera not having moved —
            // which is exactly what an orbit does.
            camera.Orbit(3f, 1f, height);

            var started = Stopwatch.GetTimestamp();

            raster.Draw(mesh, camera, width, height, atlas);

            best = Math.Min(best, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        covered = raster.Covered;

        return best;
    }

    /// <summary>A subdivided quad in the z = 0 plane, as a model-sized triangle count.</summary>
    /// <param name="cells">How many cells across. The triangle count is twice its square.</param>
    /// <returns>The projection.</returns>
    static PaintProjection Grid(int cells) {
        List<Vector3> points = [];
        List<Vector2> layout = [];
        List<int> indices = [];

        for (var row = 0; row <= cells; row++) {
            for (var column = 0; column <= cells; column++) {
                var u = (float)column / cells;
                var v = (float)row / cells;

                points.Add(new((u * 2f) - 1f, (v * 2f) - 1f, 0f));
                layout.Add(new(u, v));
            }
        }

        for (var row = 0; row < cells; row++) {
            for (var column = 0; column < cells; column++) {
                var corner = (row * (cells + 1)) + column;

                indices.AddRange([corner, corner + 1, corner + cells + 2]);
                indices.AddRange([corner, corner + cells + 2, corner + cells + 1]);
            }
        }

        return PaintProjection.Over([.. points], [.. layout], [.. indices]);
    }

    /// <summary>A quad in the z = 0 plane whose layout is the whole unit square.</summary>
    /// <returns>The projection.</returns>
    static PaintProjection Quad() =>
        PaintProjection.Over(
            [new(-1f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f), new(-1f, 1f, 0f)],
            [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)],
            [0, 1, 2, 0, 2, 3]
        );

    /// <summary>A stack whose evaluation costs one pass per layer, and says how many it ran.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own rather than <c>PaintCostTests</c>', which is private to that class</b> — and
    ///     deliberately left that way: what a shared one would buy is six lines, and what it would
    ///     cost is a fixture two suites can silently change under each other.
    /// </remarks>
    sealed class FlatStack(int width, int height, int layers) : IPaintStack {
        /// <summary>How many slices have been asked for.</summary>
        public int Evaluations { get; private set; }

        /// <summary>How many layer passes those slices cost between them.</summary>
        public int LayerPasses { get; private set; }

        /// <inheritdoc />
        public PaintImage Evaluate(PaintStackSlice slice) {
            Evaluations++;
            LayerPasses += layers;

            PaintImage image = new(width, height);

            image.Fill(slice == PaintStackSlice.Below ? 0xFF202020u : 0u);

            return image;
        }
    }
}
