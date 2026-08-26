// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Ecs;
using Vixen.Core.Mathematics;
using Vixen.Editor.Ui;
using Vixen.Engine.Cameras;
using Vixen.Engine.Players;
using Vixen.Physics.Characters;
using Vixen.Physics.Ecs;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Terrain;
using Vixen.Rendering.Water;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Water.Physics;

namespace Vixen.Editor.App;

/// <summary>The editor's glyphs, as SVG path data on the grid every icon set authors against.</summary>
/// <remarks>
///     <para>
///         <b>Written as path data because there is a parser now.</b> <c>EditorIcons</c> is two dozen
///         shapes assembled from <c>Line</c>, <c>Ring</c> and <c>Disc</c> calls, which is what you do
///         when a string of path data is not something the engine can read — and every one of them
///         took a paragraph of arithmetic to say what <c>M4 9h3l4-4v14l-4-4H4z</c> says. See
///         <see cref="SvgPath" />. The older file is not being rewritten: its glyphs are the chrome's,
///         they work, and a rewrite would be churn with a pixel-diff at the end of it.
///     </para>
///     <para>
///         ⚠ <b>Filled shapes rather than strokes, which is what makes them read at 16 pixels.</b>
///         A stroked outline at icon size is a one-pixel line that either aliases or disappears
///         depending on the device scale; a filled silhouette is legible at any size and is what
///         Material, Fluent and every platform icon set switched to. Strokes are used only where the
///         shape <i>is</i> a line — an arc of sound, a path a camera travels.
///     </para>
///     <para>
///         ⚠ <b>Colour is per family and the shape is per type, and the split is deliberate.</b> An
///         inspector with nine camera components on it is unreadable if all nine are the same picture
///         and equally unreadable if they are nine unrelated pictures in nine colours. One hue per
///         subsystem makes the panel scannable — this block is the camera rig, that one is audio —
///         and the glyph inside it says which member. <c>StandardIcons</c> makes the same argument
///         about the Project grid.
///     </para>
///     <para>
///         ⚠ <b>Literal colours, not tokens, and they are the same in both themes.</b> These are
///         Material's 400-level hues, which were chosen to sit on white and on charcoal alike; the
///         alternative — a token per family — would be eleven custom properties for a difference
///         nobody would author.
///     </para>
/// </remarks>
static class MaterialIcons {
    // ── Families ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Audio: a pink that nothing else in the editor uses.</summary>
    public static Color4 Audio { get; } = Hue(0xEC407A);

    /// <summary>Cameras and the rig components that drive them.</summary>
    public static Color4 Camera { get; } = Hue(0x5C6BC0);

    /// <summary>Anything about the player: input, intent, the controller itself.</summary>
    public static Color4 Player { get; } = Hue(0x26A69A);

    /// <summary>Rendering, which is the family the eye expects to be warm.</summary>
    public static Color4 Render { get; } = Hue(0xFFA726);

    /// <summary>Geometry, as distinct from the light falling on it.</summary>
    public static Color4 Geometry { get; } = Hue(0xAB47BC);

    /// <summary>Mass and motion: bodies, colliders, the velocities they carry.</summary>
    /// <remarks>
    ///     ⚠ <b>Steel, and the only desaturated family here on purpose.</b> A body is a property of
    ///     a thing rather than a thing itself — it sits in an inspector <em>beside</em> the mesh and
    ///     the material rather than instead of them — so a physics row that shouted would make every
    ///     object look like a physics object. It is a hue and not a grey, because grey reads as
    ///     disabled.
    /// </remarks>
    public static Color4 Physics { get; } = Hue(0x78909C);

    /// <summary>Terrain, foliage and the splines that place them.</summary>
    public static Color4 Terrain { get; } = Hue(0x66BB6A);

    /// <summary>Water, and the things that float on it.</summary>
    /// <remarks>
    ///     ⚠ <b>A blue distinct from <see cref="Camera" />'s indigo</b>, which is the only other cool
    ///     hue here: a zone row and a virtual-camera row sit next to each other in an outliner far
    ///     more often than either sits next to anything else.
    /// </remarks>
    public static Color4 Water { get; } = Hue(0x29B6F6);

