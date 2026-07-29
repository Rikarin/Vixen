// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Text;

namespace Vixen.Ui;

/// <summary>What a draw command draws.</summary>
public enum DrawCommandKind : byte {
    /// <summary>A filled rectangle, with optional rounded corners.</summary>
    Rectangle,

    /// <summary>An outline drawn inside a rectangle's edges.</summary>
    Border,

    /// <summary>A blurred rectangle, drawn behind the element that cast it.</summary>
    /// <remarks>
    ///     The same quad and the same shader as <see cref="Rectangle" />, with a blur radius that
    ///     turns the distance field's one-pixel edge into a soft one. It is a separate kind rather
    ///     than a flag because the geometry differs: a shadow's quad is larger than the box it is
    ///     cast by, and the blur has to reach the edge of it.
    /// </remarks>
    Shadow,

    /// <summary>A run of positioned glyphs in one font.</summary>
    Text,

    /// <summary>A texture, stretched over a rectangle.</summary>
    /// <remarks>
    ///     An image, a video frame, a viewport's render target. What the texture <i>is</i> belongs to
    ///     the renderer — see <see cref="DrawCommand.Image" /> for why this layer names it with a
    ///     number rather than a handle.
    /// </remarks>
    Image,

    /// <summary>The inside of a path.</summary>
    Path,

    /// <summary>A line along a path.</summary>
    PathStroke,

    /// <summary>Everything after this is clipped to a rectangle, until the matching pop.</summary>
    ClipPush,

    /// <summary>Ends the clip the last push started.</summary>
    ClipPop
}

