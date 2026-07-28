// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Agents;

/// <summary>Another agent, as the one avoiding it sees it.</summary>
/// <param name="Position">Where it is.</param>
/// <param name="Velocity">How it is moving.</param>
/// <param name="Radius">How wide it is.</param>
public readonly record struct AvoidanceNeighbour(Vector3 Position, Vector3 Velocity, float Radius);

/// <summary>How hard an agent tries to do each of the things avoidance is trading off.</summary>
/// <remarks>
///     Properties with initialisers rather than positional parameters with defaults, because
///     <c>new LocalAvoidanceSettings()</c> on a positional record struct calls the struct's own
///     parameterless constructor and zeroes everything — the defaults written beside the parameters
///     would never be applied, and an agent whose avoidance weights are all zero avoids nothing.
///     <see langword="default" /> still means all-zero, which is why <see cref="LocalAvoidance" />
///     treats it as "use the defaults".
/// </remarks>
public readonly record struct LocalAvoidanceSettings {
    /// <summary>The defaults.</summary>
    public LocalAvoidanceSettings() { }

    /// <summary>How far into the future a collision is worth avoiding, in seconds.</summary>
    public float TimeHorizon { get; init; } = 2.5f;

    /// <summary>How many speeds to sample.</summary>
    public int Rings { get; init; } = 3;

    /// <summary>How many directions to sample at each speed.</summary>
    public int Samples { get; init; } = 8;

    /// <summary>How much the agent wants to keep going where it was going.</summary>
    public float DesiredWeight { get; init; } = 2f;

    /// <summary>How much it wants to keep doing what it was doing. Damps dithering.</summary>
    public float CurrentWeight { get; init; } = 0.75f;

    /// <summary>How much it wants to not hit anything.</summary>
    public float TimeWeight { get; init; } = 2.5f;

    /// <summary>
    ///     How much it prefers the side the encounter is already leaning towards. This is what stops
    ///     two agents meeting head-on from both stepping the same way for ever.
    /// </summary>
    public float SideWeight { get; init; } = 0.75f;
}

/// <summary>
///     Picks the velocity that gets closest to what an agent wants without walking into anybody.
/// </summary>
/// <remarks>
///     <para>
///         Reciprocal velocity obstacles, sampled. Candidate velocities are scored by how far they
///         are from the desired one, how far from the current one, and how soon they would end in a
///         collision; the best-scoring candidate wins. The reciprocal part is that each agent assumes
///         the other will take half the responsibility for getting out of the way — the relative
///         velocity used in the collision test is <c>2v - vₐ - v♭</c> — which is what stops two agents
///         from both dodging the whole distance and oscillating.
///     </para>
///     <para>
///         Sampling rather than solving. The exact answer is the boundary of a union of cones and is
///         both harder to compute and no better in practice: the agent re-decides sixty times a
///         second, so a slightly-wrong velocity for one frame is invisible, and a sampler degrades
///         gracefully in the crowded case where the exact answer is "there is no admissible velocity".
///     </para>
///     <para>
///         Stateless, so one instance serves every agent, and nothing here allocates.
///     </para>
/// </remarks>
public sealed class LocalAvoidance {
    /// <summary>Creates an avoidance sampler.</summary>
    /// <param name="settings">The weights and the sample counts.</param>
    public LocalAvoidance(LocalAvoidanceSettings settings = default) =>
        Settings = settings == default ? new() : settings;

    /// <summary>The weights and sample counts in use.</summary>
    public LocalAvoidanceSettings Settings { get; }

