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

    /// <summary>
    ///     Why this node drew something other than what it was asked for, this frame — or null,
    ///     meaning every input it needed was there.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The state the whole tree is full of and nothing could see.</b> A compositor node
    ///         asked for an input it did not get almost never fails: it takes a designed-in fallback,
    ///         draws, and leaves every counter healthy — an absent camera becomes an identity matrix,
    ///         an absent shadow map becomes the default white view, an absent frame becomes zeroed
    ///         light. Each of those is the right thing to do and none of them is the thing the author
    ///         asked for, so the only place the difference existed was in the picture.
    ///     </para>
    ///     <para>
    ///         The nodes that already answered this each invented their own word for it —
    ///         <c>Skipped</c> on nine compute fills, <c>PreviewReason</c> on the terrain,
    ///         <c>MissingMeshes</c> on the foliage draw, and four parallel <c>…Skipped</c> strings on
    ///         <c>ScreenProbeGatherRenderer</c> hand-forwarded from its children. This is that, under
    ///         one name, on the base class every one of them already derives, so
    ///         <see cref="GraphicsCompositor.Degradations" /> can collect the frame's whole list
    ///         without knowing a single node type.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A counter and not a log line, and that is the point.</b>
    ///         <c>Vixen.Rendering.PostFx</c> and <c>Vixen.Rendering.Water</c> emit no log events at
    ///         all and take no <c>ILogger</c>; giving each degrade its own event would mean a logger
    ///         on nodes that have never needed one, plus a register row and a guide page per site. A
    ///         host that wants lines writes them once, off the collected list.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This frame, not the worst frame there has ever been</b> — <c>TerrainSceneRenderer</c>
    ///         already says so about <c>PreviewReason</c> and it is the property that makes the answer
    ///         worth reading. A node must therefore call <see cref="Degrade" /> on <em>both</em> paths
    ///         each time it decides, so recovering is as visible as degrading.
    ///     </para>
    /// </remarks>
    public string? Degraded { get; private set; }

    /// <summary>Records why this node is drawing something other than what it was asked for.</summary>
    /// <param name="reason">
    ///     What was missing and what happened instead, as a sentence a developer who did not write
    ///     the node can act on — or null, for "everything I needed was there".
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Say both halves. "No camera" names the input; "no camera, so the cascades are fitted to
    ///         a fabricated frustum at the origin" names the consequence, which is the half that tells
    ///         somebody staring at a wrong picture whether they are looking at this.
    ///     </para>
    ///     <para>
    ///         Called from whichever phase makes the decision — usually <see cref="Build" />, because
    ///         that is where a node reads the frame. <see cref="Record" /> may run on several threads,
    ///         but each node writes only its own field, so recording a degrade there is safe and
    ///         reading the tree afterwards is a different phase.
    ///     </para>
    /// </remarks>
    protected void Degrade(string? reason) => Degraded = reason;

    /// <summary>Declares the views and stages this node needs, before anything is culled.</summary>
    protected internal virtual void Collect(GraphicsCompositor compositor) { }

    /// <summary>Declares this node's render-graph passes.</summary>
    protected internal virtual void Build(GraphicsCompositor compositor, CompositorFrame frame) { }

    /// <summary>Records this node's work into a pass somebody else opened.</summary>
    protected internal virtual void Record(GraphicsCompositor compositor, RenderDrawContext context) { }

    /// <summary>Runs a child node's phase, for a node built out of other nodes.</summary>
    /// <remarks>
    ///     <para>
    ///         Composition is how most of this tree is written — a bloom chain is nine full-screen
    ///         passes, a post effect is one — and the three phase methods are <c>protected internal</c>
    ///         because the compositor drives them and nothing else should. Those two facts collide the
    ///         moment a composite node lives in another assembly: <c>internal</c> does not reach it,
    ///         and <c>protected</c> does not let an instance call another instance's.
    ///     </para>
    ///     <para>
    ///         So this is the seam, and it is deliberately the *only* thing that widens: a subclass
    ///         anywhere can drive a child it owns, and nothing outside the hierarchy gains the ability
    ///         to build a node the compositor did not ask for. Without it, "a post effect is a node
    ///         over a full-screen pass" would be a sentence only <c>Vixen.Rendering</c> could write —
    ///         and a game's own effect could not be one at all.
    ///     </para>
    /// </remarks>
    protected static void BuildChild(SceneRenderer child, GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(child);

        if (child.Enabled) {
            child.Build(compositor, frame);
        }
    }

    /// <summary>Runs a child node's collect phase.</summary>
    /// <remarks>See <see cref="BuildChild" /> — the same seam, for the phase that declares views.</remarks>
    protected static void CollectChild(SceneRenderer child, GraphicsCompositor compositor) {
        ArgumentNullException.ThrowIfNull(child);

        if (child.Enabled) {
            child.Collect(compositor);
        }
    }

    /// <summary>Runs a child node's record phase.</summary>
    /// <remarks>See <see cref="BuildChild" /> — the same seam, for the phase that draws.</remarks>
    protected static void RecordChild(SceneRenderer child, GraphicsCompositor compositor, RenderDrawContext context) {
        ArgumentNullException.ThrowIfNull(child);

        if (child.Enabled) {
            child.Record(compositor, context);
        }
    }

    /// <summary>The nodes underneath this one, for a walk over the whole frame.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Empty for a leaf, and overridden by the two nodes that nest.</b> The tree already
    ///         existed — a sequence has children and so does a render pass — but each held them under
    ///         its own name, so nothing could walk a frame without knowing every node type there is.
    ///         <see cref="GraphicsCompositor.Apply" /> is the first thing that has to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A chain's internal passes are not here.</b> <c>BloomRenderer</c> owns nine
    ///         full-screen passes and <c>AutoExposureRenderer</c> owns a dispatch per halving; those
    ///         are the node's implementation rather than nodes a document named, and exposing them
    ///         would let a walk reach inside a node and change something the node is about to
    ///         overwrite.
    ///     </para>
    /// </remarks>
    public virtual IReadOnlyList<SceneRenderer> Nested => [];

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
    public override IReadOnlyList<SceneRenderer> Nested => [.. Children];

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
