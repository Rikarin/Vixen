// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Ui.Rendering;

/// <summary>
///     Turns a frame's draw list into the vertices a renderer submits.
/// </summary>
/// <remarks>
///     <para>
///         The last step that is still the interface's own. Everything below here is a pipeline, a
///         buffer and a scissor; everything above is elements and styles. Being a pure function of a
///         draw list is what lets the whole of it be tested without a device — the same argument the
///         draw list itself makes about being testable without a window.
///     </para>
///     <para>
///         ⚠ <b>Boxes are one quad each, not a tessellated outline.</b> A rounded rectangle and its
///         border are both a signed distance the shader evaluates per pixel, so a corner is exact at
///         any radius and costs four vertices — where tessellating one costs a vertex count that
///         grows with the radius and is still faceted. That is also why the two share a batch kind:
///         one shader draws both, with the thickness deciding whether the inside is filled.
///     </para>
///     <para>
///         ⚠ <b>Clips are resolved here rather than replayed.</b> A draw list pushes and pops; a
///         renderer sets a scissor. Carrying the resolved rectangle on each draw means the renderer
///         never holds a stack, and cannot be caught out by a batch it skipped having left one
///         behind.
///     </para>
///     <para>
///         ⚠ <b>This is also where a colour is brought into what the surface can show</b> — see
///         <see cref="Gamut" />. It is the last stage that is still the interface's own and the
///         first that knows which surface the picture is for, which makes it the only place the
///         repair can sit: above it the draw list is deliberately device-independent, and below it
///         the colour is already inside a <c>UNORM</c> attachment that has truncated it.
///     </para>
/// </remarks>
public sealed class UiGeometryBuilder {
    // ⚠ Direct-mapped and fixed-size on purpose. The failure mode of a collision here is that a
    // colour is searched for again — a slower frame, never a different picture — and that is the
    // property that lets the cache have no eviction policy, no allocation and no growth. A
    // dictionary would be exact and would put a hash and a probe on the path of *every* colour,
    // including the in-gamut ones that must not pay for this at all; those never reach the cache
    // because `Show` answers them with comparisons before it gets here.
    readonly ShownColour[] shown = new ShownColour[256];
    readonly List<UiVertex> vertices = [];
    readonly List<uint> indices = [];
    readonly List<UiDraw> draws = [];
    readonly List<UiShape> shapes = [];
    readonly List<Rectangle> clips = [];
    readonly List<Vector2> points = [];
    readonly List<Contour> contours = [];
    readonly List<PathVertex> triangles = [];
    readonly Dictionary<ulong, Tessellation> tessellations = [];
    int frame;

    /// <summary>The icon cache, made over whichever atlas the frame's glyphs are in.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the glyph cache rather than handed in, so that no caller changes.</b> The
    ///     icons have to be in the <i>same</i> texture as the glyphs — see <see cref="IconAtlas" /> —
    ///     so there is exactly one right answer for which atlas to use, and asking a host to supply it
    ///     would be asking it to repeat something already known. Rebuilt if the atlas ever changes,
    ///     which is what a host swapping its font cache does.
    /// </remarks>
    IconAtlas? icons;

    /// <summary>How many distinct paths the tessellation cache is holding.</summary>
    public int CachedPaths => tessellations.Count;

    /// <summary>The icons this builder has drawn from the atlas, or null before it has drawn any.</summary>
    public IconAtlas? Icons => icons;

    /// <summary>How many of the last frame's field paths the atlas could not take.</summary>
    /// <remarks>
    ///     ⚠ <b>The number that says the saving is not happening.</b> A refused path is tessellated
    ///     instead, so the picture is right and the cost is what it was before there was an atlas —
    ///     which is exactly the failure that is invisible from the outside. A figure that stays above
    ///     zero means paths are being asked for as art that are not art: too large, or even-odd.
    /// </remarks>
    public int RefusedFields { get; private set; }

    /// <summary>How many of the last frame's paths were drawn as four vertices from the atlas.</summary>
    public int FieldPaths { get; private set; }

    /// <summary>How many of the last frame's paths had to be tessellated rather than re-used.</summary>
    /// <remarks>
    ///     ⚠ <b>The number to watch when a still window costs anything.</b> Chrome that is not moving
    ///     should tessellate nothing; a figure that stays high while the interface is idle means
    ///     something is emitting a path whose *geometry* changes every frame — an animated glyph, a
    ///     progress arc, a caret drawn as a path — and that path is paying for itself sixty times a
    ///     second.
    /// </remarks>
    public int TessellatedPaths { get; private set; }

    /// <summary>How many paths the cache keeps before it drops the ones this frame did not use.</summary>
    /// <remarks>
    ///     ⚠ <b>A ceiling rather than a target, and it only bites on a frame that drew more distinct
    ///     paths than this.</b> An editor's whole chrome is a couple of hundred; the number is here so
    ///     that a document which scrolls a thousand distinct icons past cannot grow the cache without
    ///     limit, not because anything is expected to reach it.
    /// </remarks>
    public int CacheCapacity { get; set; } = 4096;

    /// <summary>How many glyphs were dropped because the atlas could not hold them.</summary>
    /// <remarks>
    ///     ⚠ Counted rather than thrown on. A glyph too large for the atlas is a configuration
    ///     mistake and not a reason for a frame to fail; what it must not be is silent, because the
    ///     symptom is a word with a hole in it.
    /// </remarks>
    public int DroppedGlyphs { get; private set; }

