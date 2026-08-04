// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>One node of a compiled tree: where it sits, what it is, and where its state lives.</summary>
/// <remarks>
///     <para>
///         <b>Its index is its priority.</b> Nodes are laid out depth-first in pre-order, so node 4
///         outranks node 9 because it is earlier in the walk — which is exactly what "left to right,
///         top to bottom" means to the person who drew the tree. There is no priority field, and
///         therefore no way for the two to disagree.
///     </para>
///     <para>
///         <b>And a subtree is a contiguous range.</b> <see cref="LastDescendant" /> makes
///         <i>"is X inside Y's subtree"</i> two comparisons rather than a walk, which is what makes
///         the abort test in doc 37 § D6 affordable at a thousand agents.
///     </para>
/// </remarks>
public struct BehaviorNode {
    /// <summary>What it is called. For diagnostics and the editor; nothing in a step reads it.</summary>
    public Symbol Name;

    /// <summary>Composite or task.</summary>
    public BehaviorNodeKind Kind;

    /// <summary>How it walks its children, when it is a composite.</summary>
    public BehaviorCompositeKind Composite;

    /// <summary>What it runs, when it is a task. An index into the agent's action registry.</summary>
    public ushort Action;

    /// <summary>What a <see cref="BehaviorCompositeKind.Parallel" /> does when its main task ends.</summary>
    public ParallelFinishMode FinishMode;

    /// <summary>Its parent's index, or <c>-1</c> for the root.</summary>
    public int Parent;

    /// <summary>Its first child's index, or <c>-1</c>.</summary>
    public int FirstChild;

    /// <summary>How many children it has.</summary>
    public int ChildCount;

    /// <summary>
    ///     The highest index in its subtree — itself, when it is a leaf.
    /// </summary>
    public int LastDescendant;

    /// <summary>Where its decorators start in the template's decorator table.</summary>
    public int DecoratorStart;

    /// <summary>How many decorators are attached to it.</summary>
    public int DecoratorCount;

    /// <summary>Where its services start in the template's service table.</summary>
    public int ServiceStart;

    /// <summary>How many services are attached to it.</summary>
    public int ServiceCount;

    /// <summary>Where its state starts in an agent's memory block.</summary>
    public int MemoryOffset;

    /// <summary>How many bytes of that block are its own.</summary>
    public int MemorySize;

    /// <summary>Its share of a <see cref="BehaviorCompositeKind.RandomSelector" />'s draw.</summary>
    public float Weight;

    /// <summary>Which nested-instance slot it owns, when it runs a tree named by a key.</summary>
    /// <remarks><c>-1</c> for everything else. See <c>RunSubtreeDynamicTask</c>.</remarks>
    public int NestedSlot;
}

/// <summary>One decorator, attached to one node.</summary>
/// <param name="Decorator">The condition.</param>
/// <param name="Node">What it gates.</param>
/// <param name="Aborts">What it may interrupt.</param>
/// <param name="KeyStart">Where its observed keys start in the template's key table.</param>
/// <param name="KeyCount">How many keys it reads.</param>
/// <param name="MemoryOffset">Where its state starts in an agent's memory block.</param>
/// <param name="MemorySize">How many bytes of that block are its own.</param>
/// <remarks>
///     ⚠ <b>Their order on a node is significant and is authored.</b> They evaluate top to bottom and
///     the first failure stops the rest, so putting the cheap test above the trace is the author's
///     decision — doc 37 § D4.
/// </remarks>
public readonly record struct BehaviorDecoratorSlot(
    BehaviorDecorator Decorator,
    int Node,
    ObserverAborts Aborts,
    int KeyStart,
    int KeyCount,
    int MemoryOffset,
    int MemorySize
);

/// <summary>One service, attached to one composite.</summary>
/// <param name="Service">What it does.</param>
/// <param name="Node">The composite whose branch it lives for.</param>
/// <param name="Interval">How often it runs, in seconds.</param>
/// <param name="RandomDeviation">
///     How much to jitter that, in seconds, from the agent's own stream.
/// </param>
/// <param name="MemoryOffset">Where its state starts in an agent's memory block.</param>
/// <param name="MemorySize">How many bytes of that block are its own.</param>
/// <remarks>
///     ⚠ <b><paramref name="RandomDeviation" /> is not a nicety.</b> An interval alone means every
///     agent spawned in the same frame ticks its service in the same frame for ever, which turns a
///     0.5 s perception update into a spike every thirty frames. The deviation is drawn from the
///     agent's own seeded stream rather than a shared one, so it is the same jitter on every machine.
/// </remarks>
public readonly record struct BehaviorServiceSlot(
    BehaviorService Service,
    int Node,
    float Interval,
    float RandomDeviation,
    int MemoryOffset,
    int MemorySize
);

/// <summary>
///     A compiled behaviour tree: a flat array of nodes, immutable, shared by every agent running it.
/// </summary>
/// <remarks>
///     <para>
///         <b>There is no per-agent field anywhere in here.</b> An agent's state is a block from an
///         <see cref="AgentMemoryPool" />, sized by <see cref="MemorySize" /> and carved into windows
///         by the offsets each node, decorator and service carries. A thousand agents on one tree is
///         a thousand small blocks, all sized at load, and nothing is allocated during a step.
///     </para>
///     <para>
///         ⚠ <b>Unreal has an escape hatch this does not: node instancing</b>, where a node that
///         cannot fit its state in plain memory gets a real object per agent. It exists because
///         Blueprint nodes hold <c>UObject</c> references. Adding it here would mean every node's
///         memory access has two paths for the life of the engine, so a node that needs a reference
///         stores an <see cref="Entity" /> or an <c>AssetId</c> instead — both of which are values.
///     </para>
/// </remarks>
public sealed class BehaviorTreeTemplate {
    readonly BehaviorNode[] nodes;
    readonly BehaviorDecoratorSlot[] decorators;
    readonly BehaviorServiceSlot[] services;
    readonly BlackboardKey[] observedKeys;
    readonly BlackboardKey[] distinctObservedKeys;
    readonly Symbol[] cooldownTags;

