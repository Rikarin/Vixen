// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
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
