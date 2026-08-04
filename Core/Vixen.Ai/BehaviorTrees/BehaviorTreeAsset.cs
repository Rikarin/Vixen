// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>One authored node, before it is compiled.</summary>
/// <remarks>
///     <para>
///         The tree as a person drew it: a root with children, each carrying its own decorators and
///         services as lists. <see cref="Children" /> is <b>ordered</b>, and that order is the whole
///         priority ordering of the compiled tree.
///     </para>
///     <para>
///         ⚠ <b>Child order is stored, not derived from a position on a canvas.</b> Unreal derives it
///         from the horizontal position of the child nodes, which makes three ordinary gestures
///         dangerous: auto-layout re-derives positions, so laying the graph out silently reorders the
///         tree; dragging a node six pixels left to line it up with its sibling changes which branch
///         wins; and a merge that resolves two positions produces a tree neither author wrote, with a
///         diff showing only coordinates. All three are silent and all three change what the agent
///         does — doc 37 § D5.
///     </para>
/// </remarks>
public sealed class BehaviorNodeDefinition {
    /// <summary>What it is called, for diagnostics and for the editor.</summary>
    public Symbol Name { get; set; }

    /// <summary>Composite or task.</summary>
    public BehaviorNodeKind Kind { get; set; }

    /// <summary>How it walks its children, when it is a composite.</summary>
    public BehaviorCompositeKind Composite { get; set; }

    /// <summary>Which registered action it runs, when it is a task.</summary>
    public Symbol Action { get; set; }

    /// <summary>What a parallel does when its main task ends.</summary>
    public ParallelFinishMode FinishMode { get; set; }

    /// <summary>Its share of a random selector's draw. Non-positive is treated as one.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>
    ///     The tree to splice in here, when this task is a static <c>RunSubtree</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Spliced at compile time rather than pushed at run time</b>, and that is the whole
    ///     reason a static subtree is worth having as its own thing. Inlining keeps pre-order index
    ///     equal to priority <i>across the boundary</i>, so a decorator in the parent can abort a
    ///     branch inside the subtree and the range test still means what it says. A pushed instance
    ///     would be opaque to both. <c>RunSubtreeDynamic</c> names its tree from a key and cannot be
    ///     inlined, so it does push — and pays exactly that price.
    /// </remarks>
    public BehaviorTreeAsset? Subtree { get; set; }

    /// <summary>Its children, in priority order.</summary>
    public List<BehaviorNodeDefinition> Children { get; } = [];

    /// <summary>Its decorators, in evaluation order.</summary>
    public List<BehaviorDecorator> Decorators { get; } = [];

    /// <summary>Its services.</summary>
    public List<BehaviorServiceDefinition> Services { get; } = [];

    /// <summary>Attaches a decorator, after the ones already there.</summary>
    /// <param name="decorator">The condition.</param>
    /// <returns>This node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decorator" /> is null.</exception>
    public BehaviorNodeDefinition With(BehaviorDecorator decorator) {
        ArgumentNullException.ThrowIfNull(decorator);
        Decorators.Add(decorator);

        return this;
    }

    /// <summary>Attaches a service.</summary>
    /// <param name="service">What it does.</param>
    /// <param name="interval">How often, in seconds.</param>
    /// <param name="randomDeviation">How much to jitter that, from the agent's own stream.</param>
    /// <returns>This node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service" /> is null.</exception>
    public BehaviorNodeDefinition With(BehaviorService service, float interval, float randomDeviation = 0f) {
        ArgumentNullException.ThrowIfNull(service);
        Services.Add(new(service, interval, randomDeviation));

        return this;
    }

    /// <summary>Adds a child, after the ones already there.</summary>
    /// <param name="child">The child.</param>
    /// <returns>This node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="child" /> is null.</exception>
    public BehaviorNodeDefinition Add(BehaviorNodeDefinition child) {
        ArgumentNullException.ThrowIfNull(child);
        Children.Add(child);

        return this;
    }
}

/// <summary>One authored service: what it does, and on what schedule.</summary>
/// <param name="Service">What it does.</param>
/// <param name="Interval">How often, in seconds.</param>
/// <param name="RandomDeviation">How much to jitter it, from the agent's own stream.</param>
public readonly record struct BehaviorServiceDefinition(
    BehaviorService Service,
    float Interval,
    float RandomDeviation
);

