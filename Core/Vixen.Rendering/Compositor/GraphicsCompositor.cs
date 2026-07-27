// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Shaders;

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

    /// <summary>Textures the host lends the frame, by the name the tree refers to them by.</summary>
    /// <remarks>
    ///     The swapchain image, and anything that has to outlive a frame — a cached shadow atlas, a
    ///     history buffer for temporal antialiasing. Everything else should be declared instead, so
    ///     the graph can size it, alias it and drop it.
    /// </remarks>
    public Dictionary<string, ImportedTexture> Imports { get; } = new(StringComparer.Ordinal);

    /// <summary>Transient resources the frame declares, by name.</summary>
    /// <remarks>
    ///     Filled by <see cref="CompositorBuilder" /> from the asset's <c>resources</c>, or by a host
    ///     building a tree in code. They are the graph's to allocate, which means two whose lifetimes
    ///     do not overlap can be the same memory.
    /// </remarks>
    public IList<RenderResourceAsset> Resources { get; } = [];

    /// <summary>The frame's reference size, which a scaled resource is a fraction of.</summary>
    public Int2 FrameSize { get; set; } = new(1, 1);

    /// <summary>
    ///     Runs a whole frame: collect, the render system's phases, then declare the graph's passes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The three in this order and not another. Collect must precede culling because it is
    ///         what decides what is culled; building must follow sorting because a pass body draws
    ///         the order sorting produced. See <see cref="RenderSystem.Draw" /> for the same argument
    ///         one level down.
    ///     </para>
    ///     <para>
    ///         Nothing is recorded here. The caller executes the graph, which is what places the
    ///         barriers, allocates the transients and drops the passes nothing needed — and which is
    ///         also the seam a caller uses to inspect a frame before it runs it.
    ///     </para>
    /// </remarks>
    public CompositorFrame Build(RenderGraph graph, EffectSystem effects, IGraphicsDevice? device = null) {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(effects);

        var frame = new CompositorFrame {
            Graph = graph,
            Effects = effects,
            Device = device,
            Size = FrameSize
        };

        foreach (var (name, imported) in Imports) {
            frame.Add(
                name,
                graph.ImportTexture(
                    imported.Texture,
                    imported.View,
                    imported.Description,
                    imported.EntryState,
                    imported.ExitState
                ),
                imported.Description.Format
            );
        }

        foreach (var declared in Resources) {
            // An import of the same name wins. A host that has a real texture for something the
            // document also describes — a scene colour that is the swapchain image in one preset and
            // an offscreen buffer in another — should not have to edit the document to say so.
            if (frame.Has(declared.Name)) {
                continue;
            }

            frame.Add(declared.Name, graph.CreateTexture(declared.Describe(FrameSize)), declared.Format);
        }

        if (Game is { Enabled: true }) {
            Collect();
            System.Draw();
            Game.Build(this, frame);
        }

        return frame;
    }

    /// <summary>Runs the collect phase alone and hands the views to the render system.</summary>
    /// <remarks>
    ///     Separate from <see cref="Build" /> for the caller that wants the phases on its own job
    ///     graph — collect on the main thread, the system's phases as jobs, building afterwards. It
    ///     is the same sequence either way.
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
