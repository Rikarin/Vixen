// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Ai.Diagnostics;

/// <summary>Fills an <see cref="AiAgentSnapshot" /> from a running agent.</summary>
/// <remarks>
///     <para>
///         The three branches doc 37 § D20's table describes, and the only three there are. Everything
///         after this point — the overlay, the editor panel, the remote channel — reads rows and does
///         not know which planner produced them.
///     </para>
///     <para>
///         ⚠ <b>A capture reads live data, so it runs on the thread that owns the agent.</b> It walks
///         a <c>World</c>, a <c>Blackboard</c> and a tree's memory block, and it re-scores a utility
///         set — which is the same rule <c>GoapSnapshot.Take</c> follows, for the same reason. What
///         comes out is strings and numbers and is safe anywhere.
///     </para>
///     <para>
///         ⚠ <b>Nothing here mutates the agent</b>, which is not free to arrange and is the difference
///         between a debugger and a heisenbug. A utility set is scored through
///         <see cref="UtilitySet.Score" />, which takes its state by <c>ref readonly</c>, rather than
///         through <c>Choose</c>, which would advance the decision clock and start cooldowns; a GOAP
///         plan is read rather than re-resolved.
///     </para>
/// </remarks>
public static class AiSnapshots {
    /// <summary>How many rows one list contributes at most, so a captured agent is bounded.</summary>
    /// <remarks>
    ///     A blackboard of two hundred keys drawn over an agent's head is a wall of text, and a
    ///     capture that grew with the content is one a panel cannot lay out. The bound is per section
    ///     rather than overall so that a long board never crowds out the plan.
    /// </remarks>
    public const int MaximumRowsPerSection = 32;

    /// <summary>Takes a picture of one agent.</summary>
    /// <param name="system">The system running it.</param>
    /// <param name="world">The world it lives in.</param>
    /// <param name="entity">The agent.</param>
    /// <param name="into">Where to put it. Cleared first.</param>
    /// <param name="time">The clock, for the re-score. Optional.</param>
    /// <returns>Whether there was an agent to photograph.</returns>
    /// <exception cref="ArgumentNullException">Any of the first three arguments is null.</exception>
    public static bool Take(
        AiSystem system,
        World world,
        Entity entity,
        AiAgentSnapshot into,
        GameTime time = default
    ) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if (!world.IsAlive(entity) || !world.Has<AiAgent>(entity)) {
            return false;
        }

        ref readonly var agent = ref world.Read<AiAgent>(entity);
        var blackboard = system.BlackboardOf(in agent);

        if (blackboard is null) {
            return false;
        }

        into.Entity = entity;
        into.Tick = system.Steps;
        into.Planner = agent.Planner;
        into.Status = agent.Status;
        into.Action = system.Actions.NameOf(agent.Action);

        var context = new AgentContext(
            world,
            entity,
            blackboard,
            system.Shared,
            time,
            agent.Seed,
            system.Actions
        );

        switch (agent.Planner) {
            case AiPlanner.BehaviorTree:
                Tree(system, in agent, into);

                break;

            case AiPlanner.Utility:
                Scored(system, in context, in agent, into);

                break;

            case AiPlanner.Goap:
                Planned(system, in context, in agent, into);

                break;

            default:
                into.Reason = agent.Enabled ? "no planner" : "disabled";

                break;
        }

        Board(blackboard, into);