    /// <summary>Whether the atlas changed while this frame's quads were being emitted.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>True means some of the frame's glyph texture coordinates may be stale</b>, and it
    ///         is the one text fault this builder can notice and cannot repair. A quad reads its
    ///         region the moment it is written, so anything that moves a region afterwards leaves it
    ///         pointing somewhere else — and there are two such things, not one. Compaction moves
    ///         <i>every</i> region at once; eviction quietly hands one glyph's slot to the next
    ///         glyph, which needs no repack at all and is the worse of the two, because what draws
    ///         is a different letter rather than a blank.
    ///     </para>
    ///     <para>
    ///         So this watches the atlas's <c>Revision</c> and not its <c>Version</c>: a version
    ///         moves only for a repack, and would miss the eviction case entirely. Any addition at
    ///         all during emission is the signal, and after the resolve pass an addition can only
    ///         mean the frame wants more distinct glyphs at once than the atlas holds — resolving
    ///         evicted what it had just put in. It over-reports in the one harmless case, an
    ///         addition that neither evicted nor repacked, and that is the right way round.
    ///     </para>
    ///     <para>
    ///         Reported rather than retried, because a retry has nothing to converge on: the second
    ///         pass evicts the same way the first did. The answer is a bigger atlas or a lower field
    ///         resolution, which is a decision for whoever built the cache — so this is the signal
    ///         that says to make it, next to <see cref="DroppedGlyphs" /> and for the same reason.
    ///     </para>
    /// </remarks>
    public bool AtlasChanged { get; private set; }

    /// <summary>How far a flattened curve may sit from the curve it replaces, in document pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Document pixels, which are device pixels only at scale one.</b> A surface drawn at
    ///     twice the scale wants half of this, or its curves are visibly faceted — the flattening
    ///     error is in the geometry and the projection magnifies it along with everything else.
    ///     Settable rather than derived, because the builder is handed a viewport and not a scale,
    ///     and inventing one would be guessing.
    /// </remarks>
    public float Tolerance { get; set; } = 0.2f;

    /// <summary>How far a path's antialiasing fringe reaches past its outline, in document pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>Half a pixel, and in document pixels like <see cref="Tolerance" />.</b> A surface drawn
    ///     at twice the scale wants half of this, or the fringe is a whole device pixel wide and the
    ///     edge reads as soft rather than smooth. Zero switches it off, which is what a caller
    ///     multisampling the pass should do: two antialiasing schemes over one edge do not make it
    ///     twice as smooth, they make a seam.
    /// </remarks>
    public float Fringe { get; set; } = 0.5f;

    /// <summary>What the surface these vertices are for can show.</summary>
    /// <remarks>
    ///     <para>
    ///         Every colour emitted is brought into this gamut by <see cref="GamutMap" /> — chroma
    ///         reduced at constant lightness and hue — instead of being left to whatever truncates it
    ///         first. Read it from the swapchain, which reports what the surface actually granted:
    ///         <c>builder.Gamut = swapChain.Gamut</c>. Never from a constant, and never from what was
    ///         *asked* for, because a surface that offered no wide colour space with enough precision
    ///         behind it stays in sRGB and mapping to P3 regardless over-saturates it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The default is sRGB and that is not "off".</b> On an ordinary display an
    ///         out-of-gamut colour used to reach a <c>UNORM</c> attachment and be clipped per channel
    ///         by fixed function, which moves the hue — measured at <c>L = 0.65, C = 0.37</c>, by up
    ///         to 42.5°. It is now repaired first, and 5.5° is what survives. So this changes what
    ///         ordinary hardware draws, and it only changes it for colours that were already being
    ///         damaged.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Mapping the endpoints is enough; there is no per-pixel pass and none is owed.</b>
    ///         Every way the shader combines colours — the gradient's <c>lerp</c>, premultiplying by
    ///         coverage, and the destination blend — is a convex combination in the working space,
    ///         and each of the three gamuts is a linear image of the unit cube and therefore convex.
    ///         A convex combination of points inside a convex set is inside it, so once the stops are
    ///         in gamut every pixel between them is too. Mapping per pixel would differ only in how
    ///         chroma is *distributed* along a ramp whose stops were both outside, never in whether a
    ///         pixel is representable — and it would cost a twelve-iteration search with a cube root
    ///         in each iteration, on every fragment of a surface the interface covers entirely.
    ///     </para>
    /// </remarks>
    public ColorGamut Gamut {
        get;
        set {
            if (field == value) {
                return;
            }

            field = value;

            // ⚠ The cache holds answers to "where does this colour land on *that* surface", so the
            // gamut is not part of the key — it is what makes the whole table stale at once. Keeping
            // it in the key instead would leave the old gamut's answers resident and let a
            // pane moved between two displays keep drawing with the previous one's repair.
            Array.Clear(shown);
        }
    } = ColorGamut.Srgb;

    /// <summary>How many of the last frame's colours were outside <see cref="Gamut" /> and repaired.</summary>
    /// <remarks>
    ///     ⚠ <b>The number that says the early-out is working.</b> An interface whose palette is in
    ///     gamut — every hex token is — should report zero, and a zero here means no colour paid for
    ///     more than the six comparisons <see cref="GamutMap.InGamut" /> costs on an sRGB surface.
    ///     A figure that climbs on a wide surface is the wrong way round and means the gamut was
    ///     never handed over.
    /// </remarks>
    public int MappedColours { get; private set; }

    /// <summary>How many of those repairs had to run the binary search rather than be remembered.</summary>
    /// <remarks>
    ///     ⚠ <b>The number to watch when a frame full of vivid colour costs anything.</b> A palette
    ///     is a few dozen values drawn thousands of times, so this should settle to roughly the count
    ///     of *distinct* out-of-gamut colours and stay there while
    ///     <see cref="MappedColours" /> stays high. The two moving together means the cache is
    ///     missing every time — either the colours genuinely all differ, which an animated gradient
    ///     does, or something is perturbing them frame to frame.
    /// </remarks>
    public int ColourSearches { get; private set; }

