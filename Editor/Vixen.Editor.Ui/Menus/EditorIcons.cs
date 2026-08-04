// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui;

namespace Vixen.Editor.Ui;

/// <summary>The glyphs the editor's own chrome is drawn with.</summary>
/// <remarks>
///     <para>
///         <b><c>ControlIcons</c>' eight are what a checkbox and a combo box cannot be drawn
///         without. These are what a <i>toolbar</i> cannot be drawn without</b>, which is a different
///         and larger set: doc 20 puts the full editor icon set at roughly a hundred and twenty
///         glyphs and calls it a design dependency rather than an engineering one. What is here is
///         the part the shell needs in order to have a toolbar at all, drawn on the same 24×24 grid
///         so that a real set drops in by name.
///     </para>
///     <para>
///         ⚠ <b>A command with no icon is a labelled button, not a blank one.</b>
///         <see cref="ToolbarPresenter" /> already falls back to the title, which is doc 20's stated
///         mitigation for exactly this: a missing glyph must cost a wider button and never a button
///         nobody can identify. Nothing here is required by anything.
///     </para>
///     <para>
///         ⚠ <b>One instance each, shared, and mutable all the same.</b> The same bargain
///         <c>ControlIcons</c> makes and for the same reason — <c>Icon</c> scales into a buffer of
///         its own and only reads these — so a caller that mutates one has changed every toolbar in
///         the process.
///     </para>
/// </remarks>
public static class EditorIcons {
    /// <summary>A filled dot. What marks the chosen member of a radio group.</summary>
    public static PathBuilder RadioMark { get; } = Disc(12f, 12f, 3.5f);

    /// <summary>A page with a corner turned. New.</summary>
    public static PathBuilder New { get; } = Outline(
        [
            new Vector2(6f, 3f), new Vector2(14f, 3f), new Vector2(19f, 8f), new Vector2(19f, 21f),
            new Vector2(6f, 21f)
        ],
        closed: true
    ).Then(path => Line(path, new Vector2(14f, 3f), new Vector2(14f, 8f), new Vector2(19f, 8f)));

    /// <summary>A folder, open. Open.</summary>
    public static PathBuilder Open { get; } = Outline(
        [
            new Vector2(3f, 19f), new Vector2(3f, 5f), new Vector2(9f, 5f), new Vector2(11f, 8f),
            new Vector2(21f, 8f), new Vector2(21f, 19f)
        ],
        closed: true
    );

    /// <summary>A floppy disk, because nothing better has ever been agreed on. Save.</summary>
    public static PathBuilder Save { get; } = Outline(
        [new Vector2(4f, 4f), new Vector2(17f, 4f), new Vector2(20f, 7f), new Vector2(20f, 20f), new Vector2(4f, 20f)],
        closed: true
    ).Then(path => path.AddRectangle(new Rectangle(8f, 4f, 8f, 6f)))
        .Then(path => path.AddRectangle(new Rectangle(7f, 13f, 10f, 7f)));

    /// <summary>An arrow curving back. Undo.</summary>
    public static PathBuilder Undo { get; } = Arrow(left: true);

    /// <summary>The same, the other way. Redo.</summary>
    public static PathBuilder Redo { get; } = Arrow(left: false);

    /// <summary>A bin. Delete.</summary>
    public static PathBuilder Delete { get; } = Line(
        new PathBuilder(),
        new Vector2(4f, 6.5f),
        new Vector2(20f, 6.5f)
    ).Then(path => Line(path, new Vector2(9f, 6.5f), new Vector2(9f, 3.5f), new Vector2(15f, 3.5f), new Vector2(15f, 6.5f)))
        .Then(path => Line(path, new Vector2(6f, 6.5f), new Vector2(7.5f, 21f), new Vector2(16.5f, 21f), new Vector2(18f, 6.5f)));

    /// <summary>Four arrowheads from a centre. The translate gizmo.</summary>
    public static PathBuilder Translate { get; } = Line(
        new PathBuilder(),
        new Vector2(12f, 3f),
        new Vector2(12f, 21f)
    ).Then(path => Line(path, new Vector2(3f, 12f), new Vector2(21f, 12f)))
        .Then(path => Head(path, new Vector2(12f, 3f), new Vector2(0f, 1f)))
        .Then(path => Head(path, new Vector2(12f, 21f), new Vector2(0f, -1f)))
        .Then(path => Head(path, new Vector2(3f, 12f), new Vector2(1f, 0f)))
        .Then(path => Head(path, new Vector2(21f, 12f), new Vector2(-1f, 0f)));

