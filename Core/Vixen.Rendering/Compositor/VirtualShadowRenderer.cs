// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

namespace Vixen.Rendering.Compositor;

/// <summary>
///     The sun's shadow at the resolution each pixel needs: phase 7's node.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="ShadowMapRenderer" />'s replacement rather than its sibling, and the reason is
///         the geometry.</b> Four cascades are four fixed resolutions over a whole frustum, and the one
///         a pixel gets is whichever slice it landed in — tolerable when the geometry is an authored LOD
///         chain, and absurd when it is a cut chosen to put a cluster's error under a pixel. This node
///         fits a <em>clipmap</em> instead: levels doubling in extent, centred on the camera and snapped
///         to whole pages, of which only the pages some pixel actually asked for are allocated or drawn.
///     </para>
///     <para>
///         <b>Four things happen here in an order nothing else can express.</b> The marks the previous
///         frame wrote are serviced, which allocates pages; the pages that hold nothing yet are drawn;
///         the page table is uploaded; and this frame's depth is marked for the next one. The first
///         three have to precede any shading pass that reads the table, and the fourth has to follow
///         whatever wrote the depth — so this node is placed after the depth prepass and before the
///         shading, and the ordering inside it is not a document's to get wrong.
///     </para>
///     <para>
///         <b>The casters are the ordinary shadow-caster stage.</b>
///         <c>docs/plan/22-virtualized-geometry.md</c> phase 7 describes clusters cast through the same
///         traversal with a different view record, which needs a visible list per view — the traversal
///         produces one list for every view it was given, with no view tag on an entry. That is a change
///         to phase 3's output rather than to this node, so it is named as owed and a virtualized mesh
///         casts through the fallback mesh phase 1 generates for exactly this: the path the virtualized
///         raster does not reach.
///     </para>
/// </remarks>
public sealed class VirtualShadowRenderer : SceneRenderer {
    readonly List<RenderView> views = [];
    readonly List<VirtualShadowLevel> records = [];
    readonly List<(int Page, int Slot, Matrix4x4 Projection)> owed = [];
    readonly List<Matrix4x4> fitted = [];

    // What Fit needs to tell a slide from a move. The matrices alone cannot: two matrices that differ
    // say only *that* the level moved, and the whole of task #317 is that a level which slid laterally
    // has kept every page's world footprint while a level whose near plane stepped has kept none.
    readonly List<Int2> fittedOrigins = [];
    readonly List<int> fittedDepthCells = [];
    readonly List<int> depthCells = [];

    // Every caster's footprint as of the last frame that looked, indexed by RenderObjectId.Index —
    // the transforms array's own indexing, and for its reason: an id is a slot and a slot is stable.
    readonly List<Footprint> footprints = [];

    Vector3 fittedLight = new(float.NaN);
    float fittedExtent = float.NaN;
    float fittedDepth = float.NaN;
    int fittedClipmap = -1;

    /// <summary>The stage that draws depth-only casters.</summary>
    public required RenderStage CasterStage { get; init; }

    /// <summary>The atlas, its pages and its residency. Null does nothing at all.</summary>
    public VirtualShadowAtlas? Atlas { get; set; }

    /// <summary>The per-view block to bind before each page's casters.</summary>
    public ViewConstants? Constants { get; set; }

    /// <summary>Where to publish what a shading pass needs, or null to publish nothing.</summary>
    public ParameterCollection? Scene { get; set; }

    /// <summary>Where the comparison sampler comes from.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>Which passes' compose slots the map is published under, qualified.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="PunctualShadowRenderer" />'s arrangement: a composed feature's bindings belong
    ///         to whichever pass composed it, so the same values are written once per pass rather than
    ///         once for a set nobody owns. Both shading passes by default, because a frame that shades a
    ///         virtualized surface and a classic one under different shadow terms is the divergence
    ///         <c>ClusteredShading</c> was extracted to prevent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The entries are the pass and then the shader filling its slot</b>, as in
    ///         <c>ForwardPlus.VirtualShadowLookup</c> — a composed slot's bindings are named for what
    ///         fills it, so <c>shadowLevels</c> reaches set 0 as
    ///         <c>ForwardPlus.VirtualShadowLookup.shadowLevels</c>. The default here used to be the two
    ///         bare pass names, which published every value under a prefix no variant declares: the map
    ///         rendered, the table uploaded, and the lookup read nothing at all.
    ///     </para>
    /// </remarks>
    public IList<string> Passes { get; } = new List<string> {
        "ForwardPlus.VirtualShadowLookup", "VisibilityResolve.VirtualShadowLookup"
    };