    /// <summary>Builds the geometry for a frame.</summary>
    /// <param name="list">The frame's draw list, already batched.</param>
    /// <param name="glyphs">Where glyph fields come from.</param>
    /// <param name="viewport">The whole surface, which is the clip when nothing has pushed one.</param>
    /// <returns>The geometry. Its lists are the builder's own and are rewritten by the next call.</returns>
    public UiGeometry Build(DrawList list, GlyphFieldCache glyphs, Rectangle viewport) {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(glyphs);

        vertices.Clear();
        indices.Clear();
        draws.Clear();
        shapes.Clear();
        clips.Clear();
        DroppedGlyphs = 0;
        AtlasChanged = false;
        TessellatedPaths = 0;
        RefusedFields = 0;
        FieldPaths = 0;
        MappedColours = 0;
        ColourSearches = 0;
        frame++;

        // ⚠ Rebuilt when the fringe moves as well as when the atlas does. The dilation that keeps a
        // field icon the same weight as a tessellated one is baked into the field, so a surface that
        // switched the fringe off would otherwise keep icons drawn for the one it had.
        if (icons is null || icons.Atlas != glyphs.Atlas || icons.Feather != Fringe) {
            icons = new IconAtlas(glyphs.Atlas) { Feather = Fringe };
        }

        // ⚠ <b>Every glyph the frame needs goes into the atlas before a single quad reads a region
        // out of it</b>, and the two have to be separate passes rather than one. Adding a glyph can
        // repack the whole texture — see `GlyphAtlas.Compact` — and a repack changes every region,
        // including the ones the quads emitted earlier in this same frame have already baked into
        // their vertices. Interleaved, the fortieth glyph of a label can therefore silently move the
        // first thirty-nine somewhere else, and what draws is the right letters read out of the
        // wrong places. Resolving first means the only packing that can happen during emission is
        // none, which is a stronger claim than doing it carefully.
        Resolve(list, glyphs);

        // ⚠ Read after the resolve pass and not before it. The atlas churning *during* resolving is
        // the arrangement working — nothing has been emitted yet, so nothing is holding a coordinate
        // — and only a change from here on invalidates what has already been written.
        var settled = glyphs.Atlas.Revision;

        var clip = viewport;

        foreach (var batch in list.Batches) {
            if (batch.Kind == BatchKind.Clip) {
                clip = Clip(list.Commands[batch.First], clip, viewport);
                continue;
            }


            var first = indices.Count;

            for (var i = 0; i < batch.Count; i++) {
                var command = list.Commands[batch.First + i];

                switch (command.Kind) {
                    case DrawCommandKind.Rectangle:
                    case DrawCommandKind.Border:
                        Box(list, command);
                        break;

                    case DrawCommandKind.Shadow:
                        Shadow(list, command);
                        break;

                    case DrawCommandKind.Image:
                        Image(command);
                        break;

                    case DrawCommandKind.Text:
                        Text(list, command, glyphs);
                        break;

                    case DrawCommandKind.Field:
                        if (Field(list, command)) {
                            break;
                        }

                        // ⚠ <b>The atlas would not take it, and the fallback cannot simply be to
                        // tessellate into this batch.</b> A field batch binds the text pipeline, which
                        // reads the atlas and reconstructs a coverage from three channels; triangles
                        // fed to it sample whatever texel their zeroed coordinates land on and come
                        // out as a smear of some other icon. So the batch's draw is closed where it
                        // stands, the triangles get a solid draw of their own, and the batch carries
                        // on afterwards. Order is preserved because the draws are appended in the
                        // order the commands were in, which is the one property painting depends on.
                        first = Close(batch.Kind, first, batch, clip);
                        Path(list, command);
                        first = Close(BatchKind.PathFill, first, batch, clip);
                        RefusedFields++;
                        break;

                    case DrawCommandKind.Path:
                    case DrawCommandKind.PathStroke:
                        Path(list, command);
                        break;

                    default:
                        break;
                }
            }

            Close(batch.Kind, first, batch, clip);
        }

        // ⚠ What the resolve pass could not prevent, said out loud. See `AtlasChanged`: a frame
        // wanting more distinct glyphs at once than the atlas holds evicts during resolving what it
        // is about to draw, so emission puts them back — and putting one back can take another one's
        // slot. Nothing here can repair that; the quads are written. What it must not be is quiet.
        AtlasChanged = glyphs.Atlas.Revision != settled;

        Trim();
        return new UiGeometry(vertices, indices, draws, shapes);
    }

    /// <summary>Puts every glyph the frame draws into the atlas, before any of it is read back.</summary>
    /// <remarks>
    ///     ⚠ <b>The result is deliberately discarded.</b> This is not a lookup, it is the side effect
    ///     of one: <c>TryGet</c> is what rasterises a glyph and packs it, and doing all of that here
    ///     is what lets <see cref="Text" /> read regions out of an atlas nothing is still moving. See
    ///     <see cref="Build" /> for why interleaving the two is the fault this exists to remove.
    ///     <para>
    ///         It also fixes the eviction order for the frame, which is a second thing worth having:
    ///         the whole working set is warmed before anything can be evicted, so an atlas under
    ///         pressure stops picking off the glyphs this very frame is about to draw.
    ///     </para>
    /// </remarks>
    void Resolve(DrawList list, GlyphFieldCache glyphs) {
        foreach (var batch in list.Batches) {
            if (batch.Kind == BatchKind.Clip) {
                continue;
            }

            for (var i = 0; i < batch.Count; i++) {
                var command = list.Commands[batch.First + i];

                // ⚠ The icons go in with the glyphs and in the same pass, because they go in the same
                // texture — so a late icon can repack the atlas out from under an early *label* just
                // as readily as a late glyph can. Two passes, one for each, would leave the second
                // one's additions landing after the first one's quads had been written.
                if (command.Kind == DrawCommandKind.Field) {
                    icons?.TryGet(list.Segments, command.Offset, command.Length, command.FillRule, out _);
                    continue;
                }

                // The same two guards `Text` applies, and for the same reason: a command naming a
                // font the list does not have is one to skip rather than to index with.
                if (command.Kind != DrawCommandKind.Text
                    || command.Font < 0
                    || command.Font >= list.Fonts.Count) {
                    continue;
                }

                var font = list.Fonts[command.Font];

                for (var g = 0; g < command.Length; g++) {
                    glyphs.TryGet(font, command.Font, list.Glyphs[command.Offset + g].GlyphId, out _);
                }
            }
        }
    }