    // ── Glyphs ──────────────────────────────────────────────────────────────────────────────────
    //
    // Every one of these is a `d` attribute on a 24 grid. They are readable as text, which is the
    // whole return on the parser: a glyph is one line and changing it does not mean re-deriving a
    // dozen control points by hand.

    /// <summary>A speaker cone. Anything that makes a noise.</summary>
    const string Speaker = "M3 9h3.5L11 4.8v14.4L6.5 15H3z";

    /// <summary>The two arcs that come out of it.</summary>
    const string SoundWaves = "M14 9.2a4.4 4.4 0 0 1 0 5.6M16.8 6.6a8.4 8.4 0 0 1 0 10.8";

    /// <summary>A headband and two cups. Whatever is listening.</summary>
    const string Headphones = "M3.6 13.2h3.2v7H3.6zM17.2 13.2h3.2v7h-3.2z";

    /// <summary>Its band, which is the one part that is a line.</summary>
    const string HeadphoneBand = "M4.4 13.2v-1.6a7.6 7.6 0 0 1 15.2 0v1.6";

    /// <summary>Three arcs spreading out. A field rather than a point.</summary>
    const string Waves = "M6.6 8.4a7.6 7.6 0 0 1 0 7.2M10.6 6.2a12 12 0 0 1 0 11.6M2.8 10.4a3.6 3.6 0 0 1 0 3.2";

    /// <summary>A band of swell, filled. Water seen from the side, which is how a surface reads.</summary>
    const string Swell = "M2 12.6c2.4 0 2.4-2.6 4.8-2.6s2.4 2.6 4.8 2.6 2.4-2.6 4.8-2.6 2.4 2.6 4.8 2.6v3.4"
        + "c-2.4 0-2.4-2.6-4.8-2.6s-2.4 2.6-4.8 2.6-2.4-2.6-4.8-2.6-2.4 2.6-4.8 2.6z";

    /// <summary>One crest, as a stroke. What is drawn over something that is in the water.</summary>
    const string Crest = "M2.4 16.6c2.4 0 2.4-2.6 4.8-2.6s2.4 2.6 4.8 2.6 2.4-2.6 4.8-2.6 2.4 2.6 4.8 2.6";

    /// <summary>A body and a lens hood. A camera.</summary>
    const string Movie = "M2.6 6.4h12.2v11.2H2.6zM16.6 10.2l4.8-3.2v10l-4.8-3.2z";

    /// <summary>A clapperboard. The thing that decides which camera is live.</summary>
    const string Director = "M2.6 9.4h18.8v9.2H2.6zM2.6 5.4h4.4l1.6 3H4.2zM8.6 5.4H13l1.6 3h-4.4zM14.6 5.4H19l1.6 3h-4.4z";

    /// <summary>A rectangle with corner marks. Framing.</summary>
    const string Frame = "M3 3h6v2.2H5.2V9H3zM15 3h6v6h-2.2V5.2H15zM3 15h2.2v3.8H9V21H3zM21 15v6h-6v-2.2h3.8V15z"
        + "M9.6 9.6h4.8v4.8H9.6z";

    /// <summary>A ring and a dot. Where a camera is pointed.</summary>
    const string Crosshair = "M11 2h2v4h-2zM11 18h2v4h-2zM2 11h4v2H2zM18 11h4v2h-4zM10.4 10.4h3.2v3.2h-3.2z";

    /// <summary>An ellipse round a dot. Something that goes around something else.</summary>
    const string Orbit = "M10.4 10.4h3.2v3.2h-3.2z";

    /// <summary>Its path, drawn rather than filled — an orbit is a line.</summary>
    const string OrbitRing = "M12 6.4c5.3 0 9.6 2.5 9.6 5.6s-4.3 5.6-9.6 5.6-9.6-2.5-9.6-5.6 4.3-5.6 9.6-5.6z";

