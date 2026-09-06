// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Ui.Renderer;

/// <summary>One interface, as the renderer sees it.</summary>
/// <param name="Geometry">This frame's geometry.</param>
/// <param name="Atlas">The glyph atlas its text draws from.</param>
/// <param name="Surface">How large the target is, in document pixels.</param>
/// <param name="Order">Where it sits among the interfaces, lowest drawn first.</param>
/// <remarks>
///     ⚠ <b>Called <c>UiInterface</c> rather than <c>UiSurface</c>, which is what it was.</b>
///     <see cref="UiSurface" /> now means something in <c>Vixen.Ui</c> — one of the windows a
///     document is shown in — and two types of that name a namespace apart is a trap rather than an
///     ambiguity: a file that imports both compiles until somebody removes the wrong <c>using</c>,
///     and the two are close enough in meaning that the mistake reads as correct.
/// </remarks>
public readonly record struct UiInterface(UiGeometry Geometry, GlyphAtlas Atlas, Int2 Surface, uint Order);

/// <summary>Draws user interfaces inside somebody else's renderer.</summary>
/// <remarks>
///     <para>
///         A thin adapter, deliberately. Everything that touches a device is in
///         <see cref="UiRenderer" />, which a golden image can drive directly; this is what makes one
///         of those reachable from a <see cref="RenderSystem" /> — and the split is why the shaders
///         can be checked against a picture without a scene, a camera or a compositor.
///     </para>
///     <para>
///         ⚠ <b>One render object per surface, not one per batch — which corrects a guess written
///         down before the renderer had been read.</b> <c>DrawBatch</c>'s remarks reasoned that
///         <c>RenderSortMode.ByGroup</c> exists "for UI and anything else already ordered", and
///         concluded that the batch index would have to be the sort group. It is not, and it cannot
///         be: the store's objects live across frames and are indexed by a dense id every feature's
///         parallel array is keyed on, so an object per batch would churn the whole store every time
///         a label changed. The painting order <i>within</i> a surface is already the order of
///         <c>UiGeometry.Draws</c>, and no sort can reach it. What the group orders is surfaces
///         against each other — a modal over a document, a tooltip over the modal — which is a real
///         ordering problem that a sort is the right answer to.
///     </para>
///     <para>
///         So the batch list was not a wasted guess and is not the thing to delete: it is what
///         <see cref="UiGeometryBuilder" /> turns into one <c>UiDraw</c> each, behind the frame diff,
///         so a still interface regroups nothing. That was the open question and this is its answer.
///     </para>
/// </remarks>
public sealed class UiRenderFeature : RootRenderFeature {
    readonly Dictionary<int, UiInterface> surfaces = [];
    readonly Dictionary<int, UiRenderer> renderers = [];

    // Reused rather than built per frame: this runs twice a frame for every mounted interface, and
    // an interface is mounted in ones and twos.
    readonly HashSet<UiRenderer> serving = [];

    /// <inheritdoc />
    public override string Name => "Ui";

    /// <summary>What draws an interface that named no renderer of its own. Set before the first
    /// frame that draws.</summary>
    /// <remarks>
    ///     <para>
    ///         Supplied rather than created here, because building it needs shader modules and the
    ///         formats of the pass — neither of which a feature knows and both of which belong to
    ///         whoever assembled the compositor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The default for <em>one</em> interface, and not a renderer the feature shares
    ///         out.</b> A <see cref="UiRenderer" /> is per-surface state: it advances its ring region
    ///         inside <c>Upload</c> and <c>Record</c> draws from the region the last upload wrote, so
    ///         two interfaces uploaded through one of these come out as two copies of the second. A
    ///         second interface therefore brings its own — see <see cref="Mount" /> — and this stays
    ///         what the first one uses so that the single-surface arrangement needs no ceremony.
    ///     </para>
    /// </remarks>
    public UiRenderer? Renderer { get; set; }