    /// <summary>Ends the draw a batch has been accumulating, and says where the next one starts.</summary>
    /// <remarks>
    ///     Ordinarily called once per batch. It is a method rather than four lines at the end of the
    ///     loop because a field path the atlas refused has to close the batch's draw early and open a
    ///     second one of a different kind — see the <c>Field</c> case in <see cref="Build" />.
    /// </remarks>
    int Close(BatchKind kind, int first, DrawBatch batch, Rectangle clip) {
        if (indices.Count > first) {
            draws.Add(
                new UiDraw(kind, first, indices.Count - first, batch.Font, clip) { Image = batch.Image }
            );
        }

        return indices.Count;
    }

    /// <summary>One small path, as a quad out of the atlas.</summary>
    /// <returns>Whether the atlas had it. False means the caller has to tessellate it instead.</returns>
    /// <remarks>
    ///     ⚠ <b>Four vertices, and the shader antialiases the edge for nothing.</b> A tessellated fill
    ///     carries its own coverage ramp in a strip of triangles along the outline — the fringe — and
    ///     this has no fringe at all: the field's distance is what the edge is made of, so the same
    ///     smoothing costs nothing and stays right at any size.
    /// </remarks>
    bool Field(DrawList list, DrawCommand command) {
        if (icons is null) {
            return false;
        }

        if (!icons.TryGet(list.Segments, command.Offset, command.Length, command.FillRule, out var field)) {
            return false;
        }

        var atlas = icons.Atlas;

        // ⚠ The quad comes back in the path's own coordinates and the command says where those are —
        // the same split a glyph run uses, and the reason two icons in different places share one
        // entry. See `IconAtlas`.
        var left = command.X + field.Quad.X;
        var top = command.Y + field.Quad.Y;

        Quad(
            left,
            top,
            left + field.Quad.Width,
            top + field.Quad.Height,
            new Vector2((float) field.Region.X / atlas.Width, (float) field.Region.Y / atlas.Height),
            new Vector2(
                (float) (field.Region.X + field.Region.Width) / atlas.Width,
                (float) (field.Region.Y + field.Region.Height) / atlas.Height
            ),
            command.Color,
            new Vector4(field.ScreenPixelRange, 0, 0, 0)
        );

        FieldPaths++;
        return true;
    }

    /// <summary>Applies a clip push or pop, keeping the stack here so the renderer has none.</summary>
    Rectangle Clip(DrawCommand command, Rectangle current, Rectangle viewport) {
        if (command.Kind == DrawCommandKind.ClipPop) {
            if (clips.Count > 0) {
                clips.RemoveAt(clips.Count - 1);
            }

            return clips.Count > 0 ? clips[^1] : viewport;
        }

        // ⚠ Intersected with what is already in force, never replacing it. A push inside a push that
        // set the scissor outright would let a child draw outside the panel that contains it, which
        // is the one thing a clip exists to prevent.
        var pushed = Intersect(new Rectangle(command.X, command.Y, command.Width, command.Height), current);
        clips.Add(pushed);
        return pushed;
    }

    static Rectangle Intersect(Rectangle a, Rectangle b) {
        var left = MathF.Max(a.X, b.X);
        var top = MathF.Max(a.Y, b.Y);
        var right = MathF.Min(a.X + a.Width, b.X + b.Width);
        var bottom = MathF.Min(a.Y + a.Height, b.Y + b.Height);

        return new Rectangle(left, top, MathF.Max(0, right - left), MathF.Max(0, bottom - top));
    }

    /// <summary>A rectangle or a border, as one quad the shader resolves.</summary>
    /// <remarks>
    ///     ⚠ <b>The parameters go in a record and the vertex carries its index.</b> Four elliptical
    ///     corners and a gradient are fourteen floats; on the vertex they would take it from
    ///     forty-eight bytes to a hundred and four, and every glyph in the frame would carry fields
    ///     no shader reads on them. Per box it is eighty bytes against the sixty-four its four
    ///     vertices already spend, and the layout does not move.
    /// </remarks>
    void Box(DrawList list, DrawCommand command) {
        if (command.Width <= 0 || command.Height <= 0) {
            return;
        }

        var half = new Vector2(command.Width / 2, command.Height / 2);

        // A plain box's uniform radius is written out as four equal corners rather than kept as a
        // separate path through the shader. One shape of parameters means one branch fewer per pixel
        // and one fewer thing that can disagree with itself.
        var style = command.HasStyle && (uint) command.Offset < (uint) list.Boxes.Count
            ? list.Boxes[command.Offset]
            : BoxStyle.Rounded(CornerRadii.Uniform(command.Radius));

        shapes.Add(Shape(half, command.Thickness, style));

        // The texture coordinate is the offset from the centre, which is what a signed distance to a
        // rounded box is written in terms of — so the shader needs no uniform per box.
        Quad(
            command.X,
            command.Y,
            command.X + command.Width,
            command.Y + command.Height,
            new Vector2(-half.X, -half.Y),
            new Vector2(half.X, half.Y),
            command.Color,
            new Vector4(shapes.Count - 1, 0, 0, 0)
        );
    }