    /// <summary>A ring with an arrowhead on it. The rotate gizmo.</summary>
    public static PathBuilder Rotate { get; } = Ring(12f, 12f, 8f, 1.8f)
        .Then(path => Head(path, new Vector2(20f, 12f), new Vector2(0f, 1f)));

    /// <summary>A small square and a large one, cornered. The scale gizmo.</summary>
    public static PathBuilder Scale { get; } = Line(
        new PathBuilder(),
        new Vector2(5f, 19f),
        new Vector2(19f, 5f)
    ).Then(path => path.AddRectangle(new Rectangle(3f, 17f, 4f, 4f)))
        .Then(path => path.AddRectangle(new Rectangle(15f, 3f, 6f, 6f)));

    /// <summary>A globe, of two lines. World space.</summary>
    public static PathBuilder World { get; } = Ring(12f, 12f, 8.5f, 1.8f)
        .Then(path => Line(path, new Vector2(3.5f, 12f), new Vector2(20.5f, 12f)))
        .Then(path => Ring(12f, 12f, 4.2f, 1.6f, into: path));

    /// <summary>A horseshoe. Snapping.</summary>
    public static PathBuilder Snap { get; } = Line(
        new PathBuilder(),
        new Vector2(6f, 20f),
        new Vector2(6f, 11f)
    ).Then(path => Line(path, new Vector2(18f, 20f), new Vector2(18f, 11f)))
        .Then(path => Ring(12f, 11f, 6f, 2f, sweep: 0.5f, into: path));

    /// <summary>Crossed lines. The floor grid.</summary>
    public static PathBuilder Grid { get; } = Line(new PathBuilder(), new Vector2(9f, 3f), new Vector2(9f, 21f))
        .Then(path => Line(path, new Vector2(15f, 3f), new Vector2(15f, 21f)))
        .Then(path => Line(path, new Vector2(3f, 9f), new Vector2(21f, 9f)))
        .Then(path => Line(path, new Vector2(3f, 15f), new Vector2(21f, 15f)));

    /// <summary>A triangle. Play.</summary>
    public static PathBuilder Play { get; } = Filled([new Vector2(7f, 4f), new Vector2(20f, 12f), new Vector2(7f, 20f)]);

    /// <summary>Two bars. Pause.</summary>
    public static PathBuilder Pause { get; } = new PathBuilder()
        .AddRectangle(new Rectangle(7f, 4f, 3.6f, 16f))
        .AddRectangle(new Rectangle(13.4f, 4f, 3.6f, 16f));

    /// <summary>A square. Stop.</summary>
    public static PathBuilder Stop { get; } = new PathBuilder().AddRectangle(new Rectangle(6f, 6f, 12f, 12f));

    /// <summary>A triangle against a bar. Step one frame.</summary>
    public static PathBuilder Step { get; } = Filled([new Vector2(5f, 4f), new Vector2(16f, 12f), new Vector2(5f, 20f)])
        .Then(path => path.AddRectangle(new Rectangle(17f, 4f, 3f, 16f)));

    /// <summary>A cog. Settings.</summary>
    public static PathBuilder Settings { get; } = Cog();

    /// <summary>Two panes and a sidebar. A layout.</summary>
    public static PathBuilder Layout { get; } = Outline(
        [new Vector2(3f, 4f), new Vector2(21f, 4f), new Vector2(21f, 20f), new Vector2(3f, 20f)],
        closed: true
    ).Then(path => Line(path, new Vector2(9f, 4f), new Vector2(9f, 20f)));

    /// <summary>A hammer's head. Build.</summary>
    public static PathBuilder Build { get; } = Line(new PathBuilder(), new Vector2(5f, 20f), new Vector2(14f, 11f))
        .Then(path => Filled([
            new Vector2(12f, 9f), new Vector2(16f, 5f), new Vector2(21f, 10f), new Vector2(17f, 14f)
        ], into: path));

    /// <summary>A stopwatch. The profiler, and anything that measures.</summary>
    public static PathBuilder Profiler { get; } = Ring(12f, 13.5f, 7.5f, 1.8f)
        .Then(path => Line(path, new Vector2(12f, 13.5f), new Vector2(12f, 8.5f)))
        .Then(path => Line(path, new Vector2(9.5f, 3f), new Vector2(14.5f, 3f)))
        .Then(path => Line(path, new Vector2(12f, 3f), new Vector2(12f, 6f)));