    /// <summary>A rail with two stops. A dolly track.</summary>
    const string Track = "M4.6 13.6a2.6 2.6 0 1 1 0-5.2 2.6 2.6 0 0 1 0 5.2zM19.4 13.6a2.6 2.6 0 1 1 0-5.2 2.6 2.6 0 0 1 0 5.2z"
        + "M4.6 9.8h14.8v2.4H4.6z";

    /// <summary>A shield. Something that stops the camera being blocked.</summary>
    const string Shield = "M12 2.4 20.4 5.6v6c0 4.6-3.6 8.4-8.4 10-4.8-1.6-8.4-5.4-8.4-10v-6z";

    /// <summary>A jagged line. Shake, noise, an impulse.</summary>
    const string Noise = "M1.6 12.8 4.4 6l2.8 12L10 4l2.8 16L15.6 8l2.8 8 1.6-3.2h2.4v2.4h-1l-3 6-2.8-8-2.8 12L7.2 20 4.4 8 2.6 12.8z";

    /// <summary>A box drawn as four corner brackets. A volume something is confined to.</summary>
    const string Bounds = "M3 3h7v2.2H5.2v4.6H3zM21 3v6.8h-2.2V5.2H14V3zM3 14.2h2.2v4.6H10V21H3zM21 14.2V21h-7v-2.2h4.8v-4.6z";

    /// <summary>A bulb with rays. A light.</summary>
    const string Bulb = "M12 2.6a6.4 6.4 0 0 1 3.8 11.6v2.2H8.2v-2.2A6.4 6.4 0 0 1 12 2.6zM8.6 17.6h6.8v1.8H8.6z"
        + "M9.6 20.4h4.8v1.4H9.6z";

    /// <summary>An isometric box. Geometry.</summary>
    const string Cube = "M12 2.2 21 7v10l-9 4.8L3 17V7zM12 4.6 5.4 8.2 12 11.8l6.6-3.6z";

    /// <summary>The line a thing is nailed to. Under a box: it does not move.</summary>
    const string Ground = "M3.4 22.2h17.2";

    /// <summary>A four-pointed star and two smaller ones. Effects.</summary>
    const string Sparkle = "M9.4 3 11.4 8 16.4 10 11.4 12 9.4 17 7.4 12 2.4 10 7.4 8z"
        + "M17.6 12.6 18.7 15.3 21.4 16.4 18.7 17.5 17.6 20.2 16.5 17.5 13.8 16.4 16.5 15.3z"
        + "M18.4 2.6 19.1 4.4 20.9 5.1 19.1 5.8 18.4 7.6 17.7 5.8 15.9 5.1 17.7 4.4z";

    /// <summary>Three sliders. A grade, a filter, a post-process stack.</summary>
    const string Tune = "M2.6 5.4h8.2v2.2H2.6zM14.6 5.4h6.8v2.2h-6.8zM2.6 10.9h4.2v2.2H2.6zM10.6 10.9h10.8v2.2H10.6z"
        + "M2.6 16.4h11.2v2.2H2.6zM17.6 16.4h3.8v2.2h-3.8z"
        + "M10.8 3.6h2.2v5.8h-2.2zM6.8 9.1H9v5.8H6.8zM13.8 14.6H16v5.8h-2.2z";

    /// <summary>Two peaks. Terrain.</summary>
    const string Mountains = "M9.2 6.6 15 15.4H3.4zM16.2 9.4 21.8 18H10.6z";

    /// <summary>Blades. Foliage.</summary>
    const string Grass = "M11 20.4v-4.6c0-4.4-2.6-8-6.6-9.4 1 4 1.4 7.2 1.2 9.6-.2 1.8 1.4 3.2 3.4 3.2z"
        + "M13 20.4v-3.2c0-3.6 2.4-6.6 6.2-7.8-1 3.4-1.4 6-1.2 8-.1 1.6-1.4 2.9-3.2 3z";

    /// <summary>A curve with two handles. A spline.</summary>
    const string Spline = "M3 17.6c6.4 0 8.6-11.2 18-11.2";