    /// <summary>The camera the clipmap is centred on and whose depth is marked.</summary>
    public RenderView? Camera { get; set; }

    /// <summary>Which depth resource to mark from.</summary>
    public string Depth { get; set; } = "SceneDepth";

    /// <summary>The direction the sun's light travels, toward the scene.</summary>
    /// <remarks>Overridden by <see cref="Sun" /> where there is one, exactly as a cascade's is.</remarks>
    public Vector3 LightDirection { get; set; } = new(-0.4f, -1f, -0.3f);

    /// <summary>Where the sun comes from, or null to use <see cref="LightDirection" />.</summary>
    /// <remarks>
    ///     ⚠ A clipmap fitted along a constant while the frame shades along a light that moved puts
    ///     every shadow at an angle nothing in the picture explains — which is why
    ///     <see cref="ShadowMapRenderer" /> takes the same source rather than a vector, and why a
    ///     rotating sun invalidates every page below.
    /// </remarks>
    public ISunSource? Sun { get; set; }

    /// <summary>One projection per shadowed spot light, or empty.</summary>
    /// <remarks>
    ///     Supplied rather than derived, because a spot's projection is
    ///     <see cref="ShadowProjections.Spot" /> of a light the scene owns and this node has no light
    ///     list. A host that renders punctual shadows already builds exactly these.
    /// </remarks>
    public IList<Matrix4x4> SpotProjections { get; } = [];

    /// <summary>How many levels the directional clipmap has.</summary>
    /// <remarks>
    ///     Eight doublings from <see cref="FirstExtent" />, which at the default reaches a kilometre and
    ///     a quarter. More levels cost nothing that is not drawn — a level nobody marks allocates no
    ///     pages — which is the difference from a cascade count, where every one is rendered whether or
    ///     not anything is in it.
    /// </remarks>
    public int ClipmapLevels { get; set; } = 8;

    /// <summary>How wide clipmap level zero is, in world units.</summary>
    public float FirstExtent { get; set; } = 10f;

    /// <summary>How deep each level's box is along the light, which is its caster range.</summary>
    public float Depthrange { get; set; } = 400f;

    /// <summary>How far the sun may drift before the clipmap refits to it, in degrees.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The knob that keeps a moving sun from being a permanent stall.</b> The light
    ///         direction enters every level's matrix raw, so without a snap a sun that moves at all —
    ///         sample 13's orbits deliberately — changes every projection every frame, and
    ///         <see cref="Fit" /> invalidates every resident page every frame. The budget then redraws
    ///         sixteen pages a frame that the next frame's refit unpublishes before the table upload
    ///         ever carried them: pages pile up allocated and owed while the map answers nothing.
    ///         <see cref="VirtualShadowMap.SnapDirection" /> holds the fitted direction still between
    ///         steps, so the pages drawn against it stay true and the invalidation happens once per
    ///         step rather than once per frame.
    ///     </para>
    ///     <para>
    ///         The step is how far the map's shadows may trail the shading's sun — half a degree is
    ///         under what the bias forgives — and its reciprocal is how often a turning sun rebuilds
    ///         the map. Zero disables the snap, which is only safe for a sun that never moves.
    ///     </para>
    /// </remarks>
    public float LightSnapDegrees { get; set; } = 0.5f;

    /// <summary>The constant depth bias the lookup compares with, in metres.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Metres, and that is the whole of the fix.</b>
    ///         <see cref="ShadowMapRenderer.ConstantBias" />'s counterpart, converted by
    ///         <see cref="VirtualShadowMap.DepthScale" /> before it is published — because a level's
    ///         box is <see cref="Depthrange" /> metres deep, so one unit of the normalised depth the
    ///         lookup compares in is four hundred metres of world at the default.
    ///     </para>
    ///     <para>
    ///         ⚠ Until this existed the lookup used its own declaration's default of 0.002 in
    ///         normalised depth, which is <em>0.8 m</em> — a hundred times the cascades'. A page that
    ///         answered therefore biased away every shadow whose caster stood within a metre of its
    ///         receiver, while the cascades kept it; the two answers could not agree, and a page
    ///         arriving put a contact shadow out.
    ///     </para>
    /// </remarks>
    public float ConstantBias { get; set; } = 0.008f;

    /// <summary>The slope-scaled depth bias, in metres. <see cref="ConstantBias" />' other half.</summary>
    public float SlopeBias { get; set; } = 0.01f;