    /// <summary>Adds the render object one interface is drawn as, and returns its id.</summary>
    /// <param name="stages">Which stages draw it — see the remarks on sorting.</param>
    /// <param name="order">Where it sits among the interfaces, lowest drawn first.</param>
    /// <param name="renderer">
    ///     What draws this interface, or <see langword="null" /> to use <see cref="Renderer" />.
    ///     Every mounted interface needs one nobody else is using; see the remarks.
    /// </param>
    /// <returns>The object to hand <see cref="Set" /> every frame the interface is drawn.</returns>
    /// <exception cref="InvalidOperationException">The feature is not in a render system yet.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half that was missing, and the reason nothing in the tree drew an interface
    ///         inside a world.</b> <see cref="Set" /> takes a <see cref="RenderObjectId" /> and no
    ///         caller could get one: the object has to be added to the store with <em>this</em>
    ///         feature's index on it, and a host that guessed at that wrote a record the wrong
    ///         feature would be asked to draw. So it is here, where the index is known, rather than
    ///         in each host.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The bounds are everything, deliberately, and not because culling is unwanted.</b>
    ///         An interface is in screen space and has no position in the world, so the frustum test
    ///         has nothing true to say about it — a sphere at the origin would leave a HUD drawn only
    ///         while the camera happened to look at the middle of the level.
    ///         <see cref="float.MaxValue" /> rather than <see cref="float.PositiveInfinity" /> for the
    ///         reason a radius is arithmetic rather than a flag: <c>VisibilityGroup</c> adds the
    ///         radius to a view's maximum distance and squares the sum, and an infinity that meets a
    ///         subtraction anywhere downstream is a NaN, which compares false and culls the thing it
    ///         was meant to keep.
    ///     </para>
    ///     <para>
    ///         The order is written to <see cref="RenderObject.SortGroup" /> as well as returned to
    ///         the caller, so a surface mounted and not yet <see cref="Set" /> sorts where it will
    ///         sort — <see cref="SortGroupOf" /> falls back to the object's own group, and the two
    ///         disagreeing is a first frame in the wrong order.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A renderer belongs to one interface, and that is what makes the
    ///         <paramref name="order" /> above mean anything.</b> The ordering exists so that a modal
    ///         can be drawn over a document and a tooltip over the modal — which is more than one
    ///         mounted interface, and is the arrangement a shared <see cref="UiRenderer" /> cannot
    ///         serve: it advances its ring region inside <c>Upload</c> and <c>Record</c> draws from
    ///         the region the <em>last</em> upload wrote, so both surfaces come out as two copies of
    ///         the second. Not an error, not a validation warning — a picture. So the second
    ///         interface brings its own renderer here, and <see cref="Upload" /> refuses a frame in
    ///         which two of them would share one rather than drawing that picture.
    ///     </para>
    /// </remarks>
    public RenderObjectId Mount(RenderStageMask stages, uint order = 0, UiRenderer? renderer = null) {
        if (System is null) {
            throw new InvalidOperationException(
                "A UiRenderFeature can only mount a surface once it is in a render system: the object "
                + "carries this feature's index, and the index is assigned by RenderSystem.AddFeature. "
                + "Call AddFeature before Mount."
            );
        }

        var id = System.Objects.Add(
            new() {
                Bounds = new(Vector3.Zero, float.MaxValue),
                Stages = stages,
                FeatureIndex = Index,
                SortGroup = order,
                IsAlive = true
            }
        );

        if (renderer is not null) {
            renderers[id.Index] = renderer;
        }

        return id;
    }

    /// <summary>Takes an interface out of the frame, object and surface together.</summary>
    /// <param name="id">What <see cref="Mount" /> returned.</param>
    /// <remarks>
    ///     ⚠ Both halves, which is why this exists beside <see cref="Remove" />. Forgetting the
    ///     surface and leaving the object alive is an object every view still culls and every stage
    ///     still collects, drawn by a feature that will not find it — quiet, and one more of them per
    ///     window closed.
    /// </remarks>
    public void Unmount(RenderObjectId id) {
        Remove(id);
        System?.Objects.Remove(id);
    }

    /// <summary>What draws one mounted interface: its own renderer, or the shared default.</summary>
    /// <param name="id">What <see cref="Mount" /> returned.</param>
    /// <returns>The renderer, or <see langword="null" /> if there is none yet.</returns>
    public UiRenderer? RendererOf(RenderObjectId id) =>
        renderers.TryGetValue(id.Index, out var own) ? own : Renderer;