    /// <summary>Its end points, which are the part you grab.</summary>
    const string SplineKnots = "M1 15.6h4.4V20H1zM18.6 4.2H23v4.4h-4.4z";

    /// <summary>A head and shoulders. A player.</summary>
    const string Person = "M12 3.2a3.8 3.8 0 1 1 0 7.6 3.8 3.8 0 0 1 0-7.6zM12 12.4c4.2 0 7.6 2.4 7.6 5.4v3H4.4v-3c0-3 3.4-5.4 7.6-5.4z";

    /// <summary>A stick and two buttons. Input.</summary>
    const string Gamepad = "M7.4 7.4h9.2a5.6 5.6 0 0 1 0 11.2H7.4a5.6 5.6 0 0 1 0-11.2z"
        + "M5.4 12h4.8v1.9H5.4zM6.9 10.6h1.9v4.8H6.9z"
        + "M15.4 10.8a1.4 1.4 0 1 1 0 2.8 1.4 1.4 0 0 1 0-2.8zM18.2 13.6a1.4 1.4 0 1 1 0 2.8 1.4 1.4 0 0 1 0-2.8z";

    /// <summary>An arrow bent round. Rotation.</summary>
    const string Rotate = "M12 4.4V1.6l4.4 3.6L12 8.8V6.4a5.8 5.8 0 1 0 5.8 5.8h2A7.8 7.8 0 1 1 12 4.4z";

    /// <summary>A shaft and a head. A direction something is going, rather than one it faces.</summary>
    const string Arrow = "M3.6 12h13.2M12.6 7.4 17.6 12l-5 4.6";

    /// <summary>A link. A reference to something that lives elsewhere.</summary>
    const string Link = "M9.4 12.9h5.2v-1.8H9.4zM7.6 7.6h3.6v1.9H7.6a2.5 2.5 0 0 0 0 5h3.6v1.9H7.6a4.4 4.4 0 0 1 0-8.8z"
        + "M12.8 7.6h3.6a4.4 4.4 0 0 1 0 8.8h-3.6v-1.9h3.6a2.5 2.5 0 0 0 0-5h-3.6z";

    /// <summary>A bell. Something that fires rather than something that plays.</summary>
    const string Event = "M12 2.2a1.7 1.7 0 0 1 1.7 1.7v.6a6.4 6.4 0 0 1 4.7 6.2v4l1.8 2.4v1.3H3.8v-1.3l1.8-2.4v-4a6.4 6.4 0 0 1 4.7-6.2v-.6A1.7 1.7 0 0 1 12 2.2z"
        + "M9.8 19.4h4.4a2.2 2.2 0 0 1-4.4 0z";

    // ── The set ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>One glyph, filled in a colour.</summary>
    static IconArt Filled(string data, Color4 colour) =>
        new(new Rectangle(0f, 0f, 24f, 24f), [new IconPath(Path(data), IconPaint.Of(colour))]);

    /// <summary>A filled glyph with a stroked one over it, for the shapes that are partly lines.</summary>
    /// <remarks>
    ///     ⚠ <b>The stroke is the second path, so it lands over the fill.</b> An arc drawn under a
    ///     silhouette is an arc you cannot see, which for a speaker is most of what says it is a
    ///     speaker rather than a flag.
    /// </remarks>
    static IconArt Struck(string fill, string stroke, Color4 colour, float width = 1.8f) =>
        new(
            new Rectangle(0f, 0f, 24f, 24f),
            [
                new IconPath(Path(fill), IconPaint.Of(colour)),
                new IconPath(Path(stroke), IconPaint.None, IconPaint.Of(colour), width)
            ]
        );

    /// <summary>A glyph that is nothing but lines.</summary>
    static IconArt Line(string data, Color4 colour, float width = 1.8f) =>
        new(new Rectangle(0f, 0f, 24f, 24f), [new IconPath(Path(data), IconPaint.None, IconPaint.Of(colour), width)]);

