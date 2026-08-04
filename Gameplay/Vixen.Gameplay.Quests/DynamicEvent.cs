// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Quests;

/// <summary>An address a chain continues to, resolved.</summary>
/// <param name="Def">Which event.</param>
/// <param name="Address">Its address, kept so a report can name one this build does not have.</param>
public readonly record struct EventLink(DefId Def, string Address);

/// <summary>One contribution tier, compiled.</summary>
public sealed class ContributionTier {
    internal ContributionTier(ContributionTierDefinition definition, QuestReward reward) {
        Definition = definition;
        Reward = reward;
    }

    /// <summary>What it was compiled from.</summary>
    public ContributionTierDefinition Definition { get; }

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>How much contribution reaches it.</summary>
    public int Minimum => Definition.Minimum;

    /// <summary>What it pays.</summary>
    public QuestReward Reward { get; }
}

/// <summary>Where a dynamic event is.</summary>
public enum DynamicEventStatus {
    /// <summary>Not started.</summary>
    Idle,

    /// <summary>Running.</summary>
    Running,

    /// <summary>Its objectives were finished.</summary>
    Succeeded,

    /// <summary>Its clock ran out, or something failed it.</summary>
    Failed
}

/// <summary>One dynamic event, compiled.</summary>
public sealed class DynamicEventTemplate {
    readonly ObjectiveTemplate[] objectives;
    readonly ContributionTier[] tiers;
    readonly EventLink[] onSuccess;
    readonly EventLink[] onFailure;

    DynamicEventTemplate(
        DynamicEventDefinition definition,
        GameplayTag tag,
        DefId scene,
        ObjectiveTemplate[] objectives,
        ContributionTier[] tiers,
        EventLink[] onSuccess,
        EventLink[] onFailure
    ) {
        Definition = definition;
        Tag = tag;
        Scene = scene;
        this.objectives = objectives;
        this.tiers = tiers;
        this.onSuccess = onSuccess;
        this.onFailure = onFailure;
    }

    /// <summary>What it was compiled from.</summary>
    public DynamicEventDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What being in it is, or <see cref="GameplayTag.None" />.</summary>
    public GameplayTag Tag { get; }

    /// <summary>Which map it happens on.</summary>
    public DefId Scene { get; }

    /// <summary>Its objectives.</summary>
    public ReadOnlySpan<ObjectiveTemplate> Objectives => objectives;

    /// <summary>Its contribution tiers, richest first.</summary>
    public ReadOnlySpan<ContributionTier> Tiers => tiers;

    /// <summary>What success starts.</summary>
    public ReadOnlySpan<EventLink> OnSuccess => onSuccess;

    /// <summary>What failure starts.</summary>
    public ReadOnlySpan<EventLink> OnFailure => onFailure;

    /// <summary>How long it may run, in seconds. Zero for no limit.</summary>
    public float Duration => MathF.Max(0f, Definition.Duration);

    /// <summary>Whether the objectives' counts grow with the crowd, or only the difficulty.</summary>
    public bool ScalesObjectives => Definition.ScalesObjectives;

    /// <summary>Whether it can end without anybody doing anything.</summary>
    /// <remarks>
    ///     ⚠ <b>Doc 28's "event chains reach a terminal state" is only true of an event that can end
    ///     on its own.</b> An event with no clock and an objective nobody is working on runs for ever,
    ///     which is a legitimate thing to author — a capture point waits to be captured — but it means
    ///     the property is about events with a duration, and the library says which ones those are
    ///     rather than the test assuming all of them are.
    /// </remarks>
    public bool IsSelfTerminating => Duration > 0f;