    /// <summary>Points an object at the surface it draws.</summary>
    /// <param name="id">The object.</param>
    /// <param name="surface">What it draws.</param>
    /// <remarks>
    ///     ⚠ Called during extraction, which is the one phase allowed to touch anything with
    ///     references in it. A <see cref="UiGeometry" /> holds the geometry builder's own lists, so
    ///     what is stored here is only valid until that builder runs again — which is exactly the
    ///     lifetime of a frame, and is why this is not a cache.
    /// </remarks>
    public void Set(RenderObjectId id, in UiInterface surface) => surfaces[id.Index] = surface;

    /// <summary>Forgets a surface, for an object that has gone away.</summary>
    /// <remarks>
    ///     The renderer it named goes with it. Left behind, the next object to be given that dense
    ///     index — the store reuses them — would inherit a renderer somebody else's window built.
    /// </remarks>
    public void Remove(RenderObjectId id) {
        surfaces.Remove(id.Index);
        renderers.Remove(id.Index);
    }

    /// <summary>Puts every mounted interface's frame where the GPU can read it.</summary>
    /// <param name="commands">
    ///     A list that is <b>not inside a render pass</b>, recorded ahead of the frame's own passes
    ///     on the same list — which is the order the graph then executes them in.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The other half of drawing, and until this existed it had no reachable caller
    ///         either.</b> <see cref="Draw" /> runs inside a render pass and can only
    ///         <see cref="UiRenderer.Record" />; <see cref="UiRenderer.Upload" /> is what writes this
    ///         frame's vertices, indices and box records into the ring and copies the glyph atlas,
    ///         and a texture copy is the one thing a Vulkan command list may not do inside a pass.
    ///         So a feature that only recorded drew from whatever region the last upload left —
    ///         which, with nothing ever uploading, is a buffer that has never been written.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The dead <see cref="UiInterface.Atlas" /> was the evidence.</b> Every surface
    ///         carried the atlas its text draws from and nothing read the field: the record was
    ///         shaped for this call and this call was not written. It is a feature that would have
    ///         drawn a scene's HUD as untextured quads, and only where a box happened to have no
    ///         glyphs in it would the picture have looked right.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One <see cref="UiRenderer" /> serves one surface, and this is where that is
    ///         enforced rather than described.</b> A renderer advances its ring region inside
    ///         <c>Upload</c> and <c>Record</c> draws from the region the <em>last</em> upload wrote,
    ///         so two mounted interfaces uploaded through one of them are both drawn from the second
    ///         one's geometry — the modal and the document behind it come out as two copies of the
    ///         modal. That is a picture and not an error, which is why the arrangement that produces
    ///         it is refused here instead: a frame is worth losing to an exception that names the
    ///         cause, and is not worth spending on a HUD that is quietly wrong.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     Two mounted interfaces resolve to the same <see cref="UiRenderer" />.
    /// </exception>
    public void Upload(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);

        serving.Clear();