        return true;
    }

    /// <summary>The active path, node by node, and the last result of every decorator on it.</summary>
    static void Tree(AiSystem system, ref readonly AiAgent agent, AiAgentSnapshot into) {
        if (system.TreeOf(in agent) is not { } instance) {
            return;
        }

        var template = instance.Template;

        into.Asset = template.Name;

        // ⚠ Cleared first, because `Take` filled it from `AiAgent.Action` and a tree agent's copy of
        // that field is never written. Between two tasks — the moment a leaf succeeds and before the
        // root has chosen the next — there is genuinely no running action, and saying so is the
        // point: the alternative is a header that reads `NameOf(0)`, which is an answer.
        into.Action = Symbol.None;

        Span<int> path = stackalloc int[MaximumRowsPerSection];
        var depth = instance.ActivePath(path);

        if (depth == 0) {
            into.Reason = instance.Overran
                ? "the last step ran out of transitions"
                : $"nothing running; last result {instance.LastResult}";
        }

        for (var index = 0; index < depth; index++) {
            var node = path[index];
            ref readonly var record = ref template[node];

            // ⚠ **The headline action comes from the live leaf, because a tree agent's
            // `AiAgent.Action` is never written.** `Advance` hands a behaviour-tree agent to
            // `BehaviorTreeInstance.Step` and returns before the field the other two planners
            // maintain — quite correctly, since the tree owns which task is running. But `Take`
            // sets `into.Action` from that field for every planner, so the overlay's and the
            // panel's "what is it doing" read `NameOf(0)` for *every* tree agent in the world:
            // whichever action happens to be registered first, presented as a fact.
            //
            // That is doc 37 § P6's trap — a planner that has chosen nothing must run nothing —
            // in its reporting form, and it survived because zero is a valid registry index and
            // the answer therefore always looked like an answer. P7's overlay test asserts a
            // readout for a *utility* agent, so no test had ever read this field off a tree.
            if (index == depth - 1 && record.Kind == BehaviorNodeKind.Task) {
                into.Action = system.Actions.NameOf(record.Action);
            }

            into.Add(
                new(
                    AiDebugSection.Doing,
                    record.Name.ToString(),
                    record.Kind.ToString(),
                    node,
                    index == depth - 1
                )
            );

            // ⚠ The decorators of the path and not of the tree. "Why is this branch the live one" is
            // answered by the gates it got through, and a tree's other four hundred decorators are
            // noise that pushes the answer off the bottom of the panel.
            for (var slot = record.DecoratorStart; slot < record.DecoratorStart + record.DecoratorCount; slot++) {
                var passed = instance.DecoratorPassed(slot);

                into.Add(
                    new(
                        AiDebugSection.Why,
                        Named(template.Decorators[slot].Decorator),
                        passed ? "passes" : "fails",
                        passed ? 1f : 0f,
                        passed
                    )
                );
            }
        }
    }

    /// <summary>Every candidate's score, and the chosen one's considerations factor by factor.</summary>
    static void Scored(AiSystem system, ref readonly AgentContext context, ref readonly AiAgent agent, AiAgentSnapshot into) {
        if (system.ScoringOf(in agent) is not { } memory || agent.Asset >= system.Sets.Count) {
            return;
        }

        var set = system.Sets[agent.Asset];

        into.Asset = set.Name;
        memory.Fit(set.Count);

        Span<float> scores = set.Count <= 64 ? stackalloc float[64] : new float[set.Count];

        set.Score(in context, in memory.State, memory.Cooldowns, scores);

        var current = memory.State.Current;
        var rows = Math.Min(set.Count, MaximumRowsPerSection);

        for (var index = 0; index < rows; index++) {
            into.Add(
                AiDebugRow.Of(AiDebugSection.Doing, set[index].Name.ToString(), scores[index], index == current)
            );
        }

        if (current < 0 || current >= set.Count) {
            into.Reason = "nothing scored above zero";

            return;
        }

        var chosen = set[current];

        into.Reason = string.Create(
            CultureInfo.InvariantCulture,
            $"{chosen.Name} at {scores[current]:0.###} after {memory.Decisions} decision(s)"
        );

        Span<float> detail = chosen.Considerations.Length <= 32
            ? stackalloc float[32]
            : new float[chosen.Considerations.Length];

        chosen.Score(in context, detail);

        for (var index = 0; index < Math.Min(chosen.Considerations.Length, MaximumRowsPerSection); index++) {
            // ⚠ Zero is the interesting one and it is why the factors are shown at all: the mean is a
            // product, so one zero vetoes the action, and "why is this scoring nothing" has exactly
            // one answer that a table of factors gives instantly.
            //
            // ⚠ And the row carries the *reading* as its number where its text is the curved score,
            // because those are two different answers: "the danger key says 0.8" and "which puts this
            // action at 0.95". The editor's curve needs the first to say where on the shape the agent
            // is sitting, which is doc 37 § Part 5's whole point. It costs a second read of the input
            // per consideration, paid only by whoever asked for the detail.
            into.Add(
                new(
                    AiDebugSection.Why,
                    chosen.Considerations[index].Name.ToString(),
                    detail[index].ToString("0.###", CultureInfo.InvariantCulture),
                    chosen.Considerations[index].Input.Read(in context),
                    detail[index] > 0f
                )
            );
        }
    }

    /// <summary>The goal, the plan and where in it, and the conditions still unmet.</summary>
    static void Planned(AiSystem system, ref readonly AgentContext context, ref readonly AiAgent agent, AiAgentSnapshot into) {
        if (system.PlanningOf(in agent) is not { } memory || agent.Asset >= system.Domains.Count) {
            return;
        }

        var domain = system.Domains[agent.Asset];
        var plan = memory.Plan;

        into.Asset = domain.Name;
        into.Reason = plan.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{plan.Goal} in {plan.Count} step(s), cost {plan.Cost:0.##}")
            : plan.Failure == PlanFailure.None
                ? memory.Pending.IsNull ? "no plan" : "thinking"
                : $"{plan.Failure} for {plan.Goal}";

        var steps = plan.Steps;

        for (var index = 0; index < Math.Min(steps.Length, MaximumRowsPerSection); index++) {
            into.Add(
                new(
                    AiDebugSection.Doing,
                    domain[steps[index]].Name.ToString(),
                    index == 0 ? "running" : "planned",
                    index,
                    index == 0
                )
            );
        }

        // The world as the planner would read it now, plus which of the head's conditions that world
        // does not satisfy — which is the difference between "the plan is stale" and "the plan is
        // fine and the action is slow".
        Span<int> keys = domain.Keys.Count <= 64 ? stackalloc int[64] : new int[domain.Keys.Count];

        domain.Keys.Project(in context, keys);

        for (var index = 0; index < Math.Min(domain.Keys.Count, MaximumRowsPerSection); index++) {
            into.Add(
                AiDebugRow.Of(AiDebugSection.Data, domain.Keys[new((ushort)index)].Name.ToString(), keys[index])
            );
        }

        var head = plan.Head;

        if (head < 0) {
            return;
        }

        foreach (var condition in domain[head].Conditions) {
            var holds = condition.Holds(keys[..domain.Keys.Count]);

            into.Add(
                new(
                    AiDebugSection.Why,
                    domain.Keys.NameOf(condition.Key).ToString(),
                    $"{Symbolic(condition.Comparison)} {condition.Value.ToString(CultureInfo.InvariantCulture)}",
                    keys[condition.Key.Index],
                    holds
                )
            );
        }
    }

    /// <summary>Every key that is set, with its value.</summary>
    /// <remarks>
    ///     ⚠ <b>Set keys only, and that is not a saving.</b> An unset key and a key holding zero are
    ///     different states and the commonest AI bug there is — a sensor that never ran — looks
    ///     exactly like the second when it is the first. A row that said <c>0</c> for both would hide
    ///     the very thing somebody opened the panel to find.
    /// </remarks>
    static void Board(Blackboard blackboard, AiAgentSnapshot into) {
        var layout = blackboard.Layout;
        var shown = 0;

        for (var index = 0; index < layout.Count && shown < MaximumRowsPerSection; index++) {
            var key = new BlackboardKey((ushort)index);

            if (!blackboard.IsSet(key)) {
                continue;
            }

            var definition = layout[key];

            into.Add(
                AiDebugRow.Of(AiDebugSection.Data, definition.Name.ToString(), Value(blackboard, key, definition.Type))
            );

            shown++;
        }
    }

    static string Value(Blackboard blackboard, BlackboardKey key, BlackboardValueType type) => type switch {
        BlackboardValueType.Bool => blackboard.GetBool(key) ? "true" : "false",
        BlackboardValueType.Int => blackboard.GetInt(key).ToString(CultureInfo.InvariantCulture),
        BlackboardValueType.Float => blackboard.GetFloat(key).ToString("0.###", CultureInfo.InvariantCulture),
        BlackboardValueType.Vector3 => blackboard.GetVector3(key).ToString(),
        BlackboardValueType.Entity => blackboard.GetEntity(key).ToString(),
        _ => blackboard.GetSymbol(key).ToString()
    };

    static string Symbolic(GoapComparison comparison) => comparison switch {
        GoapComparison.Less => "<",
        GoapComparison.LessOrEqual => "≤",
        GoapComparison.Greater => ">",
        _ => "≥"
    };

    /// <summary>A decorator's name, which it does not carry, so its type answers for it.</summary>
    /// <remarks>
    ///     ⚠ <b>A <see cref="BehaviorDecorator" /> has no name field and is not getting one.</b> A
    ///     name would be a string per decorator per template, paid by every agent in the game so that
    ///     a panel nobody has open can read better; the type is already there and says the same thing.
    /// </remarks>
    static string Named(BehaviorDecorator decorator) {
        var name = decorator.GetType().Name;

        return name.EndsWith("Decorator", StringComparison.Ordinal)
            ? name[..^"Decorator".Length]
            : name;
    }
}