    /// <summary>
    ///     ⚠ <b>Parsed once into a static, because an <see cref="IconArt" /> is shared by every
    ///     element that draws it</b> — see its own remarks — and because a parse per icon per panel
    ///     rebuild would put a string scan in the inspector's build path for no reason at all.
    /// </summary>
    static PathBuilder Path(string data) =>
        SvgPath.TryParse(data)
        ?? throw new InvalidOperationException(
            $"'{data}' is not path data. It is a literal in this file, so this is a typo rather than "
            + "anything a user did — see SvgPathTests for what the grammar accepts."
        );

    /// <summary>An <c>0xRRGGBB</c> literal as a colour, so the table above reads like a palette.</summary>
    static Color4 Hue(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    // ── Files and folders ───────────────────────────────────────────────────────────────────────

    /// <summary>A folder's back panel and tab.</summary>
    const string FolderBack = "M2.2 5.8A1.8 1.8 0 0 1 4 4h5.4l2.3 2.5h8.5A1.8 1.8 0 0 1 22 8.3v1.5H2.2z";

    /// <summary>Its front, which is the panel a gradient runs down.</summary>
    const string FolderFront = "M2.2 9.4h19.6v8.8A1.8 1.8 0 0 1 20 20H4a1.8 1.8 0 0 1-1.8-1.8z";

    /// <summary>The band of light along the top of the front panel.</summary>
    const string FolderSheen = "M2.2 9.4h19.6v2.6H2.2z";

    /// <summary>A page with the corner turned.</summary>
    const string PageBody = "M5 2.6h8.6L19.2 8.3v13.1H5z";

    /// <summary>The turned corner itself, which is what makes it read as paper.</summary>
    const string PageFold = "M13.6 2.6 19.2 8.3h-5.6z";

    /// <summary>
    ///     A folder in the blue every desktop uses, faked as a gradient by stacking three tones.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Three flat paths rather than one gradient, and it is worth saying why plainly.</b>
    ///     <see cref="IconPaint" /> has no gradient and the drawing layer's gradients belong to
    ///     <c>BoxStyle</c> — they are a lane on a rectangle shape, not something an arbitrary filled
    ///     path can carry, and threading one through would mean the tessellator, the shape struct and
    ///     the shader. What is here is the technique the flat icon sets use anyway: a darker back, a
    ///     mid front, and a band of light along the top edge. At sixteen pixels it is
    ///     indistinguishable from a two-stop ramp; at ninety-six you can see the step.
    /// </remarks>
    public static IconArt Folder { get; } = new(
        new Rectangle(0f, 0f, 24f, 24f),
        [
            new IconPath(Path(FolderBack), IconPaint.Of(Hue(0x2E77D0))),
            new IconPath(Path(FolderFront), IconPaint.Of(Hue(0x4A9BF7))),
            new IconPath(Path(FolderSheen), IconPaint.Of(Hue(0x62ACFF)))
        ]
    );

    /// <summary>A sheet of paper in a hue, with a glyph on it.</summary>
    /// <param name="hue">What kind of file it is, as a colour.</param>
    /// <param name="glyph">Path data drawn over the page, or <see langword="null" /> for a blank one.</param>
    /// <param name="mark">What the glyph is drawn in. White reads on every hue this is used with.</param>
    /// <returns>The art.</returns>
    /// <remarks>
    ///     ⚠ <b>A page plus a mark rather than a bare glyph, which is what the Project grid had.</b>
    ///     A tile is a picture with a caption under it — <c>StandardIcons</c>'s own words — and a
    ///     floating cog at tile size reads as an icon that failed to load. A sheet of paper reads as a
    ///     file at a glance and the mark on it says which kind, which is the arrangement every file
    ///     manager on earth arrived at.
    /// </remarks>
    public static IconArt Page(Color4 hue, string? glyph = null, Color4 mark = default) {
        List<IconPath> paths = [
            new(Path(PageBody), IconPaint.Of(hue)),
            new(Path(PageFold), IconPaint.Of(Darker(hue, 0.72f)))
        ];

        if (glyph is not null) {
            paths.Add(new IconPath(Path(glyph), IconPaint.Of(mark.A > 0f ? mark : new Color4(1f, 1f, 1f, 0.92f))));
        }

        return new IconArt(new Rectangle(0f, 0f, 24f, 24f), paths);
    }

    /// <summary>The same hue, further down. What the folded corner and the back panel are drawn in.</summary>
    static Color4 Darker(Color4 colour, float by) => new(colour.R * by, colour.G * by, colour.B * by, colour.A);

    /// <summary>A small mark for a page, drawn at about half scale in its middle.</summary>
    public static class Marks {
        /// <summary>Mountains in a frame. A texture.</summary>
        public const string Texture = "M7.4 11.6h9.4v6.6H7.4zM8.6 17.2l2.4-3 1.6 2 2-2.6 2.4 3.6z";

        /// <summary>A ringed globe. A scene.</summary>
        public const string Scene = "M12 10.4a4 4 0 1 1 0 8 4 4 0 0 1 0-8zm0 1.4a2.6 2.6 0 1 0 0 5.2 2.6 2.6 0 0 0 0-5.2z"
            + "M7.6 13.7h8.8v1.4H7.6z";

        /// <summary>A little box. A model or a mesh.</summary>
        public const string Model = "M12 9.6 17 12.3v5.4L12 20.4 7 17.7v-5.4zm0 1.7-3.2 1.7 3.2 1.7 3.2-1.7z";

        /// <summary>A sphere with a highlight. A material.</summary>
        public const string Material = "M12 9.8a4.6 4.6 0 1 1 0 9.2 4.6 4.6 0 0 1 0-9.2zM10.1 11.6a2.4 2.4 0 0 0 2.6 1.2 2.4 2.4 0 0 1-2.6-1.2z";

        /// <summary>A note. Audio.</summary>
        public const string Audio = "M15.6 9.4v6.2a2.2 2.2 0 1 1-1.5-2.1V11l-3.9.9v4.9a2.2 2.2 0 1 1-1.5-2.1v-4.4z";

        /// <summary>A play triangle. Video.</summary>
        public const string Video = "M9.8 10.6 16.6 14.6 9.8 18.6z";

        /// <summary>Braces. Something the editor wrote itself.</summary>
        public const string Native = "M10.4 9.8v1.5h-1v2.4H8.2v1.6h1.2v2.4h1v1.5H8.6a1.4 1.4 0 0 1-1.4-1.4v-2.5H6.2v-1.6h1v-2.5a1.4 1.4 0 0 1 1.4-1.4z"
            + "M13.6 9.8h1.8a1.4 1.4 0 0 1 1.4 1.4v2.5h1v1.6h-1v2.5a1.4 1.4 0 0 1-1.4 1.4h-1.8v-1.5h1v-2.4h1.2v-1.6h-1.2v-2.4h-1z";
    }

    /// <summary>The picture for every component the editor ships.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every one of them, which is the point.</b> Three types had a registration and the
    ///         other thirty drew the generic chip — an inspector where two rows in nine are
    ///         identifiable is one where the icon column is decoration. The list is by hand rather
    ///         than derived: what a component <i>looks like</i> is not a function of its name, and a
    ///         convention that guessed would be wrong in a way nobody could correct.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A plugin's component takes this same list's place with <c>[EditorIcon]</c></b>,
    ///         which is doc 36 § D6 and is the reason none of this is a <c>switch</c> anywhere.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<TypeIcon> Components { get; } = [
        // Audio.
        new(typeof(AudioSource), Struck(Speaker, SoundWaves, Audio)),
        new(typeof(AudioListenerComponent), Struck(Headphones, HeadphoneBand, Audio, 2f)),
        new(typeof(AudioSpatial), Line(Waves, Audio)),
        new(typeof(AudioReverbZoneRef), Line(Waves, Audio, 2.4f)),
        new(typeof(AudioClipRef), Filled(Link, Audio)),
        new(typeof(AudioEventRef), Filled(Event, Audio)),

        // Cameras, and the rig that drives one.
        new(typeof(Camera), Filled(Movie, Camera)),
        new(typeof(VirtualCamera), Filled(Movie, Camera)),
        new(typeof(CameraDirector), Filled(Director, Camera)),
        new(typeof(CameraConfiner), Filled(Bounds, Camera)),
        new(typeof(CameraOcclusion), Filled(Shield, Camera)),
        new(typeof(CameraNoise), Filled(Noise, Camera)),
        new(typeof(CameraImpulseListener), Filled(Event, Camera)),
        new(typeof(ComposerAim), Filled(Crosshair, Camera)),
        new(typeof(HardLookAim), Filled(Crosshair, Camera)),
        new(typeof(MatchTargetAim), Filled(Crosshair, Camera)),
        new(typeof(PovAim), Filled(Crosshair, Camera)),
        new(typeof(FramingBody), Filled(Frame, Camera)),
        new(typeof(FollowBody), Filled(Person, Camera)),
        new(typeof(HardLockBody), Filled(Link, Camera)),
        new(typeof(OrbitBody), Struck(Orbit, OrbitRing, Camera)),
        new(typeof(TrackedDollyBody), Filled(Track, Camera)),

        // The player.
        new(typeof(PlayerController), Filled(Person, Player)),
        new(typeof(MoveIntent), Filled(Gamepad, Player)),
        new(typeof(ControlRotation), Filled(Rotate, Player)),

        // What is drawn, and what the light does to it.
        new(typeof(Light), Filled(Bulb, Render)),
        new(typeof(PostProcessVolume), Filled(Tune, Render)),
        new(typeof(VfxEmitter), Filled(Sparkle, Render)),
        new(typeof(MeshRenderable), Filled(Cube, Geometry)),
        new(typeof(PrimitiveShape), Filled(Cube, Geometry)),

        // A box on a line: the whole content of the claim is that this one does not move, which is
        // what lets the sun's cascades keep its shadow between frames.
        new(typeof(StaticShadowCaster), Struck(Cube, Ground, Geometry)),

        // Sliders over a mesh, which is what a blend-shape weight list is: one number per shape, and
        // the shapes belong to the geometry rather than to this component.
        new(typeof(BlendShapeWeights), Struck(Cube, Tune, Geometry)),

        // Mass and motion. ⚠ These arrived in the editor's own set only when `Vixen.Editor.App`
        // came to reference `Vixen.Physics` for play mode — the components are years older, and the
        // icon test is what noticed the day they started shipping.
        new(typeof(RigidBody), Filled(Cube, Physics)),
        new(typeof(Collider), Line(Bounds, Physics)),
        new(typeof(LinearVelocity), Line(Arrow, Physics)),
        new(typeof(AngularVelocity), Line(Rotate, Physics)),

        // A person with a direction, in the physics family rather than the player's: a character
        // controller is a body that resolves against the world, and a game may drive one with no
        // player behind it at all.
        new(typeof(CharacterMovement), Struck(Person, Arrow, Physics)),

        // Terrain and what grows on it.
        new(typeof(TerrainComponent), Filled(Mountains, Terrain)),
        new(typeof(TerrainGrassComponent), Filled(Grass, Terrain)),

        // Grass inside the volume brackets: what the row says is "this is a painted region", which
        // is the one thing that distinguishes it from the grass layer above and the blocker below.
        new(typeof(FoliageVolumeComponent), Struck(Grass, Bounds, Terrain)),
        new(typeof(FoliageBlockerComponent), Filled(Grass, Terrain)),
        new(typeof(SplinePlacedComponent), Struck(SplineKnots, Spline, Terrain, 2f)),

        // Water, and what floats on it. The zone is swell inside the volume brackets — the same
        // "this is a painted region" reading `FoliageVolumeComponent` gets, because that is exactly
        // what distinguishes a window over the water from the water in it.
        new(typeof(WaterZoneComponent), Struck(Swell, Bounds, Water)),
        new(typeof(WaterBodyComponent), Filled(Swell, Water)),

        // And a hull with a waterline across it, which is the one thing a buoyancy row has to say.
        new(typeof(BuoyancyBody), Struck(Cube, Crest, Water)),
        new(typeof(BuoyancyState), Line(Crest, Water, 2.4f))
    ];
}
