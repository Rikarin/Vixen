// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>What spacing the floor grid picks, which lines it emphasises, and where it stops.</summary>
public class GridTests {
    const int Height = 800;

    static EditorCamera Camera(float distance = 10f) => new() { Distance = distance };

    [Fact]
    public void The_spacing_is_a_round_number_that_is_legible_from_here() {
        var grid = new SceneGrid();

        foreach (var distance in new[] { 0.5f, 5f, 50f, 500f, 5000f }) {
            var camera = Camera(distance);
            var spacing = grid.Spacing(camera, Height);
            var pixels = spacing / (camera.OrthographicHeight / Height);

            // Within a factor of two of what was asked for, at five orders of magnitude of distance.
            // That is the whole job: a fixed spacing is a grey haze from two hundred metres up and
            // three lines from half a metre away.
            Assert.InRange(pixels, grid.TargetSpacing * 0.5f, grid.TargetSpacing * 2f);

            // And a number a person reads as round: 1, 2 or 5 times a power of ten.
            var decade = MathF.Pow(10f, MathF.Round(MathF.Log10(spacing)));
            var mantissa = spacing / decade;

            Assert.Contains(
                new[] { 0.1f, 0.2f, 0.5f, 1f, 2f, 5f, 10f },
                candidate => MathF.Abs(candidate - mantissa) < 1e-3f
            );
        }
    }

    [Fact]
    public void The_emphasised_lines_are_at_round_places_and_stay_there_when_the_view_pans() {
        var grid = new SceneGrid();
        var camera = Camera();
        var spacing = grid.Spacing(camera, Height);

        var before = Emphasised(grid, camera, spacing);

        // Panned by half a step, which is what used to move the emphasis by a whole line: the "every
        // tenth" test was on the loop index, and the loop starts wherever the pivot snapped to.
        camera.Pivot = new Vector3(spacing * 3.5f, 0f, 0f);

        var after = Emphasised(grid, camera, spacing);

        Assert.NotEmpty(before);
        Assert.All(before, x => Assert.True(IsMultiple(x, spacing * 10f), $"{x} is not a multiple of {spacing * 10f}"));
        Assert.All(after, x => Assert.True(IsMultiple(x, spacing * 10f), $"{x} is not a multiple of {spacing * 10f}"));
    }

    [Fact]
    public void The_lines_through_the_origin_are_the_axis_colours() {
        var grid = new SceneGrid();
        var camera = Camera();
        var lines = grid.Build(camera, Height);

        // A line at constant x running along z *is* the z axis when x is zero, and a line at constant
        // z running along x is the x axis. Blue and red, which is what the corner cross says too.
        Assert.Contains(lines, line => Near(line.From.X, 0f) && Near(line.To.X, 0f) && Same(line.Colour, grid.AxisZColour));
        Assert.Contains(lines, line => Near(line.From.Z, 0f) && Near(line.To.Z, 0f) && Same(line.Colour, grid.AxisXColour));
    }

    [Fact]
    public void Every_line_fades_out_at_its_far_end() {
        var grid = new SceneGrid();
        var lines = grid.Build(Camera(), Height);

        Assert.NotEmpty(lines);

        // A level is a finite number of finite lines, and without this its far edge is a hard
        // rectangle drawn across the scene. Each line is emitted as two halves meeting under the
        // pivot: solid where you are looking, gone at the rim.
        Assert.All(lines, line => Assert.True(line.ToColour.A < line.Colour.A || line.Colour.A == 0f));
    }