    /// <summary>Whether a caster that moved retires the pages it was under and the pages it is under.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The correctness half of the cache, and it is on by default because the alternative is
    ///         a wrong picture rather than a slow one.</b> A page is drawn once and kept until
    ///         something says its depths are stale; a moved light says so for every page it owns and a
    ///         re-fitted level for every page of that level, but until this existed a moved
    ///         <em>object</em> said nothing at all. Its shadow then stayed exactly where the object had
    ///         been — a silhouette standing in the air with nothing casting it — for as long as
    ///         nothing else happened to retire that page, which in a still scene is the rest of the
    ///         session.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both ends of the move, and the "was" end is the visible one.</b> Retiring only
    ///         where the object now is leaves the stale silhouette behind and adds a correct one
    ///         beside it, which looks like a duplicate rather than like a bug.
    ///     </para>
    ///     <para>
    ///         Turning it off is what a scene of nothing but static geometry may do to skip a walk of
    ///         the object store — see <see cref="MovedCasters" /> for what the walk costs and what it
    ///         is worth.
    ///     </para>
    /// </remarks>
    public bool InvalidateMovedCasters { get; set; } = true;

    /// <summary>Where to find a caster's world matrix, so that a turn counts as a move.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="PunctualShadowRenderer.CasterTransforms" />' key and its argument:
    ///         <see cref="RenderObject.Bounds" /> is a sphere and a sphere is invariant under
    ///         rotation, so a caster that turned on the spot has the bounds it had. Its shadow did
    ///         not, and without this key nothing retires the page it falls on.
    ///     </para>
    ///     <para>
    ///         Null leaves the comparison on bounds alone, which is what a frame with no transform
    ///         feature can honestly say. That is the same bargain the punctual cache makes.
    ///     </para>
    /// </remarks>
    public RenderDataKey<Matrix4x4>? CasterTransforms { get; set; }

    /// <summary>How many pages may be allocated per frame.</summary>
    public int PagesPerFrame { get; set; } = 16;

    /// <summary>How many pages may be drawn per frame.</summary>
    /// <remarks>
    ///     A budget on <see cref="PageResidency.Service" />'s terms. A camera that turns to face a city
    ///     allocates every page it can see at once, and drawing all of them in one frame spends that
    ///     frame's whole caster budget on pages the next frame may not want. What is left over is drawn
    ///     a frame or two later, and the cascades cover it in the meantime.
    /// </remarks>
    public int DrawsPerFrame { get; set; } = 16;

    /// <summary>How many pages the last frame's marking asked for.</summary>
    public int MarkedPages => Atlas?.MarkedPages ?? 0;

    /// <summary>How many pages the last frame drew.</summary>
    public int DrawnPages { get; private set; }

    /// <summary>
    ///     How many of <see cref="MarkedPages" /> this frame's table answers, and how many it does not.
    /// </summary>
    /// <remarks>
    ///     <see cref="VirtualShadowAtlas.AnsweredPages" />, which is where the argument for measuring it
    ///     at the table upload is made. <see cref="AbsentPages" /> is the count of pages this frame
    ///     shades from the cascades instead, and it is the only counter here that is about coverage
    ///     rather than about effort.
    /// </remarks>
    public int AnsweredPages => Atlas?.AnsweredPages ?? 0;

    /// <inheritdoc cref="AnsweredPages" />
    public int AbsentPages => Atlas?.AbsentPages ?? 0;

    /// <summary>How many pages <see cref="Fit" /> invalidated this frame, and over how many levels.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a refit costs, stated rather than inferred.</b> The budget redraws
    ///         <see cref="DrawsPerFrame" /> pages a frame, so a scene where this stands above that on
    ///         most frames is a scene whose map is structurally unable to answer, however healthy
    ///         <see cref="DrawnPages" /> looks. <see cref="LightSnapDegrees" />, the clipmap's page
    ///         snap and <see cref="VirtualShadowMap.DepthStep" /> exist to keep it low, and whether
    ///         they do is a question about the scene's own rates.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="RefitLevels" /> is not a share of this, and after task #317 the two
    ///         come apart.</b> A level that slid laterally is a refit that invalidates the thirty-two
    ///         pages of one column rather than all thousand and twenty-four, because a page is
    ///         addressed toroidally — so a walking camera now refits as often as it ever did and
    ///         throws away a fraction as much.
    ///     </para>
    /// </remarks>
    public int InvalidatedPages { get; private set; }

    /// <inheritdoc cref="InvalidatedPages" />
    public int RefitLevels { get; private set; }