/// <summary>One thing to draw, in document space.</summary>
/// <remarks>
///     <para>
///         <b>One flat struct rather than a class per primitive</b>, with the fields a given kind
///         does not use left at zero. A draw list is walked once per frame in order and never
///         polymorphically dispatched on; a hierarchy would cost a pointer chase and an allocation
///         per command to model something the consumer answers with a <c>switch</c> anyway.
///     </para>
///     <para>
///         Comparable by value, which is what makes the frame diff a memcmp rather than a visitor.
///     </para>
/// </remarks>
/// <param name="Kind">What it draws.</param>
/// <param name="X">Its left edge in document space.</param>
/// <param name="Y">Its top edge.</param>
/// <param name="Width">Its width.</param>
/// <param name="Height">Its height.</param>
/// <param name="Color">Its colour, in linear space. Unused by the clip commands.</param>
/// <param name="Radius">Its corner radius. Zero for square corners.</param>
/// <param name="Thickness">A border's width. Zero for the other kinds.</param>
public readonly record struct DrawCommand(
    DrawCommandKind Kind,
    float X,
    float Y,
    float Width,
    float Height,
    Color4 Color,
    float Radius,
    float Thickness
) {
    /// <summary>Where this command's data starts in the side buffer its kind uses.</summary>
    /// <remarks>
    ///     <para>
    ///         Some things to draw are not a fixed number of numbers. A run of glyphs is as long as
    ///         its text, so it lives in <see cref="DrawList.Glyphs" /> and the command names a range
    ///         of it — which keeps the command a flat comparable struct and keeps the variable-length
    ///         part in one contiguous array a renderer can upload in a single copy.
    ///     </para>
    ///     <para>
    ///         Init-only rather than positional so that the kinds that use nothing here are still
    ///         written as the eight arguments they always were. They are still fields, so they are
    ///         still part of the value equality the frame diff is built on.
    ///     </para>
    /// </remarks>
    public int Offset { get; init; }

    /// <summary>How many entries of the side buffer this command uses.</summary>
    public int Length { get; init; }

    /// <summary>Which font, as an index into <see cref="DrawList.Fonts" />.</summary>
    /// <remarks>
    ///     An index rather than the face itself, so that the command stays a struct with no reference
    ///     to trace and so that batching can compare fonts with an integer. Only meaningful for
    ///     <see cref="DrawCommandKind.Text" />; zero and unread for everything else, like the other
    ///     fields a kind does not use.
    /// </remarks>
    public int Font { get; init; }

    /// <summary>Which texture, as the renderer's own name for one.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Opaque on purpose, exactly as <c>Viewport.RenderTarget</c> is.</b> A texture view
    ///         is <c>Vixen.Graphics</c>' vocabulary and this assembly does not reference it — the
    ///         whole bargain <c>Vixen.Ui</c> makes is that it describes what to draw and knows nothing
    ///         about how. A renderer registers a number for a texture it owns and is handed the number
    ///         back; nothing in between has to know what it stands for.
    ///     </para>
    ///     <para>
    ///         Zero is "no image", which is what a command of any other kind carries.
    ///     </para>
    /// </remarks>
    public ulong Image { get; init; }

    /// <summary>Which part of the texture to draw, as UVs from the top-left.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero-to-one and not pixels</b>, because the command does not know how big the texture
    ///     is — only the renderer does. A sub-rectangle is how a sprite sheet, a nine-slice and a
    ///     flipped video frame are all expressed, and the default covers the whole texture.
    /// </remarks>
    public Rectangle Source { get; init; } = new(0f, 0f, 1f, 1f);

    /// <summary>How the destination is cut for a nine-slice, in document pixels.</summary>
    /// <remarks>
    ///     <para>
    ///         Empty — the default — draws the image as one stretched quad, which is what every
    ///         image was before there was a nine-slice. Anything else cuts this command's rectangle
    ///         into nine and draws each cell from the matching cell of <see cref="Source" />, so a
    ///         panel's corners keep their size at any box size while its edges and middle stretch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not a new command kind, and that is the point.</b> A nine-sliced image is the same
    ///         texture through the same pipeline as a stretched one, so it carries the same
    ///         <see cref="DrawCommandKind.Image" /> and batches with the images around it — nine quads
    ///         in a run rather than a draw of its own. Making it a kind would have split every batch
    ///         it appeared in, to describe geometry the renderer never sees.
    ///     </para>
    /// </remarks>
    public NineSlice Slice { get; init; }

    /// <summary>How <see cref="Source" /> is cut to match <see cref="Slice" />, in UVs.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second inset rather than the same one, because the two are in different spaces
    ///         and neither can be derived from the other here.</b> The destination is in document
    ///         pixels and the source is in UVs, and converting between them needs the texture's size
    ///         in texels — which this assembly does not know, for exactly the reason <see cref="Source" />
    ///         is not in pixels either. Whoever registered the texture converts.
    ///     </para>
    ///     <para>
    ///         Empty falls back to one stretched quad even when <see cref="Slice" /> is set: a source
    ///         with no border to preserve is a plain image, and cutting it anyway would smear
    ///         zero-width strips of it along eight of the nine cells.
    ///     </para>
    /// </remarks>
    public NineSlice SourceSlice { get; init; }

    /// <summary>Whether a nine-slice leaves its middle cell undrawn.</summary>
    /// <remarks>
    ///     ⚠ <b>Inverted from how it reads, because a struct's default is all-zeroes.</b> Every other
    ///     framework spells this "fill centre" and defaults it to true; a <c>bool</c> that defaulted
    ///     to false under that name would make the ordinary nine-slice — a panel with a background —
    ///     the one a caller had to opt into, and a hollow frame the one they got by saying nothing.
    ///     The same argument <see cref="MiterLimit" /> makes about sentinels, answered by naming the
    ///     unusual case instead.
    /// </remarks>
    public bool HollowCentre { get; init; }

    /// <summary>The font size in pixels, which is what scales a glyph's outline.</summary>
    /// <remarks>
    ///     Carried even though the glyph positions are already in pixels, because a position is not a
    ///     size: the renderer still has to know how big to draw the glyph it has been told where to
    ///     put.
    /// </remarks>
    public float FontSize { get; init; }

    /// <summary>
    ///     Whether this box has a <see cref="BoxStyle" /> in <see cref="DrawList.Boxes" />, and where.
    /// </summary>
    /// <remarks>
    ///     ⚠ A box's side buffer holds at most one entry, so <see cref="Length" /> is zero or one —
    ///     which is the same <c>Offset</c>/<c>Length</c> pair a glyph run and a path use, meaning the
    ///     same thing, rather than a third convention for the one kind that needs a single record.
    ///     Zero means a plain box: a flat colour and the uniform <see cref="Radius" />.
    /// </remarks>
    public bool HasStyle => Length > 0;

    /// <summary>How a filled path decides what is inside it.</summary>
    /// <remarks>Only meaningful for <see cref="DrawCommandKind.Path" />.</remarks>
    public PathFillRule FillRule { get; init; }

    /// <summary>How a stroked path turns a corner.</summary>
    /// <remarks>
    ///     Only meaningful for <see cref="DrawCommandKind.PathStroke" />. On the command rather than
    ///     on whatever tessellates it, because a join is part of the stroke somebody asked for — the
    ///     same argument as <see cref="Thickness" />, which nobody would have put anywhere else.
    /// </remarks>
    public LineJoin Join { get; init; }

    /// <summary>How a stroked path's open ends are finished.</summary>
    public LineCap Cap { get; init; }

    /// <summary>How far a miter may reach, as a multiple of the half width.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero means the default</b>, which is four — CSS's and SVG's. A sentinel rather than a
    ///     real value because this is a struct: its default is all-zeroes, and a miter limit of zero
    ///     means every corner bevels, so a caller who set the thickness and nothing else would get a
    ///     shape with no corners at all. <see cref="Radius" /> gets away with a real zero because a
    ///     square corner is a sensible default; this does not.
    /// </remarks>
    public float MiterLimit { get; init; }
}