    /// <summary>How much harder it is with this many people.</summary>
    /// <param name="participants">How many.</param>
    /// <returns>The multiplier, never below one.</returns>
    /// <remarks>
    ///     <b>Monotone non-decreasing in <paramref name="participants" />, and that is asserted.</b>
    ///     One clamped linear term is the whole of it — see <see cref="EventScalingDefinition" /> for
    ///     why there is no room for a designer to bend it downwards.
    /// </remarks>
    public float Scale(int participants) {
        var scaling = Definition.Scaling;
        var over = Math.Max(0, participants - Math.Max(1, scaling.BaseParticipants));
        var maximum = MathF.Max(1f, scaling.Maximum);

        return Math.Clamp(1f + (over * MathF.Max(0f, scaling.PerParticipant)), 1f, maximum);
    }

    /// <summary>Which tier a contribution reaches.</summary>
    /// <param name="contribution">How much somebody did.</param>
    /// <returns>The tier, or null when they did too little for any.</returns>
    /// <remarks>
    ///     ⚠ <b>Tiers are sorted at compile time and searched richest first.</b> The same rule the
    ///     progression library's ranked tracks have, and for the same reason: a search from the bottom
    ///     is right only while a designer happens to author them in order.
    /// </remarks>
    public ContributionTier? TierFor(int contribution) {
        foreach (var tier in tiers) {
            if (contribution >= tier.Minimum) {
                return tier;
            }
        }

        return null;
    }

    internal static DynamicEventTemplate Compile(
        DynamicEventDefinition definition,
        GameplayTagTable tags,
        QuestObjectiveRegistry registry,
        List<string> problems
    ) {
        if (definition.Objectives.Count == 0) {
            problems.Add($"'{definition.Address}' has no objectives, so nothing can ever succeed it.");
        }

        if (definition.Duration <= 0f && definition.OnFailure.Count > 0) {
            problems.Add(
                $"'{definition.Address}' has a failure branch and no duration, so nothing can ever fail "
                + "it and the branch is unreachable."
            );
        }

        var objectives = QuestLibrary.CompileObjectives(
            definition.Address,
            "objectives",
            definition.Objectives,
            tags,
            registry,
            problems
        );

        var tiers = definition.Tiers
            .Select(tier => new ContributionTier(tier, CompileReward(tier.Rewards)))
            .OrderByDescending(tier => tier.Minimum)
            .ToArray();

        return new(
            definition,
            tags.Resolve(definition.Tag),
            DefId.From(definition.Scene),
            objectives,
            tiers,
            [.. definition.OnSuccess.Select(address => new EventLink(DefId.From(address), address))],
            [.. definition.OnFailure.Select(address => new EventLink(DefId.From(address), address))]
        );

        static QuestReward CompileReward(QuestRewardDefinition? rewards) =>
            rewards is null
                ? QuestReward.None
                : new(
                    Math.Max(0, rewards.Experience),
                    Grants(rewards.Items),
                    Grants(rewards.Currencies),
                    Grants(rewards.Reputation),
                    Grants(rewards.Choices)
                );

        static QuestGrant[] Grants(List<QuestGrantDefinition> grants) =>
            [.. grants.Select(grant => new QuestGrant(DefId.From(grant.Def), grant.Def, grant.Count))];
    }
}

/// <summary>One running dynamic event on one realm.</summary>
/// <remarks>
///     <para>
///         <b>Realm-scoped, which is the only thing that makes it not a quest.</b> The objectives are
///         a quest stage's, tracked by the same <see cref="ObjectiveTracker" /> with no owner set, so
///         everybody's kills count towards the same number.
///     </para>
///     <para>
///         ⚠ <b>Contribution rather than tap-ownership, and it is recorded per participant as the
///         event runs.</b> Deciding who "gets" an event when it ends — by first hit, most damage or
///         killing blow — is the mechanic that makes a world boss a race and a passer-by an intruder.
///         Doc 28 picks tiers deliberately: everyone who did enough gets their tier, and one player's
///         reward is not another player's loss.
///     </para>
/// </remarks>
public sealed class DynamicEventInstance : IDisposable {
    readonly Dictionary<ulong, int> contributions = [];
    readonly ObjectiveTracker tracker;