    /// <summary>A blurred box, as one quad grown far enough for the blur to land inside it.</summary>
    /// <remarks>
    ///     <para>
    ///         The same distance field and the same shader as <see cref="Box" />: a shadow is a box
    ///         whose one-pixel edge has been widened to the blur radius. What differs is the
    ///         <i>quad</i> — a soft edge reaches outwards, and drawing it on the box's own quad would
    ///         cut the blur off square at the boundary, which reads as a shadow with a crease in it.
    ///     </para>
    ///     <para>
    ///         ⚠ Grown by twice the blur rather than once. The distance at which coverage reaches
    ///         zero is the blur radius, but the field is evaluated from the box's edge and the
    ///         falloff is symmetric about it, so the visible tail runs a blur *beyond* the point the
    ///         edge itself has moved to. One blur of margin leaves a faint straight line where the
    ///         quad ends.
    ///     </para>
    /// </remarks>
    void Shadow(DrawList list, DrawCommand command) {
        if (command.Width <= 0 || command.Height <= 0) {
            return;
        }

        var half = new Vector2(command.Width / 2, command.Height / 2);
        var blur = MathF.Max(command.Thickness, 0f);
        var margin = blur * 2f;

        var style = command.HasStyle && (uint) command.Offset < (uint) list.Boxes.Count
            ? list.Boxes[command.Offset]
            : BoxStyle.Rounded(CornerRadii.Uniform(command.Radius));

        // Thickness zero: a shadow is a fill, and a border's band would hollow it out.
        shapes.Add(Shape(half, 0f, style, blur));

        Quad(
            command.X - margin,
            command.Y - margin,
            command.X + command.Width + margin,
            command.Y + command.Height + margin,
            new Vector2(-half.X - margin, -half.Y - margin),
            new Vector2(half.X + margin, half.Y + margin),
            command.Color,
            new Vector4(shapes.Count - 1, 0, 0, 0)
        );
    }

    /// <summary>One textured quad, or the nine a nine-slice cuts it into.</summary>
    /// <remarks>
    ///     ⚠ <b>No shape entry and no distance field.</b> An image is the one thing the interface
    ///     draws that is already a picture: there is nothing to round, no border to inset and no
    ///     coverage to compute, so it is four vertices and the UVs the command asked for. Rounding
    ///     an image's corners would need the box shader's field and the image shader's sample at
    ///     once, which is a fourth pipeline and not a fourth branch.
    /// </remarks>
    void Image(DrawCommand command) {
        if (command.Image == 0) {
            // Nothing registered. Drawing it with whatever texture happens to be bound would put the
            // font atlas in the hole, which reads as a rendering fault rather than as a missing image.
            return;
        }

        // Either inset missing means there is nothing to preserve: a destination cut with no source
        // cut behind it would sample eight zero-width strips and smear them along the edges, and a
        // source cut with no destination cut has nowhere to put them.
        if (command.Slice.IsEmpty || command.SourceSlice.IsEmpty) {
            Quad(
                command.X,
                command.Y,
                command.X + command.Width,
                command.Y + command.Height,
                new Vector2(command.Source.X, command.Source.Y),
                new Vector2(command.Source.X + command.Source.Width, command.Source.Y + command.Source.Height),
                command.Color,
                Vector4.Zero
            );

            return;
        }

        Sliced(command);
    }

    /// <summary>An image cut into nine, each cell drawn from the matching cell of the source.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Nine quads in the batch the one quad would have been in.</b> Same texture, same
    ///         pipeline, same descriptor set — so a nine-sliced panel followed by an icon from the
    ///         same atlas is still one draw. That is the whole of what "shares the UI batcher" buys,
    ///         and it is why the slice lives on the image command rather than beside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The destination inset is fitted to the box and the source inset is not.</b> A
    ///         panel drawn narrower than its own two corners has to show them compressed; fitting the
    ///         source as well would leave them undistorted and quietly read different texels, which
    ///         looks like the artwork changed rather than like the box got small.
    ///     </para>
    ///     <para>
    ///         Empty cells are skipped rather than emitted degenerate. An inset with no top border
    ///         makes three of the nine zero-high, and four vertices that enclose no pixels still cost
    ///         a vertex fetch and six indices in every frame that draws the panel.
    ///     </para>
    /// </remarks>
    void Sliced(DrawCommand command) {
        Span<Rectangle> destination = stackalloc Rectangle[NineSlice.CellCount];
        Span<Rectangle> source = stackalloc Rectangle[NineSlice.CellCount];

        var box = new Rectangle(command.X, command.Y, command.Width, command.Height);

        command.Slice.Fit(command.Width, command.Height).Split(box, destination);
        command.SourceSlice.Split(command.Source, source);

        for (var cell = 0; cell < NineSlice.CellCount; cell++) {
            if (destination[cell].IsEmpty || (command.HollowCentre && cell == NineSlice.Centre)) {
                continue;
            }

            Quad(
                destination[cell].Left,
                destination[cell].Top,
                destination[cell].Right,
                destination[cell].Bottom,
                new Vector2(source[cell].Left, source[cell].Top),
                new Vector2(source[cell].Right, source[cell].Bottom),
                command.Color,
                Vector4.Zero
            );
        }
    }