        foreach (var (index, surface) in surfaces) {
            if (Serve(index) is not { } renderer) {
                continue;
            }

            renderer.Upload(commands, surface.Geometry, surface.Atlas);
        }
    }

    /// <summary>Resolves one interface's renderer, refusing to hand the same one out twice.</summary>
    /// <remarks>
    ///     ⚠ <b>Reference identity, not a count of mounted surfaces.</b> Two interfaces are fine and
    ///     are what the sort order is for; two interfaces <i>through one renderer</i> is the
    ///     arrangement that draws the wrong picture, and the two are not the same question — a caller
    ///     that mounts three and gives each its own is correct, and a caller that mounts two and
    ///     leaves both on <see cref="Renderer" /> is not.
    /// </remarks>
    UiRenderer? Serve(int index) {
        var renderer = renderers.TryGetValue(index, out var own) ? own : Renderer;

        if (renderer is null) {
            return null;
        }

        if (!serving.Add(renderer)) {
            throw new InvalidOperationException(
                "Two mounted interfaces resolve to the same UiRenderer. A UiRenderer holds the ring "
                + "region its last Upload wrote and Record draws from that region, so both surfaces "
                + "would be drawn from whichever uploaded second. Pass a UiRenderer of its own to "
                + "UiRenderFeature.Mount for every interface after the first."
            );
        }

        return renderer;
    }

    /// <summary>Renders every mounted interface's composited groups into surfaces of their own.</summary>
    /// <param name="commands">
    ///     The same kind of list <see cref="Upload" /> takes and, in practice, the same one: a list
    ///     that is <b>not inside a render pass</b>, because each group opens one and passes do not
    ///     nest. Call it after <see cref="Upload" /> and before the frame's own passes begin.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="commands" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The third outside-the-pass half, and skipping it does not degrade a frame — it
    ///         draws a different one.</b> <see cref="UiRenderer.Compose" />'s own remarks call itself
    ///         optional, and read alone that is true: <c>Record</c> skips a group's draws only where
    ///         a surface exists, so a host that never composes gets the flat walk. But
    ///         <c>UiGeometryBuilder</c> emits a group's contents at <i>alpha one</i> precisely so the
    ///         surface can carry the fade, so the flat walk over a faded panel is not a faded panel
    ///         drawn approximately — it is an <b>opaque</b> one. A HUD's half-transparent inventory
    ///         panel comes out solid, and nothing anywhere says so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The backdrop is left at its default here, which is a picture and not an
    ///         oversight.</b> <see cref="UiRenderer.Compose" />'s <c>beneath</c> wants what the host
    ///         had already painted where the interface will be, and inside a world renderer that is
    ///         the scene — which at this point in the frame <em>has not been drawn</em>, because
    ///         these passes are recorded ahead of the caller's own. So a <c>backdrop-filter</c> over
    ///         a HUD blurs the interface above it and reads a transparent field for the world behind
    ///         it. Every other group composites correctly. Supplying it needs the scene's colour
    ///         target from the frame before, which is a decision about latency rather than a missing
    ///         call, and is not made here.
    ///     </para>
    ///     <para>
    ///         The scale is one, matching <see cref="Draw" />'s <c>Record</c>. The two agreeing is
    ///         what matters — a group's surface is allocated at <c>Compose</c>'s scale and sampled at
    ///         <c>Record</c>'s — and neither knows the display's, because
    ///         <see cref="UiInterface" /> carries a size and not a density.
    ///     </para>
    /// </remarks>
    public void Compose(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);

        // ⚠ Through `Serve`, exactly as `Upload` and `Draw` are, and this is not a tidy-up. A
        // `UiRenderer` holds the ring region its last upload wrote, so composing every mounted
        // interface through one renderer composes whichever uploaded second, twice — the same
        // wrong picture the upload half refuses, drawn one stage later. A feature with one
        // interface, which is every host today, resolves to `Renderer` and is unaffected.
        serving.Clear();

        foreach (var (index, surface) in surfaces) {
            if (Serve(index) is not { } renderer) {
                continue;
            }

            renderer.Compose(commands, surface.Geometry, surface.Surface);
        }
    }

    /// <inheritdoc />
    protected override void Draw(
        RenderSystem system,
        RenderDrawContext context,
        ReadOnlySpan<RenderNode> nodes
    ) {
        serving.Clear();

        foreach (var node in nodes) {
            if (!surfaces.TryGetValue(node.Object.Index, out var surface)) {
                continue;
            }

            // ⚠ Checked here as well as in `Upload`, and not because the two can disagree. A host
            // that never uploaded reaches this method having drawn from a buffer nothing wrote —
            // which is the failure the upload half exists to stop — and it must not be the path on
            // which a shared renderer is quietly tolerated.
            if (Serve(node.Object.Index) is not { } renderer) {
                continue;
            }

            renderer.Record(context.CommandList, surface.Geometry, surface.Surface);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ The surface's own order, and <b>the stage this is drawn in has to sort
    ///     <c>ByGroup</c></b>. Every other mode puts depth in the key, and an interface has no depth —
    ///     two surfaces at the same distance would then sort by object id, which is whichever one
    ///     happened to be created first.
    /// </remarks>
    protected override uint SortGroupOf(RenderSystem system, RenderObjectId id, RenderStage stage) =>
        surfaces.TryGetValue(id.Index, out var surface) ? surface.Order : system.Objects[id].SortGroup;
}
