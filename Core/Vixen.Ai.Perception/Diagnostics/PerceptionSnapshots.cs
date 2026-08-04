// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai.Diagnostics;
using Vixen.Ai.Perception.Ecs;
using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Ai.Perception.Diagnostics;

/// <summary>Adds what an agent can sense to a snapshot of what it is thinking.</summary>
/// <remarks>
///     <para>
///         Doc 37 § D20's fourth row — <i>its senses</i> — which is the same for all three planners
///         and is therefore not any of their business. It lives here rather than in
///         <c>AiSnapshots</c> because a perceived list is <c>Vixen.Ai.Perception</c>'s, and
///         <c>Vixen.Ai</c> refuses to know that perception exists: a game running behaviour trees
///         with no physics world links one assembly and stops.
///     </para>
///     <para>
///         ⚠ <b>A separate call rather than a hook.</b> The alternative is an interface on
///         <c>AiSnapshots</c> that perception registers into, which is a seam that would have exactly
///         one implementation for ever — and doc 34 § P9's rule is that a seam with one implementation
///         is a guess. A caller that has a <c>PerceptionSystem</c> makes two calls.
///     </para>
/// </remarks>
public static class PerceptionSnapshots {
    /// <summary>How many perceived targets one snapshot carries.</summary>
    public const int MaximumTargets = 16;

    /// <summary>Adds an agent's perceived list to a snapshot.</summary>
    /// <param name="perception">The system that senses for it.</param>
    /// <param name="world">The world it lives in.</param>
    /// <param name="entity">The agent.</param>
    /// <param name="into">The snapshot to add to.</param>
    /// <returns>How many rows were added.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static int Add(PerceptionSystem perception, World world, Entity entity, AiAgentSnapshot into) {
        ArgumentNullException.ThrowIfNull(perception);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(into);

        if (perception.PerceivedBy(world, entity) is not { } perceived) {
            return 0;
        }

        var now = perception.Clock;
        var added = 0;

        foreach (var target in perceived.Targets) {
            if (added == MaximumTargets) {
                break;
            }

            // ⚠ The age is the number, not the strength. "When did I last actually see it" is the
            // question a stuck agent is answered by — a guard walking to a corner it saw somebody in
            // eight seconds ago is behaving correctly and looks broken, and the age is what says so.
            into.Add(
                new(
                    AiDebugSection.Senses,
                    target.Source.ToString(),
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{target.Sense}, {(target.Current ? "current" : "remembered")}, {target.AgeAt(now):0.#} s"
                    ),
                    target.AgeAt(now),
                    target.Current
                )
            );

            added++;
        }

        return added;
    }

    /// <summary>Whether an agent senses at all, and how it is configured, as one row.</summary>
    /// <param name="perception">The system that senses for it.</param>
    /// <param name="world">The world it lives in.</param>
    /// <param name="entity">The agent.</param>
    /// <param name="into">The snapshot to add to.</param>
    /// <returns>Whether a row was added.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static bool Describe(PerceptionSystem perception, World world, Entity entity, AiAgentSnapshot into) {
        ArgumentNullException.ThrowIfNull(perception);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(into);

        if (!world.IsAlive(entity) || !world.Has<AiPerception>(entity)) {
            return false;
        }

        ref readonly var listener = ref world.Read<AiPerception>(entity);
        var config = listener.Config < perception.Configs.Count ? perception.Configs[listener.Config] : null;

        into.Add(
            AiDebugRow.Of(
                AiDebugSection.Senses,
                "perception",
                config is null
                    ? "no config"
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"team {listener.Team}, every {config.Interval:0.##} s, remembers {config.Memory:0.#} s"
                    ),
                listener.Enabled
            )
        );

        return true;
    }
}
