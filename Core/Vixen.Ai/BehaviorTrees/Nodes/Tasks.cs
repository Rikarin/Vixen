// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Ai.Diagnostics;
using Vixen.Core;

namespace Vixen.Ai;

/// <summary>How the world reaches one blackboard key.</summary>
/// <remarks>
///     <para>
///         doc 37 § D13's local world sensor. It is the seam a project replaces to say "how hungry am
///         I", "how much ammo is left", "how far is the leash" — and it has two front ends onto it:
///         <see cref="UpdateBlackboardService" /> runs it on a tree's schedule, and P6's GOAP runs
///         the same object with no tree anywhere.
///     </para>
///     <para>
///         Local means <i>per agent</i>. The global form — one query per agent type per pass, for
///         "is it night" — lands with GOAP, which is the phase that has a type to hang it on.
///     </para>
/// </remarks>
public interface IWorldSensor {
    /// <summary>Reads the world and writes what it found.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="blackboard">Where to write.</param>
    /// <param name="key">Which key to write.</param>
    void Sense(in AgentContext context, Blackboard blackboard, BlackboardKey key);
}

/// <summary>A task that runs a tree of its own, and therefore needs a slot to keep it in.</summary>
/// <remarks>
///     What <c>BehaviorTreeCompiler</c> looks for to decide whether a node needs a nested-instance
///     slot. It is an interface rather than a flag on the node because the decision belongs to the
///     task — a project's own "run the tree named by this key" task wants the same slot.
/// </remarks>
public interface INestedTreeTask {
    /// <summary>Which tree to run, given the agent.</summary>
    /// <param name="context">The agent.</param>
    /// <returns>The tree, or null when the key names nothing.</returns>
    BehaviorTreeTemplate? Resolve(in AgentContext context);
}

/// <summary>Waits a fixed number of seconds.</summary>
public sealed class WaitTask(float seconds) : IAgentAction {
    /// <summary>How many bytes it needs. Registered with the action, per doc 37 § D3.</summary>
    public static int StateSize => Unsafe.SizeOf<float>();

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        ref var waited = ref MemoryMarshal.AsRef<float>(state);

        waited += delta;

        return waited >= seconds ? ActionStatus.Succeeded : ActionStatus.Running;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>Waits for however long a key says, with an optional random deviation.</summary>
public sealed class WaitBlackboardTimeTask(BlackboardKey key, float randomDeviation = 0f) : IAgentAction {
    /// <summary>How many bytes it needs.</summary>
    public static int StateSize => Unsafe.SizeOf<TimerState>();

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) {
        ref var timer = ref MemoryMarshal.AsRef<TimerState>(state);

        timer.Stamp = 0f;
        timer.Count = 0;
    }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        ref var timer = ref MemoryMarshal.AsRef<TimerState>(state);
        var target = context.Blackboard.IsSet(key) ? context.Blackboard.GetFloat(key) : 0f;

        if (randomDeviation > 0f) {
            target += (context.Random(0x77 ^ (uint)key.Index) - 0.5f) * 2f * randomDeviation;
        }

        timer.Stamp += delta;

        return timer.Stamp >= target ? ActionStatus.Succeeded : ActionStatus.Running;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>Succeeds or fails at once. The branch terminator.</summary>
/// <remarks>
///     Two of these are what most trees actually need for "and then stop trying" and "this branch is
///     a dead end" — and having a node say so is what stops authors reaching for an inverter on an
///     empty sequence to mean the same thing.
/// </remarks>
public sealed class FinishWithTask(ActionStatus result) : IAgentAction {
    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) =>
        result == ActionStatus.Running ? ActionStatus.Succeeded : result;

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>Narrates the tree into the debug record, so a headless run can be read afterwards.</summary>
/// <remarks>
///     ⚠ <b>Into the recorder, not into a log sink.</b> doc 37 § D20 has one debug surface for all
///     three planners and P7 builds the visual log over the same records — so a task that wrote
///     somewhere else would be a second place to look. It costs nothing when the recorder is off,
///     which it is by default.
/// </remarks>
public sealed class LogTask(Symbol message, AiPlanner planner = AiPlanner.BehaviorTree) : IAgentAction {
    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        context.Debug?.Record(
            new(context.Entity, context.Time.FrameCount, planner, message, ActionStatus.Succeeded, 0, 1, message, 0f)
        );

