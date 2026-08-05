// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Quests;

/// <summary>The verbs the shipped objective types wait for, and the tag root they sit under.</summary>
/// <remarks>
///     <b>Declared by <see cref="QuestModule" /> so that they exist whether or not content mentions
///     them.</b> A verb absent from the baked table resolves to an empty range, and doc 28's rule is
///     that an empty range matches nothing — so without these, every shipped objective type would
///     compile to a subscription that never fires, in a build whose content happened not to name the
///     verb.
/// </remarks>
public static class QuestVerbs {
    /// <summary>The tag every verb sits under.</summary>
    public const string Root = "Event";

    /// <summary>Something died.</summary>
    public const string Kill = "Event.Kill";

    /// <summary>Something was acquired, or lost with a negative amount.</summary>
    public const string Collect = "Event.Collect";

    /// <summary>Somewhere was arrived at.</summary>
    public const string Reach = "Event.Reach";

    /// <summary>Something was used.</summary>
    public const string Interact = "Event.Interact";

    /// <summary>An escortee got where it was going.</summary>
    public const string Escort = "Event.Escort";

    /// <summary>An escortee died or was lost.</summary>
    public const string EscortFailed = "Event.EscortFailed";

    /// <summary>The subject of a survival objective died.</summary>
    public const string Death = "Event.Death";

    /// <summary>Something was handed over.</summary>
    public const string Deliver = "Event.Deliver";

    /// <summary>Somewhere was found for the first time.</summary>
    public const string Discover = "Event.Discover";

    /// <summary>Something was made.</summary>
    public const string Craft = "Event.Craft";

    /// <summary>Currency left an account.</summary>
    public const string Spend = "Event.Spend";

    /// <summary>Every one of them, for a tag table builder.</summary>
    public static IReadOnlyList<string> All { get; } = [
        Root,
        Kill,
        Collect,
        Reach,
        Interact,
        Escort,
        EscortFailed,
        Death,
        Deliver,
        Discover,
        Craft,
        Spend
    ];
}

/// <summary>The ten objective types doc 28 says the engine ships.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>They are nine verbs, one clock and one level, and that is worth admitting.</b> Nine of
///         them differ only in which verb they wait for; <c>Collect</c> differs in one bit, because
///         having ten ore stops being true when nine are sold; <c>Survive</c> differs in counting
///         seconds instead of events. A design that hid that behind ten hand-written counters would be
///         ten places for the "no objective completes twice" rule to be got wrong, and it would make an
///         eleventh type look like a day's work rather than five lines.
///     </para>
///     <para>
///         <b>What carries the variety is the <em>filter</em>, not the type.</b> "Kill ten undead in
///         Queensdale" and "kill the Shatterer" are both <c>Kill</c>; the difference is a tag query and
///         a scene, which a designer writes and nobody compiles.
///     </para>
/// </remarks>
public static class QuestObjectives {
    /// <summary>All ten, in the order doc 28 lists them.</summary>
    public static IReadOnlyList<IQuestObjective> Shipped { get; } = [
        new KillObjective(),
        new CollectObjective(),
        new ReachObjective(),
        new InteractObjective(),
        new EscortObjective(),
        new SurviveObjective(),
        new DeliverObjective(),
        new DiscoverObjective(),
        new CraftObjective(),
        new SpendObjective()
    ];

    /// <summary>Kill things. A tally.</summary>
    public sealed class KillObjective : IQuestObjective {
        /// <inheritdoc />
        public string Type => "Kill";

        /// <inheritdoc />
        public string Verb => QuestVerbs.Kill;
    }

    /// <summary>Have things. A level, so losing them un-progresses it.</summary>
    public sealed class CollectObjective : IQuestObjective {
        /// <inheritdoc />
        public string Type => "Collect";

        /// <inheritdoc />
        public string Verb => QuestVerbs.Collect;

        /// <inheritdoc />
        public bool IsLevel => true;
    }

    /// <summary>Get somewhere.</summary>
    public sealed class ReachObjective : IQuestObjective {
        /// <inheritdoc />
        public string Type => "Reach";

        /// <inheritdoc />
        public string Verb => QuestVerbs.Reach;
    }

    /// <summary>Use something.</summary>
    public sealed class InteractObjective : IQuestObjective {
        /// <inheritdoc />
        public string Type => "Interact";

        /// <inheritdoc />
        public string Verb => QuestVerbs.Interact;
    }

    /// <summary>Get somebody somewhere alive. Fails if they die.</summary>
    public sealed class EscortObjective : IQuestObjective {
        /// <inheritdoc />
        public string Type => "Escort";

        /// <inheritdoc />
        public string Verb => QuestVerbs.Escort;

        /// <inheritdoc />
        public string FailVerb => QuestVerbs.EscortFailed;
    }

    /// <summary>Stay alive for <c>count:</c> seconds. Fails on death.</summary>
    public sealed class SurviveObjective : IQuestObjective {
        /// <inheritdoc />
        public string Type => "Survive";

        /// <inheritdoc />
        /// <remarks>Empty: nothing advances it but the clock.</remarks>
        public string Verb => string.Empty;

        /// <inheritdoc />
        public string FailVerb => QuestVerbs.Death;

        /// <inheritdoc />
        public bool IsTimed => true;
    }

    /// <summary>Hand something over.</summary>
    public sealed class DeliverObjective : IQuestObjective {
        /// <inheritdoc />
        public string Type => "Deliver";

        /// <inheritdoc />
        public string Verb => QuestVerbs.Deliver;
    }

    /// <summary>Find somewhere for the first time.</summary>
    public sealed class DiscoverObjective : IQuestObjective {
        /// <inheritdoc />
        public string Type => "Discover";

        /// <inheritdoc />
        public string Verb => QuestVerbs.Discover;
    }

    /// <summary>Make something.</summary>
    public sealed class CraftObjective : IQuestObjective {
        /// <inheritdoc />
        public string Type => "Craft";

        /// <inheritdoc />
        public string Verb => QuestVerbs.Craft;
    }

    /// <summary>Spend currency.</summary>
    public sealed class SpendObjective : IQuestObjective {
        /// <inheritdoc />
        public string Type => "Spend";

        /// <inheritdoc />
        public string Verb => QuestVerbs.Spend;
    }
}
