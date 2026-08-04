// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ai.Perception;

/// <summary>How often one listener senses, given where it is.</summary>
/// <remarks>
///     <para>
///         The second of doc 37 § D15's three bounds, and the one that scales with the population
///         rather than with the level: a broad phase makes each pass cheap, and this decides how many
///         passes there are.
///     </para>
///     <para>
///         ⚠ <b>Why this is not <c>IAgentGovernor</c>, which doc 37 § Part 4 lists distance LOD
///         against.</b> <c>IAgentGovernor.Plan</c> is handed a tick and a population and nothing else
///         — deliberately, because <c>AgentSchedule</c> is eight bytes and a plan that enumerated its
///         agents would allocate once a frame. Distance needs a position per listener, which is a
///         thing that interface cannot see and should not grow. So the distance-LOD row belongs here,
///         where the pass already has every listener's position in hand.
///     </para>
/// </remarks>
public interface IPerceptionGovernor {
    /// <summary>Seconds until this listener's next pass.</summary>
    /// <param name="config">What it senses with.</param>
    /// <param name="distance">
    ///     How far it is from the focus, in metres. <b>Zero when no focus is set</b>, which is what
    ///     makes "nobody told me where the player is" mean full rate rather than the slowest band.
    /// </param>
    /// <returns>The interval, in seconds.</returns>
    float IntervalFor(PerceptionConfig config, float distance);
}

/// <summary>Everybody senses at the configured rate.</summary>
/// <remarks>The default, and what a level with a hundred agents wants: LOD is not free to reason about.</remarks>
public sealed class FixedRateGovernor : IPerceptionGovernor {
    /// <summary>The one there needs to be.</summary>
    public static FixedRateGovernor Instance { get; } = new();

    /// <inheritdoc />
    public float IntervalFor(PerceptionConfig config, float distance) {
        ArgumentNullException.ThrowIfNull(config);

        return config.Interval;
    }
}

/// <summary>Agents far from the focus sense less often.</summary>
/// <remarks>
///     <para>
///         Three bands rather than a curve, because a continuous falloff makes every agent's interval
///         a different number and the jitter that keeps them off each other's frames then has nothing
///         to be jittered around. Three bands and a deviation give a schedule somebody can reason
///         about: near agents are exact, far ones are approximately quarter-rate, and no agent's
///         reaction time is a mystery.
///     </para>
///     <para>
///         With the shipped defaults and a 0.1 s interval, the far band lands on <b>4 Hz</b> — doc 37
///         § D15's number for what an agent behind the player is worth.
///     </para>
///     <para>
///         ⚠ <b>The focus is where the player is, not where the camera is.</b> A cutscene camera that
///         swung across a level would otherwise wake every agent it passed, and a spectator camera in
///         a multiplayer game would give whoever it followed a different AI from everybody else's.
///     </para>
/// </remarks>
public sealed class DistanceLodGovernor : IPerceptionGovernor {
    /// <summary>Inside this, in metres, the configured rate.</summary>
    public float NearRadius { get; init; } = 25f;

    /// <summary>Beyond this, in metres, the slowest rate.</summary>
    public float FarRadius { get; init; } = 60f;

    /// <summary>What to multiply the interval by between the two radii.</summary>
    public float MidScale { get; init; } = 1.5f;

    /// <summary>What to multiply it by beyond <see cref="FarRadius" />.</summary>
    public float FarScale { get; init; } = 2.5f;

    /// <inheritdoc />
    public float IntervalFor(PerceptionConfig config, float distance) {
        ArgumentNullException.ThrowIfNull(config);

        if (distance >= FarRadius) {
            return config.Interval * FarScale;
        }

        return distance >= NearRadius ? config.Interval * MidScale : config.Interval;
    }
}
