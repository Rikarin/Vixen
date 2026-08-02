// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>One line of the grid.</summary>
/// <param name="From">One end, in world space.</param>
/// <param name="To">The other.</param>
/// <param name="Colour">What to draw it in at <paramref name="From" />, alpha included.</param>
/// <param name="ToColour">What to draw it in at <paramref name="To" />.</param>
/// <remarks>
///     ⚠ <b>A colour at each end, because a grid line has to be able to disappear.</b> A level is a
///     finite number of finite lines and its far edge is a hard rectangle drawn across the scene
///     unless the ends fade out — which is the one thing that makes a bounded grid read as an
///     unbounded floor. <c>LineVertex</c> has carried a colour per vertex from the start for exactly
///     this; the grid was the caller that was not using it.
/// </remarks>
public readonly record struct GridLine(Vector3 From, Vector3 To, Color4 Colour, Color4 ToColour) {
    /// <summary>A line of one colour along its whole length.</summary>
    /// <param name="from">One end.</param>
    /// <param name="to">The other.</param>
    /// <param name="colour">What to draw it in.</param>
    public GridLine(Vector3 from, Vector3 to, Color4 colour)
        : this(from, to, colour, colour) { }
}

/// <summary>The floor grid, at whatever spacing is legible from where the camera is.</summary>
/// <remarks>
///     <para>
///         <b>The spacing is chosen, not fixed.</b> A one-metre grid is a grey haze from two hundred
///         metres up and three lines from half a metre away. The spacing is the power of ten (times
///         one, two or five) that puts the lines roughly <see cref="TargetSpacing" /> pixels apart,
///         which is the same rule a chart's axis ticks follow and for the same reason.
///     </para>
///     <para>
///         <b>Two levels are drawn and the finer one fades.</b> Snapping the spacing from one step of
///         the sequence to the next in one frame makes the grid flash as the camera moves; drawing
///         the next level down at an alpha that falls to zero as it becomes too dense makes the
///         transition continuous, and it is what every editor that got this right does.
///     </para>
///     <para>
///         ⚠ <b>Every decision is made from the world coordinate, not from a loop index.</b> Which
///         lines are major, which are the axes and where a level starts are all facts about the
///         world; a loop index is a fact about where the camera happens to be, because the lines are
///         laid out from the pivot. Deriving the first of those from the second is what made the
///         emphasised lines march sideways one line at a time as the view was panned — a grid that
///         is subtly, continuously wrong and that nobody can point at.
///     </para>
///     <para>
///         <b>It is a list of lines, not a draw call.</b> What draws them is the viewport's business
///         — a debug-line renderer, a shader, an overlay — and generating them here is what makes
///         "does the grid pick a sane spacing" a unit test.
///     </para>
/// </remarks>
public sealed class SceneGrid {
    /// <summary>How many lines one level may have across, whatever the camera asks for.</summary>
    /// <remarks>
    ///     ⚠ <b>A ceiling on the work, not on the reach.</b> The reach is chosen from what the pane
    ///     can see, and a camera pitched at the horizon can see arbitrarily far — so without a cap a
    ///     grid at a grazing angle is a hundred thousand segments and a frame that takes a second.
    ///     Hitting it makes the grid stop short of the horizon, which the distance fade turns into a
    ///     grid that fades out rather than one that ends.
    /// </remarks>
    public const int MaximumLines = 512;

    /// <summary>Whether the grid is drawn at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The plane it draws, which is the ground until somebody moves it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 24's D5: the grid is a <i>view</i> of a <see cref="WorkPlane" /> rather than a
    ///         thing of its own.</b> Everything above still holds — the 1-2-5 sequence, the emphasis on
    ///         round numbers, the reach in screen heights — and all of it now happens in the plane's
    ///         own two directions rather than in world X and Z. A default plane is the identity, so a
    ///         grid nobody has moved is the grid that was here before.
    ///     </para>
    ///     <para>
    ///         Settable, so that four panes and the placement service can be handed the same one:
    ///         where the designer is building is a fact about them, not about which pane they are
    ///         looking through.
    ///     </para>
    /// </remarks>
    public WorkPlane Plane { get; set; } = new();

    /// <summary>How far apart the lines should look, in render pixels.</summary>
    public float TargetSpacing { get; set; } = 48f;

    /// <summary>How far a level reaches, in screen-heights of world at the pivot's depth.</summary>
    /// <remarks>
    ///     ⚠ <b>In screen-heights rather than in world units or in lines.</b> World units would make
    ///     the grid a fixed rectangle that vanishes when you zoom out and costs a hundred thousand
    ///     segments when you zoom in; a count of lines is the same thing said differently, because
    ///     the spacing is already chosen to put them a fixed number of pixels apart. Screen-heights
    ///     is the one unit that is the same at every distance: four of them is a floor that runs off
    ///     every edge of the pane with room to spare, at every zoom.
    /// </remarks>
    public float Reach { get; set; } = 4f;