/// <summary>A frame's worth of drawing, and whether it differs from the last one.</summary>
/// <remarks>
///     <para>
///         Doc 09 asks for the list to be diffed at the <i>command</i> level so that a static user
///         interface re-submits a cached command buffer instead of rebuilding one. That is what
///         <see cref="Version" /> is: it changes when the drawing changes and not when the drawing
///         is merely rebuilt, so a renderer can compare one integer instead of a list.
///     </para>
///     <para>
///         ⚠ The comparison is against the <i>previous content</i>, not against a dirty flag. A flag
///         says what the framework believes changed; this says what actually did — and the two part
///         company exactly when something is invalidated too eagerly, which is the failure a cache
///         is supposed to absorb rather than propagate.
///     </para>
/// </remarks>
public sealed class DrawList {
    readonly List<DrawCommand> commands = [];
    readonly List<DrawCommand> previous = [];
    readonly List<PositionedGlyph> glyphs = [];
    readonly List<PositionedGlyph> previousGlyphs = [];
    readonly List<PathSegment> segments = [];
    readonly List<PathSegment> previousSegments = [];
    readonly List<BoxStyle> boxes = [];
    readonly List<BoxStyle> previousBoxes = [];
    readonly List<FontFace> fonts = [];
    readonly List<DrawBatch> batches = [];

    /// <summary>The commands, in the order they are drawn.</summary>
    public IReadOnlyList<DrawCommand> Commands => commands;

    /// <summary>Every glyph of every text command, back to back.</summary>
    /// <remarks>
    ///     One array rather than one per run, because a renderer uploads it whole and because a run
    ///     of eight glyphs is not worth an allocation of its own several hundred times a frame.
    /// </remarks>
    public IReadOnlyList<PositionedGlyph> Glyphs => glyphs;

    /// <summary>Every step of every path, back to back.</summary>
    public IReadOnlyList<PathSegment> Segments => segments;

    /// <summary>The styles of the boxes that needed one.</summary>
    /// <remarks>
    ///     Only the boxes that are more than a colour, a size and one radius are in here, which in a
    ///     real interface is a small minority of them.
    /// </remarks>
    public IReadOnlyList<BoxStyle> Boxes => boxes;

    /// <summary>The faces the text commands refer to, in the order they were first used.</summary>
    public IReadOnlyList<FontFace> Fonts => fonts;

    /// <summary>The commands grouped into runs a renderer can submit together.</summary>
    /// <remarks>
    ///     A partition of <see cref="Commands" />: every command is in exactly one batch, in order,
    ///     so a consumer walks this alone.
    /// </remarks>
    public IReadOnlyList<DrawBatch> Batches => batches;

    /// <summary>How many times the batches have been rebuilt.</summary>
    /// <remarks>
    ///     Exposed for the same reason <c>UiDocument.StylesApplied</c> is: the claim is that a frame
    ///     that drew the same thing does no batching work, and a claim about work avoided that cannot
    ///     be measured is one nobody can check.
    /// </remarks>
    public int Batched { get; private set; }

    /// <summary>Bumped whenever the commands differ from the previous frame's.</summary>
    public int Version { get; private set; }

    /// <summary>Whether the last <see cref="EndFrame" /> changed anything.</summary>
    public bool ChangedLastFrame { get; private set; }

    /// <summary>Starts collecting a frame.</summary>
    public void BeginFrame() {
        previous.Clear();
        previous.AddRange(commands);
        commands.Clear();

        previousGlyphs.Clear();
        previousGlyphs.AddRange(glyphs);
        glyphs.Clear();

        previousSegments.Clear();
        previousSegments.AddRange(segments);
        segments.Clear();

        previousBoxes.Clear();
        previousBoxes.AddRange(boxes);
        boxes.Clear();

        // The fonts are not kept for comparison, because a command referring to a different face
        // refers to it by a different index and the commands are compared. Rebuilt each frame so
        // that a face nothing draws with any more is not held alive by the list that stopped using
        // it.
        fonts.Clear();
    }