    /// <summary>A speech line and a chevron. The console.</summary>
    public static PathBuilder Console { get; } = Outline(
        [new Vector2(3f, 4f), new Vector2(21f, 4f), new Vector2(21f, 20f), new Vector2(3f, 20f)],
        closed: true
    ).Then(path => Line(path, new Vector2(7f, 10f), new Vector2(10.5f, 13f), new Vector2(7f, 16f)))
        .Then(path => Line(path, new Vector2(13f, 16f), new Vector2(18f, 16f)));

    /// <summary>A folder, closed. What a directory row and a directory tile draw.</summary>
    public static PathBuilder Folder { get; } = Outline(
        [
            new Vector2(3f, 19.5f), new Vector2(3f, 5f), new Vector2(9.5f, 5f), new Vector2(11.5f, 7.5f),
            new Vector2(21f, 7.5f), new Vector2(21f, 19.5f)
        ],
        closed: true
    );

    /// <summary>A page with a corner turned. A file nothing more specific claims.</summary>
    public static PathBuilder File { get; } = Outline(
        [
            new Vector2(6f, 3f), new Vector2(14f, 3f), new Vector2(19f, 8f), new Vector2(19f, 21f),
            new Vector2(6f, 21f)
        ],
        closed: true
    ).Then(path => Line(path, new Vector2(14f, 3f), new Vector2(14f, 8f), new Vector2(19f, 8f)));

    /// <summary>A wireframe box. An entity carrying a mesh.</summary>
    /// <remarks>
    ///     An isometric cube rather than a square: a square is what a hundred other glyphs are, and
    ///     the whole job of a row's icon is to be recognised without being read.
    /// </remarks>
    public static PathBuilder Cube { get; } = Outline(
        [
            new Vector2(12f, 2.5f), new Vector2(20.5f, 7f), new Vector2(20.5f, 17f), new Vector2(12f, 21.5f),
            new Vector2(3.5f, 17f), new Vector2(3.5f, 7f)
        ],
        closed: true,
        width: 1.6f
    ).Then(path => Line(path, new Vector2(3.5f, 7f), new Vector2(12f, 11.5f), new Vector2(20.5f, 7f)))
        .Then(path => Line(path, new Vector2(12f, 11.5f), new Vector2(12f, 21.5f)));

    /// <summary>A disc with rays. An entity carrying a light.</summary>
    public static PathBuilder Light { get; } = Ring(12f, 12f, 4.2f, 1.6f)
        .Then(path => Line(path, new Vector2(12f, 2f), new Vector2(12f, 5f)))
        .Then(path => Line(path, new Vector2(12f, 19f), new Vector2(12f, 22f)))
        .Then(path => Line(path, new Vector2(2f, 12f), new Vector2(5f, 12f)))
        .Then(path => Line(path, new Vector2(19f, 12f), new Vector2(22f, 12f)))
        .Then(path => Line(path, new Vector2(5f, 5f), new Vector2(7.2f, 7.2f)))
        .Then(path => Line(path, new Vector2(16.8f, 16.8f), new Vector2(19f, 19f)))
        .Then(path => Line(path, new Vector2(19f, 5f), new Vector2(16.8f, 7.2f)))
        .Then(path => Line(path, new Vector2(7.2f, 16.8f), new Vector2(5f, 19f)));

    /// <summary>A body and a lens hood. An entity carrying a camera.</summary>
    public static PathBuilder Camera { get; } = Outline(
        [new Vector2(3f, 7f), new Vector2(14f, 7f), new Vector2(14f, 17f), new Vector2(3f, 17f)],
        closed: true,
        width: 1.6f
    ).Then(path => Filled([
        new Vector2(16f, 9.5f), new Vector2(21f, 6.5f), new Vector2(21f, 17.5f), new Vector2(16f, 14.5f)
    ], path));

    /// <summary>Three axes from a point. An entity carrying nothing but a transform.</summary>
    public static PathBuilder Entity { get; } = Line(
        new PathBuilder(),
        new Vector2(12f, 12f),
        new Vector2(12f, 4f)
    ).Then(path => Line(path, new Vector2(12f, 12f), new Vector2(20f, 16f)))
        .Then(path => Line(path, new Vector2(12f, 12f), new Vector2(4f, 16f)))
        .Then(path => Disc(12f, 12f, 2f, path));

