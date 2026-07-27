// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.Compositor;

/// <summary>
///     The frame's structure: which views exist, which stages they draw, and into what.
/// </summary>
/// <remarks>
///     <para>
///         The third of the three ideas docs/plan/06 takes from Stride verbatim, and the last to be
///         built. <strong>The frame is data the user edits, not code.</strong> "Swap forward for
///         deferred" is a different tree of <see cref="SceneRenderer" />s rather than a different
///         build of the engine, and the three shipped presets — Forward+, deferred, mobile forward —
///         are three assets over the same features.
///     </para>
///     <para>
///         <strong>Collect runs before the render system, and that ordering is the design.</strong>
///         Views are declared by the nodes that draw them, so the frame's view list is derived from
///         the compositor rather than handed to it: a shadow cascade exists because something draws
///         it, and a stage is in a view's mask because a node asked for it. A host that set the mask
///         itself would eventually set one nothing draws, and pay for culling into a list nobody
///         reads.
///     </para>
///     <para>
///         What is <em>not</em> here is the render graph. A node names its own attachments today,
///         where later it will name a transient resource and let barriers and aliasing be derived —
///         but that changes where a <see cref="RenderPassRenderer" /> gets its textures, not the
///         shape of this tree.
///     </para>
/// </remarks>
public sealed class GraphicsCompositor(RenderSystem system) {
    readonly List<RenderView> views = [];

    /// <summary>The render system this composes a frame for.</summary>
    public RenderSystem System { get; } = system;

    /// <summary>The root of the graph — the whole frame.</summary>
    public SceneRenderer? Game { get; set; }

    /// <summary>The views this frame's collect phase declared, in first-use order.</summary>
    public IReadOnlyList<RenderView> Views => views;

    /// <summary>Declares that this frame uses a view. Idempotent.</summary>
    /// <remarks>
    ///     <para>
    ///         Called from <see cref="SceneRenderer.Collect" />. Idempotent because a view is normally
    ///         drawn by more than one node — an opaque stage and a transparent one share a camera —
    ///         and making each node declare it is what keeps a node independent of what else is in
    ///         the tree.
    ///     </para>
    ///     <para>
    ///         First use in a frame clears the view's stage mask, so a stage removed from the tree
    ///         stops being collected for rather than lingering in a mask nobody rebuilt. Add stages
    ///         through <see cref="Use(RenderView, RenderStage)" />, which orders the two correctly.
    ///     </para>
    /// </remarks>
    public void Use(RenderView view) {
        ArgumentNullException.ThrowIfNull(view);

        if (views.Contains(view)) {
            return;
        }

        view.Stages = RenderStageMask.None;
        views.Add(view);
    }

    /// <summary>Declares that this frame draws a stage from a view.</summary>
    public void Use(RenderView view, RenderStage stage) {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(stage);

        Use(view);
        view.Stages |= stage.Mask;
    }

    /// <summary>
    ///     Runs a whole frame: collect, then the render system's phases, then record.
    /// </summary>
    /// <remarks>
    ///     The three in this order and not another. Collect must precede culling because it is what
    ///     decides what is culled; recording must follow sorting because it is what consumes the
    ///     order. See <see cref="RenderSystem.Draw" /> for the same argument one level down.
    /// </remarks>
    public void Draw(RenderDrawContext context) {
        ArgumentNullException.ThrowIfNull(context);

        if (Game is not { Enabled: true }) {
            return;
        }

        Collect();
        System.Draw();
        Game.Draw(this, context);
    }

    /// <summary>Runs the collect phase alone and hands the views to the render system.</summary>
    /// <remarks>
    ///     Separate from <see cref="Draw" /> for the caller that wants the phases on its own job
    ///     graph — collect on the main thread, the system's phases as jobs, recording on several
    ///     threads at once. It is the same sequence either way.
    /// </remarks>
    public void Collect() {
        if (Game is not { Enabled: true }) {
            return;
        }

        // Cleared each frame rather than accumulated: a view that stopped being drawn — a probe that
        // went out of range, a cascade the shadow distance dropped — must stop being culled for, and
        // a list that only ever grows would keep paying for it.
        views.Clear();
        Game.Collect(this);
        System.SetViews(views);
    }
}
