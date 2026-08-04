// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Quests;

/// <summary>How an event gets harder as more people turn up.</summary>
/// <remarks>
///     ⚠ <b>Monotone by construction, because doc 28's test for this is that it is.</b> The scale is
///     one clamped linear term and nothing else — no table, no per-band override, no "and a bit more
///     for the fifth player". A shape a designer can bend is a shape that can bend downwards, and an
///     event that got <em>easier</em> when a tenth player arrived is a mechanic for griefing it.
/// </remarks>
[DataContract("EventScaling")]
public sealed class EventScalingDefinition {
    /// <summary>How many people it is balanced for. Below this it does not get easier.</summary>
    public int BaseParticipants { get; set; } = 1;

    /// <summary>How much harder each additional person makes it.</summary>
    public float PerParticipant { get; set; } = 0.15f;

    /// <summary>The most it ever scales to.</summary>
    public float Maximum { get; set; } = 5f;
}

/// <summary>One band of contribution and what it pays.</summary>
[DataContract("ContributionTier")]
public sealed class ContributionTierDefinition {
    /// <summary>What it is called — <c>Gold</c>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How much contribution reaches it.</summary>
    public int Minimum { get; set; }

    /// <summary>What reaching it pays on success.</summary>
    public QuestRewardDefinition Rewards { get; set; } = new();
}

/// <summary>When a world boss comes round.</summary>
/// <remarks>
///     ⚠ <b>Authored here and enacted somewhere else.</b> Doc 28 puts the schedule in
///     <c>Live.Instances.Cluster</c> because it is fleet-wide: one realm deciding when the boss spawns
///     gives every shard a different boss. So this is the content the cluster reads, and nothing in
///     this library looks at a clock.
/// </remarks>
[DataContract("EventSchedule")]
public sealed class EventScheduleDefinition {
    /// <summary>How often it comes round, in seconds. Zero for an event nothing schedules.</summary>
    public float IntervalSeconds { get; set; }

    /// <summary>How long the window to join it stays open, in seconds.</summary>
    public float WindowSeconds { get; set; }

    /// <summary>How far into an interval the first one is, in seconds.</summary>
    public float OffsetSeconds { get; set; }
}

/// <summary>A dynamic event: a quest stage with the scope moved off one player and onto a realm.</summary>
/// <remarks>
///     <para>
///         <b>The same machine as a quest, and doc 28 says so.</b> Objectives, filters, progress and
///         completion are the quest half's; what differs is who owns the progress — a realm rather than
///         a journal — and that success and failure <em>both</em> lead somewhere.
///     </para>
///     <para>
///         <b>A failed escort starting "retake the camp" is the point.</b> An event with only a success
///         edge is a quest with extra machinery; the failure edge is what makes a chain feel like a
///         place things happen to rather than a list of things to do.
///     </para>
/// </remarks>
[DataContract("DynamicEventDefinition")]
public sealed record DynamicEventDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What the banner says.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The address of the map it happens on.</summary>
    public string Scene { get; set; } = string.Empty;

    /// <summary>What being in it is — <c>Event.Active.CampDefence</c>. Empty for one nothing asks about.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Its objectives. Finishing the required ones succeeds it.</summary>
    public List<QuestObjectiveDefinition> Objectives { get; set; } = [];

    /// <summary>How long it may run, in seconds. Running out fails it. Zero for no limit.</summary>
    public float Duration { get; set; }

    /// <summary>How it scales with participants.</summary>
    public EventScalingDefinition Scaling { get; set; } = new();

    /// <summary>Whether the objectives' counts scale too, or only the difficulty.</summary>
    public bool ScalesObjectives { get; set; }

    /// <summary>Its contribution tiers, in any order.</summary>
    public List<ContributionTierDefinition> Tiers { get; set; } = [];

    /// <summary>The addresses of the events success starts.</summary>
    public List<string> OnSuccess { get; set; } = [];

    /// <summary>The addresses of the events failure starts.</summary>
    public List<string> OnFailure { get; set; } = [];

    /// <summary>When it comes round, for a world boss.</summary>
    public EventScheduleDefinition Schedule { get; set; } = new();

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var objective in Objectives) {
            foreach (var tag in objective.TargetTags) {
                tags.Add(tag);
            }

            foreach (var tag in objective.ExcludeTags) {
                tags.Add(tag);
            }
        }
    }
}