    /// <summary>Chooses a velocity.</summary>
    /// <param name="position">Where the agent is.</param>
    /// <param name="radius">How wide it is.</param>
    /// <param name="velocity">How it is moving now.</param>
    /// <param name="desired">How it would like to be moving.</param>
    /// <param name="maxSpeed">The fastest it can go.</param>
    /// <param name="neighbours">Who else is nearby.</param>
    /// <returns>The velocity to use.</returns>
    public Vector3 Sample(
        Vector3 position,
        float radius,
        Vector3 velocity,
        Vector3 desired,
        float maxSpeed,
        ReadOnlySpan<AvoidanceNeighbour> neighbours
    ) {
        if (neighbours.IsEmpty || maxSpeed <= 0) {
            return desired;
        }

        var best = desired;
        var bestPenalty = Penalty(position, radius, velocity, desired, desired, maxSpeed, neighbours);

        // Standing still is deliberately not a candidate. It is very often the lowest-penalty
        // velocity — nothing can be hit at zero speed — and it is a *stable* one: two agents who both
        // choose it are then two stationary obstacles for whom it is still the best answer, and they
        // face each other for ever. Every candidate here moves, so a jam resolves itself even when
        // the resolution is somebody taking the long way round.
        var heading = desired.LengthSquared() > 1e-6f ? desired : velocity;
        var baseAngle = heading.LengthSquared() > 1e-6f ? MathF.Atan2(heading.Z, heading.X) : 0f;
        var step = MathF.Tau / Settings.Samples;

        for (var ring = 1; ring <= Settings.Rings; ring++) {
            var speed = maxSpeed * ring / Settings.Rings;

            for (var sample = 0; sample < Settings.Samples; sample++) {
                // Every other ring is offset by half a step, so the samples do not all lie on the
                // same few directions — the same reason a dither pattern is not a grid.
                var angle = baseAngle + (sample * step) + (ring % 2 == 0 ? step * 0.5f : 0f);
                var candidate = new Vector3(MathF.Cos(angle) * speed, 0f, MathF.Sin(angle) * speed);
                var penalty = Penalty(position, radius, velocity, desired, candidate, maxSpeed, neighbours);

                if (penalty < bestPenalty) {
                    bestPenalty = penalty;
                    best = candidate;
                }
            }
        }

        return best;
    }

    float Penalty(
        Vector3 position,
        float radius,
        Vector3 velocity,
        Vector3 desired,
        Vector3 candidate,
        float maxSpeed,
        ReadOnlySpan<AvoidanceNeighbour> neighbours
    ) {
        var desiredPenalty = Settings.DesiredWeight * (NavGeometry.Distance2D(candidate, desired) / maxSpeed);
        var currentPenalty = Settings.CurrentWeight * (NavGeometry.Distance2D(candidate, velocity) / maxSpeed);

        var soonest = Settings.TimeHorizon;
        var side = 0f;
        var counted = 0;

        foreach (var neighbour in neighbours) {
            // The reciprocal assumption: both agents are expected to move, so the relative velocity
            // this candidate implies is twice it, less both agents' current velocities.
            var relative = (candidate * 2f) - velocity - neighbour.Velocity;

            var towards = neighbour.Position - position;
            var distance = towards.Length();
            var direction = distance > 1e-6f ? towards / distance : Vector3.UnitX;

            // Which way this encounter is already leaning. Passing that way is cheaper than passing
            // the other, and the two agents work it out from the same geometry, so they agree.
            var perpendicular = NavGeometry.Cross2D(direction, neighbour.Velocity - desired) < 0.01f
                ? new Vector3(-direction.Z, 0f, direction.X)
                : new Vector3(direction.Z, 0f, -direction.X);

            side += Math.Clamp(
                MathF.Min((Dot2D(direction, relative) * 0.5f) + 0.5f, Dot2D(perpendicular, relative) * 2f),
                0f,
                1f
            );

            counted++;

            if (!Sweep(position, radius, relative, neighbour, out var enter, out var exit)) {
                continue;
            }

            // Already overlapping. Half the exit time, so that getting out is urgent but not
            // infinitely so — an agent standing inside another one still has to choose a direction.
            if (enter < 0f && exit > 0f) {
                enter = -enter * 0.5f;
            }

            if (enter >= 0f && enter < soonest) {
                soonest = enter;
            }
        }

        var sidePenalty = counted > 0 ? Settings.SideWeight * (side / counted) : 0f;
        var timePenalty = Settings.TimeWeight / (0.1f + (soonest / Settings.TimeHorizon));

        return desiredPenalty + currentPenalty + sidePenalty + timePenalty;
    }

    /// <summary>When two circles moving at a constant relative velocity would touch, and stop touching.</summary>
    static bool Sweep(Vector3 position, float radius, Vector3 relative, in AvoidanceNeighbour neighbour, out float enter, out float exit) {
        enter = 0f;
        exit = 0f;

        var offset = neighbour.Position - position;
        var combined = radius + neighbour.Radius;

        var a = Dot2D(relative, relative);

        if (a < 1e-6f) {
            return false;
        }

        var b = Dot2D(relative, offset);
        var c = Dot2D(offset, offset) - (combined * combined);
        var discriminant = (b * b) - (a * c);

        if (discriminant < 0f) {
            return false;
        }

        var root = MathF.Sqrt(discriminant);
        enter = (b - root) / a;
        exit = (b + root) / a;

        return true;
    }

    static float Dot2D(Vector3 left, Vector3 right) => (left.X * right.X) + (left.Z * right.Z);
}
