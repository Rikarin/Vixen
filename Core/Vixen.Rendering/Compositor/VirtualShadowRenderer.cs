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
    ///     <b>What a refit costs, stated rather than inferred.</b> A level whose projection moved has
    ///     every one of its resident pages unpublished at once, and the budget redraws
    ///     <see cref="DrawsPerFrame" /> of them a frame — so a scene where this is non-zero on most
    ///     frames is a scene whose map is structurally unable to answer, however healthy
    ///     <see cref="DrawnPages" /> looks. <see cref="LightSnapDegrees" /> and the clipmap's page snap
    ///     exist to keep it at zero, and whether they do is a question about the scene's own rates.
    /// </remarks>
    public int InvalidatedPages { get; private set; }

    /// <inheritdoc cref="InvalidatedPages" />
    public int RefitLevels { get; private set; }

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
    ///         invalidates its pages.</b> A page's identity is its position in a level's grid, so a
    ///         level whose projection changed has pages whose depths are about somewhere else — and the
    ///         page snap is what makes that rare rather than constant: a camera that moved less than a
    ///         page leaves every footprint bit-identical.
    ///     </para>
    /// </remarks>
    protected internal override void Collect(GraphicsCompositor compositor) {
        ArgumentNullException.ThrowIfNull(compositor);

        DrawnPages = 0;
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
            var grid = new Int2(within % VirtualShadowMap.PagesPerSide, within / VirtualShadowMap.PagesPerSide);
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

        var levels = Math.Clamp(ClipmapLevels, 0, VirtualShadowMap.MaxLevels - SpotProjections.Count);

        for (var level = 0; level < levels; level++) {
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
                    Light = 0u
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

        if (previous != records.Count) {
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

                for (var page = first; page < first + VirtualShadowMap.PagesPerMap; page++) {
                    if (atlas.Pages.Invalidate(page)) {
                        InvalidatedPages++;
                    }
                }
            }
        }

        fitted.Clear();

        for (var level = 0; level < records.Count; level++) {
            fitted.Add(records[level].ViewProjection);
        }

        atlas.Begin(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(records),
            Math.Min(levels, records.Count),
            VirtualShadowMap.TexelOf(0, FirstExtent)
        );
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
