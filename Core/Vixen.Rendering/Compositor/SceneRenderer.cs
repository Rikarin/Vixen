// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.Compositor;

/// <summary>
///     One node of the compositor graph: a thing that happens, in order, to produce a frame.
/// </summary>
/// <remarks>
///     <para>
///         Stride's <c>SceneRendererBase</c>, and the reason docs/plan/06 calls the compositor an
///         asset: a frame's structure is a tree of these, and a tree is data. "Swap forward for
///         deferred" is then a different tree rather than a different build of the engine.
///     </para>
///     <para>
///         <strong>Two phases, and the split is the whole design.</strong>
///         <see cref="Collect" /> runs before the render system does anything and is where a node
///         says which views it needs and which stages those views draw — so the frame's view list is
///         <em>derived from the compositor</em> rather than handed to it. <see cref="Draw" /> runs
///         after culling and sorting and may only record. A node that created a view in
///         <see cref="Draw" /> would have created it after culling had already run without it.
///     </para>
/// </remarks>
public abstract class SceneRenderer {
    /// <summary>The node's name, for debug groups, profiling and the compositor asset.</summary>
    public string Name { get; init; } = "";

    /// <summary>Whether this node runs at all.</summary>
    /// <remarks>
    ///     Both phases or neither. A node disabled between them would have declared a view that
    ///     nothing then draws, which culls objects into a list no one reads.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Declares the views and stages this node needs, before anything is culled.</summary>
    protected internal virtual void Collect(GraphicsCompositor compositor) { }

    /// <summary>Records this node's work.</summary>
    protected internal virtual void Draw(GraphicsCompositor compositor, RenderDrawContext context) { }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? GetType().Name : Name;
}

/// <summary>Several renderers, run in order.</summary>
/// <remarks>
///     The only structure the graph has, and deliberately so: a frame is a sequence, and the
///     dependencies between passes are resource dependencies that the render graph derives from what
///     each pass reads and writes. Making the compositor itself a DAG would ask a user to state
///     twice what the graph can work out once.
/// </remarks>
public sealed class SceneRendererSequence : SceneRenderer {
    /// <summary>The children, in the order they run.</summary>
    public IList<SceneRenderer> Children { get; } = [];

    /// <inheritdoc />
    protected internal override void Collect(GraphicsCompositor compositor) {
        foreach (var child in Children) {
            if (child.Enabled) {
                child.Collect(compositor);
            }
        }
    }

    /// <inheritdoc />
    protected internal override void Draw(GraphicsCompositor compositor, RenderDrawContext context) {
        foreach (var child in Children) {
            if (child.Enabled) {
                child.Draw(compositor, context);
            }
        }
    }
}

/// <summary>A renderer made of two callbacks, for a host that has not made an asset of it yet.</summary>
/// <remarks>
///     Stride ships the same escape hatch and it earns its place: a debug overlay, a one-off
///     experiment or an editor gizmo pass is a node in the graph without being a type in the engine.
/// </remarks>
public sealed class DelegateSceneRenderer : SceneRenderer {
    /// <summary>What to run in the collect phase.</summary>
    public Action<GraphicsCompositor>? OnCollect { get; init; }

    /// <summary>What to run in the draw phase.</summary>
    public Action<GraphicsCompositor, RenderDrawContext>? OnDraw { get; init; }

    /// <inheritdoc />
    protected internal override void Collect(GraphicsCompositor compositor) => OnCollect?.Invoke(compositor);

    /// <inheritdoc />
    protected internal override void Draw(GraphicsCompositor compositor, RenderDrawContext context) =>
        OnDraw?.Invoke(compositor, context);
}