    /// <summary>A run of glyphs, one quad each, from the atlas.</summary>
    void Text(DrawList list, DrawCommand command, GlyphFieldCache glyphs) {
        if (command.Font < 0 || command.Font >= list.Fonts.Count) {
            return;
        }

        var font = list.Fonts[command.Font];
        var size = command.FontSize;
        var atlas = glyphs.Atlas;

        for (var i = 0; i < command.Length; i++) {
            var glyph = list.Glyphs[command.Offset + i];

            if (!glyphs.TryGet(font, command.Font, glyph.GlyphId, out var entry)) {
                // A glyph that draws nothing is not a drop; one the atlas refused is.
                if (font.GetOutline(glyph.GlyphId).IsEmpty) {
                    continue;
                }

                DroppedGlyphs++;
                continue;
            }

            // ⚠ <b>A glyph's position is relative to the run, not to the surface.</b> The command
            // carries where the line starts and each glyph carries its offset along it, so that two
            // identical labels in different places hold identical glyph runs — which is what lets
            // the batcher and the frame diff notice they are the same. Reading the offset as
            // absolute puts every label in the top-left corner.
            var penX = command.X + glyph.X;
            var penY = command.Y + glyph.Y;

            // ⚠ The placement is in ems and the pen is in pixels, so the size multiplies one and not
            // the other. And y runs down the surface while a font's runs up, which is why the top
            // edge is a subtraction.
            var left = penX + (entry.Left * size);
            var right = penX + (entry.Right * size);
            var top = penY - (entry.Top * size);
            var bottom = penY - (entry.Bottom * size);

            var u0 = (float)entry.Region.X / atlas.Width;
            var v0 = (float)entry.Region.Y / atlas.Height;
            var u1 = (float)(entry.Region.X + entry.Region.Width) / atlas.Width;
            var v1 = (float)(entry.Region.Y + entry.Region.Height) / atlas.Height;

            Quad(
                left,
                top,
                right,
                bottom,
                new Vector2(u0, v0),
                new Vector2(u1, v1),
                command.Color,
                new Vector4(entry.ScreenPixelRange * size, 0, 0, 0)
            );
        }
    }

    /// <summary>A path, filled or stroked, as loose triangles.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The only kind that is real geometry rather than a quad the shader resolves.</b> A
    ///         box and a glyph are both a distance function evaluated per pixel, so they cost four
    ///         vertices whatever their shape; an arbitrary path has no such function, so it is
    ///         tessellated.
    ///     </para>
    ///     <para>
    ///         ⚠ Which is why it is also the only kind whose edge has to be <i>drawn</i>. The interior
    ///         comes out at full coverage and a strip along the outline carries the ramp from one to
    ///         zero, and the coverage travels in the vertex where the other two kinds put a distance.
    ///     </para>
    /// </remarks>
    /// <summary>Turns one path command into triangles, re-using the last ones where it can.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Flattening and tessellating is the most expensive thing this builder does, and an
    ///         interface asks for the same paths frame after frame.</b> The draw list is rebuilt every
    ///         frame from absolute coordinates, so an icon that has not moved emits byte-identical
    ///         segments each time — which is exactly the condition a cache wants.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Without this, one changing character costs the whole window.</b> The frame-time
    ///         readout in a scene pane rewrites its label sixty times a second; that alone made the
    ///         draw list differ, which took away every chance to re-use the frame, which meant
    ///         re-tessellating every icon on screen. An editor's icons are filled outlines whose
    ///         strokes were pre-expanded into quads and joint rectangles, so one twenty-pixel glyph is
    ///         a couple of hundred segments — an outliner of two dozen rows measured 21,000 segments
    ///         and 143ms a frame in Release. The cost tracked what was <i>visible</i> rather than what
    ///         was <i>happening</i>, which is the signature of a missing cache.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Colour is deliberately not part of the key.</b> It is applied when the vertices are
    ///         written and never reaches the tessellator, so a glyph that tints on hover still re-uses
    ///         its triangles — which is the case that would otherwise miss on every frame of a mouse
    ///         moving across a list.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A hit is confirmed against the stored segments, not against the hash.</b> A hash
    ///         collision would otherwise draw one path's geometry for another — a wrong picture rather
    ///         than a slow one, and the kind of fault that appears on one machine and not the next.
    ///         Confirming costs a comparison over the segments the miss would have read anyway.
    ///     </para>
    /// </remarks>
    void Path(DrawList list, DrawCommand command) {
        var key = KeyOf(list, command);

        if (!tessellations.TryGetValue(key, out var cached) || !cached.Matches(list, command, Tolerance, Fringe)) {
            Tessellate(list, command);

            cached = new Tessellation(list, command, Tolerance, Fringe, triangles);
            tessellations[key] = cached;
            TessellatedPaths++;
        }

        cached.LastUsed = frame;
        Emit(cached.Triangles, command.Color, new Vector2(command.X, command.Y));
    }

    void Tessellate(DrawList list, DrawCommand command) {
        points.Clear();
        contours.Clear();
        triangles.Clear();

        PathFlattener.Flatten(list.Segments, command.Offset, command.Length, Tolerance, points, contours);

        if (contours.Count == 0) {
            return;
        }

        // ⚠ A field the atlas refused is a *fill*, and reading only `Path` here would send it down the
        // stroke branch — where a thickness of zero produces no geometry at all. The icon would simply
        // not be there, on exactly the paths the atlas could not take.
        if (command.Kind is DrawCommandKind.Path or DrawCommandKind.Field) {
            PathTessellator.Fill(points, contours, command.FillRule, triangles);
            PathTessellator.FillFringe(points, contours, command.FillRule, Fringe, triangles);
        } else {
            PathTessellator.Stroke(
                points,
                contours,
                command.Thickness,
                command.Join,
                command.Cap,
                Tolerance,
                triangles,
                command.MiterLimit > 0 ? command.MiterLimit : PathTessellator.DefaultMiterLimit,
                Fringe
            );
        }
    }