    /// <summary>How many casters moved this frame, and how many pages that cost.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The pair is the whole diagnosis, and neither number alone is.</b>
    ///         <see cref="CasterInvalidations" /> is a share of <see cref="InvalidatedPages" />, so a
    ///         frame whose map cannot converge is read by asking which of the two terms dominates: a
    ///         large <see cref="RefitLevels" /> is the clipmap moving under a camera or a sun, and a
    ///         large <see cref="MovedCasters" /> is the scene itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Pages per moved caster is the number that says whether the bound is the problem.</b>
    ///         A caster's footprint is a page or two on the fine levels and a fraction of one on the
    ///         coarse ones, so a handful of pages per mover is what a correct scene looks like. A
    ///         hundred is a bounding sphere that covers a building — a merged batch, or a skinned mesh
    ///         whose bounds were fitted to its whole animation — and the cure is the bound rather than
    ///         anything here.
    ///     </para>
    /// </remarks>
    public int MovedCasters { get; private set; }

    /// <inheritdoc cref="MovedCasters" />
    public int CasterInvalidations { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <b>The pages are chosen here and not in <c>Build</c>, because a page is a view.</b> A
    ///         caster has to be culled against the page's own volume before anything is recorded, and
    ///         collect is the phase that exists for exactly that — the same reason
    ///         <see cref="ShadowMapRenderer" /> fits its cascades here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The levels are refitted before the marks are serviced, and a level that moved
    ///         invalidates its pages.</b> Which of them depends on <em>how</em> it moved: a page's
    ///         identity is its <see cref="VirtualShadowMap.ToroidalOf" /> address, which a lateral
    ///         slide leaves alone except on the column and row that wrapped, and which every other
    ///         kind of move — the sun's snap stepping, the near plane stepping along the light —
    ///         invalidates wholesale because every stored depth in the level shifted. The page snap is
    ///         what makes even the slide rare: a camera that moved less than a page leaves every
    ///         footprint bit-identical and nothing is invalidated at all.
    ///     </para>
    /// </remarks>
    protected internal override void Collect(GraphicsCompositor compositor) {
        ArgumentNullException.ThrowIfNull(compositor);

        DrawnPages = 0;
        MovedCasters = 0;
        CasterInvalidations = 0;
        owed.Clear();

        if (Atlas is not { } atlas || Camera is not { } camera) {
            return;
        }

        // Snapped before anything reads it, so the fit, the page views and their sort hint all share
        // one direction — see LightSnapDegrees for what an unsnapped sun costs.
        var sunward = VirtualShadowMap.SnapDirection(
            Sun?.Sun is { } star ? star.Direction : LightDirection,
            LightSnapDegrees
        );

        Fit(atlas, camera, sunward);

        // ⚠ **After the fit and before the marks are serviced, and both halves of that matter.** The
        // levels a footprint is measured against have to be *this* frame's, or a caster is retired
        // from pages of a window that has already slid; and a page a moved caster retires has to be
        // back in the pending queue before TakePending runs below, or its redraw waits a frame it
        // does not need to.
        Displace(compositor.System, atlas);

        // Last frame's marks, which is what allocates. A frame late by construction — see
        // VirtualShadowAtlas — and the cost of the latency is a page briefly shadowed by the cascades.
        atlas.ServiceMarks(PagesPerFrame);

        foreach (var page in atlas.Pages.TakePending(DrawsPerFrame)) {
            if (!atlas.Pages.TryGetAllocation(page, out var slot)) {
                continue;
            }

            var level = page / VirtualShadowMap.PagesPerMap;

            if (level >= records.Count) {
                continue;
            }

            var within = page - (level * VirtualShadowMap.PagesPerMap);

            // ⚠ **The address back to a window cell, which is the half of the toroidal scheme no
            // shader does.** A page is owed a draw by its address, and PageProjection wants the
            // rectangle of the level's clip space it occupies — which is where the *window* puts it,
            // not where the address does. Skipping the inverse draws every page of a level that has
            // ever slid out of the wrong part of the world: real geometry, plausible depths, in the
            // wrong place, and no counter here would say so.
            var toroidal = new Int2(
                within % VirtualShadowMap.PagesPerSide,
                within / VirtualShadowMap.PagesPerSide
            );

            var grid = VirtualShadowMap.GridOf(toroidal, records[level].Origin);
            var projection = VirtualShadowMap.PageProjection(records[level].ViewProjection, grid);

            owed.Add((page, slot, projection));
        }

        while (views.Count < owed.Count) {
            views.Add(new($"{this}.Page{views.Count}"));
        }

        for (var i = 0; i < owed.Count; i++) {
            var view = views[i];

            view.ViewProjection = owed[i].Projection;

            // The light's position rather than the camera's, for ShadowMapRenderer's reason: sorting a
            // depth-only pass front-to-back is front-to-back *from the light*, which is what early-Z
            // rewards. A page's own centre projected back along the light is as close as this gets.
            view.Position = camera.Position - (Vector3.Normalize(sunward) * Depthrange);
            view.MaximumDistance = 0f;

            compositor.Use(view, CasterStage);
        }

        // The table as it stands *after* this frame's allocations and before this frame's draws, so a
        // page allocated and not yet drawn is absent to a shading pass rather than a slot holding the
        // last page's depths. VirtualShadowPages.Table is where that argument is made at length.
        atlas.UploadTable();

        if (Scene is { } parameters) {
            // The two biases are metres here and normalised depth there, and the multiply is the
            // whole difference between a bias and a number — see ConstantBias.
            var scale = VirtualShadowMap.DepthScale(Depthrange);

            atlas.Publish(
                parameters,
                [.. Passes],
                Samplers,
                camera,
                compositor.FrameSize.Y,
                ConstantBias * scale,
                SlopeBias * scale
            );
        }
    }

    /// <summary>Refits the clipmap and the spots, invalidating whatever moved.</summary>
    void Fit(VirtualShadowAtlas atlas, RenderView camera, Vector3 light) {
        var previous = records.Count;

        records.Clear();
        depthCells.Clear();

        var levels = Math.Clamp(ClipmapLevels, 0, VirtualShadowMap.MaxLevels - SpotProjections.Count);

        for (var level = 0; level < levels; level++) {
            var cell = VirtualShadowMap.ClipmapCell(level, FirstExtent, camera.Position, light, Depthrange);

            depthCells.Add(cell.Light);

            records.Add(
                new() {
                    ViewProjection = VirtualShadowMap.ClipmapProjection(
                        level,
                        FirstExtent,
                        camera.Position,
                        light,
                        Depthrange
                    ),
                    First = (uint)(records.Count * VirtualShadowMap.PagesPerMap),
                    Kind = (uint)VirtualShadowKind.Clipmap,
                    TexelWorldSize = VirtualShadowMap.TexelOf(level, FirstExtent),
                    Light = 0u,

                    // Asked for rather than built from `cell` here, because the y negation that makes
                    // the toroidal arithmetic work on both axes is a fact about the page grid's
                    // handedness and belongs in exactly one place — see VirtualShadowLevel.Origin.
                    Origin = VirtualShadowMap.ClipmapOrigin(
                        level,
                        FirstExtent,
                        camera.Position,
                        light,
                        Depthrange
                    )
                }
            );
        }

        for (var spot = 0; spot < SpotProjections.Count && records.Count < VirtualShadowMap.MaxLevels; spot++) {
            records.Add(
                new() {
                    ViewProjection = SpotProjections[spot],
                    First = (uint)(records.Count * VirtualShadowMap.PagesPerMap),
                    Kind = (uint)VirtualShadowKind.Spot,
                    TexelWorldSize = 0f,
                    Light = (uint)(spot + 1)
                }
            );
        }

        // ⚠ **A level that moved has pages about somewhere else — that level's pages, not the
        // atlas's.** A page's identity is its cell in the level's own grid, so a projection that
        // changed makes that level's pages stale and no one else's. The old test watched level zero
        // alone and threw the whole atlas away, on the claim that "every level moves together: they
        // share a snapped centre". They do not — each level snaps at its *own* page granularity
        // (ClipmapProjection's step is extent over the page count, and the extent doubles per
        // level), so level zero recentres every ~0.3 m of walking while the coarse levels stand
        // still for tens of metres. Under that test a moving player invalidated all eight levels'
        // pages near-continuously, the sixteen-a-frame budget never caught up, and the map was
        // perpetually absent exactly when it was being looked at.
        InvalidatedPages = 0;
        RefitLevels = 0;

        // ⚠ **A slide is not a move, and telling them apart is task #317.** Every level's projection
        // is a function of the light, the extent, the depth range and the camera's cell — so when the
        // first three are what they were, any matrix that changed changed because the camera's cell
        // did, and a cell that changed *laterally* slid the window without touching a single page's
        // world footprint. Anything else — the sun stepping its half-degree, a level count that moved
        // the page ranges, the near plane stepping along the light — moves every stored depth in the
        // level and is stale wholesale.
        var comparable = previous == records.Count && fitted.Count == records.Count;

        var slidOnly = comparable
            && fittedClipmap == levels
            && fittedLight == light
            && fittedExtent.Equals(FirstExtent)
            && fittedDepth.Equals(Depthrange);

        if (!comparable) {
            // The structure changed — levels appeared, vanished, or spots shifted the mapping from
            // page range to record — and a page range that changed meaning is stale wholesale.
            InvalidatedPages = atlas.Pages.InvalidateAll();
            RefitLevels = records.Count;
        } else {
            for (var level = 0; level < records.Count; level++) {
                if (fitted[level] == records[level].ViewProjection) {
                    continue;
                }

                RefitLevels++;

                var first = level * VirtualShadowMap.PagesPerMap;
                var slid = slidOnly && level < levels && depthCells[level] == fittedDepthCells[level];

                if (slid) {
                    Slide(atlas, first, fittedOrigins[level], records[level].Origin);
                } else {
                    for (var page = first; page < first + VirtualShadowMap.PagesPerMap; page++) {
                        if (atlas.Pages.Invalidate(page)) {
                            InvalidatedPages++;
                        }
                    }
                }
            }
        }

        fitted.Clear();
        fittedOrigins.Clear();
        fittedDepthCells.Clear();

        for (var level = 0; level < records.Count; level++) {
            fitted.Add(records[level].ViewProjection);
            fittedOrigins.Add(records[level].Origin);
        }

        fittedDepthCells.AddRange(depthCells);
        fittedClipmap = levels;
        fittedLight = light;
        fittedExtent = FirstExtent;
        fittedDepth = Depthrange;

        atlas.Begin(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(records),
            Math.Min(levels, records.Count),
            VirtualShadowMap.TexelOf(0, FirstExtent)
        );
    }

    /// <summary>Invalidates only the pages a lateral slide handed to a different part of the world.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The whole of what toroidal addressing buys, spent here.</b> A window that slid one
    ///         page has thirty-one of its thirty-two columns over the same world as before; under the
    ///         window-cell addressing this replaced, every one of the thousand and twenty-four pages
    ///         changed its name and <c>Fit</c> threw the level away to say that one column arrived.
    ///         Now the arriving column is the only one whose address means somewhere new, and the
    ///         thirty-two pages of it are the whole cost.
    ///     </para>
    ///     <para>
    ///         Asked per axis and per address rather than derived from the difference, because the
    ///         difference has four cases — either sign, wrapped or not — and
    ///         <see cref="VirtualShadowMap.PageSurvives" /> answers all four by construction. A slide
    ///         of a whole window or more survives nothing, which falls out of the same test rather
    ///         than needing a guard.
    ///     </para>
    /// </remarks>
    void Slide(VirtualShadowAtlas atlas, int first, Int2 before, Int2 after) {
        Span<bool> columns = stackalloc bool[VirtualShadowMap.PagesPerSide];
        Span<bool> rows = stackalloc bool[VirtualShadowMap.PagesPerSide];

        for (var index = 0; index < VirtualShadowMap.PagesPerSide; index++) {
            columns[index] = !VirtualShadowMap.PageSurvives(index, before.X, after.X);
            rows[index] = !VirtualShadowMap.PageSurvives(index, before.Y, after.Y);
        }

        for (var y = 0; y < VirtualShadowMap.PagesPerSide; y++) {
            for (var x = 0; x < VirtualShadowMap.PagesPerSide; x++) {
                if (!columns[x] && !rows[y]) {
                    continue;
                }

                if (atlas.Pages.Invalidate(first + (y * VirtualShadowMap.PagesPerSide) + x)) {
                    InvalidatedPages++;
                }
            }
        }
    }

    /// <summary>Retires the pages every caster that moved was under, and the pages it is under now.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The object store rather than a stage's node list, and that is the load-bearing
    ///         choice.</b> <see cref="PunctualShadowRenderer" /> reads the nodes a view collected and
    ///         has to re-test them, because with device-side culling and no readback the list a host
    ///         is handed is the <em>conservative</em> set — everything that could be visible — which
    ///         nearly made that cache worthless while every counter read healthy. A caster's footprint
    ///         is not a question about any view at all, so there is nothing here to be misled by: the
    ///         store is the whole scene, once, and the levels do the culling by construction — a
    ///         sphere outside a level's volume spans none of its pages.
    ///     </para>
    ///     <para>
    ///         <b>A slot's history and not merely its bounds.</b> An object that stopped casting, or
    ///         died, has to retire the pages it was under exactly as one that moved does; an object
    ///         that appeared has to retire the ones it arrived on. All three are the same comparison
    ///         against what this slot said last frame, which is why <see cref="Footprint" /> carries
    ///         whether it was casting at all rather than being cleared on removal.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pages are named by <see cref="VirtualShadowMap.ToroidalOf" /> and not by the
    ///         window cell <see cref="VirtualShadowMap.PageSpan" /> answers.</b> The two agree only
    ///         while a level's origin is zero, so a version that skipped the conversion would retire
    ///         the right pages until the camera walked one page and somebody else's pages after that
    ///         — a cache corruption that draws perfectly plausible shadows in the wrong place.
    ///     </para>
    /// </remarks>
    void Displace(RenderSystem system, VirtualShadowAtlas atlas) {
        if (!InvalidateMovedCasters || records.Count == 0) {
            return;
        }

        var objects = system.Objects.All;
        var transforms = CasterTransforms is { } key ? system.Objects.Data.Data(key) : default;
        var casting = CasterStage.Mask;

        while (footprints.Count < objects.Length) {
            footprints.Add(default);
        }

        for (var index = 0; index < objects.Length; index++) {
            ref readonly var candidate = ref objects[index];

            var current = new Footprint {
                Bounds = candidate.Bounds,
                Transform = index < transforms.Length ? transforms[index] : Matrix4x4.Identity,
                Casts = candidate.IsAlive && candidate.Stages.Intersects(casting)
            };

            Moved(atlas, index, current);
        }

        // A store that shrank — a scene unloaded, or Clear — leaves slots this loop no longer walks
        // holding pages that still carry their shadows. Retiring them here rather than never is the
        // same rule one slot down: what is not there any more is a page that is wrong.
        for (var index = objects.Length; index < footprints.Count; index++) {
            Moved(atlas, index, default);
        }

        if (footprints.Count > objects.Length) {
            footprints.RemoveRange(objects.Length, footprints.Count - objects.Length);
        }
    }

    /// <summary>Compares one slot with what it said last frame, and retires both footprints if it moved.</summary>
    void Moved(VirtualShadowAtlas atlas, int index, in Footprint current) {
        var previous = footprints[index];

        var same = previous.Bounds.Equals(current.Bounds) && previous.Transform == current.Transform;

        if (previous.Casts == current.Casts && (!current.Casts || same)) {
            return;
        }

        footprints[index] = current;
        MovedCasters++;

        if (previous.Casts) {
            Shadowed(atlas, previous.Bounds);
        }

        if (current.Casts) {
            Shadowed(atlas, current.Bounds);
        }
    }

    /// <summary>Retires every page a sphere's shadow can reach, over every level of every map.</summary>
    void Shadowed(VirtualShadowAtlas atlas, BoundingSphere bounds) {
        for (var level = 0; level < records.Count; level++) {
            var record = records[level];

            var span = VirtualShadowMap.PageSpan(
                record.ViewProjection,
                bounds.Center,
                bounds.Radius,
                out var first,
                out var last
            );

            if (!span) {
                continue;
            }

            for (var y = first.Y; y <= last.Y; y++) {
                for (var x = first.X; x <= last.X; x++) {
                    var page = VirtualShadowMap.IndexOf(
                        record.First,
                        VirtualShadowMap.ToroidalOf(new(x, y), record.Origin)
                    );

                    if (!atlas.Pages.Invalidate(page)) {
                        continue;
                    }

                    InvalidatedPages++;
                    CasterInvalidations++;
                }
            }
        }
    }

    /// <summary>What one object's shadow was last time this node looked.</summary>
    /// <remarks>
    ///     The transform is here rather than derived from the bounds because a sphere is invariant
    ///     under rotation and a shadow is not — <see cref="CasterTransforms" />. It is the identity
    ///     for every slot when no key is set, which makes the comparison bounds-only without a branch
    ///     per object.
    /// </remarks>
    struct Footprint {
        public BoundingSphere Bounds;
        public Matrix4x4 Transform;
        public bool Casts;
    }

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (Atlas is not { } atlas || Camera is not { } camera || !atlas.EnsureAtlas()) {
            return;
        }

        var depth = frame.Texture(ToString(), Depth);
        var size = frame.Size;
        var pages = owed;
        var target = atlas;

        // The page draws, into a texture the graph does not own — see VirtualShadowAtlas: a page's
        // contents are about the world rather than about a frame, and a transient would be discarded at
        // the end of the pass that wrote it, which is a cache that never holds anything.
        if (pages.Count > 0 || !atlas.IsBuilt) {
            frame.Graph.AddPass(
                $"{this}.Pages",
                pass => {
                    pass.SideEffect();

                    pass.Execute(
                        graphContext => {
                            var context = frame.Context(graphContext.CommandList);
                            var previous = context.Output;

                            // No colour attachment at all, exactly as the cascade pass has none: a
                            // shadow pass writes depth and nothing else.
                            context.Output = new([], VirtualShadowAtlas.Format);
                            context.ViewConstants = Constants;

                            Record(compositor, graphContext.CommandList, context, target, pages);

                            context.Output = previous;
                        }
                    );
                }
            );
        }

        // And this frame's marks, for the next frame to service. After the depth exists and after the
        // draws, so a page allocated this frame is not asked for again before it has been drawn.
        frame.Graph.AddPass(
            $"{this}.Mark",
            pass => {
                // ⚠ **Graphics, though the body is a dispatch.** What it marks are the atlas's page
                // tables, which outlive the frame and are never graph resources — see Record's
                // remarks — so no wait edge can be derived from what this produces, and a hoisted
                // pass would be one the next frame's page service races. The declared read of depth
                // is real and is what orders this after the draw either way.
                pass.Kind = PassKind.Graphics;
                pass.Reads(depth);
                pass.SideEffect();

                pass.Execute(
                    graphContext => target.Mark(graphContext.CommandList, graphContext.View(depth), camera, size)
                );
            }
        );
    }

