// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Perception.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;

namespace Vixen.Samples.AiVillage;

/// <summary>Where the intruder is, as a function of how long the village has been running.</summary>
/// <remarks>
///     <para>
///         <b>The only thing in this sample that moves without deciding to, and that is what makes
///         it evidence.</b> Everything else — the guard leaving its beat, the villager running for
///         the refuge, the guard going back to its beat — is a consequence of where this is. A
///         sample that drove the agents would prove that the agents can be driven.
///     </para>
///     <para>
///         ⚠ <b>A pure function of elapsed seconds, and the caller accumulates the seconds.</b>
///         <c>Samples/13</c>'s <c>ScriptedWalk</c> records why: a script that reads a clock walks a
///         different path on a slower machine, and the test that catches it is <i>"wall time passing
///         does not advance the script"</i> — hand it a delta of zero after sleeping and nothing may
///         move. There is no <c>Stopwatch</c> here and no <c>DateTime</c>.
///     </para>
///     <para>
///         ⚠ <b>And the accumulation is a <c>double</c>.</b> Sixty single-precision additions of
///         <c>1f / 60f</c> come to 0.99999994, which over six hundred frames is a whole frame of
///         drift — enough to move a waypoint boundary and change which action a log line records.
///     </para>
/// </remarks>
public static class Intrusion {
    /// <summary>Outside the village, past everybody's sight radius.</summary>
    public static readonly Vector3 Start = new(44f, 0f, 44f);

    /// <summary>The middle of the village: between the guard's beat and the villager's bench.</summary>
    public static readonly Vector3 Middle = new(12f, 0f, 18f);

    /// <summary>
    ///     The script: wait outside, walk in, linger, walk back out, wait outside again.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The lingering matters and the leaving matters more.</b> An intruder that only ever
    ///     arrives proves that a decision can change once; a decision that changes <i>back</i> when
    ///     the world changes back is the one that fails when a lose-sight radius is missing or an
    ///     abort scope is wrong — and doc 37's perception README records exactly that symptom as
    ///     five changes of mind against one.
    /// </remarks>
    static readonly (double At, Vector3 Where)[] Waypoints = [
        (0.0, Start),
        (3.0, Start),
        (9.0, Middle),
        (15.0, Middle),
        (21.0, Start)
    ];

    /// <summary>When the script has said everything it has to say.</summary>
    public static double Duration => Waypoints[^1].At;

    /// <summary>Where the intruder is at a moment.</summary>
    /// <param name="seconds">Seconds since the village started.</param>
    /// <returns>Its position.</returns>
    public static Vector3 At(double seconds) {
        if (seconds <= Waypoints[0].At) {
            return Waypoints[0].Where;
        }

        for (var index = 1; index < Waypoints.Length; index++) {
            var (at, where) = Waypoints[index];

            if (seconds >= at) {
                continue;
            }

            var (from, start) = (Waypoints[index - 1].Where, Waypoints[index - 1].At);
            var span = at - start;

            // A zero-length leg would divide by zero; two waypoints at the same instant mean the
            // later one, which is what a reader of the table above expects.
            return span <= 0.0 ? where : Vector3.Lerp(from, where, (float) ((seconds - start) / span));
        }

        return Waypoints[^1].Where;
    }
}

/// <summary>Walks the intruder along <see cref="Intrusion" />'s script, once a frame.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A system with a declared order rather than a line in <c>OnUpdate</c>, and the
///         difference is a frame.</b> <c>VixenApplication</c> runs <c>EngineLoop.Frame</c> <i>before</i>
///         the game's own update — deliberately, so that a game reads a world that has already been
///         stepped — so an intruder moved from <c>OnUpdate</c> is an intruder every agent perceives
///         one frame late. Declaring <c>[UpdateBefore(typeof(PerceptionSystem))]</c> puts the move
///         where a reader of this sample would assume it already was.
///     </para>
///     <para>
///         ⚠ <b>It accumulates its own elapsed time rather than reading <c>GameTime.Total</c>.</b>
///         That is what makes the script survive a fixed-step host, a variable-step one and a test
///         that hands it whatever deltas it likes — and it is the property
///         <c>Elapsed_is_the_sum_of_the_deltas_and_not_a_wall_clock</c> asserts.
///     </para>
/// </remarks>
/// <param name="intruder">Which entity to walk.</param>
[UpdateInGroup(SystemPhase.Update)]
[UpdateBefore(typeof(PerceptionSystem))]
public sealed class IntruderSystem(Entity intruder) : SystemBase, IDeclaredAccess {
    /// <summary>Seconds of script consumed so far.</summary>
    public double Elapsed { get; private set; }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare().Write<LocalTransform>().Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Elapsed += context.Time.DeltaSeconds;

        if (context.World.Has<LocalTransform>(intruder)) {
            context.World.Get<LocalTransform>(intruder).Position = Intrusion.At(Elapsed);
        }

        return dependency;
    }
}