    internal DynamicEventInstance(DynamicEventTemplate template, GameplayEventBus bus) {
        Template = template;
        tracker = new(bus, template.Objectives);

        tracker.Advanced += advance => {
            if (advance.Instigator != 0 && advance.Amount > 0) {
                Contribute(advance.Instigator, advance.Amount);
            }
        };
    }

    /// <summary>What it is.</summary>
    public DynamicEventTemplate Template { get; }

    /// <summary>Which event.</summary>
    public DefId Id => Template.Id;

    /// <summary>Where it is.</summary>
    public DynamicEventStatus Status { get; private set; } = DynamicEventStatus.Running;

    /// <summary>How long it has been running, in seconds.</summary>
    public float Elapsed { get; private set; }

    /// <summary>How long it has left, or zero when it has no clock.</summary>
    public float Remaining => Template.Duration <= 0f ? 0f : MathF.Max(0f, Template.Duration - Elapsed);

    /// <summary>What is tracking its objectives.</summary>
    public ObjectiveTracker Objectives => tracker;

    /// <summary>How many people have contributed anything.</summary>
    public int Participants => contributions.Count;

    /// <summary>How hard it currently is.</summary>
    public float Scale => Template.Scale(Participants);

    /// <summary>Whether it is over.</summary>
    public bool IsTerminal => Status is DynamicEventStatus.Succeeded or DynamicEventStatus.Failed;

    /// <summary>Everybody who contributed, and how much.</summary>
    public IReadOnlyDictionary<ulong, int> Contributions => contributions;

    /// <summary>Records that somebody did something.</summary>
    /// <param name="participant">Who.</param>
    /// <param name="amount">How much. Never lowers a contribution.</param>
    /// <returns>Their contribution now.</returns>
    /// <remarks>
    ///     Also how a game credits work an objective does not count — healing, reviving, repairing a
    ///     wall — which is most of what a support player does at an event.
    /// </remarks>
    public int Contribute(ulong participant, int amount) {
        if (participant == 0) {
            return 0;
        }

        var total = contributions.GetValueOrDefault(participant) + Math.Max(0, amount);

        contributions[participant] = total;

        if (Template.ScalesObjectives) {
            tracker.Rescale(Scale);
        }

        return total;
    }

    /// <summary>How much somebody did.</summary>
    /// <param name="participant">Who.</param>
    /// <returns>Their contribution, or zero.</returns>
    public int ContributionOf(ulong participant) => contributions.GetValueOrDefault(participant);

    /// <summary>Which tier somebody earned.</summary>
    /// <param name="participant">Who.</param>
    /// <returns>The tier, or null.</returns>
    public ContributionTier? TierOf(ulong participant) => Template.TierFor(ContributionOf(participant));

    /// <summary>Advances it.</summary>
    /// <param name="delta">How much time passed, in seconds.</param>
    /// <returns>Whether it ended on this tick.</returns>
    public bool Tick(float delta) {
        if (IsTerminal) {
            return false;
        }

        Elapsed += MathF.Max(0f, delta);
        tracker.Tick(delta);

        if (tracker.IsComplete) {
            return Finish(DynamicEventStatus.Succeeded);
        }

        if (tracker.IsFailed) {
            return Finish(DynamicEventStatus.Failed);
        }

        // ⚠ The clock is checked last. An event whose final objective completes on the same tick its
        // duration runs out succeeded — the work was done — and checking the clock first would fail an
        // event a player had just finished, which reads as the server cheating.
        return Template.Duration > 0f && Elapsed >= Template.Duration && Finish(DynamicEventStatus.Failed);
    }

    /// <summary>Ends it, however a game decides to.</summary>
    /// <param name="status">How it ended.</param>
    /// <returns>Whether this is what ended it.</returns>
    public bool Finish(DynamicEventStatus status) {
        if (IsTerminal || status is DynamicEventStatus.Idle or DynamicEventStatus.Running) {
            return false;
        }

        Status = status;
        tracker.Dispose();

        return true;
    }

    /// <inheritdoc />
    public void Dispose() => tracker.Dispose();
}
