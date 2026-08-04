// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ai;

/// <summary>Which of the four kinds a node is.</summary>
/// <remarks>
///     Two of the four are nodes; the other two — decorators and services — are <i>attached</i> to a
///     node rather than being one, so they are not in this enum. That is the shape doc 37 § D4 takes
///     from Unreal: an attachment is always exactly one edge to exactly one parent, can never be
///     shared, and has no position of its own, so drawing it as an edge would be a wire the author
///     has to route to say nothing.
/// </remarks>
public enum BehaviorNodeKind : byte {
    /// <summary>Ordered children and a rule for walking them.</summary>
    Composite,

    /// <summary>A leaf, and an <see cref="IAgentAction" />.</summary>
    Task
}

/// <summary>How a composite walks its children.</summary>
/// <remarks>
///     A closed list of five, dispatched by a switch rather than through an interface. It is closed
///     because forty years of behaviour-tree literature has produced two of these and Unreal added
///     the rest; a project that wants a sixth wants a different tree, and doc 37 § Part 4 lists the
///     seams this subsystem does have — a composite is not one of them.
/// </remarks>
public enum BehaviorCompositeKind : byte {
    /// <summary>Children left to right until one <b>succeeds</b>; fails if all fail.</summary>
    Selector,

    /// <summary>Children left to right until one <b>fails</b>; succeeds if all succeed.</summary>
    Sequence,

    /// <summary>
    ///     One main task plus a background branch — Unreal's <c>SimpleParallel</c>, under the name
    ///     people look for.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One main task, not N branches.</b> True N-way parallelism makes the abort scope in
    ///     doc 37 § D6 ill-defined — two branches whose decorators want to abort each other — and
    ///     every engine that offered it has a page explaining why it does not do what people expect.
    ///     Child 0 must be a task; child 1 is the background branch and may be anything.
    /// </remarks>
    Parallel,

    /// <summary>A selector over a shuffled order, with per-child weights, from the agent's stream.</summary>
    RandomSelector,

    /// <summary>
    ///     A selector that re-evaluates from child zero every step rather than resuming where it was.
    /// </summary>
    /// <remarks>
    ///     Explicit and separately named, because <i>"does a selector resume"</i> is the question
    ///     every implementation answers differently and silently. <see cref="Selector" /> resumes;
    ///     this one does not, so a higher-priority child whose decorator has started passing takes
    ///     over at the top of the next step with no observer anywhere.
    /// </remarks>
    Priority
}

/// <summary>What a decorator may interrupt when the data it reads changes.</summary>
/// <remarks>
///     <para>
///         Unreal's four, which Unity's Behavior package independently arrived at. The test is two
///         integer comparisons against the pre-order index range of the decorated node, which is the
///         whole reason a template is laid out depth-first — doc 37 § D6.
///     </para>
///     <para>
///         ⚠ <b>The scope is Unity's, not Unreal's.</b> An observer affects only the siblings under
///         its own parent composite, and that composite restarts from child zero. Unreal's abort
///         reaches further up the tree, which is more powerful and is the subject of most of the
///         confusion in its forums — a decorator two levels above the running task aborting a branch
///         it does not visibly contain. A rule the editor can <i>draw</i> is a rule an author can
///         predict.
///     </para>
/// </remarks>
[Flags]
public enum ObserverAborts : byte {
    /// <summary>Gates entry and nothing else. The default, and the cheapest.</summary>
    None = 0,

    /// <summary>
    ///     If it starts failing while its own subtree is running, abort what is running.
    /// </summary>
    Self = 1,

    /// <summary>
    ///     If it starts passing while something <i>after</i> its subtree is running, take over.
    /// </summary>
    LowerPriority = 2,

    /// <summary>Both tests.</summary>
    Both = Self | LowerPriority
}

/// <summary>What a <see cref="BehaviorCompositeKind.Parallel" /> does when its main task finishes.</summary>
public enum ParallelFinishMode : byte {
    /// <summary>Abort the background branch at once and finish with the main task's result.</summary>
    Immediate,

    /// <summary>Let the background branch finish what it is doing first.</summary>
    Delayed
}
