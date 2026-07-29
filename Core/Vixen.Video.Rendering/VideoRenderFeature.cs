// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Video.Rendering;

/// <summary>Draws videos inside somebody else's renderer.</summary>
/// <remarks>
///     <para>
///         A thin adapter, deliberately, and the same shape <c>UiRenderFeature</c> is: everything that
///         touches a device is in <see cref="VideoRenderer" />, which a sample or a golden image can
///         drive directly, and this is what makes one of those reachable from a
///         <see cref="RenderSystem" />.
///     </para>
///     <para>
///         ⚠ <b>Screen-space, and that is a scope rather than a shortcut.</b> A video quad placed in
///         a rectangle of the target is what a cutscene, a menu background and a panel in a user
///         interface all are, and it is what this draws. A video on a <i>surface in the world</i> — a
///         television in a corridor, lit by the scene's lights — is a material on a mesh, which is
///         <c>MaterialRenderFeature</c>'s and, once it lands, Raven's: three planes and six
///         coefficients are exactly what a material node would consume, and nothing here is in its
///         way.
///     </para>
///     <para>
///         ⚠ <b>One render object per video, not one per frame of it.</b> The store's objects live
///         across frames and are indexed by a dense id every feature's parallel array is keyed on, so
///         an object that came and went with each picture would churn the whole store twenty-five
///         times a second. What changes per frame is the contents of the textures, which the upload
///         has already dealt with by the time anything here runs.
///     </para>
/// </remarks>
public sealed class VideoRenderFeature : RootRenderFeature {
    readonly Dictionary<int, VideoDraw> draws = [];

    /// <inheritdoc />
    public override string Name => "Video";

    /// <summary>What actually draws. Set before the first frame that draws.</summary>
    /// <remarks>
    ///     Supplied rather than created here, because building it needs shader modules and the formats
    ///     of the pass — neither of which a feature knows and both of which belong to whoever
    ///     assembled the compositor.
    /// </remarks>
    public VideoRenderer? Renderer { get; set; }

    /// <summary>How large the target is, in the units the draws' rectangles are measured in.</summary>
    /// <remarks>
    ///     ⚠ The <i>logical</i> surface, not the framebuffer, whenever the two differ — see
    ///     <see cref="VideoRenderer.Record" />, which explains what handing over the wrong one looks
    ///     like on screen.
    /// </remarks>
    public Int2 Surface { get; set; }

    /// <summary>Points an object at the video it draws.</summary>
    /// <param name="id">The object.</param>
    /// <param name="draw">What it draws.</param>
    /// <remarks>
    ///     ⚠ Called during extraction, which is the one phase allowed to touch anything with
    ///     references in it. A <see cref="VideoDraw" /> holds a <c>VideoTexture</c>, whose contents
    ///     are whatever the upload put there this frame.
    /// </remarks>
    public void Set(RenderObjectId id, in VideoDraw draw) => draws[id.Index] = draw;

    /// <summary>Forgets a video, for an object that has gone away.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether there was one.</returns>
    public bool Remove(RenderObjectId id) => draws.Remove(id.Index);

    /// <summary>What an object is currently drawing.</summary>
    /// <param name="id">The object.</param>
    /// <param name="draw">What it draws, if anything.</param>
    /// <returns>Whether it does.</returns>
    public bool TryGet(RenderObjectId id, out VideoDraw draw) => draws.TryGetValue(id.Index, out draw);

    /// <inheritdoc />
    protected override void Draw(
        RenderSystem system,
        RenderDrawContext context,
        ReadOnlySpan<RenderNode> nodes
    ) {
        if (Renderer is null || Surface.X <= 0 || Surface.Y <= 0) {
            return;
        }

        Renderer.Begin();

        foreach (var node in nodes) {
            if (draws.TryGetValue(node.Object.Index, out var draw)) {
                Renderer.Record(context.CommandList, in draw, Surface);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ The draw's own order, and <b>the stage this is drawn in has to sort <c>ByGroup</c></b>.
    ///     Every other mode puts depth in the key, and a screen-space video has none — two videos at
    ///     the same distance would then sort by object id, which is whichever one happened to be
    ///     created first.
    /// </remarks>
    protected override uint SortGroupOf(RenderSystem system, RenderObjectId id, RenderStage stage) =>
        draws.TryGetValue(id.Index, out var draw) ? draw.Order : system.Objects[id].SortGroup;
}
