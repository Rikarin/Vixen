// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Rendering;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;

namespace Vixen.Engine.Renderer;

/// <summary>
///     How many texels across the frame needs each of its textures to be, said once a frame.
/// </summary>
/// <remarks>
///     <para>
///         <b>The join <see cref="AssetTextureSource.Want" /> was written for and that nothing
///         made.</b> Extraction knows a drawable's bounds and which material it is painted in;
///         <see cref="AssetMaterialSource" /> knows which textures a material samples; neither knows
///         the other, so a streamed texture had no size on it and every texture in a scene wanted to
///         be complete. That is a correct default — see <see cref="AssetTextureSource.Update" /> —
///         and it means the pool bounds memory without prioritising it: under pressure the
///         least-recently-used order decides which texture is blurry, and the least-recently-used
///         order knows nothing about what is in front of the camera.
///     </para>
///     <para>
///         <b>Demand is the caller's decision and residency is the service's.</b> This computes what
///         a frame would like and asks for it; <see cref="TextureStreamer" /> owns the budget, the
///         queue and the eviction order, and is free to answer with something coarser.
///         <c>GrassResidency</c> makes the same split from the other side and its remarks say why:
///         one component deciding both is a component with two budgets in it.
///     </para>
///     <para>
///         <b>Every frame, and decomposed by material rather than by drawable.</b> The per-drawable
///         pass is one distance, one multiply and one <c>max</c> into an array indexed by material —
///         the same shape and the same cost as <c>LodRenderFeature.Prepare</c>, which already walks
///         this list once per view per frame. Only then does the per-material pass fan the number
///         out to the material's textures, and materials number in the tens where drawables number
///         in the thousands. Amortising over frames was the alternative and is worse for the reason
///         the whole class exists: a want that lags the camera is a swap that happens late, and a
///         swap is an upload. What removes churn is the hysteresis below, not a lower rate.
///     </para>
///     <para>
///         ⚠ <b>One frame stale, and deliberately.</b> This reads <c>RenderSystem.Visibility</c>,
///         which the compositor fills during <see cref="SceneRenderHost.Draw" /> — so what it sees at
///         the top of <see cref="WorldRenderer.Draw" /> is the previous frame's cull. A page takes
///         many frames to arrive, so a signal one frame behind the camera is exact enough; the
///         alternative is running this after the frame is recorded, where the wants it produced could
///         not reach the uploads that same frame.
///     </para>
///     <para>
///         ⚠ <b>A texture no material here paints keeps whatever behaviour it had.</b> Terrain layer
///         textures reach the streamer through <c>AssetTerrainTextures</c>, particle materials
///         through a second <see cref="MaterialRenderFeature" />, and a project's own
///         <see cref="Vixen.Rendering.Ecs.IMaterialSource" /> through neither — none of those is
///         surveyed, so none is sized, so each falls into the "sampled and not sized wants to be
///         complete" branch that has always been there. This narrows what it can see and silences
///         nothing.
///     </para>
/// </remarks>
public sealed class TextureDemand {
    /// <summary>Reference to slot in <see cref="order" />, rebuilt every frame.</summary>
    readonly Dictionary<AssetReference, int> slots = [];

    readonly List<AssetReference> order = [];

    /// <summary>The raw maximum width over this frame's users, parallel to <see cref="order" />.</summary>
    readonly List<int> raw = [];

    /// <summary>The rung each texture settled on last frame, and where this frame's are built.</summary>
    /// <remarks>
    ///     Two dictionaries swapped rather than one mutated, because "a texture nothing wanted this
    ///     frame forgets its rung" is then the absence of a copy rather than a second pass looking
    ///     for stale entries. A texture that leaves the view and comes back starts from the width it
    ///     is at now, which is right: the rung is a memory of a camera that is no longer there.
    /// </remarks>
    Dictionary<AssetReference, int> rungs = [];
    Dictionary<AssetReference, int> settling = [];

    int[] widths = [];

