// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Ai;

/// <summary>Runs a utility set as a behaviour-tree task, until something interrupts it.</summary>
/// <param name="set">The set to score.</param>
/// <param name="actions">Where its actions are registered, for their state sizes.</param>
/// <remarks>
///     <para>
///         doc 37 § D2 is what makes this possible, and it is the payoff for that decision: a tree
///         handles the parts of a character that are a <i>procedure</i> — patrol, then investigate,
///         then return — and a utility set handles the part that is a <i>judgement</i>, which is
///         everything the character does once a fight starts. Neither technique is asked to be the
///         other, and both choose from the same action registry.
///     </para>
///     <para>
///         ⚠ <b>It never finishes on its own.</b> A set is a standing judgement rather than a
///         procedure with an end, so this stays <c>Running</c> and is meant to be aborted by a
///         decorator above it — which is the same shape as a <c>Patrol</c> under a perception
///         decorator. It fails only when the set has nothing to say at all, so a branch whose whole
///         set is vetoed gives way rather than standing there.
///     </para>
///     <para>
///         ⚠ <b>Everything lives in the span, including the sub-action's own state.</b> The layout is
///         the header, then a cooldown stamp per action, then the widest action's block — which is
///         why <see cref="RequiredState" /> is an instance property. An action never owns its state,
///         and an action that runs other actions owns theirs even less.
///     </para>
/// </remarks>
public sealed class RunUtilitySetTask(UtilitySet set, AgentActionRegistry actions) : IAgentAction {
    readonly UtilitySet set = set ?? throw new ArgumentNullException(nameof(set));
    readonly AgentActionRegistry actions = actions ?? throw new ArgumentNullException(nameof(actions));

    /// <summary>How many bytes this task needs, for the set it was given.</summary>
    /// <remarks>
    ///     ⚠ An instance property and not a static one, because the size depends on the set — how many
    ///     actions it has and how large the widest of them is. Every other task in the library has a
    ///     static size, and this is the one that cannot.
    /// </remarks>
    public int RequiredState => set.RequiredState(Widest());

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) {
        if (state.Length >= UtilitySet.HeaderSize) {
            // Explicitly, rather than relying on the span being zeroed: a zeroed header says "action
            // zero is running", which would skip that action's own Start.
            MemoryMarshal.AsRef<UtilityState>(state) = UtilityState.Fresh;
        }
    }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        if (state.Length < RequiredState) {
            return ActionStatus.Failed;
        }

        ref var header = ref MemoryMarshal.AsRef<UtilityState>(state);
        var cooldowns = MemoryMarshal.Cast<byte, float>(state.Slice(UtilitySet.HeaderSize, set.Count * sizeof(float)));
        var inner = state[(UtilitySet.HeaderSize + (set.Count * sizeof(float)))..];
        var running = header.Current;

        Span<float> scores = set.Count <= 32 ? stackalloc float[32] : new float[set.Count];

        var chosen = set.Choose(in context, ref header, cooldowns, delta, scores);

        if (chosen < 0) {
            return ActionStatus.Failed;
        }

        var action = actions[set[chosen].Action];

        if (chosen != running) {
            if (running >= 0) {
                actions[set[running].Action].Abort(in context, inner);
            }

            inner.Clear();
            action.Start(in context, inner);
        }

        var status = action.Tick(in context, inner, delta);

        if (status != ActionStatus.Running) {
            // The chosen action is over, so the set is asked again on the next tick rather than at the
            // next interval — and the task itself keeps running, because the set has not finished.
            set.Finished(ref header, cooldowns);
        }

        return ActionStatus.Running;
    }

    /// <inheritdoc />
    /// <remarks>Whatever the set had chosen is aborted too, or a branch that lost gets to keep walking.</remarks>
    public void Abort(in AgentContext context, Span<byte> state) {
        if (state.Length < RequiredState) {
            return;
        }

        ref var header = ref MemoryMarshal.AsRef<UtilityState>(state);

        if (header.Current >= 0) {
            var inner = state[(UtilitySet.HeaderSize + (set.Count * sizeof(float)))..];

            actions[set[header.Current].Action].Abort(in context, inner);
        }

        header = UtilityState.Fresh;
    }

    int Widest() {
        var widest = 0;

        foreach (var action in set.Actions) {
            widest = Math.Max(widest, actions.StateSize(action.Action));
        }

        return widest;
    }
}
