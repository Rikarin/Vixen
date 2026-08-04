// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Ai.Perception;

/// <summary>Something that happened, which some senses are the only way of noticing.</summary>
/// <param name="Sense">Which sense it reaches.</param>
/// <param name="Source">Who caused it.</param>
/// <param name="Target">Who it happened to, or <see cref="Entity.Null" /> for anyone in range.</param>
/// <param name="Position">Where it happened.</param>
/// <param name="Strength">How loud, or how much damage.</param>
/// <param name="Stamp">The clock reading when it was reported.</param>
/// <param name="Sequence">
///     Where it came in the order of reports. ⚠ <b>What a listener consumes against, and the clock is
///     not.</b> The clock only advances inside a step, so an event reported after a pass in the same
///     frame carries exactly the clock that pass recorded — and a listener comparing clocks would
///     decide it had already heard a gunshot that had not happened yet.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>Hearing and damage are events, and sight and touch are states, and that difference is
///         why both machines exist.</b> An entity is continuously visible, so sight is sampled: it can
///         be asked at any moment and the answer is a property of the world right now. A gunshot is
///         not continuously audible — an entity is not "audible", an <i>event</i> is — so a hearing
///         sense that sampled would have to sample the exact frame the shot happened, which at 4 Hz
///         means hearing one shot in six.
///     </para>
///     <para>
///         So events are kept for <see cref="Ecs.PerceptionSystem.EventMemory" /> seconds and each
///         listener consumes the ones newer than its own last pass. A listener at 4 Hz hears every
///         shot; it just hears them up to a quarter of a second late, which is the reaction time it
///         was configured for.
///     </para>
/// </remarks>
public readonly record struct StimulusEvent(
    AiSense Sense,
    Entity Source,
    Entity Target,
    Vector3 Position,
    float Strength,
    float Stamp,
    long Sequence
);

/// <summary>What one frame of perception cost.</summary>
/// <param name="Listeners">How many listeners there are.</param>
/// <param name="Sources">How many stimuli sources there are.</param>
/// <param name="Passes">How many listeners actually sensed this frame.</param>
/// <param name="Examined">
///     How many source-distance tests were done. ⚠ <b>The number the broad phase exists to make
///     small</b>: without one it is <c>Passes × Sources</c> by construction.
/// </param>
/// <param name="Cells">How many grid cells were walked.</param>
/// <param name="Candidates">How many sources came out of the radius test and into the sense tests.</param>
/// <param name="ConeTests">How many cone tests were done.</param>
/// <param name="Traces">
///     How many occlusion raycasts were done. The expensive one, and the reason the other three
///     numbers matter.
/// </param>
/// <remarks>
///     ⚠ <b>The report is a deliverable and not a diagnostic</b>, for the reason
///     <c>AgentSchedule</c>'s is: a perception system that quietly stopped noticing things is a frame
///     budget met by an AI nobody agreed to. This is what a project reads to find out what its
///     radius, its interval and its LOD bands actually bought.
/// </remarks>
public readonly record struct PerceptionStats(
    int Listeners,
    int Sources,
    int Passes,
    int Examined,
    int Cells,
    int Candidates,
    int ConeTests,
    int Traces
) {
    /// <summary>What the same frame would have cost with no broad phase at all.</summary>
    public int ExaminedWithoutBroadPhase => Passes * Sources;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Passes}/{Listeners} listeners × {Sources} sources: {Examined} examined "
        + $"(a scan would be {ExaminedWithoutBroadPhase}), {Cells} cells, {Candidates} candidates, "
        + $"{ConeTests} cone tests, {Traces} traces";
}