    /// <summary>Builds the survey over a frame's objects and the sources behind them.</summary>
    /// <param name="system">The render system whose objects, views and visibility are surveyed.</param>
    /// <param name="meshes">The feature whose objects these are.</param>
    /// <param name="materials">The feature that says which material each object is painted in.</param>
    /// <param name="painter">Which textures a material samples.</param>
    /// <param name="textures">Where the wants are said.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public TextureDemand(
        RenderSystem system,
        MeshRenderFeature meshes,
        MaterialRenderFeature materials,
        AssetMaterialSource painter,
        AssetTextureSource textures
    ) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(painter);
        ArgumentNullException.ThrowIfNull(textures);

        System = system;
        Meshes = meshes;
        Materials = materials;
        Painter = painter;
        Textures = textures;
    }

    /// <summary>The render system whose objects are surveyed.</summary>
    public RenderSystem System { get; }

    /// <summary>The feature whose objects they are.</summary>
    public MeshRenderFeature Meshes { get; }

    /// <summary>The feature that says which material each is painted in.</summary>
    public MaterialRenderFeature Materials { get; }

    /// <summary>Which textures a material samples.</summary>
    public AssetMaterialSource Painter { get; }

    /// <summary>Where the wants are said.</summary>
    public AssetTextureSource Textures { get; }

    /// <summary>
    ///     How many pixels tall the output is, which is what turns a projected size into texels.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Here rather than on <see cref="RenderView" />, on
    ///         <c>VirtualGeometryRenderFeature.ScreenHeight</c>'s terms and for its reason: a view
    ///         carries the resolution-independent half of the projection and the pixels are a
    ///         property of the target every view renders into. <see cref="WorldRenderer.Draw" /> sets
    ///         it from <see cref="SceneRenderHost.FrameSize" /> every frame, so an application that
    ///         resizes its window needs to do nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero surveys nothing, and that is the safe direction rather than an oversight.</b>
    ///         With no height there is no texel count, and a want of zero texels means <em>the
    ///         smallest level</em> — so a survey that ran anyway would quietly drop every texture in
    ///         the scene to its floor. Wanting nothing leaves each texture in the branch it was in
    ///         before this existed, which is the picture a project already had.
    ///     </para>
    /// </remarks>
    public int ScreenHeight { get; set; }

    /// <summary>
    ///     How far past a rung a width has to go before the rung moves, as a fraction of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A swap is an upload, so the wanted width must not oscillate at a threshold.</b> A
    ///         camera drifting across the boundary between two mip levels would otherwise ask for the
    ///         larger one and the smaller one on alternate frames, and each of those answers is a
    ///         fresh image, a fresh view and a tail copied up — <c>GrassResidency</c>'s own remarks
    ///         make the identical argument about a cell re-scattered every frame, and
    ///         <c>LodRenderFeature.Hysteresis</c> about a level chosen twice a second.
    ///     </para>
    ///     <para>
    ///         An eighth: between <c>LodRenderFeature</c>'s tenth and <c>GrassResidency</c>'s
    ///         0.15, and exactly representable in binary so the band's edges are the same numbers on
    ///         every machine — which matters here, because the claim being made is that the same
    ///         camera produces the same rung frame after frame.
    ///     </para>
    /// </remarks>
    public float Hysteresis { get; set; } = 0.125f;

    /// <summary>How many textures were given a width by the last <see cref="Update" />.</summary>
    public int Sized => order.Count;

    /// <summary>How many times a texture's rung has moved up since this was created.</summary>
    public long Promotions { get; private set; }

    /// <summary>And down. A texture that leaves the view is neither: it stops being asked for.</summary>
    public long Demotions { get; private set; }

    /// <summary>The width the last <see cref="Update" /> asked for, for a texture it sized.</summary>
    /// <param name="texture">Which texture.</param>
    /// <param name="width">The settled width in texels, when this returns true.</param>
    /// <returns>False for a texture nothing visible sampled, which is not the same as zero.</returns>
    public bool TryGetWidth(AssetReference texture, out int width) {
        if (slots.ContainsKey(texture) && rungs.TryGetValue(texture, out var rung)) {
            width = 1 << rung;
            return true;
        }

        width = 0;
        return false;
    }

    /// <summary>Surveys the frame's drawables and says what each of their textures should be.</summary>
    /// <remarks>
    ///     ⚠ <b>Returns immediately when streaming is off</b>, which is the other half of "a zero pool
    ///     is exactly what existed before streaming did": no walk of the object list, no dictionary,
    ///     no per-frame cost of any kind.
    /// </remarks>
    public void Update() {
        if (Textures.Streaming is null) {
            return;
        }

        slots.Clear();
        order.Clear();
        raw.Clear();

        if (ScreenHeight > 0) {
            Survey();
        }

        Apply();

        (rungs, settling) = (settling, rungs);
        settling.Clear();
    }

    /// <summary>Fills this frame's raw maximum width for every texture something visible samples.</summary>
    void Survey() {
        var count = Materials.Materials.Count;

        if (count == 0 || System.Views.Count == 0) {
            return;
        }

        if (widths.Length < count) {
            Array.Resize(ref widths, count);
        }

        Array.Clear(widths, 0, count);

        var indices = System.Objects.Data.Data(Materials.MaterialIndex);
        var objects = System.Objects.All;

        // The maximum over users, per material. One texture painted on a distant rock and a near wall
        // is wanted at the wall's size — the last one seen would give whichever the object list
        // happened to end with, which is a wanted width that changes when an unrelated entity is
        // created.
        foreach (var view in System.Views) {
            if (view.ScreenHeightScale <= 0f) {
                continue;
            }

            for (var index = 0; index < objects.Length && index < indices.Length; index++) {
                ref readonly var candidate = ref objects[index];

                if (!candidate.IsAlive || candidate.FeatureIndex != Meshes.Index) {
                    continue;
                }

                var material = indices[index];

                if (material <= 0 || material >= count) {
                    continue;
                }

                if (!System.Visibility.IsVisible(view.Index, new(index))) {
                    continue;
                }

                var width = TextureStreamer.WantedWidth(candidate.Bounds, view, ScreenHeight);

                if (width > widths[material]) {
                    widths[material] = width;
                }
            }
        }

        // And the fan-out, over materials rather than over drawables. Index 0 is the "no material"
        // sentinel and is not one.
        for (var material = 1; material < count; material++) {
            if (widths[material] <= 0) {
                continue;
            }

            if (Painter.TexturesOf(Materials.Materials[material]) is not { } textures) {
                continue;
            }

            for (var index = 0; index < textures.Count; index++) {
                Demand(textures[index], widths[material]);
            }
        }
    }

    /// <summary>Records a width for a texture, keeping the largest anything asked for.</summary>
    void Demand(AssetReference texture, int width) {
        if (slots.TryGetValue(texture, out var slot)) {
            if (width > raw[slot]) {
                raw[slot] = width;
            }

            return;
        }

        slots[texture] = order.Count;
        order.Add(texture);
        raw.Add(width);
    }

    /// <summary>Settles each raw width onto a rung and says it.</summary>
    void Apply() {
        for (var index = 0; index < order.Count; index++) {
            var texture = order[index];
            var known = rungs.TryGetValue(texture, out var was);
            var rung = known ? Settle(was, raw[index]) : Rung(raw[index]);

            if (known && rung > was) {
                Promotions++;
            } else if (known && rung < was) {
                Demotions++;
            }

            settling[texture] = rung;
            Textures.Want(texture, 1 << rung);
        }

        // Whatever is left in `rungs` belongs to a texture nothing visible sampled, and is dropped
        // rather than re-asked. Not asking is what makes it degrade: a page that is not touched ages
        // in the least-recently-used order and is the next thing evicted when something in front of
        // the camera needs the room. Asking again at the old width is how a texture that has left the
        // view never gives it back.
    }

    /// <summary>
    ///     The power-of-two width a raw width settles on, keeping the current one inside the band.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The rungs are powers of two because the levels are.</b> What a want actually decides
    ///         is a mip level, and <c>TextureStreamer.LevelFor</c> compares the wanted width against
    ///         <c>Width >> level</c> — so quantising to a power of two puts the dead band around the
    ///         <em>same</em> number the swap decision turns on, rather than near it. A band around a
    ///         boundary that is not the real boundary stops some of the churn and not the rest.
    ///     </para>
    ///     <para>
    ///         A jump of more than one rung is taken whole. The condition is written against the
    ///         current rung rather than the next, so a camera that teleported clears it by a wide
    ///         margin and lands where it belongs in one frame; the band only ever holds up a move to
    ///         an adjacent rung, which is the only move a drifting camera makes.
    ///     </para>
    /// </remarks>
    /// <param name="current">The rung it is on, which a texture surveyed last frame has.</param>
    /// <param name="width">This frame's raw wanted width.</param>
    /// <returns>The rung: the width asked for is <c>1 &lt;&lt; result</c>.</returns>
    int Settle(int current, int width) {
        var target = Rung(width);
        var margin = MathF.Max(Hysteresis, 0f);

        if (target > current) {
            return width > (1 << current) * (1f + margin) ? target : current;
        }

        if (target < current) {
            return width <= (1 << (current - 1)) * (1f - margin) ? target : current;
        }

        return current;
    }

    /// <summary>The smallest rung whose width covers a raw width, with no band and no memory.</summary>
    /// <remarks>What a texture not surveyed last frame gets: there is nothing to be sticky about.</remarks>
    static int Rung(int width) {
        var rung = 0;

        while (rung < MaximumRung && 1 << rung < width) {
            rung++;
        }

        return rung;
    }

    /// <summary>65_536 texels: past every format's maximum extent, and it cannot overflow a shift.</summary>
    const int MaximumRung = 16;
}