    /// <summary>A chip with pins. A component, where nothing more specific is declared.</summary>
    /// <remarks>
    ///     ⚠ <b>A fallback picture is not the same as no picture, which is what an inspector full of
    ///     unlabelled foldouts had.</b> Only three component types ship a <c>TypeIcon</c>, so a header
    ///     that drew one only when a registration existed drew nothing for a transform, a rigid body,
    ///     an audio source or anything a game declares — a column of headers where the icon slot was
    ///     empty on most rows and occupied on a few, which reads as a rendering fault rather than as a
    ///     distinction. A generic glyph makes the slot mean "this is a component", and the specific
    ///     ones then mean something on top of that.
    ///     <para>
    ///         Deliberately not the cube: that one is geometry, and a fallback that looked like a
    ///         mesh would say something false about every component that is not one.
    ///     </para>
    /// </remarks>
    public static PathBuilder Component { get; } = Outline(
        [new Vector2(7f, 7f), new Vector2(17f, 7f), new Vector2(17f, 17f), new Vector2(7f, 17f)],
        closed: true,
        width: 1.6f
    ).Then(path => Line(path, new Vector2(3.5f, 10f), new Vector2(7f, 10f)))
        .Then(path => Line(path, new Vector2(3.5f, 14f), new Vector2(7f, 14f)))
        .Then(path => Line(path, new Vector2(17f, 10f), new Vector2(20.5f, 10f)))
        .Then(path => Line(path, new Vector2(17f, 14f), new Vector2(20.5f, 14f)));

    /// <summary>A cone and two arcs. An entity that makes a noise.</summary>
    public static PathBuilder Speaker { get; } = Filled([
        new Vector2(4f, 9.5f), new Vector2(7.5f, 9.5f), new Vector2(11.5f, 5.5f), new Vector2(11.5f, 18.5f),
        new Vector2(7.5f, 14.5f), new Vector2(4f, 14.5f)
    ]).Then(path => Line(path, new Vector2(14.5f, 9f), new Vector2(16.5f, 12f), new Vector2(14.5f, 15f)))
        .Then(path => Line(path, new Vector2(18f, 6.5f), new Vector2(20.5f, 12f), new Vector2(18f, 17.5f)));

    /// <summary>An open eye. A row the scene is drawing.</summary>
    /// <remarks>
    ///     ⚠ <b>Here rather than in <c>ControlIcons</c>, which the padlock beside it comes from.</b>
    ///     That file's bar is "a shape without which a control in <i>that</i> assembly cannot be
    ///     drawn", and no control there has a visibility toggle — an outliner does, and an outliner
    ///     is the editor's. The two glyphs sit side by side on a row all the same, which is why this
    ///     one is drawn at the padlock's weight rather than at this file's.
    /// </remarks>
    public static PathBuilder Eye { get; } = Lens().Then(path => Ring(12f, 12f, 2.5f, 1.7f, into: path));

    /// <summary>The same eye with a stroke through it. A row that is hidden.</summary>
    /// <remarks>
    ///     ⚠ <b>A second glyph rather than the same one dimmed.</b> Hiding is the state a user goes
    ///     looking for when something has vanished from the viewport, and "which of these two eyes is
    ///     the faint one" is not a question a column of forty rows can be asked. The slash reads at a
    ///     glance and reads on a monochrome skin, which is <c>ControlIcons.Unlock</c>'s argument.
    /// </remarks>
    public static PathBuilder EyeOff { get; } = Lens()
        .Then(path => Ring(12f, 12f, 2.5f, 1.7f, into: path))
        .Then(path => Line(path, new Vector2(4.5f, 20f), new Vector2(19.5f, 4f)));

    /// <summary>A question mark's dot and hook. Help.</summary>
    public static PathBuilder Help { get; } = Ring(12f, 12f, 9f, 1.8f)
        .Then(path => Line(path, new Vector2(9f, 9.5f), new Vector2(12f, 7.5f), new Vector2(14.5f, 10f), new Vector2(12f, 13f)))
        .Then(path => Line(path, new Vector2(12f, 13f), new Vector2(12f, 14.5f)))
        .Then(path => Disc(12f, 17.5f, 1.2f, into: path));