/// <summary>A behaviour tree as it was authored: a name and a root.</summary>
/// <remarks>
///     The in-memory authoring domain. The <c>.vxbt</c> file, its importer and the node editor over
///     it are doc 37's P2; what P1 owns is this shape and the compiler that turns it into a
///     <see cref="BehaviorTreeTemplate" />, so that the runtime can be finished and tested before
///     there is a canvas to draw one on.
/// </remarks>
public sealed class BehaviorTreeAsset {
    /// <summary>Creates one.</summary>
    /// <param name="name">What the tree is called.</param>
    /// <param name="root">Its root node.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root" /> is null.</exception>
    public BehaviorTreeAsset(string name, BehaviorNodeDefinition root) {
        ArgumentNullException.ThrowIfNull(root);

        Name = Symbol.Intern(name);
        Root = root;
    }

    /// <summary>What the tree is called.</summary>
    public Symbol Name { get; }

    /// <summary>Its root.</summary>
    public BehaviorNodeDefinition Root { get; }
}

/// <summary>Shorthand for building a tree in code, for tests, tools and samples.</summary>
/// <remarks>
///     Not the authoring surface a designer uses — that is P2's editor — but the one the runtime is
///     tested through, and the one a game reaches for when a tree is small enough that a file would
///     be ceremony.
/// </remarks>
public static class BehaviorTree {
    /// <summary>A selector: children until one succeeds.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="children">Its children, in priority order.</param>
    /// <returns>The node.</returns>
    public static BehaviorNodeDefinition Selector(string name, params BehaviorNodeDefinition[] children) =>
        Composite(name, BehaviorCompositeKind.Selector, children);

    /// <summary>A sequence: children until one fails.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="children">Its children, in order.</param>
    /// <returns>The node.</returns>
    public static BehaviorNodeDefinition Sequence(string name, params BehaviorNodeDefinition[] children) =>
        Composite(name, BehaviorCompositeKind.Sequence, children);

    /// <summary>A selector that re-evaluates from child zero every step.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="children">Its children, in priority order.</param>
    /// <returns>The node.</returns>
    public static BehaviorNodeDefinition Priority(string name, params BehaviorNodeDefinition[] children) =>
        Composite(name, BehaviorCompositeKind.Priority, children);

    /// <summary>A selector over a shuffled order.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="children">Its children. Set each one's <c>Weight</c> to skew the draw.</param>
    /// <returns>The node.</returns>
    public static BehaviorNodeDefinition RandomSelector(string name, params BehaviorNodeDefinition[] children) =>
        Composite(name, BehaviorCompositeKind.RandomSelector, children);

    /// <summary>A main task with a background branch beside it.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="mode">What to do with the branch when the main task ends.</param>
    /// <param name="main">The main task. Must be a task.</param>
    /// <param name="background">The branch that runs alongside it.</param>
    /// <returns>The node.</returns>
    public static BehaviorNodeDefinition Parallel(
        string name,
        ParallelFinishMode mode,
        BehaviorNodeDefinition main,
        BehaviorNodeDefinition background
    ) {
        var node = Composite(name, BehaviorCompositeKind.Parallel, [main, background]);

        node.FinishMode = mode;

        return node;
    }

    /// <summary>A leaf that runs a registered action.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="action">The action's registered name. Defaults to the node's name.</param>
    /// <returns>The node.</returns>
    public static BehaviorNodeDefinition Task(string name, string? action = null) => new() {
        Name = Symbol.Intern(name),
        Kind = BehaviorNodeKind.Task,
        Action = Symbol.Intern(action ?? name)
    };

    /// <summary>A leaf that runs another tree, spliced in at compile time.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="subtree">The tree to splice.</param>
    /// <returns>The node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="subtree" /> is null.</exception>
    public static BehaviorNodeDefinition Subtree(string name, BehaviorTreeAsset subtree) {
        ArgumentNullException.ThrowIfNull(subtree);

        return new() {
            Name = Symbol.Intern(name),
            Kind = BehaviorNodeKind.Task,
            Subtree = subtree
        };
    }

    /// <summary>Wraps a root in an asset.</summary>
    /// <param name="name">What the tree is called.</param>
    /// <param name="root">Its root.</param>
    /// <returns>The asset.</returns>
    public static BehaviorTreeAsset Asset(string name, BehaviorNodeDefinition root) => new(name, root);

    static BehaviorNodeDefinition Composite(
        string name,
        BehaviorCompositeKind kind,
        BehaviorNodeDefinition[] children
    ) {
        var node = new BehaviorNodeDefinition {
            Name = Symbol.Intern(name),
            Kind = BehaviorNodeKind.Composite,
            Composite = kind
        };

        node.Children.AddRange(children);

        return node;
    }
}