    /// <summary>How many lines to draw either side of the middle, per level.</summary>
    /// <remarks>
    ///     ⚠ <b>Only a ceiling now.</b> <see cref="Reach" /> is what decides how far a level goes;
    ///     this bounds the count when the spacing is small enough that the reach would ask for more
    ///     than <see cref="MaximumLines" /> of them.
    /// </remarks>
    public int Extent { get; set; } = MaximumLines / 2;

    /// <summary>The colour of an ordinary line.</summary>
    public Color4 LineColour { get; set; } = new(0.5f, 0.5f, 0.5f, 0.25f);

    /// <summary>The colour of every tenth line.</summary>
    /// <remarks>
    ///     ⚠ <b>Brighter as well as more opaque, and it has to be both.</b> The two used to differ
    ///     only in alpha, which stopped saying anything the moment the grid started fading its lines
    ///     out with distance: an emphasised line at the edge of the level came out fainter than an
    ///     ordinary one under the pivot, so the emphasis said "near" rather than "round number".
    /// </remarks>
    public Color4 MajorColour { get; set; } = new(0.66f, 0.66f, 0.66f, 0.45f);

    /// <summary>The colour of the line through the origin along X.</summary>
    public Color4 AxisXColour { get; set; } = new(0.87f, 0.29f, 0.33f, 0.8f);

    /// <summary>The colour of the line through the origin along Z.</summary>
    public Color4 AxisZColour { get; set; } = new(0.29f, 0.51f, 0.90f, 0.8f);

    /// <summary>The spacing the grid would use from where a camera is.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="height">How tall the viewport is, in render pixels.</param>
    /// <returns>The spacing, in world units.</returns>
    /// <remarks>
    ///     ⚠ <b>The plane's chosen step wins where there is one.</b> That is what makes "the grid I can
    ///     see" and "the grid I snap to" one number — see <see cref="WorkPlane.Step" /> — and it is
    ///     what the doubling and halving verbs write.
    /// </remarks>
    public float Spacing(EditorCamera camera, int height) => Levels(camera, height).Coarse;

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

        var levels = Levels(camera, height);
        List<GridLine> lines = [];

        // ⚠ The finer level first, so the coarse one is drawn over it. Both are translucent and both
        // land on the same lines wherever they coincide, and the emphasised colour has to be the one
        // that wins — otherwise every major line is a major line with a faint one on top of it, which
        // at these alphas is a major line that is the wrong colour.
        if (levels.Fade > 0.01f) {
            Level(lines, camera, levels.Fine, levels.Fade, major: false);
        }

        Level(lines, camera, levels.Coarse, 1f, major: true);