    /// <summary>Every glyph above, by the id a plugin or a theme would name it.</summary>
    /// <remarks>
    ///     ⚠ <b>An id per icon, because doc 20 asks for one.</b> A plugin putting a button on the
    ///     toolbar cannot reference a static property of an assembly it does not compile against —
    ///     it has a manifest and a name — so the set has to be reachable by string. Ordinal, like a
    ///     command id, and for the same reason.
    /// </remarks>
    public static IReadOnlyDictionary<string, PathBuilder> All { get; } = new Dictionary<string, PathBuilder>(
        StringComparer.Ordinal
    ) {
        ["radio-mark"] = RadioMark,
        ["new"] = New,
        ["open"] = Open,
        ["save"] = Save,
        ["undo"] = Undo,
        ["redo"] = Redo,
        ["delete"] = Delete,
        ["translate"] = Translate,
        ["rotate"] = Rotate,
        ["scale"] = Scale,
        ["world"] = World,
        ["snap"] = Snap,
        ["grid"] = Grid,
        ["play"] = Play,
        ["pause"] = Pause,
        ["stop"] = Stop,
        ["step"] = Step,
        ["settings"] = Settings,
        ["layout"] = Layout,
        ["build"] = Build,
        ["profiler"] = Profiler,
        ["console"] = Console,
        ["help"] = Help,
        ["folder"] = Folder,
        ["file"] = File,
        ["cube"] = Cube,
        ["light"] = Light,
        ["camera"] = Camera,
        ["entity"] = Entity,
        ["component"] = Component,
        ["speaker"] = Speaker,
        ["eye"] = Eye,
        ["eye-off"] = EyeOff
    };

    /// <summary>The glyph with an id, or <see langword="null" />.</summary>
    /// <param name="id">Its id, as <see cref="All" /> keys it.</param>
    /// <returns>The glyph, or <see langword="null" /> — which a toolbar draws as a label.</returns>
    public static PathBuilder? Find(string id) {
        ArgumentNullException.ThrowIfNull(id);
        return All.GetValueOrDefault(id);
    }

    /// <summary>Runs a step against a path and hands the path back, so the shapes above read as one.</summary>
    /// <remarks>
    ///     An expression-bodied glyph is worth a three-line helper: a shape assembled from four
    ///     statements needs a local, a name and a return, which for twenty-odd icons is sixty lines
    ///     that say nothing.
    /// </remarks>
    static PathBuilder Then(this PathBuilder path, Action<PathBuilder> step) {
        step(path);
        return path;
    }

    /// <summary>A polyline, expanded to a fillable outline.</summary>
    /// <remarks>
    ///     ⚠ <b>Filled rather than stroked, which is <c>ControlIcons</c>' argument.</b> A stroke
    ///     command carries one thickness in document space, so an icon authored at 24 and drawn at 16
    ///     would keep its line weight and read as heavy. Expanding here makes the weight part of the
    ///     geometry, so it scales with everything else — and it is why every shape in this file is a
    ///     closed area even when it looks like a line.
    /// </remarks>
    static PathBuilder Line(PathBuilder into, params ReadOnlySpan<Vector2> points) => Stroke(into, points, 1.8f);

    static PathBuilder Outline(ReadOnlySpan<Vector2> points, bool closed, float width = 1.8f) {
        var path = new PathBuilder();
        Stroke(path, points, width);

        if (closed && points.Length > 2) {
            Stroke(path, [points[^1], points[0]], width);
        }

        return path;
    }

    static PathBuilder Filled(ReadOnlySpan<Vector2> points, PathBuilder? into = null) {
        var path = into ?? new PathBuilder();

        if (points.Length < 3) {
            return path;
        }

        path.MoveTo(points[0]);

        for (var index = 1; index < points.Length; index++) {
            path.LineTo(points[index]);
        }

        path.Close();
        return path;
    }

    static PathBuilder Disc(float x, float y, float radius, PathBuilder? into = null) =>
        (into ?? new PathBuilder()).AddEllipse(
            new Rectangle(x - radius, y - radius, radius * 2f, radius * 2f)
        );

    /// <summary>An annulus, as a polygon per edge rather than as two ellipses.</summary>
    /// <remarks>
    ///     ⚠ <b>Two ellipses would need an even-odd fill rule to leave a hole, and the draw list has
    ///     one rule.</b> Stroking the circumference as a closed polyline gives a ring that fills
    ///     correctly whatever the rule is, at the cost of a segment count — thirty-two, which at
    ///     sixteen pixels is smoother than the display.
    /// </remarks>
    static PathBuilder Ring(float x, float y, float radius, float width, float sweep = 1f, PathBuilder? into = null) {
        var path = into ?? new PathBuilder();
        var steps = Math.Max(3, (int) (32 * sweep));
        var points = new Vector2[steps + 1];

        for (var index = 0; index <= steps; index++) {
            var angle = index / (float) steps * sweep * MathF.Tau;
            points[index] = new Vector2(x + (MathF.Cos(angle) * radius), y + (MathF.Sin(angle) * radius));
        }

        return Stroke(path, points, width);
    }