    internal BehaviorTreeTemplate(
        Symbol name,
        BehaviorNode[] nodes,
        BehaviorDecoratorSlot[] decorators,
        BehaviorServiceSlot[] services,
        BlackboardKey[] observedKeys,
        BlackboardKey[] distinctObservedKeys,
        Symbol[] cooldownTags,
        BlackboardLayout layout,
        int memorySize,
        int nestedSlotCount
    ) {
        Name = name;
        this.nodes = nodes;
        this.decorators = decorators;
        this.services = services;
        this.observedKeys = observedKeys;
        this.distinctObservedKeys = distinctObservedKeys;
        this.cooldownTags = cooldownTags;
        Layout = layout;
        MemorySize = memorySize;
        NestedSlotCount = nestedSlotCount;
    }

    /// <summary>What the tree is called.</summary>
    public Symbol Name { get; }

    /// <summary>The shape of the blackboard its keys were resolved against.</summary>
    public BlackboardLayout Layout { get; }

    /// <summary>How many bytes one agent running this tree needs.</summary>
    public int MemorySize { get; }

    /// <summary>How many nodes it has.</summary>
    public int Count => nodes.Length;

    /// <summary>How many nested instances an agent running it may need.</summary>
    public int NestedSlotCount { get; }

    /// <summary>The named cooldowns any decorator in this tree reads or starts, once each.</summary>
    /// <remarks>What sizes an agent's cooldown table, so that the table is an array and not a map.</remarks>
    public ReadOnlySpan<Symbol> CooldownTags => cooldownTags;

    /// <summary>Where the "what did each decorator last answer" bitset starts in a memory block.</summary>
    internal int ResultBitsOffset { get; init; }

    /// <summary>Where the "has each decorator ever been asked" bitset starts.</summary>
    /// <remarks>
    ///     Not redundant with <see cref="ResultBitsOffset" />: <i>changed</i> is meaningless until
    ///     there is a previous answer, and without this every decorator would look like it had just
    ///     flipped the first time anything was written.
    /// </remarks>
    internal int EvaluatedBitsOffset { get; init; }

    /// <summary>Where the per-service "when may it run again" timers start.</summary>
    internal int ServiceTimerOffset { get; init; }

    /// <summary>The nodes, in pre-order.</summary>
    public ReadOnlySpan<BehaviorNode> Nodes => nodes;

    /// <summary>Every decorator in the tree, grouped by the node it is attached to.</summary>
    public ReadOnlySpan<BehaviorDecoratorSlot> Decorators => decorators;

    /// <summary>Every service in the tree, grouped by the composite it is attached to.</summary>
    public ReadOnlySpan<BehaviorServiceSlot> Services => services;

    /// <summary>
    ///     The keys any observing decorator reads, once each.
    /// </summary>
    /// <remarks>
    ///     What an instance registers itself on when it starts. Registering the union once rather
    ///     than per branch is deliberate: the scope test is a range comparison done when the change
    ///     is <i>resolved</i>, so registering per branch would buy nothing but churn — see
    ///     <see cref="BehaviorTreeInstance" />.
    /// </remarks>
    public ReadOnlySpan<BlackboardKey> ObservedKeys => distinctObservedKeys;

    /// <summary>The node at an index.</summary>
    /// <param name="index">Its pre-order index.</param>
    public ref readonly BehaviorNode this[int index] => ref nodes[index];

    /// <summary>Whether one node's subtree contains another.</summary>
    /// <param name="ancestor">The subtree's root.</param>
    /// <param name="index">The node to test.</param>
    /// <returns>Whether it is inside.</returns>
    /// <remarks>Two comparisons, and this is the abort test in its entirety.</remarks>
    public bool Contains(int ancestor, int index) =>
        index >= ancestor && index <= nodes[ancestor].LastDescendant;

    /// <summary>The keys one decorator reads.</summary>
    /// <param name="slot">The decorator.</param>
    /// <returns>Its keys.</returns>
    public ReadOnlySpan<BlackboardKey> KeysOf(in BehaviorDecoratorSlot slot) =>
        observedKeys.AsSpan(slot.KeyStart, slot.KeyCount);

    /// <summary>A one-line-per-node dump, for a golden test and for a bug report.</summary>
    /// <returns>The dump.</returns>
    public string Dump() {
        var text = new System.Text.StringBuilder();

        for (var index = 0; index < nodes.Length; index++) {
            ref readonly var node = ref nodes[index];
            var what = node.Kind == BehaviorNodeKind.Composite ? node.Composite.ToString() : "Task";

            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{index,3} {new string(' ', Depth(index) * 2)}{what} {node.Name}")
                .Append(System.Globalization.CultureInfo.InvariantCulture, $" [last {node.LastDescendant}, memory {node.MemoryOffset}+{node.MemorySize}");

            if (node.DecoratorCount > 0) {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $", {node.DecoratorCount} decorator(s)");
            }

            if (node.ServiceCount > 0) {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $", {node.ServiceCount} service(s)");
            }

            text.Append(']').Append('\n');
        }

        return text.ToString();
    }

    int Depth(int index) {
        var depth = 0;

        for (var walk = nodes[index].Parent; walk >= 0; walk = nodes[walk].Parent) {
            depth++;
        }

        return depth;
    }
}