        return ActionStatus.Succeeded;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>Runs the tree a key names, as a task of the tree it is in.</summary>
/// <remarks>
///     <para>
///         The dynamic half of doc 37 § Part 3's subtree pair. The <i>static</i> half is not a task at
///         all at run time — <c>BehaviorTreeCompiler</c> splices a named subtree into the parent's
///         flat array, so pre-order still equals priority across the boundary and a decorator above
///         can abort a branch below.
///     </para>
///     <para>
///         ⚠ <b>This one cannot be spliced, and pays for it.</b> A tree named by a key is not known
///         until the agent runs, so it gets an instance of its own: its own memory block, its own
///         observers, its own active node. The parent tree can abort the whole of it and can see
///         nothing inside it, which is the honest cost of choosing a tree at run time.
///     </para>
/// </remarks>
public sealed class RunSubtreeDynamicTask(BlackboardKey key, BehaviorTreeLibrary library)
    : IAgentAction, INestedTreeTask {
    /// <summary>How many bytes it needs: the node it was started on.</summary>
    public static int StateSize => Unsafe.SizeOf<int>();

    /// <inheritdoc />
    public BehaviorTreeTemplate? Resolve(in AgentContext context) =>
        context.Blackboard.IsSet(key) && library.TryGet(context.Blackboard.GetSymbol(key), out var template)
            ? template
            : null;

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) => MemoryMarshal.AsRef<int>(state) = -1;

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        var child = Resolve(in context);

        if (child is null) {
            return ActionStatus.Failed;
        }

        // The node index is not on the context — a task takes AgentContext so that one object serves
        // all three planners — so the tree stashes it before the task runs. See BehaviorTreeInstance.
        var instance = context.RunningTree?.Nested(context.RunningNode, child, in context);

        if (instance is null) {
            return ActionStatus.Failed;
        }

        var status = instance.Step(in context, delta);

        return status;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) {
        var child = Resolve(in context);

        if (child is not null) {
            context.RunningTree?.Nested(context.RunningNode, child, in context)?.Abort(in context);
        }
    }
}

/// <summary>Trees by name, so that a key can name one.</summary>
/// <remarks>
///     Deliberately small: a dictionary and a list. What a game actually loads trees through is P2's
///     asset pipeline; this is what a dynamic subtree, a spawner and a test resolve a name against in
///     the meantime, and what the pipeline will fill.
/// </remarks>
public sealed class BehaviorTreeLibrary {
    readonly Dictionary<Symbol, BehaviorTreeTemplate> byName = [];
    readonly List<BehaviorTreeTemplate> ordered = [];

    /// <summary>How many trees it holds.</summary>
    public int Count => ordered.Count;

    /// <summary>The tree at an index, which is what an <c>AiAgent</c> names.</summary>
    /// <param name="index">Its index.</param>
    public BehaviorTreeTemplate this[int index] => ordered[index];

    /// <summary>Adds a tree.</summary>
    /// <param name="template">The tree.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="template" /> is null.</exception>
    /// <exception cref="InvalidOperationException">A tree of that name is already in it.</exception>
    public int Add(BehaviorTreeTemplate template) {
        ArgumentNullException.ThrowIfNull(template);

        if (!byName.TryAdd(template.Name, template)) {
            throw new InvalidOperationException($"'{template.Name}' is already in this library.");
        }

        ordered.Add(template);

        return ordered.Count - 1;
    }

    /// <summary>Looks a tree up by name.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="template">Where to put it.</param>
    /// <returns>Whether the library has it.</returns>
    public bool TryGet(Symbol name, out BehaviorTreeTemplate? template) => byName.TryGetValue(name, out template);

    /// <summary>Looks a tree's index up by name.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Its index, or <c>-1</c>.</returns>
    public int IndexOf(Symbol name) => byName.TryGetValue(name, out var template) ? ordered.IndexOf(template) : -1;
}