    /// <summary>A solid triangle pointing along a direction, for an arrow's end.</summary>
    static PathBuilder Head(PathBuilder path, Vector2 tip, Vector2 direction) {
        var side = new Vector2(-direction.Y, direction.X) * 3f;
        var back = direction * 4.5f;

        return Filled([tip, tip + back + side, tip + back - side], path);
    }

    static PathBuilder Arrow(bool left) {
        var path = new PathBuilder();
        var sign = left ? -1f : 1f;

        // The bow: a half-ring, mirrored by negating the x offset from the centre.
        var points = new Vector2[13];

        for (var index = 0; index < points.Length; index++) {
            var angle = MathF.PI + (index / (float) (points.Length - 1) * MathF.PI * 0.9f);
            points[index] = new Vector2(12f + (MathF.Cos(angle) * 7f * sign), 14f + (MathF.Sin(angle) * 7f));
        }

        Stroke(path, points, 1.8f);
        return Head(path, points[0], new Vector2(sign, 1f));
    }

    /// <summary>An eye's outline: two arcs meeting in a point at each corner, as one closed polyline.</summary>
    /// <remarks>
    ///     ⚠ <b>Not an ellipse.</b> An eye drawn as one reads as a rugby ball with a dot in it — the
    ///     corners are what say "eye", and they only appear if the rim falls to zero faster than a
    ///     circle does. The exponent below is what does that, and is the whole difference between the
    ///     two shapes.
    /// </remarks>
    static PathBuilder Lens() {
        const int Steps = 16;

        var points = new Vector2[(Steps * 2) + 1];

        for (var index = 0; index <= Steps; index++) {
            points[index] = Rim(index / (float) Steps, up: true);
        }

        // Back along the underside, ending on the point the top started from — so the outline closes
        // itself without a segment that doubles back over the first one.
        for (var index = 0; index < Steps; index++) {
            points[Steps + 1 + index] = Rim((Steps - 1 - index) / (float) Steps, up: false);
        }

        return Stroke(new PathBuilder(), points, 1.7f);

        static Vector2 Rim(float along, bool up) {
            var x = 3f + (along * 18f);
            var offset = (x - 12f) / 9f;
            var lift = MathF.Pow(MathF.Max(0f, 1f - (offset * offset)), 0.72f) * 5.4f;

            return new Vector2(x, up ? 12f - lift : 12f + lift);
        }
    }

    static PathBuilder Cog() {
        var path = Ring(12f, 12f, 8f, 2f);

        // Eight teeth, each a rectangle rotated about the centre — written as a quad rather than as a
        // transform because a PathBuilder has no matrix and four corners is the whole of the work.
        for (var tooth = 0; tooth < 8; tooth++) {
            var angle = tooth / 8f * MathF.Tau;
            var along = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var across = new Vector2(-along.Y, along.X) * 1.6f;

            var inner = new Vector2(12f, 12f) + (along * 7f);
            var outer = new Vector2(12f, 12f) + (along * 10.5f);

            Filled([inner + across, outer + across, outer - across, inner - across], path);
        }

        Ring(12f, 12f, 3.2f, 1.8f, into: path);
        return path;
    }

    /// <inheritdoc cref="Line" />
    static PathBuilder Stroke(PathBuilder path, ReadOnlySpan<Vector2> points, float width) {
        var half = width * 0.5f;

        for (var index = 0; index + 1 < points.Length; index++) {
            var from = points[index];
            var to = points[index + 1];

            var delta = to - from;
            var length = delta.Length();

            if (length <= float.Epsilon) {
                continue;
            }

            var normal = new Vector2(-delta.Y, delta.X) / length * half;

            path.MoveTo(from + normal);
            path.LineTo(to + normal);
            path.LineTo(to - normal);
            path.LineTo(from - normal);
            path.Close();
        }

        // The joints, as squares. The ends are left butted, which is what every shape here wants —
        // a rounded cap would be a curve per end for a difference of a pixel.
        for (var index = 1; index + 1 < points.Length; index++) {
            path.AddRectangle(new Rectangle(points[index].X - half, points[index].Y - half, width, width));
        }

        return path;
    }
}