    /// <summary>What a path's triangles depend on, hashed.</summary>
    /// <remarks>
    ///     ⚠ <b>Every input the tessellator reads and nothing else.</b> An input left out is a stale
    ///     picture — a stroke that kept its old width — and an input put in that the tessellator does
    ///     not read is a miss per frame, which is the cache not working while looking like it does.
    /// </remarks>
    ulong KeyOf(DrawList list, DrawCommand command) {
        var hash = 14695981039346656037UL;

        Mix(ref hash, (ulong) command.Kind);
        Mix(ref hash, (ulong) command.FillRule);
        Mix(ref hash, (ulong) BitConverter.SingleToUInt32Bits(command.Thickness));
        Mix(ref hash, (ulong) command.Join);
        Mix(ref hash, (ulong) command.Cap);
        Mix(ref hash, BitConverter.SingleToUInt32Bits(command.MiterLimit));
        Mix(ref hash, BitConverter.SingleToUInt32Bits(Tolerance));
        Mix(ref hash, BitConverter.SingleToUInt32Bits(Fringe));
        Mix(ref hash, (ulong) command.Length);

        for (var i = 0; i < command.Length; i++) {
            var segment = list.Segments[command.Offset + i];

            Mix(ref hash, (ulong) segment.Verb);
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P0.X));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P0.Y));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P1.X));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P1.Y));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P2.X));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P2.Y));
        }

        return hash;
    }

    static void Mix(ref ulong hash, ulong value) {
        hash = (hash ^ value) * 1099511628211UL;
    }

    /// <summary>Drops what this frame did not draw, once there is more held than the ceiling allows.</summary>
    void Trim() {
        if (tessellations.Count <= CacheCapacity) {
            return;
        }

        // ⚠ Materialised before removing, because a dictionary cannot be written while it is being
        // enumerated — and this runs on a frame that has already drawn more paths than the ceiling,
        // so the allocation is the cheapest thing about it.
        foreach (var key in tessellations.Where(entry => entry.Value.LastUsed != frame).Select(entry => entry.Key).ToArray()) {
            tessellations.Remove(key);
        }
    }

    /// <summary>Writes a path's triangles, moved to where the command put them.</summary>
    /// <remarks>
    ///     ⚠ <b>Every other kind has read <c>X</c> and <c>Y</c> as its position since there were draw
    ///     commands; a path was the one that ignored them, and it is a strict generalisation to stop.</b>
    ///     A path emitted in absolute coordinates carries zero here and draws exactly where it did.
    ///     What it buys is the option of the other arrangement — geometry in the path's own space and
    ///     the position on the command — which is what makes a cache over the geometry hit for the
    ///     same shape drawn in two places. <c>Icon</c> takes it; nothing else has to.
    /// </remarks>
    void Emit(ReadOnlySpan<PathVertex> triangles, Color4 color, Vector2 origin) {
        // Once per path, not once per triangle — an icon is a couple of hundred of them in one
        // colour, and this is the difference between one early-out test and two hundred.
        color = Show(color);

        for (var i = 0; i + 2 < triangles.Length; i += 3) {
            var start = (uint)vertices.Count;

            // ⚠ No vertex is shared, and there is nothing to share. The fill decomposes into
            // trapezoids that meet only along band boundaries, where the two sides have different x;
            // the stroke overlaps rather than seams. An index buffer over this is the identity, and
            // building one would cost a hash per vertex to discover that.
            for (var corner = 0; corner < 3; corner++) {
                var vertex = triangles[i + corner];

                // The coverage rides where the other two kinds put a distance, so the solid shader
                // reads `Shape.x` for the same reason the text shader does. Nothing else is read: a
                // path has no field to sample and no shape to evaluate.
                vertices.Add(
                    new UiVertex(
                        vertex.Position + origin,
                        Vector2.Zero,
                        color,
                        new Vector4(vertex.Coverage, 0, 0, 0)
                    )
                );

                indices.Add(start + (uint)corner);
            }
        }
    }

    /// <summary>One path's triangles, and the inputs they were made from.</summary>
    /// <remarks>
    ///     ⚠ <b>The segments are kept, not just their hash.</b> They are what confirms a hit — see
    ///     <see cref="Path" /> — and keeping them costs about what the triangles do, which for a path
    ///     that would otherwise be re-tessellated every frame is a bargain either way.
    /// </remarks>
    sealed class Tessellation {
        readonly PathSegment[] segments;
        readonly DrawCommandKind kind;
        readonly PathFillRule fillRule;
        readonly float thickness;
        readonly LineJoin join;
        readonly LineCap cap;
        readonly float miterLimit;
        readonly float tolerance;
        readonly float fringe;

        public PathVertex[] Triangles { get; }

        public int LastUsed { get; set; }

        public Tessellation(
            DrawList list,
            DrawCommand command,
            float tolerance,
            float fringe,
            List<PathVertex> triangles
        ) {
            segments = new PathSegment[command.Length];

            for (var i = 0; i < command.Length; i++) {
                segments[i] = list.Segments[command.Offset + i];
            }

            kind = command.Kind;
            fillRule = command.FillRule;
            thickness = command.Thickness;
            join = command.Join;
            cap = command.Cap;
            miterLimit = command.MiterLimit;
            this.tolerance = tolerance;
            this.fringe = fringe;
            Triangles = [.. triangles];
        }

        public bool Matches(DrawList list, DrawCommand command, float tolerance, float fringe) {
            if (command.Length != segments.Length
                || command.Kind != kind
                || command.FillRule != fillRule
                || command.Thickness != thickness
                || command.Join != join
                || command.Cap != cap
                || command.MiterLimit != miterLimit
                || tolerance != this.tolerance
                || fringe != this.fringe) {
                return false;
            }

            for (var i = 0; i < segments.Length; i++) {
                if (list.Segments[command.Offset + i] != segments[i]) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Brings one colour into what the surface can show, remembering the answer.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The in-gamut test is ahead of the cache, not behind it, and that ordering is the
    ///         point.</b> Almost every colour a frame draws is showable — an interface's palette is
    ///         in gamut by construction, and on a wide surface even a vivid one usually is — so the
    ///         common path must be the cheapest thing available, and on an sRGB surface that is six
    ///         comparisons against <c>[0, 1]</c> with no matrix, no hash and no memory traffic. Put
    ///         a cache probe in front of it and every showable colour in the frame pays a hash to be
    ///         told what a comparison already knew.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Alpha is not part of the key, because it is not part of the answer.</b> Coverage
    ///         has no gamut to be outside of and the mapper carries it through untouched, so one
    ///         entry serves a token drawn at every opacity a <c>/50</c> modifier can ask for —
    ///         which, for a palette used through opacity modifiers, is most of what would otherwise
    ///         be distinct keys.
    ///     </para>
    /// </remarks>
    Color4 Show(Color4 colour) {
        var linear = new Vector3(colour.R, colour.G, colour.B);

        if (GamutMap.InGamut(linear, Gamut)) {
            return colour;
        }

        MappedColours++;

        // Hash the three channels' bit patterns rather than their values: two colours that differ
        // below what a float can express are the same entry, and that is exactly what should happen.
        var bits = HashCode.Combine(
            BitConverter.SingleToUInt32Bits(colour.R),
            BitConverter.SingleToUInt32Bits(colour.G),
            BitConverter.SingleToUInt32Bits(colour.B)
        );

        ref var slot = ref shown[(uint) bits % (uint) shown.Length];

        if (slot.Occupied && slot.Source == linear) {
            return new Color4(slot.Shown.X, slot.Shown.Y, slot.Shown.Z, colour.A);
        }

        ColourSearches++;
        var mapped = GamutMap.Map(linear, Gamut);
        slot = new ShownColour { Source = linear, Shown = mapped, Occupied = true };

        return new Color4(mapped.X, mapped.Y, mapped.Z, colour.A);
    }

    /// <summary>One box's record, with every colour the shader will read brought into gamut.</summary>
    /// <param name="half">Half the box's extent.</param>
    /// <param name="thickness">A border's width, or zero.</param>
    /// <param name="style">Its side-buffer entry.</param>
    /// <param name="blur">A shadow's spread, or zero.</param>
    /// <returns>The record.</returns>
    /// <remarks>
    ///     ⚠ <b>One place rather than two call sites spelling the same eleven arguments</b>, and that
    ///     is not tidiness: a box and a shadow that disagreed about which lane the interpolation space
    ///     went in would draw a shadow with a different ramp from the box it belongs to, which reads
    ///     as a compositing bug rather than as a typo.
    /// </remarks>
    UiShape Shape(Vector2 half, float thickness, BoxStyle style, float blur = 0f) =>
        new(
            half,
            thickness,
            style.Corners,
            style.Shape,
            style.Space,
            style.GradientAxis,
            End(style),
            Via(style),
            style.HasVia,
            style.Stops,
            blur
        );

    /// <summary>A gradient's far colour, brought into the surface's gamut — if there is a gradient.</summary>
    /// <remarks>
    ///     ⚠ <b>Guarded on the shape, because <see cref="UiShape" /> carries an end colour whether or
    ///     not one is used and the shader reads it only when there is a gradient.</b> Mapping a
    ///     field nothing samples would be work spent on nothing, and worse, it would let a colour
    ///     that never reaches a pixel show up in <see cref="MappedColours" /> — a diagnostic that
    ///     counts invisible repairs is one nobody can act on.
    /// </remarks>
    Color4 End(BoxStyle style) => style.HasGradient ? Show(style.GradientEnd) : style.GradientEnd;

    /// <summary>And the middle stop's, on the same terms — read only when there is one.</summary>
    /// <remarks>
    ///     ⚠ Guarded on <see cref="BoxStyle.HasVia" /> and not just on the gradient. The middle colour
    ///     of a two-stop gradient is a lane the shader never samples, so mapping it would inflate
    ///     <see cref="MappedColours" /> on every ordinary <c>bg-linear-*</c> in the interface.
    /// </remarks>
    Color4 Via(BoxStyle style) =>
        style.HasGradient && style.HasVia ? Show(style.GradientVia) : style.GradientVia;

    /// <summary>One remembered repair: where a colour was, and where it lands on this surface.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Occupied</c> rather than a sentinel key.</b> A cleared table is all zeroes, and
    ///     zero is opaque black — which is in gamut, so it can never be asked for here and a
    ///     zero-key collision could not actually occur. The flag is kept anyway because that
    ///     argument depends on a property of another type, and a table that is correct only while
    ///     <c>GamutMap.InGamut(default)</c> keeps saying true is the kind of coupling that breaks
    ///     silently and one comparison the wrong colour.
    /// </remarks>
    struct ShownColour {
        public Vector3 Source;
        public Vector3 Shown;
        public bool Occupied;
    }

    /// <summary>Four vertices and six indices, wound the same way every time.</summary>
    void Quad(
        float left,
        float top,
        float right,
        float bottom,
        Vector2 textureMin,
        Vector2 textureMax,
        Color4 color,
        Vector4 shape
    ) {
        var start = (uint)vertices.Count;

        // Once per quad rather than once per vertex: the four corners share a colour, and asking
        // four times would be four early-out tests to reach one answer.
        color = Show(color);

        vertices.Add(new UiVertex(new Vector2(left, top), textureMin, color, shape));
        vertices.Add(new UiVertex(new Vector2(right, top), new Vector2(textureMax.X, textureMin.Y), color, shape));
        vertices.Add(new UiVertex(new Vector2(right, bottom), textureMax, color, shape));
        vertices.Add(new UiVertex(new Vector2(left, bottom), new Vector2(textureMin.X, textureMax.Y), color, shape));

        indices.Add(start);
        indices.Add(start + 1);
        indices.Add(start + 2);
        indices.Add(start);
        indices.Add(start + 2);
        indices.Add(start + 3);
    }
}