    /// <summary>Adds a command.</summary>
    /// <param name="command">The command.</param>
    public void Add(DrawCommand command) => commands.Add(command);

    /// <summary>Puts a run of glyphs in the side buffer.</summary>
    /// <param name="run">The glyphs.</param>
    /// <returns>Where they start, for the command that refers to them.</returns>
    public int AddGlyphs(List<PositionedGlyph> run) {
        ArgumentNullException.ThrowIfNull(run);

        var offset = glyphs.Count;
        glyphs.AddRange(run);

        return offset;
    }

    /// <summary>Puts a box's style in the side buffer.</summary>
    /// <param name="style">The style.</param>
    /// <returns>Where it went, for the command that refers to it.</returns>
    public int AddBox(BoxStyle style) {
        boxes.Add(style);
        return boxes.Count - 1;
    }

    /// <summary>Puts a path in the side buffer.</summary>
    /// <param name="path">The path.</param>
    /// <returns>Where it starts, for the command that refers to it.</returns>
    public int AddPath(PathBuilder path) {
        ArgumentNullException.ThrowIfNull(path);

        var offset = segments.Count;
        segments.AddRange(path.Segments);

        return offset;
    }

    /// <summary>Finds or adds a font.</summary>
    /// <param name="font">The face.</param>
    /// <returns>Its index, for the command that draws with it.</returns>
    /// <remarks>
    ///     A linear search, because an interface uses a handful of faces and a dictionary would cost
    ///     a hash per text command to avoid comparing four pointers.
    /// </remarks>
    public int AddFont(FontFace font) {
        ArgumentNullException.ThrowIfNull(font);

        var index = fonts.IndexOf(font);
        if (index >= 0) {
            return index;
        }

        fonts.Add(font);
        return fonts.Count - 1;
    }

    /// <summary>Finishes a frame and works out whether anything moved.</summary>
    /// <returns>Whether the drawing differs from the previous frame's.</returns>
    public bool EndFrame() {
        ChangedLastFrame = Differs();

        if (ChangedLastFrame) {
            Version++;

            // Behind the diff rather than beside it. Batching is a walk of every command in the
            // interface, and a frame that drew the same thing has the same batches by construction —
            // so the cached command buffer the version exists to protect keeps its batches with it.
            DrawBatcher.Build(commands, batches);
            Batched++;
        }

        return ChangedLastFrame;
    }

    /// <summary>Whether this frame's commands differ from the last one's.</summary>
    /// <remarks>
    ///     A loop rather than <c>SequenceEqual</c>, because this runs once per frame over every
    ///     command in the interface and the LINQ form allocates two enumerators to do the same
    ///     comparison. The early exit on length is worth having for the same reason: a frame that
    ///     added an element does not need to compare the elements that did not change.
    /// </remarks>
    bool Differs() {
        if (commands.Count != previous.Count
            || glyphs.Count != previousGlyphs.Count
            || segments.Count != previousSegments.Count
            || boxes.Count != previousBoxes.Count) {
            return true;
        }

        for (var i = 0; i < commands.Count; i++) {
            if (commands[i] != previous[i]) {
                return true;
            }
        }

        // ⚠ The side buffer has to be compared too, and it is not a formality. A command names a
        // range of it, so two frames whose text changed from one string to another of the same
        // length hold byte-identical commands and completely different glyphs — the label would
        // change and the version would not, and a renderer trusting the version would keep drawing
        // the old word.
        for (var i = 0; i < glyphs.Count; i++) {
            if (glyphs[i] != previousGlyphs[i]) {
                return true;
            }
        }

        // The same argument as the glyphs, and it bites harder: a control that animates a chart
        // emits the same command over the same range every frame and moves only the points.
        for (var i = 0; i < segments.Count; i++) {
            if (segments[i] != previousSegments[i]) {
                return true;
            }
        }

        // And once more for the box styles. A button whose gradient is being animated emits the same
        // command over the same range every frame and moves only the end colour — so a diff that read
        // the commands alone would report the frame unchanged and keep drawing the old gradient.
        for (var i = 0; i < boxes.Count; i++) {
            if (boxes[i] != previousBoxes[i]) {
                return true;
            }
        }

        return false;
    }
}
