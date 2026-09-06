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
    /// <summary>How many repairs the colour cache can hold.</summary>
    /// <remarks>
    ///     Internal so a test can say which slot a colour lands in and therefore construct a pair
    ///     that shares one. A cache whose only colliding pairs are the ones a hash happens to produce
    ///     today is a cache whose collision behaviour is never actually exercised.
    /// </remarks>
    internal const int Slots = 256;

    /// <summary>How far a box's quad reaches past the box, so its antialiasing ramp has somewhere to land.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A box used to be drawn on a quad exactly its own size, and that silently threw
    ///         away the outer half of every edge's ramp.</b> The shader resolves coverage <i>inside</i>
    ///         the geometry, so a fragment the field would have shaded is never generated if the
    ///         rasteriser did not produce it. On an integer-aligned edge that costs nothing — the
    ///         first sample is already half a pixel inside, where coverage is 1. On a
    ///         <b>half-pixel-aligned</b> one it costs half the edge, and for a one-pixel hairline that
    ///         is half the ink of the whole primitive: a connector from x=18.5 to x=19.5 covers half
    ///         of column 18 and half of column 19, and drew only the first of them, because the sample
    ///         at 19.5 sits on the right edge and the half-open rule gives it to the neighbour.
    ///     </para>
    ///     <para>
    ///         One pixel and not a half. Coverage reaches zero half a pixel out, so a half would be
    ///         exactly enough for the ramp and nothing for <c>fwidth</c>: the device takes the band
    ///         width from the derivative of the distance across a 2×2 quad, and a quad straddling the
    ///         geometric edge of the primitive derives it from helper invocations rather than from
    ///         neighbours it actually shaded.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Boxes only.</b> Shadows, paths and glyphs already carry their own margin — a
    ///         shadow's is twice its blur, a glyph's is its field padding — and adding this to those
    ///         would be adding it twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not <see cref="Fringe" />, which is a different quantity with a similar name.</b>
    ///         That one is how far a <i>path's</i> outline is offset outwards to be ramped by vertex
    ///         alpha, it is settable, and zero is a legitimate value for a caller that multisamples
    ///         the pass. This is the room a shader needs to do its own coverage at all, so there is
    ///         no setting of it that is correct at zero.
    ///     </para>
    /// </remarks>
    internal const float BoxMargin = 1f;

    // ⚠ Fixed-size and two-way on purpose. The failure mode of a collision here is that a colour is
    // searched for again — a slower frame, never a different picture — and that is the property that
    // lets the cache have no eviction policy, no allocation and no growth. A dictionary would be
    // exact and would put a hash and a probe on the path of *every* colour, including the in-gamut
    // ones that must not pay for this at all; those never reach the cache because `Show` answers
    // them with comparisons before it gets here.
    readonly ShownColour[] shown = new ShownColour[Slots];
    readonly List<UiVertex> vertices = [];
    readonly List<uint> indices = [];
    readonly List<UiDraw> draws = [];
    readonly List<UiShape> shapes = [];
    readonly List<UiMask> masks = [];
    readonly List<Rectangle> clips = [];
    readonly List<UiLayer> layers = [];
    readonly List<Opening> opening = [];
    readonly List<Vector2> points = [];
    readonly List<Contour> contours = [];
    readonly List<PathVertex> triangles = [];
    readonly Dictionary<ulong, Tessellation> tessellations = [];
    int frame;
    int layerNumber;

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

    /// <summary>What the interface's white is worth in the units the target is drawn in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The luminance half of the same handover <see cref="Gamut" /> is the chromaticity
    ///         half of, and until #670 there was no such thing.</b> A <c>DrawCommand.Color</c> is
    ///         linear and display-referred: <c>#fff</c> is one, meaning <i>as bright as this surface
    ///         gets</i>. That is the right unit for a swapchain whose white is the display's, which
    ///         is every window this framework has drawn into so far — so the default is one and one
    ///         is a no-op, exactly.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is wrong by four orders of magnitude for a scene-referred target, and wrong
    ///         invisibly.</b> The renderer works in cd/m², where a sunlit surface is tens of
    ///         thousands; a HUD drawn into that pass with a white of <i>one</i> is one candela, which
    ///         is not dim, it is black — and a pass that never ran looks the same. Whoever owns the
    ///         pass sets this: BT.2408's reference white is 203 cd/m², which is what an SDR interface
    ///         composited into an HDR frame is normally worth.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Applied after the gamut map and never before it.</b> The map's search works in a
    ///         perceptual space over the unit cube, so it wants the authored 0–1 value; the scale is
    ///         what carries the repaired colour into the target's units. For the same reason this is
    ///         not part of the cache key and does not clear the table — the cache stores where a
    ///         colour <i>lands</i>, which is a fact about the surface's primaries and not about how
    ///         bright its white is. ⚠ Alpha is untouched: coverage is a fraction, not a luminance.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A layer target has to be able to hold it.</b> An opacity group, a blur or a mask
    ///         composites through an offscreen surface, and <c>UiRenderer</c> gives that surface the
    ///         pass's own colour format — so this is safe exactly when the pass is float, and a white
    ///         level above one into an eight-bit <c>UNORM</c> pass would clamp at the composite
    ///         rather than at the final blend.
    ///     </para>
    /// </remarks>
    public float WhiteLevel { get; set; } = 1f;

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

    /// <summary>The draw-list version and extent the geometry this builder holds was made from.</summary>
    /// <remarks>
    ///     <c>(-1, default)</c> before the first build, which no real draw list can equal —
    ///     <c>DrawList.Version</c> is a content hash and an empty extent is not a window.
    /// </remarks>
    public (int Version, Rectangle Extent) Built { get; private set; } = (-1, default);

    /// <summary>How many frames this builder has actually tessellated.</summary>
    /// <remarks>
    ///     ⚠ <b>Read against <see cref="TessellationsSkipped" /> and never alone</b>, the way
    ///     <c>UiDocument.Diagnostics.DrawListsBuilt</c> is read against <c>DrawListsChanged</c>. This
    ///     is the recording half of what doc 49 § 7.3 calls an idle frame: the draw list is rebuilt
    ///     every frame by <c>DrawListBuilder</c> and always will be until there is a retained surface,
    ///     but flattening and tessellating it is skipped for a window whose drawing did not change,
    ///     and until these two counters existed that saving could only be described in watts.
    /// </remarks>
    public int Tessellations { get; private set; }

    /// <summary>How many frames were answered with the vertices this builder already had.</summary>
    public int TessellationsSkipped { get; private set; }

    /// <summary>Builds the geometry for a frame, or keeps the geometry it already has.</summary>
    /// <param name="list">The frame's draw list, already batched.</param>
    /// <param name="glyphs">Where glyph fields come from.</param>
    /// <param name="viewport">The whole surface, which is the clip when nothing has pushed one.</param>
    /// <param name="frame">The geometry to draw, left as it was when nothing had to be rebuilt.</param>
    /// <returns>Whether anything was rebuilt.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The key is three things and each of the three has been the one that was
    ///         missing.</b> The draw list's <c>Version</c> says the drawing changed; the extent says
    ///         a window was resized without its contents changing, which keeps the version and still
    ///         needs new vertices because the builder is what turns a command's clip into a scissor
    ///         in the new extent; and <see cref="AtlasChanged" /> says the last build repacked the
    ///         glyph texture, which moves every region already baked into the vertices — so a frame
    ///         that skipped after a repack would draw the right letters read out of the wrong places.
    ///         The atlas is the one part of the key that is not a property of the window, because the
    ///         cache is shared.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Here rather than in a host, and that is this method's whole reason for
    ///         existing.</b> The key was written out twice — once in <c>UiWindowSurface.Tessellate</c>
    ///         and once, verbatim, in <c>EditorHost.Build</c>, which never called the method that
    ///         already did it. Two copies of a three-part key in the two hosts is the shape this
    ///         repository keeps finding: a feature wired into one renderer and not the other. It is
    ///         also what made the saving unmeasurable, since neither copy can be reached from a test
    ///         without a window.
    ///     </para>
    ///     <para>
    ///         Safe to answer with the geometry already held because <see cref="Build" /> writes into
    ///         the builder's own lists and a skipped frame does not call it — so the vertices a
    ///         skipped frame draws are the ones the last build left, which is exactly the claim.
    ///     </para>
    /// </remarks>
    public bool TryBuild(DrawList list, GlyphFieldCache glyphs, Rectangle viewport, ref UiGeometry frame) {
        ArgumentNullException.ThrowIfNull(list);

        if (Built == (list.Version, viewport) && !AtlasChanged) {
            TessellationsSkipped++;
            return false;
        }

        frame = Build(list, glyphs, viewport);
        Built = (list.Version, viewport);
        Tessellations++;

        return true;
    }

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
        masks.Clear();
        clips.Clear();
        layers.Clear();
        opening.Clear();

        // ⚠ Restarted every frame rather than left to run, so that a frame's geometry is a pure
        // function of its draw list. A counter that carried across frames would give the same picture
        // different layer numbers on the second frame, and the geometry would compare unequal to
        // itself — which is exactly the claim the frame diff upstream is built on.
        layerNumber = 0;
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

            if (batch.Kind == BatchKind.Layer) {
                Layer(list, list.Commands[batch.First], clip, viewport);
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

        // ⚠ An unclosed group is dropped rather than closed here. A push with no pop means the draw
        // list is malformed, and inventing a composite for it would put the rest of the frame — every
        // draw after the push — inside a surface nobody asked for, which is a blank window rather than
        // a visible mistake. The clip stack takes the same view of an unbalanced push.
        opening.Clear();

        Trim();
        return new UiGeometry(vertices, indices, draws, shapes) { Layers = layers, Masks = masks };
    }

    /// <summary>Opens or closes a composited group, and emits the quad that composites it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The bounds are the ink, and they are read from the vertices rather than from the
    ///         command.</b> See <see cref="UiLayer" />: opacity isolates a subtree without bounding it,
    ///         so a child that overflows its translucent parent is still part of the group. The vertices
    ///         emitted between the push and the pop are the only complete account of what the group
    ///         drew, and they are already here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Rounded out to whole pixels, never in.</b> A group's edge is antialiased, so its
    ///         outermost pixel is partly covered; a bound rounded to the nearest pixel would cut that
    ///         pixel off on whichever side the fraction fell the wrong way, and the group would composite
    ///         with a hairline missing along one edge. Rounding out costs at most a pixel of surface on
    ///         each side and cannot lose ink.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Narrowed by the clip in force where the group opened — and that clip is
    ///         <i>equal</i> to the one in force where it closes, which is worth saying because it looks
    ///         like it should not be.</b> A group's element pushes and pops its own <c>overflow</c> clip
    ///         entirely inside the bracket, so the stack is back where it started by the time the pop
    ///         arrives. <s>Those differ whenever the group's own element also clips.</s> They do not,
    ///         and a sabotage that read the pop-time clip instead changed no picture and failed no test.
    ///         The entry clip is still what is stored, because storing it does not <i>depend</i> on that
    ///         balance: an unbalanced push somewhere inside the group would otherwise composite the
    ///         whole group against a scissor belonging to something within it.
    ///     </para>
    /// </remarks>
    void Layer(DrawList list, in DrawCommand command, Rectangle clip, Rectangle viewport) {
        if (command.Kind == DrawCommandKind.LayerPush) {
            opening.Add(
                new Opening(
                    draws.Count,
                    vertices.Count,
                    command.Color.A,
                    clip,
                    command.Blur,
                    command.Filter,
                    command.Offset,
                    command.HasMask ? command.Length : 0,
                    command.Shadow,
                    command.Backdrop,
                    new Rectangle(command.X, command.Y, command.Width, command.Height),
                    command.Transform,
                    command.Blend
                )
            );
            return;
        }

        if (opening.Count == 0) {
            // A pop with no push. Dropped for the same reason the clip stack drops one: there is no
            // surface to composite, and inventing one would composite the whole frame so far.
            return;
        }

        var open = opening[^1];
        opening.RemoveAt(opening.Count - 1);

        // ⚠ <b>Outset before the clip narrows it, and outset at all only because a blur is the one
        // thing a group does that <see cref="Ink" /> cannot see.</b> Every other primitive arrives
        // here already expanded to the quad that contains its ink — that is the whole of `Ink`'s
        // argument — but a blur is applied to the group's finished surface, after those quads have
        // been rasterised, so it moves coverage to texels no vertex of the group ever touched. Left
        // un-outset, the composite quad cuts the halo off flush with the unblurred silhouette, which
        // is a soft edge with a hard line across it: the picture looks like a blur that failed rather
        // than like a bound that was wrong.
        //
        // ⚠ The order matters and is the opposite way round from what "clip, then grow" would give.
        // An ancestor's `overflow: hidden` clips the *filtered* result — Filter Effects 1 § 5 — so
        // the halo is grown out of the ink and then cut by the clip, not grown out of an already-cut
        // rectangle, which would let the halo escape a clip the group is inside.
        var ink = Ink(open.Vertex);

        // ⚠ <b>The wider of the two kernels and not their sum, and it is the shadow's presence that
        // makes this a <c>max</c> rather than a second outset.</b> A <c>drop-shadow</c> is a Gaussian
        // over the group's <i>finished surface</i> — see <see cref="UiLayer.Shadow" /> — so its taps
        // read texels this pass has to have left defined, which is why the outset has to cover it at
        // all. They read the surface and not the shadow, so the two reaches do not compose: a group
        // that is `blur(4px) drop-shadow(0 0 2px)` needs twelve pixels of margin and not eighteen.
        var reach = Math.Max(
            UiLayer.KernelRadius(open.Blur, 1f),
            UiLayer.KernelRadius(open.Shadow?.Blur ?? 0f, 1f)
        );

        if (reach > 0) {
            ink = new Rectangle(ink.X - reach, ink.Y - reach, ink.Width + (2f * reach), ink.Height + (2f * reach));
        }

        // ⚠ <b>The clip is pulled back through the transform before it narrows anything, and the
        // viewport is not.</b> The two look like one rectangle here and are two different facts. An
        // ancestor's `overflow: hidden` cuts the *transformed* result — Transforms 1 § 3 and Filter
        // Effects 1 § 5 agree that ancestors clip in their own space — so what this group may keep is
        // whatever *lands* inside the clip, which is the clip's pre-image and not the clip. Narrowing
        // by the clip itself would cut a rotated panel's corner off before the rotation had swung it
        // into view: a bite out of the picture on the side the clip happens to be, present only when
        // the element is near an edge, which is as hard to see as a bug gets. The scissor on the draw
        // below still cuts the transformed quad by the real rectangle, so nothing escapes the clip —
        // this only decides how much of the surface is worth keeping.
        //
        // ⚠ <b>The viewport stays where it is, and that is a real limit rather than an oversight.</b>
        // The surface is the viewport's size and holds the group at the coordinates it always had —
        // see <see cref="UiLayer" /> — so content that was never on screen was never rasterised into
        // it and no transform can bring it back. An element mostly outside the viewport, scaled down
        // to fit, shows only the part that was already visible. Recovering it would mean sizing the
        // surface to the pre-image instead, which is the per-group translation this design spent the
        // viewport's memory to avoid. Written down in docs/guide/ui/compositing.md rather than
        // discovered.
        var reachable = Intersect(open.Clip, viewport);

        if (open.Transform is { } placed) {
            if (placed.Invert() is not { } undo) {
                // A degenerate transform — `scale: 0`, or one axis of it. The group paints no pixels,
                // so there is no surface worth allocating and nothing to composite. Dropped here for
                // the empty group's reason, and matching what `UiDocument.HitTest` does with the same
                // matrix: an element that cannot be seen does not take the pointer either.
                return;
            }

            reachable = Intersect(undo.Bounds(reachable), viewport);
        }

        // ⚠ <b>The border box and not the ink, clipped by the same two rectangles the group's own
        // bounds are, and dropped whole when nothing is left.</b> CSS clips a backdrop filter to the
        // element's border box — so a panel whose child overflows it, or whose own <c>blur-*</c> grew
        // the ink, must not filter the backdrop over the overflow. Nulling the backdrop here rather
        // than leaving an empty rectangle is <c>cast</c>'s arrangement above and for its reason: both
        // executors read <see cref="UiLayer.Backdrop" /> as the whole answer, and one that named a
        // surface no quad draws would have each of them allocate one and capture into it for nobody.
        // ⚠ <b>And nulled outright when the group is transformed, which is the one interaction of
        // this feature that is refused rather than implemented.</b> Every other thing a group does
        // survives a transform for free, because each of them happens in the surface's own space and
        // the matrix is spent afterwards on the composite quad: a blur convolves the surface, a colour
        // matrix is per pixel, a mask is read from the composite's own untransformed coordinate, and a
        // drop shadow displaces in local space and is carried along. CSS orders them the same way —
        // filter, then mask, then transform — so all four come out right without being told.
        //
        // A backdrop does not, and the reason is that it is the one surface holding something the
        // group did not draw. `UiRenderer.Capture` replays the draw-list prefix into a surface and both
        // executors read it at the coordinates the quad covers, so a *rotated* backdrop quad would have
        // to sample a rotated window of the captured picture — four texture coordinates that are no
        // longer an axis-aligned rectangle, a capture region that is no longer `BackdropBounds`, and a
        // clip to the border box that is no longer a rectangle either. Sampling the untransformed patch
        // instead would show the scene from where the element *was* rather than from where it is, which
        // under a rotation is simply the wrong picture. Dropped here, once, so that both executors go on
        // reading `UiLayer.Backdrop` as the whole answer.
        //
        // ⚠ <b>Decided <i>above</i> the empty-group guard rather than below it, which is the ordering
        // the guard now depends on.</b> An element that paints nothing keeps its group only when a
        // backdrop survived this block, so the answer has to be in hand before that question is
        // asked. `cast` still comes after, because a shadow is a function of the group's own bounds
        // and those are not settled until then.
        var behind = open.Transform is null ? open.Backdrop : null;
        var backdropBounds = default(Rectangle);

        if (behind is not null) {
            backdropBounds = Intersect(open.Box, reachable);

            if (backdropBounds.Width <= 0f || backdropBounds.Height <= 0f) {
                behind = null;
            }
        }

        var bounds = Intersect(ink, reachable);

        if (bounds.Width <= 0f || bounds.Height <= 0f) {
            // ⚠ <b>An empty group survives exactly one thing, and it is the one surface it did not
            // paint.</b> Everything else a group carries — the blur, the colour matrix, the mask, the
            // drop shadow — is a function of its own ink, so with no ink there is provably nothing to
            // show and a layer here would ask both executors for a zero-sized texture, which is a
            // validation error rather than an empty picture. A backdrop is the exception because it
            // holds what was painted *behind* the element, which an element that paints nothing has
            // exactly as much of as one that paints a background. CSS says so plainly: Filter Effects
            // 2 § 2 clips the filtered backdrop to the border box and never mentions the element's
            // own paint, and `backdrop-blur-md` on a bare `div` is a picture a browser shows.
            //
            // ⚠ The border box is what the group is then bounded by, and it is not a substitute for
            // the ink — it is the only rectangle in play. `backdropBounds` is that box already
            // narrowed by the clip and the viewport, and it is non-empty by construction here or
            // `behind` would have been nulled above. The group's own surface stays empty and its
            // composite quad draws transparent black over it, which costs one quad and is what makes
            // this the same code path as every other group rather than a second kind of layer.
            if (behind is null) {
                return;
            }

            bounds = backdropBounds;
        }

        // ⚠ <b>Decided before the layer is built, because the shadow can be clipped away while the
        // group is not, and a layer that named a surface nothing draws would have both executors
        // allocate one and blur into it for nobody.</b> The displacement is applied to the group's
        // already-clipped bounds and the result clipped again: a `drop-shadow(0 400px 0)` inside an
        // `overflow: hidden` panel is a group that composites normally and a shadow that does not
        // exist. Both executors read <see cref="UiLayer.Shadow" /> as the whole answer, so nulling it
        // here is the one place that has to decide.
        var cast = open.Shadow;
        var shadowBounds = default(Rectangle);

        if (cast is { } shadow) {
            shadowBounds = Intersect(
                new Rectangle(bounds.X + shadow.Offset.X, bounds.Y + shadow.Offset.Y, bounds.Width, bounds.Height),
                reachable
            );

            if (shadowBounds.Width <= 0f || shadowBounds.Height <= 0f) {
                cast = null;
            }
        }

        // ⚠ All three drawn off the one counter and in this order, so that they are distinct by
        // construction — see <see cref="UiLayer.ShadowImage" />, which argues against deriving one
        // from another. A group with no shadow and no backdrop consumes one number and not three,
        // which is what keeps a frame's numbering unchanged by a feature it does not use.
        var image = LayerImage(layerNumber++);
        var backdropImage = behind is null ? 0UL : LayerImage(layerNumber++);
        var shadowImage = cast is null ? 0UL : LayerImage(layerNumber++);

        var layer = new UiLayer(open.Draw, draws.Count - open.Draw, bounds, open.Alpha) {
            Image = image,
            Blur = open.Blur,

            // ⚠ Carried with no effect on the bounds, the surface or the quad, because a blend
            // changes only what the composite's fragment arithmetic does with the destination — see
            // `UiBlend.Apply`. It is the one field here that outsets nothing and schedules nothing.
            Blend = open.Blend,

            // ⚠ <b>Carried with no outset of the group's bounds, and that is not the colour matrix's
            // reason.</b> A backdrop's Gaussian does move coverage — but it moves it within a surface
            // of its own, which the capture pass fills and confines for itself, and the result is then
            // clipped to the border box rather than allowed to spread. So the group's own ink is
            // untouched by it. See <c>UiRenderer.Capture</c>, which is where that outset happens.
            Backdrop = behind,
            BackdropImage = backdropImage,
            BackdropBounds = backdropBounds,

            // ⚠ Carried whole, offset included, even though the offset is already spent on the quad
            // below. Both executors need the blur to produce the surface and neither needs the
            // offset; carrying it anyway is what lets a reader check the quad's displacement against
            // the declaration instead of trusting that the builder subtracted the right thing.
            Shadow = cast,
            ShadowImage = shadowImage,

            // ⚠ <b>Carried, and deliberately with no outset of its own.</b> The blur above grew the
            // ink because a Gaussian moves coverage to texels no vertex touched. A colour matrix
            // moves none: it is a function of the texel it is written to, so a group that is
            // `grayscale` and nothing else covers exactly the rectangle it covered before, and
            // growing the bounds "to be safe" would spend surface on pixels that are provably
            // transparent — `UiColorMatrix.Apply` maps transparent black to transparent black.
            Filter = open.Filter,

            // ⚠ The range is filled below rather than here, because the entries have to be rebased
            // onto the viewport's origin on the way in and `masks.Count` is where they will land.
            MaskFirst = masks.Count,
            MaskCount = open.MaskCount,

            // ⚠ Carried un-applied, because the three quads below have already spent it on their
            // vertices. The one consumer is `UiRenderer.BlurSurface`, which needs to know that the
            // composite quad no longer covers the surface it is convolving — see `UiLayer.Transform`.
            Transform = open.Transform
        };

        // ⚠ <b>Copied into this frame's own buffer rather than pointing back into the draw list's,
        // and the rebase is the reason it has to be a copy.</b> `DrawListBuilder` works in absolute
        // document coordinates and knows nothing of the viewport; the composite quad's UV, a few
        // lines up, is deliberately *relative* to the viewport because the surface covers the
        // viewport rather than the document. Both executors recover a mask's point as `uv × size`, so
        // every entry's box has to be in the space that product lands in. Left absolute, a mask would
        // sit correctly on every viewport whose origin is zero — which is every test and most frames
        // — and slide by the origin on the ones where it is not.
        //
        // ⚠ Un-outset for the colour matrix's reason and un-narrowed for neither's. A mask only ever
        // *removes* coverage, so it can no more grow the ink than a matrix can — and it must not
        // shrink the bounds either, however tempting that is on a ramp that reaches zero halfway
        // across. The bounds are what both executors allocate and clear; a mask that narrowed them
        // would be deciding the group's extent from a coverage the *composite* applies, which the
        // surface's own contents know nothing about.
        for (var entry = 0; entry < open.MaskCount; entry++) {
            var shape = list.Masks[open.MaskFirst + entry];
            masks.Add(shape with { Centre = shape.Centre - new Vector2(viewport.X, viewport.Y) });
        }

        // ⚠ <b>Inserted in pre-order rather than appended, and the number is a counter rather than the
        // position.</b> Groups close innermost first, so appending would give post-order — and a
        // consumer walking the draws with a stack needs to meet the outer group before the inner one it
        // contains. The two cannot be told apart by <see cref="UiLayer.First" /> alone either: a
        // translucent element that paints nothing of its own before a translucent child opens both
        // groups at the same draw index, and only the wider <c>Count</c> says which is outside. The
        // number stays a counter because a dropped group leaves no gap that way.
        var at = layers.Count;

        while (at > 0 && (layers[at - 1].First > layer.First
            || (layers[at - 1].First == layer.First && layers[at - 1].Count < layer.Count))) {
            at--;
        }

        layers.Insert(at, layer);

        // ⚠ <b>The shadow is a second ordinary image draw, emitted <i>before</i> the group's, and
        // that ordering is the whole of "composited under".</b> Nothing in either executor knows a
        // shadow from a nested group's composite: both are premultiplied surfaces sampled by a quad,
        // and paint order is what puts one behind the other. Emitting it after would draw a
        // silhouette over the element that cast it, which is a solid block of the shadow's colour and
        // not a subtle error.
        //
        // ⚠ <b>The quad is displaced and its texture coordinates are not, which is the whole of
        // "offset".</b> The surface it samples is the viewport's size and holds the group where the
        // group is, so a quad drawn at <c>bounds + offset</c> reading <c>bounds</c> is the silhouette
        // moved. Subtracting the offset from the <i>clipped</i> rectangle rather than from
        // <c>bounds</c> is what keeps that true after a clip has taken a bite out of it: the UV has
        // to name the part of the surface this quad actually covers, which is no longer the whole of
        // what it would have covered.
        //
        // ⚠ <b>The alpha is the shadow colour's times the group's own.</b> Both executors scale all
        // four channels of a premultiplied sample by the quad's alpha — see
        // <c>SoftwareUiRasterizer.Composite</c> — so this is the one place the colour's alpha can go;
        // <see cref="UiDropShadow.Tint" /> is a three-row matrix and cannot carry it. A group at
        // <c>opacity: 0.5</c> with a shadow at 25% gets one eighth, which is what nesting two fades
        // means.
        // ⚠ <b>First of the three quads, which is what "the filtered backdrop is behind the element"
        // means in a painter's algorithm.</b> Nothing in either executor knows a backdrop from a
        // nested group's composite — both are premultiplied surfaces sampled by a quad — so paint
        // order is the whole of it. Emitting it after the group's own composite would draw the blurred
        // scene *over* the panel, which is not a subtle error; emitting it after the shadow would put
        // the element's own drop shadow behind its backdrop, which is.
        //
        // ⚠ <b>The quad covers <c>backdropBounds</c> — the border box — and its texture coordinates
        // are the plain viewport-relative ones.</b> The capture surface is the viewport's size and
        // holds the picture where the picture is, so there is no displacement here of the kind the
        // shadow's quad carries: a backdrop is read from exactly where it is drawn.
        //
        // ⚠ <b>The alpha is the group's own times the backdrop's <c>opacity()</c>.</b> Both executors
        // scale all four channels of a premultiplied sample by the quad's alpha, so this is the one
        // place <see cref="UiBackdrop.Alpha" /> can go — <see cref="UiColorMatrix" /> has no alpha
        // row. Multiplying the group's own opacity in is what CSS means by the backdrop image being
        // painted inside the element's stacking context: a half-opaque glass panel shows half a
        // blurred backdrop over half the sharp one, which is what a browser draws.
        if (behind is { } filtered) {
            var backdropFirst = indices.Count;

            Quad(
                backdropBounds.X,
                backdropBounds.Y,
                backdropBounds.X + backdropBounds.Width,
                backdropBounds.Y + backdropBounds.Height,
                new Vector2(
                    (backdropBounds.X - viewport.X) / viewport.Width,
                    (backdropBounds.Y - viewport.Y) / viewport.Height
                ),
                new Vector2(
                    (backdropBounds.X + backdropBounds.Width - viewport.X) / viewport.Width,
                    (backdropBounds.Y + backdropBounds.Height - viewport.Y) / viewport.Height
                ),
                new Color4(1f, 1f, 1f, open.Alpha * filtered.Alpha),
                new Vector4(1f, 0f, 0f, 0f)
            );

            draws.Add(
                new UiDraw(
                    BatchKind.Image,
                    backdropFirst,
                    indices.Count - backdropFirst,
                    0,
                    Intersect(open.Clip, viewport)
                ) {
                    Image = backdropImage
                }
            );
        }

        if (cast is { } drop) {
            var shadowFirst = indices.Count;

            Quad(
                shadowBounds.X,
                shadowBounds.Y,
                shadowBounds.X + shadowBounds.Width,
                shadowBounds.Y + shadowBounds.Height,
                new Vector2(
                    (shadowBounds.X - drop.Offset.X - viewport.X) / viewport.Width,
                    (shadowBounds.Y - drop.Offset.Y - viewport.Y) / viewport.Height
                ),
                new Vector2(
                    (shadowBounds.X + shadowBounds.Width - drop.Offset.X - viewport.X) / viewport.Width,
                    (shadowBounds.Y + shadowBounds.Height - drop.Offset.Y - viewport.Y) / viewport.Height
                ),
                new Color4(1f, 1f, 1f, open.Alpha * drop.Colour.A),
                new Vector4(1f, 0f, 0f, 0f),

                // ⚠ Positions transformed, texture coordinates not — and the displacement above stays
                // in the surface's space, which is what makes the order right. CSS applies the filter
                // and then the transform, so a rotated panel's shadow is offset in the panel's own
                // frame and swings round with it, rather than always falling to the same corner of the
                // screen. Both readings look identical at zero degrees.
                open.Transform
            );

            draws.Add(
                new UiDraw(
                    BatchKind.Image,
                    shadowFirst,
                    indices.Count - shadowFirst,
                    0,
                    Intersect(open.Clip, viewport)
                ) {
                    Image = shadowImage
                }
            );
        }

        // ⚠ <b>The composite is an ordinary image draw, and its <c>Shape.X</c> is one.</b> The layer's
        // surface holds *premultiplied* colour, because that is what every UI pipeline writes and what
        // the blend state consumes — where an ordinary image is straight-alpha content the shader has
        // to premultiply on the way out. Sampling one as the other doubles the multiply and shows as a
        // dark fringe around everything in the group, so the shader is told which it is being handed.
        // Zero is the straight-alpha case, which is what every image quad already carries.
        var first = indices.Count;

        Quad(
            bounds.X,
            bounds.Y,
            bounds.X + bounds.Width,
            bounds.Y + bounds.Height,
            // ⚠ Relative to the viewport's own origin, which is not always zero. The surface covers the
            // viewport, so a UV is where a document coordinate sits *within* it — dividing the raw
            // coordinate would be right only for a surface whose top left is the document's.
            new Vector2((bounds.X - viewport.X) / viewport.Width, (bounds.Y - viewport.Y) / viewport.Height),
            new Vector2(
                (bounds.X + bounds.Width - viewport.X) / viewport.Width,
                (bounds.Y + bounds.Height - viewport.Y) / viewport.Height
            ),
            new Color4(1f, 1f, 1f, open.Alpha),
            new Vector4(1f, 0f, 0f, 0f),

            // ⚠ <b>This is the transform.</b> Four positions moved, four texture coordinates left
            // alone, and a picture that was rasterised upright arrives rotated or scaled. Nothing
            // downstream is told: the software rasteriser interpolates the coordinate by barycentrics
            // and the device by its own rasteriser, and both are exact for an affine.
            open.Transform
        );

        // ⚠ The scissor is the ancestor clip *untransformed*, which is the other half of the pre-image
        // above. The clip belongs to an ancestor and cuts in the ancestor's space, so the rectangle
        // here is the real one; what the pre-image decided was only how much of the surface was worth
        // keeping on the way in.
        draws.Add(
            new UiDraw(BatchKind.Image, first, indices.Count - first, 0, Intersect(open.Clip, viewport)) {
                Image = layer.Image
            }
        );
    }

    /// <summary>The bounding box of every vertex emitted since a group opened.</summary>
    /// <remarks>
    ///     ⚠ <b>Positions only, and that is exact rather than approximate.</b> Every UI primitive is
    ///     already expanded to the quad that contains its ink before it reaches the vertex list — a
    ///     shadow's quad is grown by its blur, a path's by its fringe, a glyph's by its field padding,
    ///     a box's by <see cref="BoxMargin" /> — because each of those shaders resolves coverage
    ///     <i>inside</i> the geometry it is given. ⚠ The box was the exception until #590, and it was
    ///     the exception silently: a hairline on a half-pixel coordinate had ink the quad did not
    ///     contain, so the hull here was right about the vertices and wrong about the picture. So no
    ///     fragment can land outside the hull of the positions, and there is no per-kind margin to add
    ///     here that would not be added twice.
    ///     <para>
    ///         ⚠ <b>A group's <c>filter: blur()</c> is the one exception, and it is not a per-kind
    ///         margin.</b> It is applied to the surface the quads were rasterised into rather than to
    ///         any of them, so it moves coverage outside this hull no matter how each primitive
    ///         expanded itself. That outset is added by <see cref="Layer" />, once per group, and
    ///         deliberately not here — adding it per vertex would grow the hull of a group that
    ///         happens to sit inside a blurred one as well.
    ///     </para>
    /// </remarks>
    Rectangle Ink(int from) {
        if (from >= vertices.Count) {
            return default;
        }

        var left = float.MaxValue;
        var top = float.MaxValue;
        var right = float.MinValue;
        var bottom = float.MinValue;

        for (var i = from; i < vertices.Count; i++) {
            var position = vertices[i].Position;
            left = MathF.Min(left, position.X);
            top = MathF.Min(top, position.Y);
            right = MathF.Max(right, position.X);
            bottom = MathF.Max(bottom, position.Y);
        }

        left = MathF.Floor(left);
        top = MathF.Floor(top);

        return new Rectangle(left, top, MathF.Ceiling(right) - left, MathF.Ceiling(bottom) - top);
    }

    /// <summary>The number a composited group's surface is named by.</summary>
    /// <remarks>
    ///     ⚠ <b>A reserved range at the top of the space, so that it cannot collide with a texture a
    ///     host registered.</b> <see cref="DrawCommand.Image" /> is whatever number a renderer handed
    ///     out for a texture it owns — <c>ThumbnailCache</c> and <c>Viewport</c> both count up from one
    ///     — and a group's surface has to be named without asking anybody, because it is discovered
    ///     while the geometry is being built. Counting <i>down</i> from the top means the two
    ///     allocators would have to issue about nine quintillion numbers each before they met.
    ///     <para>
    ///         Public because the agreement is across assemblies: <c>Vixen.Ui.Renderer</c> registers a
    ///         texture under this number and <c>SoftwareUiRasterizer</c> looks a surface up by it, and
    ///         neither can be allowed to have its own copy of the rule.
    ///     </para>
    /// </remarks>
    public static ulong LayerImage(int index) => ulong.MaxValue - (ulong) index;

    /// <summary>A group that has been pushed and not yet popped.</summary>
    /// <remarks>
    ///     ⚠ <b>The filter is carried from the <i>push</i>, and <see cref="UiGeometryBuilder.Layer" />
    ///     never looks at the pop's copy.</b> `DrawListBuilder.Emit` writes the same values onto both
    ///     brackets, so reading either is right today — and the push is the one that has to be read,
    ///     because a pop with no push is dropped and a `LayerPop` a caller assembled by hand carries
    ///     whatever it carries. The same argument the entry clip already makes a few lines up.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <c>Box</c> is the element's own border box, in document pixels — the <c>LayerPush</c>'s
    ///     own rectangle — and is read <i>only</i> by the backdrop. It is deliberately not confused
    ///     with <c>Ink</c>: a group's ink is what its subtree drew, which a child overflowing the
    ///     element makes bigger and a blur makes bigger still. CSS clips a backdrop filter to the
    ///     border box, so this is the rectangle that has to survive to
    ///     <see cref="UiLayer.BackdropBounds" />.
    /// </remarks>
    readonly record struct Opening(
        int Draw,
        int Vertex,
        float Alpha,
        Rectangle Clip,
        float Blur,
        UiColorMatrix? Filter,
        int MaskFirst,
        int MaskCount,
        UiDropShadow? Shadow,
        UiBackdrop? Backdrop,
        Rectangle Box,
        UiTransform? Transform,
        UiBlendMode Blend
    );

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
        //
        // ⚠ Grown by `BoxMargin`, exactly as a shadow's quad is grown by its blur, and for the same
        // reason: the shader resolves coverage *inside* the geometry it is given, so an edge whose
        // antialiasing ramp reaches outside the box needs somewhere for the ramp to land.
        // `half` is unchanged — it is the *box's*, not the quad's — so the field still measures from
        // the boundary the caller asked for and the two extra rings simply fall to zero coverage.
        Quad(
            command.X - BoxMargin,
            command.Y - BoxMargin,
            command.X + command.Width + BoxMargin,
            command.Y + command.Height + BoxMargin,
            new Vector2(-half.X - BoxMargin, -half.Y - BoxMargin),
            new Vector2(half.X + BoxMargin, half.Y + BoxMargin),
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
    ///     <para>
    ///         ⚠ <b>One step of linear probing, and it is what makes "two colours are two searches" a
    ///         property rather than a hope.</b> With a bare direct-mapped table any two tokens that
    ///         happened to share a slot evicted each other on every lookup, so a palette of two
    ///         colours could cost a <c>GamutMap.Map</c> per quad for the life of the process —
    ///         invisible except as time. Checking the next slot as well costs one comparison on a
    ///         path that is already about to spend a thousand nanoseconds, and it makes any
    ///         <em>pair</em> of colours resident whatever they hash to, because the second one lands
    ///         beside the first instead of on top of it.
    ///     </para>
    /// </remarks>
    Color4 Show(Color4 colour) {
        var linear = new Vector3(colour.R, colour.G, colour.B);

        if (GamutMap.InGamut(linear, Gamut)) {
            return Lit(linear, colour.A);
        }

        MappedColours++;

        var home = HomeSlot(colour);
        ref var first = ref shown[home];

        if (first.Occupied && first.Source == linear) {
            return Lit(first.Shown, colour.A);
        }

        ref var second = ref shown[home + 1 == Slots ? 0 : home + 1];

        if (second.Occupied && second.Source == linear) {
            return Lit(second.Shown, colour.A);
        }

        ColourSearches++;
        var mapped = GamutMap.Map(linear, Gamut);

        // The free one of the pair if there is one, and the home slot otherwise. Evicting only when
        // three colours want the same pair is what keeps this a fixed table with no policy: nothing
        // is ranked, nothing is aged, and the worst a wrong choice costs is another search.
        ref var slot = ref first;

        if (first.Occupied && !second.Occupied) {
            slot = ref second;
        }

        slot = new ShownColour { Source = linear, Shown = mapped, Occupied = true };

        return Lit(mapped, colour.A);
    }

    /// <summary>Puts a showable colour into the target's units.</summary>
    /// <remarks>
    ///     The one place <see cref="WhiteLevel" /> is spent, so that every path out of
    ///     <see cref="Show" /> — in gamut, remembered, and freshly mapped — scales the same way. ⚠ A
    ///     scale applied on one of the three is worse than none: the cached path is the one a second
    ///     frame takes, so the interface would be right until it stopped changing.
    /// </remarks>
    /// <param name="shown">The colour, in gamut, still display-referred.</param>
    /// <param name="alpha">Its coverage, which is not a luminance and is carried through.</param>
    /// <returns>The colour a vertex carries.</returns>
    Color4 Lit(Vector3 shown, float alpha) =>
        new(shown.X * WhiteLevel, shown.Y * WhiteLevel, shown.Z * WhiteLevel, alpha);

    /// <summary>The first of the two slots a colour may be remembered in.</summary>
    /// <remarks>
    ///     <para>
    ///         Hashes the three channels' bit patterns rather than their values: two colours that
    ///         differ below what a float can express are the same entry, and that is exactly what
    ///         should happen. Alpha is absent for the reason <see cref="Show" /> gives.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A fixed mix rather than <see cref="HashCode.Combine{T1,T2,T3}" />, which folds in
    ///         a seed drawn once per process.</b> Under that, which two colours shared a slot was
    ///         decided afresh at every start — so the table's collision behaviour could not be
    ///         reproduced on the run where it misbehaved, and a test that counted searches for two
    ///         particular colours was an instrument whose verdict was a coin toss at roughly 1 in
    ///         <see cref="Slots" />. Nothing here is exposed to untrusted input; a seeded hash buys
    ///         this table no defence and costs it reproducibility.
    ///     </para>
    /// </remarks>
    internal static int HomeSlot(Color4 colour) {
        var hash = 2166136261u;
        hash = (hash ^ BitConverter.SingleToUInt32Bits(colour.R)) * 16777619u;
        hash = (hash ^ BitConverter.SingleToUInt32Bits(colour.G)) * 16777619u;
        hash = (hash ^ BitConverter.SingleToUInt32Bits(colour.B)) * 16777619u;

        // ⚠ Then avalanched, because FNV-1a ends in a multiply and a product's *low* bits carry
        // almost none of the operand's — and the low bits are the whole of the index below. Without
        // this, three channels that differ only in their mantissas' last bits land in a handful of
        // slots.
        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;

        return (int) (hash % Slots);
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
            blur,
            style.PaintCentre,
            style.PaintExtent,
            style.AreaCentre,
            style.AreaHalf
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
    /// <remarks>
    ///     ⚠ <b><paramref name="placed" /> moves the four positions and leaves the four texture
    ///     coordinates exactly where they were, and that asymmetry is the whole of how a transform is
    ///     painted.</b> The coordinates name a place in a surface the group was rasterised into
    ///     untransformed; the positions say where that picture goes. Transforming both would be a
    ///     no-op, and transforming the coordinates alone would sample a rotated window onto an
    ///     upright picture.
    ///     <para>
    ///         ⚠ <b>Exact rather than approximate, and only for an affine.</b> Both executors
    ///         interpolate the coordinate linearly across the two triangles — the software one by
    ///         barycentrics in <c>SoftwareUiRasterizer.Triangle</c>, the device by the rasteriser's own
    ///         — and the composition of an affine map with a linear interpolation is that same
    ///         interpolation, so the two triangles agree along the shared diagonal and no seam appears.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A projective map does not have that property, and this paragraph used to say the
    ///         type could not express one — it can now.</b> <see cref="UiTransform" /> is a 3×3 since
    ///         #547, so <see cref="UiTransform.Apply" /> here silently drops the <c>w</c> that a
    ///         perspective needs: the four positions land correctly and the texture coordinate between
    ///         them is interpolated as though they were affine, which is a seam along the shared
    ///         diagonal and a picture that is wrong in the middle and right at the corners. Nothing
    ///         constructs a projective transform yet, so nothing reaches this — and
    ///         <see cref="UiTransform.Project" /> is the call this becomes when #548 gives the vertex
    ///         format somewhere to put it.
    ///     </para>
    ///     <para>
    ///         Null is the ordinary case and costs one null check per quad, on a path that already
    ///         does a gamut lookup per quad.
    ///     </para>
    /// </remarks>
    void Quad(
        float left,
        float top,
        float right,
        float bottom,
        Vector2 textureMin,
        Vector2 textureMax,
        Color4 color,
        Vector4 shape,
        UiTransform? placed = null
    ) {
        var start = (uint)vertices.Count;

        // Once per quad rather than once per vertex: the four corners share a colour, and asking
        // four times would be four early-out tests to reach one answer.
        color = Show(color);

        var topLeft = new Vector2(left, top);
        var topRight = new Vector2(right, top);
        var bottomRight = new Vector2(right, bottom);
        var bottomLeft = new Vector2(left, bottom);

        if (placed is { } matrix) {
            topLeft = matrix.Apply(topLeft);
            topRight = matrix.Apply(topRight);
            bottomRight = matrix.Apply(bottomRight);
            bottomLeft = matrix.Apply(bottomLeft);
        }

        vertices.Add(new UiVertex(topLeft, textureMin, color, shape));
        vertices.Add(new UiVertex(topRight, new Vector2(textureMax.X, textureMin.Y), color, shape));
        vertices.Add(new UiVertex(bottomRight, textureMax, color, shape));
        vertices.Add(new UiVertex(bottomLeft, new Vector2(textureMin.X, textureMax.Y), color, shape));

        indices.Add(start);
        indices.Add(start + 1);
        indices.Add(start + 2);
        indices.Add(start);
        indices.Add(start + 2);
        indices.Add(start + 3);
    }
}
