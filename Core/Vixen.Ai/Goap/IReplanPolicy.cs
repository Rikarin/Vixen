// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ai;

/// <summary>What an agent's planning looks like right now.</summary>
/// <param name="HasPlan">Whether it has a plan with anything left in it.</param>
/// <param name="Finished">Whether the step it was running has just ended.</param>
/// <param name="Failed">Whether that step failed, as opposed to succeeding.</param>
/// <param name="Elapsed">Seconds since the plan was made.</param>
/// <param name="Remaining">How many steps are left, the running one included.</param>
/// <param name="Asked">Whether something explicitly asked for a re-plan.</param>
public readonly record struct ReplanContext(
    bool HasPlan,
    bool Finished,
    bool Failed,
    float Elapsed,
    int Remaining,
    bool Asked
);

/// <summary>When an agent thinks again.</summary>
/// <remarks>
///     doc 37 § D11's seam, with crashkonijn's three controller shapes as the shipped
///     implementations. ⚠ <b>An agent that re-plans every frame is a search per agent per frame</b>,
///     which is the cost this whole subsystem is arranged to avoid; one that never re-plans walks into
///     a door that closed after the plan was made.
/// </remarks>
public interface IReplanPolicy {
    /// <summary>Whether to ask for a new plan.</summary>
    /// <param name="context">How the agent's planning is going.</param>
    /// <returns>Whether to resolve.</returns>
    bool ShouldReplan(in ReplanContext context);
}

/// <summary>The policies that ship.</summary>
public static class ReplanPolicies {
    /// <summary>Re-plan when there is nothing to do, or the step ended.</summary>
    /// <remarks>The default, and the cheapest thing that behaves sensibly.</remarks>
    public static IReplanPolicy Reactive { get; } = new ReactiveReplanPolicy();

    /// <summary>The same, and on an interval as well, so a better plan can be discovered.</summary>
    /// <param name="interval">How often, in seconds.</param>
    /// <returns>The policy.</returns>
    /// <remarks>
    ///     ⚠ What "a better plan" costs is a resolve per agent per interval, whatever else is
    ///     happening. It is the right default for a simulation and the wrong one for a fight.
    /// </remarks>
    public static IReplanPolicy Proactive(float interval = 2f) => new ProactiveReplanPolicy(interval);

    /// <summary>Only when something asks.</summary>
    /// <remarks>
    ///     For a game that knows when its world changed — a door opened, an item was picked up — and
    ///     would rather say so than have every agent poll for it.
    /// </remarks>
    public static IReplanPolicy Manual { get; } = new ManualReplanPolicy();
}

sealed class ReactiveReplanPolicy : IReplanPolicy {
    public bool ShouldReplan(in ReplanContext context) =>
        context.Asked || !context.HasPlan || context.Finished || context.Failed;
}

sealed class ProactiveReplanPolicy(float interval) : IReplanPolicy {
    public bool ShouldReplan(in ReplanContext context) =>
        context.Asked
        || !context.HasPlan
        || context.Finished
        || context.Failed
        || context.Elapsed >= MathF.Max(0.05f, interval);
}

sealed class ManualReplanPolicy : IReplanPolicy {
    // ⚠ Still re-plans when there is no plan at all. "Manual" means the game decides when to think
    // again, not that an agent with nothing to do stands there for ever waiting to be told.
    public bool ShouldReplan(in ReplanContext context) => context.Asked || !context.HasPlan;
}

/// <summary>What one agent remembers about its planning between frames.</summary>
/// <remarks>
///     The managed half, beside the agent's slot the way a <c>BehaviorTreeInstance</c> and a
///     <c>UtilityMemory</c> are. A plan is a list and a request is a ticket, and neither goes in a
///     chunk column.
/// </remarks>
public sealed class GoapMemory {
    /// <summary>The plan, of which only the head is committed.</summary>
    public GoapPlan Plan { get; } = new();

    /// <summary>The resolve that has been asked for, or <see cref="GoapPlanRequest.Null" />.</summary>
    public GoapPlanRequest Pending { get; internal set; } = GoapPlanRequest.Null;

    /// <summary>Seconds since the plan was made.</summary>
    public float Elapsed { get; internal set; }

    /// <summary>Whether something asked for a re-plan.</summary>
    public bool Asked { get; set; }

    /// <summary>Whether the running step has just ended.</summary>
    public bool Finished { get; internal set; }

    /// <summary>Whether it ended by failing.</summary>
    public bool Failed { get; internal set; }

    /// <summary>Which action of the domain is running, or <c>-1</c>.</summary>
    public int Current { get; internal set; } = -1;

    /// <summary>How many plans it has had. Reads well in a log.</summary>
    public int Plans { get; internal set; }

    /// <summary>Forgets everything, which is what a recycled agent slot needs.</summary>
    public void Reset() {
        Plan.Clear();
        Pending = GoapPlanRequest.Null;
        Elapsed = 0f;
        Asked = false;
        Finished = false;
        Failed = false;
        Current = -1;
        Plans = 0;
    }

    /// <summary>How its planning is going, as a policy is asked about it.</summary>
    /// <returns>The context.</returns>
    public ReplanContext Describe() => new(Plan.Count > 0, Finished, Failed, Elapsed, Plan.Count, Asked);
}