        return lines;
    }

    /// <summary>Which two spacings are drawn from where a camera is, and how much the finer shows.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="height">How tall the viewport is, in render pixels.</param>
    /// <returns>The coarse spacing, the fine one, and the fine one's opacity.</returns>
    /// <remarks>
    ///     <para>
    ///         The 1-2-5 sequence, which is what a person reads as round numbers. Plain powers of ten
    ///         jump by a factor of ten and spend most of the range at the wrong density.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fine level is the previous step of that sequence, not a tenth of the coarse
    ///         one.</b> A tenth is between four and five pixels apart at every distance — permanently
    ///         too dense to read and permanently drawn — so the fade computed from it never moved off
    ///         a tenth and the level it controlled was a haze that cost two hundred segments a frame
    ///         and showed nothing. One step down is half or two fifths of the coarse spacing, which is
    ///         legible at one end of the range and not at the other, which is what a fade is for.
    ///     </para>
    /// </remarks>
    public (float Coarse, float Fine, float Fade) Levels(EditorCamera camera, int height) {
        ArgumentNullException.ThrowIfNull(camera);

        if (height <= 0) {
            return (1f, 0.5f, 0f);
        }

        var worldPerPixel = camera.OrthographicHeight / height;
        var wanted = worldPerPixel * TargetSpacing;

        if (wanted <= 0f || !float.IsFinite(wanted)) {
            return (1f, 0.5f, 0f);
        }

        var decade = MathF.Pow(10f, MathF.Floor(MathF.Log10(wanted)));
        var normalised = wanted / decade;

        var (coarse, fine) = normalised switch {
            < 1.5f => (1f, 0.5f),
            < 3.5f => (2f, 1f),
            < 7.5f => (5f, 2f),
            _ => (10f, 5f)
        };

        coarse *= decade;
        fine *= decade;

        // ⚠ The designer's choice, and it replaces the sequence rather than being rounded onto it. A
        // level blocked out at four metres has to stay at four metres while the camera moves, which is
        // the whole reason `]` and `[` exist; the finer level stays half of whatever is drawn so that
        // every visible line is still on the lattice a snap lands on.
        if (Plane.Step is { } chosen) {
            coarse = chosen;
            fine = chosen * 0.5f;
        }

        // How far apart the finer level lands on screen. A step of the sequence is between two fifths
        // and a half of the one above it, so this runs from about two thirds of `TargetSpacing` down
        // to about a quarter of it as the view zooms out within one bracket — and the band below is
        // that range, so the level goes from fully drawn to gone once per bracket rather than sitting
        // at one value for ever.
        var apart = fine / worldPerPixel;
        var fade = Math.Clamp((apart - (TargetSpacing * 0.25f)) / (TargetSpacing * 0.3f), 0f, 1f);

        return (coarse, fine, fade);
    }

    /// <summary>Adds one level's worth of lines.</summary>
    /// <remarks>
    ///     ⚠ <b>Every line is emitted as two halves meeting under the pivot.</b> A colour at each end
    ///     can only fade one way along a segment, and what a grid has to do is be solid where you are
    ///     looking and gone at the edges — which is three colours across one line. Splitting at the
    ///     middle is what buys the third, and it costs a segment count this already bounds.
    /// </remarks>
    void Level(List<GridLine> lines, EditorCamera camera, float spacing, float opacity, bool major) {
        // ⚠ The pivot brought into the plane's own space and flattened onto it. On the ground plane
        // this is the pivot's X and Z and nothing has changed; on a wall it is where the camera is
        // looking, measured along the wall — which is what keeps the lines under the work rather than
        // under wherever the world origin happens to be.
        var centre = Plane.ToLocal(camera.Pivot);

        var originX = MathF.Round(centre.X / spacing) * spacing;
        var originZ = MathF.Round(centre.Z / spacing) * spacing;

        var wanted = (int) MathF.Ceiling(Reach * camera.OrthographicHeight / spacing);
        var extent = Math.Clamp(wanted, 1, Math.Min(Extent, MaximumLines / 2));
        var reach = extent * spacing;

        for (var step = -extent; step <= extent; step++) {
            var x = originX + (step * spacing);
            var z = originZ + (step * spacing);

            // How far across the level this line sits. Squared, so the falloff is slow in the middle
            // — where the lines are being read — and quick at the rim, where the point is only that
            // there is no rim.
            var across = MathF.Abs(step) / (float) extent;
            var alpha = opacity * (1f - (across * across));

            Split(new(x, 0f, originZ), new Vector3(0f, 0f, reach), Colour(x, spacing, major, AxisZColour), alpha);
            Split(new(originX, 0f, z), new Vector3(reach, 0f, 0f), Colour(z, spacing, major, AxisXColour), alpha);
        }

        void Split(Vector3 middle, Vector3 arm, Color4 colour, float alpha) {
            if (alpha <= 0.001f) {
                return;
            }

            var solid = Fade(colour, alpha);
            var clear = Fade(colour, 0f);

            // Out of the plane's space at the last moment, so that everything above is the arithmetic
            // it always was and only the two ends of each segment know where the plane is.
            var from = Plane.ToWorld(middle);
            var along = Quaternion.Transform(arm, Plane.Rotation);

            lines.Add(new(from, from + along, solid, clear));
            lines.Add(new(from, from - along, solid, clear));
        }
    }

    /// <summary>What one line is drawn in: an axis, every tenth, or an ordinary one.</summary>
    /// <remarks>
    ///     ⚠ <b>Both tests are on the world coordinate, and the "every tenth" one used to be on the
    ///     loop index.</b> They are the same kind of question — is this line at a round place — and
    ///     the index only answers it when the level happens to start at the origin, which it does
    ///     only when the pivot is there. Everywhere else the emphasis sat on ten arbitrary lines that
    ///     slid sideways as the view was panned.
    /// </remarks>
    Color4 Colour(float coordinate, float spacing, bool major, Color4 axis) {
        // Relative to the spacing, because the coordinates a level lands on are multiples of it and
        // the rounding error in one grows with the size of the level. A fixed epsilon is one that
        // stops finding the axis somewhere out past a few thousand units.
        var tolerance = spacing * 1e-3f;

        if (MathF.Abs(coordinate) < tolerance) {
            return axis;
        }

        if (!major) {
            return LineColour;
        }

        var tenth = spacing * 10f;

        return MathF.Abs(coordinate - (MathF.Round(coordinate / tenth) * tenth)) < tolerance
            ? MajorColour
            : LineColour;
    }

    static Color4 Fade(Color4 colour, float alpha) => new(colour.R, colour.G, colour.B, colour.A * alpha);
}