    [Fact]
    public void The_finer_level_fades_in_as_it_becomes_legible_and_out_when_it_stops_being() {
        var grid = new SceneGrid();
        var seen = new List<float>();

        // Swept across a decade, which is the range over which the coarse spacing walks the 1-2-5
        // sequence and the fine one walks it behind. The fade used to be computed from a tenth of the
        // coarse spacing — four or five pixels at every distance — so it never left one tenth and the
        // level it controlled was a permanent invisible haze costing two hundred segments a frame.
        for (var step = 0; step <= 40; step++) {
            var camera = Camera(1f * MathF.Pow(10f, step / 40f));

            seen.Add(grid.Levels(camera, Height).Fade);
        }

        Assert.Contains(seen, fade => fade > 0.9f);
        Assert.Contains(seen, fade => fade < 0.1f);
    }

    [Fact]
    public void The_finer_level_is_one_step_of_the_sequence_and_not_a_tenth() {
        var grid = new SceneGrid();
        var (coarse, fine, _) = grid.Levels(Camera(), Height);

        // Half or two fifths of the coarse spacing, so it is legible at one end of the range and not
        // at the other — which is the only reason a fade has anything to do.
        Assert.InRange(fine / coarse, 0.39f, 0.51f);
    }

    [Fact]
    public void A_level_reaches_past_the_edge_of_the_pane_at_every_distance() {
        var grid = new SceneGrid();

        foreach (var distance in new[] { 0.5f, 10f, 1000f }) {
            var camera = Camera(distance);
            var lines = grid.Build(camera, Height);
            var reach = lines.Max(line => (line.To - line.From).Length());

            // In screen-heights rather than world units or line counts, so it is the same at every
            // zoom: a floor that runs off every edge of the pane with room to spare.
            Assert.True(
                reach >= camera.OrthographicHeight,
                $"at {distance} units away the grid reaches {reach} and the pane shows {camera.OrthographicHeight}"
            );
        }
    }

    [Fact]
    public void The_work_is_bounded_however_far_out_the_camera_is() {
        var grid = new SceneGrid { Reach = 10_000f };
        var lines = grid.Build(Camera(), Height);

        // The reach is chosen from what the pane can see and a camera at the horizon can see for
        // ever. Without a ceiling that is a frame that takes a second; with one the grid stops short
        // and the distance fade makes stopping short look like fading out.
        //
        // Two levels, `MaximumLines` steps and one more across each, running both ways, each split
        // into two halves — which is the ceiling written out rather than a number that happens to be
        // above what today's defaults produce.
        var ceiling = 2 * (SceneGrid.MaximumLines + 1) * 2 * 2;

        Assert.True(lines.Count <= ceiling, $"{lines.Count} lines is more than the {ceiling} the cap allows");
    }

    [Fact]
    public void Turning_it_off_draws_nothing() {
        var grid = new SceneGrid { Enabled = false };

        Assert.Empty(grid.Build(Camera(), Height));
    }

    /// <summary>The x of every emphasised line running along z.</summary>
    /// <remarks>
    ///     ⚠ Matched on hue and not on alpha. Every line's alpha carries how far across the level it
    ///     sits, so an emphasised line is only its own colour where the fade has not touched it —
    ///     which is one line out of the hundreds a level draws.
    /// </remarks>
    static IReadOnlyList<float> Emphasised(SceneGrid grid, EditorCamera camera, float spacing) =>
        grid.Build(camera, Height)
            .Where(line => SameHue(line.Colour, grid.MajorColour) && line.Colour.A > 0f && Near(line.From.X, line.To.X))
            .Select(line => line.From.X)
            .ToArray();

    static bool IsMultiple(float value, float step) =>
        MathF.Abs(value - (MathF.Round(value / step) * step)) < step * 1e-2f;

    static bool Near(float left, float right) => MathF.Abs(left - right) < 1e-3f;

    static bool Same(Color4 left, Color4 right) => SameHue(left, right) && MathF.Abs(left.A - right.A) < 1e-4f;

    static bool SameHue(Color4 left, Color4 right) =>
        MathF.Abs(left.R - right.R) < 1e-4f && MathF.Abs(left.G - right.G) < 1e-4f
        && MathF.Abs(left.B - right.B) < 1e-4f;
}