    /// <summary>Draws each owed page into its slot of the atlas.</summary>
    /// <remarks>
    ///     <para>
    ///         One render pass per page rather than one pass with a viewport per page, and the reason is
    ///         the clear: a page has to start empty, a <c>LoadAction.Clear</c> applies to the pass's
    ///         <em>render area</em>, and a render area is a per-pass fact — so confining each page's
    ///         clear to its own rectangle means a pass per page. Clearing without the render area wipes
    ///         every cached page in the atlas, which is the one thing this system exists not to do —
    ///         and did, for as long as the code believed the scissor confined the clear.
    ///     </para>
    ///     <para>
    ///         ⚠ Recorded rather than declared, so the graph never sees the atlas. That is deliberate
    ///         and it is the cost of a target that outlives the frame — nothing aliases it, nothing
    ///         transitions it, and the barrier below is this node's own.
    ///     </para>
    /// </remarks>
    void Record(
        GraphicsCompositor compositor,
        ICommandList list,
        RenderDrawContext context,
        VirtualShadowAtlas atlas,
        List<(int Page, int Slot, Matrix4x4 Projection)> pages
    ) {
        var texels = atlas.Pages.AtlasTexels;

        list.Barrier(
            new(
                [],
                [new(atlas.Texture, atlas.IsBuilt ? ResourceState.ShaderRead : ResourceState.Undefined, ResourceState.DepthStencilWrite)]
            )
        );

        for (var i = 0; i < pages.Count && i < views.Count; i++) {
            var origin = VirtualShadowMap.AtlasOrigin(pages[i].Slot, atlas.Pages.PagesPerSide);
            var rect = new ScissorRect(origin.X, origin.Y, VirtualShadowMap.PageTexels, VirtualShadowMap.PageTexels);

            // ⚠ **Cleared, and the render area is what confines the clear — not the scissor.** A
            // scissor confines draws; the load op runs when the pass begins, before any draw, over
            // the pass's render area. The comment that stood here said the scissor confined both,
            // and the backend's render area was the whole attachment — so every page's pass wiped
            // every other cached page, and after a frame that drew N pages only the last held real
            // depth while the table still mapped them all. Under the reverse-Z compare that read as
            // full shadow from nowhere; with the compare fixed it would read as full light.
            list.BeginRenderPass(
                new(
                    [],
                    new(atlas.View, LoadAction.Clear, StoreAction.Store, 0f),
                    $"{this}.Page{pages[i].Page}",
                    rect
                )
            );

            var viewport = new Viewport(origin.X, origin.Y, VirtualShadowMap.PageTexels, VirtualShadowMap.PageTexels);

            list.SetViewport(viewport);

            // The scissor still matters for the draws — the neighbouring page is an unrelated part
            // of the world rather than the next cascade out, so a caster whose triangle crossed the
            // edge would write a depth that shadows somewhere else entirely. It shares the render
            // area's rectangle; they confine different things.
            list.SetScissor(rect);

            compositor.System.Record(views[i], CasterStage, context);

            list.EndRenderPass();

            atlas.Pages.Drawn(pages[i].Page);
            DrawnPages++;
        }

        list.Barrier(new([], [new(atlas.Texture, ResourceState.DepthStencilWrite, ResourceState.ShaderRead)]));

        atlas.MarkCleared();
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "VirtualShadow" : Name;
}
