// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Ai.Perception.Ecs;

/// <summary>An entity that notices things.</summary>
/// <remarks>
///     <para>
///         The same shape as <c>AiAgent</c> and for its reasons: an index into a library, a slot the
///         system owns, and a few numbers. The perceived list is a managed object with a list in it,
///         so it lives beside the slot in <see cref="PerceptionSystem" /> rather than in a chunk
///         column.
///     </para>
///     <para>
///         <b><c>[Component]</c> without <c>[DataContract]</c>, deliberately.</b>
///         <see cref="ListenerIndex" /> is the system's own bookkeeping and a saved copy of it would
///         name a slot that does not exist in the process that loads it. <see cref="AiStimuliSource" />
///         is the placeable half, because being <i>perceivable</i> really is a property of a level's
///         contents.
///     </para>
///     <para>
///         ⚠ <b>A listener does not have to be an <c>AiAgent</c>.</b> A security camera, a trap and a
///         trigger volume all want to notice things without deciding anything, and a perception pass
///         that required a planner would make each of those carry one. The binding to a blackboard is
///         what needs an agent, and it is skipped when there is not one.
///     </para>
/// </remarks>
[Component]
public struct AiPerception {
    /// <summary>Which configuration it senses with. An index into the system's library.</summary>
    public ushort Config;

    /// <summary>Its slot with the system. Owned by <see cref="PerceptionSystem" />.</summary>
    /// <remarks>
    ///     Assigned on join and stable for the entity's life, for the reason
    ///     <c>AiAgent.ScheduleIndex</c> gives: chunk order changes on any unrelated spawn, and the
    ///     jittered update phase has to stay with the agent rather than with wherever it sits today.
    /// </remarks>
    public int ListenerIndex;

    /// <summary>Which side it is on. What <see cref="IPerceptionFilter" /> reads.</summary>
    public byte Team;

    /// <summary>Whether it senses at all. A dead or blinded agent sets it false.</summary>
    public bool Enabled;

    /// <summary>Seconds until its next pass. Owned by the system.</summary>
    /// <remarks>
    ///     ⚠ Counts down rather than comparing against a next-time stamp, so that a listener whose
    ///     interval is stretched by distance LOD does not have to be woken to be re-planned: the
    ///     stretch applies when the countdown is refilled, which is the moment the distance was
    ///     measured.
    /// </remarks>
    public float Countdown;

    /// <summary>A listener that has not joined a system yet.</summary>
    /// <param name="config">Its index in the system's <see cref="PerceptionLibrary" />.</param>
    /// <param name="team">Which side it is on.</param>
    /// <returns>The component.</returns>
    public static AiPerception Sensing(int config, byte team = 0) => new() {
        Config = (ushort)config,
        Team = team,
        ListenerIndex = -1,
        Enabled = true
    };
}

/// <summary>An entity that can be noticed.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Perceivability is opt-in, and that is the first of doc 37 § D15's three bounds.</b>
///         A level has tens of thousands of entities and a handful of them are worth looking for. A
///         perception pass that scanned everything with a transform would be a pass whose cost is the
///         level's size rather than the number of things that matter, and no amount of broad phase
///         fixes that — the broad phase is over <i>these</i>.
///     </para>
///     <para>
///         Scene-placeable, unlike <see cref="AiPerception" />: every field here is authored, so a
///         designer marks the player, the guards and the noisy machinery in the level editor.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct AiStimuliSource {
    /// <summary>Which senses can find it. <see cref="SenseMask.None" /> is how a disguise works.</summary>
    public SenseMask Senses;

    /// <summary>Which side it is on.</summary>
    public byte Team;

    /// <summary>How strongly it registers. Scales a noise's range and rides along on every report.</summary>
    public float Strength;

    /// <summary>Where the perceivable point is above its position, in metres.</summary>
    /// <remarks>
    ///     The other half of <see cref="SightSettings.EyeHeight" />: a trace aims at the chest, not at
    ///     the feet, because the feet are behind whatever the thing is standing on.
    /// </remarks>
    public float Height;

    /// <summary>Whether it registers at all. A corpse or a despawning pickup sets it false.</summary>
    public bool Enabled;

    /// <summary>A source everything can find.</summary>
    /// <param name="team">Which side it is on.</param>
    /// <param name="senses">Which senses find it.</param>
    /// <returns>The component.</returns>
    public static AiStimuliSource Perceivable(byte team = 0, SenseMask senses = SenseMask.All) => new() {
        Senses = senses,
        Team = team,
        Strength = 1f,
        Height = 1f,
        Enabled = true
    };
}
