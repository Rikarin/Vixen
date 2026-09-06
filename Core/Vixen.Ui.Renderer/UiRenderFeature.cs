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

    /// <inheritdoc />
    public override string Name => "Ui";

    /// <summary>What actually draws. Set before the first frame that draws.</summary>
    /// <remarks>
    ///     Supplied rather than created here, because building it needs shader modules and the
    ///     formats of the pass — neither of which a feature knows and both of which belong to
    ///     whoever assembled the compositor.
    /// </remarks>
    public UiRenderer? Renderer { get; set; }

    /// <summary>Adds the render object one interface is drawn as, and returns its id.</summary>
    /// <param name="stages">Which stages draw it — see the remarks on sorting.</param>
    /// <param name="order">Where it sits among the interfaces, lowest drawn first.</param>
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
    /// </remarks>
    public RenderObjectId Mount(RenderStageMask stages, uint order = 0) {
        if (System is null) {
            throw new InvalidOperationException(
                "A UiRenderFeature can only mount a surface once it is in a render system: the object "
                + "carries this feature's index, and the index is assigned by RenderSystem.AddFeature. "
                + "Call AddFeature before Mount."
            );
        }

        return System.Objects.Add(
            new() {
                Bounds = new(Vector3.Zero, float.MaxValue),
                Stages = stages,
                FeatureIndex = Index,
                SortGroup = order,
                IsAlive = true
            }
        );
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
    public void Remove(RenderObjectId id) => surfaces.Remove(id.Index);

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
    ///         ⚠ <b>One <see cref="Renderer" /> serves one surface, whatever <see cref="Mount" />
    ///         allows.</b> <c>UiRenderer</c> advances its ring region inside <c>Upload</c> and
    ///         <c>Record</c> draws from the region the <em>last</em> upload wrote, so two mounted
    ///         interfaces uploaded through one renderer are both drawn from the second one's
    ///         geometry. That is a renderer-per-surface arrangement this feature does not have yet
    ///         and is tracked separately; the loop here is written for the arrangement that exists,
    ///         which is one mounted interface, and does not pretend the second one works.
    ///     </para>
    /// </remarks>
    public void Upload(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);

        if (Renderer is null) {
            return;
        }

        foreach (var surface in surfaces.Values) {
            Renderer.Upload(commands, surface.Geometry, surface.Atlas);
        }
    }

    /// <inheritdoc />
    protected override void Draw(
        RenderSystem system,
        RenderDrawContext context,
        ReadOnlySpan<RenderNode> nodes
    ) {
        if (Renderer is null) {
            return;
        }

        foreach (var node in nodes) {
            if (!surfaces.TryGetValue(node.Object.Index, out var surface)) {
                continue;
            }

            Renderer.Record(context.CommandList, surface.Geometry, surface.Surface);
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
