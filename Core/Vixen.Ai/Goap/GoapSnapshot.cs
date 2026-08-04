// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Ai;

/// <summary>Everything one resolve needs, taken off the world in one go.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The search reads this and never the world, and that is what makes a resolve a job.</b>
///         doc 37 § D16 puts GOAP resolves on jobs because they are expensive and unbounded — and a
///         search that reached into a <c>World</c> or a <c>Blackboard</c> from a worker thread would be
///         a data race the scheduler cannot see. So the world keys are projected, the targets are
///         sensed and the costs are computed at <i>submit</i>, on the thread that owns the agent, and
///         what crosses to the job is a few arrays of numbers.
///     </para>
///     <para>
///         It is also what doc 37 § D10 means by the graph being built once: what is per agent is the
///         condition evaluations and the costs, and both of them are here.
///     </para>
///     <para>
///         ⚠ <b>A snapshot is a moment, and a plan made from it can be stale by the time it lands.</b>
///         That is the trade a queue makes and it is why § D11 commits only the head: the head's
///         conditions are re-checked against the live world before it starts.
///     </para>
/// </remarks>
public sealed class GoapSnapshot {
    /// <summary>Creates a snapshot sized for a domain.</summary>
    /// <param name="domain">The domain it will be taken against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="domain" /> is null.</exception>
    public GoapSnapshot(GoapDomain domain) {
        ArgumentNullException.ThrowIfNull(domain);

        Domain = domain;
        World = new int[Math.Max(1, domain.Keys.Count)];
        Costs = new float[Math.Max(1, domain.Count)];
        Targets = new GoapTarget[Math.Max(1, domain.Count)];
    }

    /// <summary>The domain it was taken against.</summary>
    public GoapDomain Domain { get; }

    /// <summary>The projected world keys.</summary>
    public int[] World { get; }

    /// <summary>What each action costs, worked out where the agent was standing.</summary>
    public float[] Costs { get; }

    /// <summary>Where each action would happen.</summary>
    public GoapTarget[] Targets { get; }

    /// <summary>Which goal to plan for.</summary>
    public int Goal { get; internal set; } = -1;

    /// <summary>Which actions this agent may use.</summary>
    public GoapCapabilities Capabilities { get; internal set; } = GoapCapabilities.All;

    /// <summary>Where the agent was.</summary>
    public Vector3 Position { get; internal set; }

    /// <summary>Takes everything a resolve needs off the world.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="goal">Which goal, or <c>-1</c> to pick the highest-priority unmet one.</param>
    /// <param name="costs">What an action costs, or null for the straight-line model.</param>
    /// <param name="sensors">Where actions happen, or null for nowhere.</param>
    /// <param name="capabilities">Which actions this agent may use.</param>
    /// <param name="distanceCost">What a metre adds, for the default cost model.</param>
    /// <returns>Whether there is anything to plan for.</returns>
    public bool Take(
        in AgentContext context,
        int goal = -1,
        IActionCostModel? costs = null,
        GoapTargetSensors? sensors = null,
        GoapCapabilities capabilities = default,
        float distanceCost = 0.1f
    ) {
        Domain.Keys.Project(in context, World);

        Capabilities = capabilities == default ? GoapCapabilities.All : capabilities;
        Position = sensors?.Where(in context) ?? Vector3.Zero;
        Goal = goal >= 0 ? goal : Wanted();

        var model = costs ?? ActionCostModels.StraightLine(distanceCost);

        for (var index = 0; index < Domain.Count; index++) {
            var action = Domain[index];

            Targets[index] = sensors is not null
                && action.Target.IsSome
                && sensors.TryResolve(action.Target, in context, out var position, out var entity)
                    ? new(true, position, entity)
                    : GoapTarget.None;

            Costs[index] = model.Cost(in context, action, Position, in Targets[index]);
        }

        return Goal >= 0;
    }

    /// <summary>The highest-priority goal that is not already true.</summary>
    /// <returns>Its index, or <c>-1</c> when everything is satisfied.</returns>
    /// <remarks>
    ///     ⚠ Priority breaks the tie, and an <i>already met</i> goal is not a candidate at all. An
    ///     implementation that planned for the highest-priority goal regardless would produce an
    ///     agent that keeps eating because "not hungry" is its most important goal.
    /// </remarks>
    public int Wanted() {
        var chosen = -1;
        var priority = int.MinValue;

        for (var index = 0; index < Domain.Goals.Length; index++) {
            var goal = Domain.Goals[index];

            if (!goal.Met(World) && goal.Priority > priority) {
                chosen = index;
                priority = goal.Priority;
            }
        }

        return chosen;
    }

    /// <summary>What a world key read when the snapshot was taken.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its value, or zero.</returns>
    public int Read(GoapWorldKey key) => key.IsValid && key.Index < World.Length ? World[key.Index] : 0;
}
