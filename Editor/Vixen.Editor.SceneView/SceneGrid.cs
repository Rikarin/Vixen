// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>One line of the grid.</summary>
/// <param name="From">One end, in world space.</param>
/// <param name="To">The other.</param>
/// <param name="Colour">What to draw it in, alpha included.</param>
public readonly record struct GridLine(Vector3 From, Vector3 To, Color4 Colour);

/// <summary>The floor grid, at whatever spacing is legible from where the camera is.</summary>
/// <remarks>
///     <para>
///         <b>The spacing is chosen, not fixed.</b> A one-metre grid is a grey haze from two hundred
///         metres up and three lines from half a metre away. The spacing is the power of ten (times
///         one, two or five) that puts the lines roughly <see cref="TargetSpacing" /> pixels apart,
///         which is the same rule a chart's axis ticks follow and for the same reason.
///     </para>
///     <para>
///         <b>Two levels are drawn and the finer one fades.</b> Snapping the spacing from one decade
///         to the next in one frame makes the grid flash as the camera moves; drawing the next level
///         down at an alpha that goes to zero as it becomes too dense makes the transition
///         continuous, and it is what every editor that got this right does.
///     </para>
///     <para>
///         <b>It is a list of lines, not a draw call.</b> What draws them is the viewport's business
///         — a debug-line renderer, a shader, an overlay — and generating them here is what makes
///         "does the grid pick a sane spacing" a unit test.
///     </para>
/// </remarks>
public sealed class SceneGrid {
    /// <summary>Whether the grid is drawn at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far apart the lines should look, in render pixels.</summary>
    public float TargetSpacing { get; set; } = 48f;

    /// <summary>How many lines to draw either side of the middle, per level.</summary>
    /// <remarks>
    ///     A count rather than a world extent, because the spacing changes with the camera and a
    ///     fixed extent would mean drawing four hundred thousand lines when zoomed out.
    /// </remarks>
    public int Extent { get; set; } = 60;

    /// <summary>The colour of an ordinary line.</summary>
    public Color4 LineColour { get; set; } = new(0.5f, 0.5f, 0.5f, 0.25f);

    /// <summary>The colour of every tenth line.</summary>
    public Color4 MajorColour { get; set; } = new(0.5f, 0.5f, 0.5f, 0.45f);

    /// <summary>The colour of the line through the origin along X.</summary>
    public Color4 AxisXColour { get; set; } = new(0.87f, 0.29f, 0.33f, 0.8f);

    /// <summary>The colour of the line through the origin along Z.</summary>
    public Color4 AxisZColour { get; set; } = new(0.29f, 0.51f, 0.90f, 0.8f);

    /// <summary>The spacing the grid would use from where a camera is.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="height">How tall the viewport is, in render pixels.</param>
    /// <returns>The spacing, in world units.</returns>
    public float Spacing(EditorCamera camera, int height) {
        ArgumentNullException.ThrowIfNull(camera);

        if (height <= 0) {
            return 1f;
        }

        var worldPerPixel = camera.OrthographicHeight / height;
        var wanted = worldPerPixel * TargetSpacing;

        if (wanted <= 0f || !float.IsFinite(wanted)) {
            return 1f;
        }

        // The 1-2-5 sequence, which is what a person reads as round numbers. Plain powers of ten
        // jump by a factor of ten and spend most of the range at the wrong density.
        var decade = MathF.Pow(10f, MathF.Floor(MathF.Log10(wanted)));
        var normalised = wanted / decade;

        return decade * (normalised switch {
            < 1.5f => 1f,
            < 3.5f => 2f,
            < 7.5f => 5f,
            _ => 10f
        });
    }

    /// <summary>The lines to draw, in world space.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="height">How tall the viewport is, in render pixels.</param>
    /// <returns>The lines.</returns>
    /// <remarks>
    ///     ⚠ <b>Centred on the camera's pivot, snapped to the spacing.</b> A grid centred on the
    ///     world origin runs out as soon as anybody builds a level more than sixty units across, and
    ///     one centred on the unsnapped pivot slides under the camera as it pans, which reads as the
    ///     ground moving.
    /// </remarks>
    public IReadOnlyList<GridLine> Build(EditorCamera camera, int height) {
        ArgumentNullException.ThrowIfNull(camera);

        if (!Enabled || height <= 0) {
            return [];
        }

        var spacing = Spacing(camera, height);
        var fine = spacing / 10f;

        // How dense the finer level would be on screen, as a fraction of where it stops being
        // legible. One means "as far apart as we wanted"; zero means "not drawn".
        var worldPerPixel = camera.OrthographicHeight / height;
        var fade = Math.Clamp(fine / worldPerPixel / TargetSpacing, 0f, 1f);

        List<GridLine> lines = [];

        if (fade > 0.01f) {
            Level(lines, camera, fine, Fade(LineColour, fade * 0.6f), major: false);
        }

        Level(lines, camera, spacing, LineColour, major: true);

        return lines;
    }

    void Level(List<GridLine> lines, EditorCamera camera, float spacing, Color4 colour, bool major) {
        var centre = camera.Pivot;
        var originX = MathF.Round(centre.X / spacing) * spacing;
        var originZ = MathF.Round(centre.Z / spacing) * spacing;
        var reach = Extent * spacing;

        for (var step = -Extent; step <= Extent; step++) {
            var x = originX + (step * spacing);
            var z = originZ + (step * spacing);

            lines.Add(new(
                new Vector3(x, 0f, originZ - reach),
                new Vector3(x, 0f, originZ + reach),
                Colour(x, step, major, colour, AxisZColour)
            ));

            lines.Add(new(
                new Vector3(originX - reach, 0f, z),
                new Vector3(originX + reach, 0f, z),
                Colour(z, step, major, colour, AxisXColour)
            ));
        }
    }

    /// <summary>What one line is drawn in: an axis, every tenth, or an ordinary one.</summary>
    /// <remarks>
    ///     The axis test is on the world coordinate and the "every tenth" test is on the step, and
    ///     they are different questions: the axis is a fact about the world and a major line is a
    ///     fact about this level of the grid. Testing both on the coordinate would make the tenth
    ///     line major only when the grid happened to be aligned to ten world units.
    /// </remarks>
    Color4 Colour(float coordinate, int step, bool major, Color4 ordinary, Color4 axis) {
        if (MathF.Abs(coordinate) < 1e-4f) {
            return axis;
        }

        return major && step % 10 == 0 ? MajorColour : ordinary;
    }

    static Color4 Fade(Color4 colour, float alpha) => new(colour.R, colour.G, colour.B, colour.A * alpha);
}
