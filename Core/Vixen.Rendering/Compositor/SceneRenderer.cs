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
///         <strong>Three phases, and each can only do its own job.</strong>
///     </para>
///     <list type="number">
///         <item><description>
///             <see cref="Collect" /> runs before the render system and says which views this node
///             needs and which stages those views draw — so the frame's view list is <em>derived
///             from the tree</em> rather than handed to it.
///         </description></item>
///         <item><description>
///             <see cref="Build" /> runs after culling and sorting and declares render-graph passes:
///             what each reads, what it writes, and what it does. Nothing is recorded here.
///         </description></item>
///         <item><description>
///             <see cref="Record" /> runs inside a pass the graph opened, with its barriers already
///             placed. Only drawing is left.
///         </description></item>
///     </list>
///     <para>
///         The last split is not bureaucracy — it is the RHI's own. A draw has to be inside a render
///         pass and a pass has to be declared before the graph can order it, so a node either owns a
///         pass (<see cref="Build" />) or draws into someone else's (<see cref="Record" />). A node
///         that tried to do both would be declaring a pass from inside one.
///     </para>
/// </remarks>
public abstract class SceneRenderer {
    /// <summary>The node's name, for debug groups, profiling and the compositor asset.</summary>
    public string Name { get; init; } = "";

    /// <summary>Whether this node runs at all.</summary>
    /// <remarks>
    ///     Every phase or none. A node disabled between them would have declared a view that nothing
    ///     then draws, which culls objects into a list no one reads.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Declares the views and stages this node needs, before anything is culled.</summary>
    protected internal virtual void Collect(GraphicsCompositor compositor) { }

    /// <summary>Declares this node's render-graph passes.</summary>
    protected internal virtual void Build(GraphicsCompositor compositor, CompositorFrame frame) { }

    /// <summary>Records this node's work into a pass somebody else opened.</summary>
    protected internal virtual void Record(GraphicsCompositor compositor, RenderDrawContext context) { }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? GetType().Name : Name;
}

/// <summary>Several renderers, run in order.</summary>
/// <remarks>
///     The only structure the tree has, and deliberately so: the <em>dependencies</em> between passes
///     are resource dependencies, and the render graph derives them from what each pass declared it
///     reads and writes. Making the compositor itself a DAG would ask an author to state twice what
///     the graph works out once — and to keep the two statements in agreement forever.
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
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        foreach (var child in Children) {
            if (child.Enabled) {
                child.Build(compositor, frame);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A sequence is useful at either level — several passes in a frame, or several stages inside
    ///     one pass — so it forwards both phases and lets its children decide which they answer.
    /// </remarks>
    protected internal override void Record(GraphicsCompositor compositor, RenderDrawContext context) {
        foreach (var child in Children) {
            if (child.Enabled) {
                child.Record(compositor, context);
            }
        }
    }
}

/// <summary>A renderer made of callbacks, for a host that has not made an asset of it yet.</summary>
/// <remarks>
///     Stride ships the same escape hatch and it earns its place: a debug overlay, a one-off
///     experiment or an editor gizmo pass is a node in the graph without being a type in the engine.
/// </remarks>
public sealed class DelegateSceneRenderer : SceneRenderer {
    /// <summary>What to run in the collect phase.</summary>
    public Action<GraphicsCompositor>? OnCollect { get; init; }

    /// <summary>What to run in the build phase.</summary>
    public Action<GraphicsCompositor, CompositorFrame>? OnBuild { get; init; }

    /// <summary>What to run inside a pass.</summary>
    public Action<GraphicsCompositor, RenderDrawContext>? OnRecord { get; init; }

    /// <inheritdoc />
    protected internal override void Collect(GraphicsCompositor compositor) => OnCollect?.Invoke(compositor);

    /// <inheritdoc />
    protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) =>
        OnBuild?.Invoke(compositor, frame);

    /// <inheritdoc />
    protected internal override void Record(GraphicsCompositor compositor, RenderDrawContext context) =>
        OnRecord?.Invoke(compositor, context);
}
