// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Quests;

/// <summary>An objective <em>type</em>: what verb it counts and how it counts it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28's named seam — "a game adds one by implementing <c>IQuestObjective</c>".</b> An
///         implementation is stateless and there is one instance per type per build: the per-player
///         progress lives in the <see cref="QuestJournal" />, because ten thousand players tracking
///         the same objective must not be ten thousand objects.
///     </para>
///     <para>
///         ⚠ <b>Almost every member has a default, and the ten shipped types are mostly verbs.</b>
///         That is the honest shape of the thing rather than an economy: an objective is a
///         subscription and a counter, and what makes <c>Kill</c> differ from <c>Craft</c> is which
///         verb it waits for. Saying so in the seam is what makes an eleventh type five lines instead
///         of a class with nine methods, eight of which would be copied from <c>Kill</c>.
///     </para>
/// </remarks>
public interface IQuestObjective {
    /// <summary>What a definition's <c>type:</c> says to get this — <c>Kill</c>.</summary>
    string Type { get; }

    /// <summary>The verb it counts — <c>Event.Kill</c>. Empty for an objective driven only by time.</summary>
    string Verb { get; }

    /// <summary>The verb that fails it outright, or empty for one that cannot be failed.</summary>
    /// <remarks>
    ///     What makes an escort an escort. Doc 28's event chains need failure to lead somewhere, and
    ///     an objective that could only ever be completed or abandoned gives them nowhere to lead from.
    /// </remarks>
    string FailVerb => string.Empty;

    /// <summary>Whether it counts seconds rather than events, with <c>count:</c> as the seconds.</summary>
    bool IsTimed => false;

    /// <summary>Whether progress can go back down.</summary>
    /// <remarks>
    ///     ⚠ <b>The one genuine difference among the shipped types.</b> <c>Collect</c> is a
    ///     <em>level</em> — "have ten ore" is untrue again the moment nine are sold — while <c>Kill</c>
    ///     is a <em>tally</em>, and ten undead stay killed. Getting this backwards is how a fetch quest
    ///     completes for somebody who picked an item up and dropped it ten times.
    /// </remarks>
    bool IsLevel => false;

    /// <summary>How much an event advances it.</summary>
    /// <param name="objective">The authored objective, for whatever parameters this type reads.</param>
    /// <param name="gameplayEvent">The event, which has already passed the filter.</param>
    /// <returns>The amount, which may be negative for a level.</returns>
    /// <remarks>
    ///     The default is the event's own amount, which is right for every shipped type: one kill,
    ///     thirty ore, five hundred gold. A type overrides it when the parameters mean something the
    ///     filter cannot express — "ore of quality three or better" reads the quality off the event and
    ///     returns zero for the rest.
    /// </remarks>
    int Advance(ObjectiveTemplate objective, in GameplayEvent gameplayEvent) => gameplayEvent.Amount;
}

/// <summary>One authored objective, compiled: its type, its filters and its target.</summary>
public sealed class ObjectiveTemplate {
    internal ObjectiveTemplate(
        QuestObjectiveDefinition definition,
        IQuestObjective kind,
        GameplayEventFilter filter,
        GameplayEventFilter failFilter
    ) {
        Definition = definition;
        Kind = kind;
        Filter = filter;
        FailFilter = failFilter;
    }

    /// <summary>What it was compiled from.</summary>
    public QuestObjectiveDefinition Definition { get; }

    /// <summary>Which objective type it is.</summary>
    public IQuestObjective Kind { get; }

    /// <summary>What advances it.</summary>
    public GameplayEventFilter Filter { get; }

    /// <summary>What fails it, or a filter that matches nothing.</summary>
    public GameplayEventFilter FailFilter { get; }

    /// <summary>How many, or how many seconds. Never below one.</summary>
    public int Count => Math.Max(1, Definition.Count);

    /// <summary>What the tracker says.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>Whether the stage completes without it.</summary>
    public bool IsOptional => Definition.Optional;

    /// <summary>Whether the tracker shows it before it is started.</summary>
    public bool IsHidden => Definition.Hidden;

    /// <summary>Whether it counts seconds.</summary>
    public bool IsTimed => Kind.IsTimed;

    /// <summary>Whether its progress can go back down.</summary>
    public bool IsLevel => Kind.IsLevel;

    /// <summary>Whether anything can ever advance it.</summary>
    /// <remarks>
    ///     False for an objective whose verb, target tags or scene named content this build does not
    ///     have. The library reports that as a problem; this is what the journal checks so that an
    ///     unsatisfiable objective is visibly stuck rather than quietly never advancing.
    /// </remarks>
    public bool IsSatisfiable => IsTimed || Filter.IsSome;
}

/// <summary>Which objective types a build has, by name.</summary>
/// <remarks>
///     <b>Explicit, like every other list in doc 28.</b> A registry filled by scanning assemblies is
///     one whose contents a trimmed publish decides, and doc 28's <see cref="IGameplayModule" /> note
///     already says what that produces: a game that works in development and ships with no quests.
/// </remarks>
public sealed class QuestObjectiveRegistry {
    readonly Dictionary<string, IQuestObjective> byType = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ten doc 28 says the engine ships, and nothing else.</summary>
    public static QuestObjectiveRegistry Default { get; } = new QuestObjectiveRegistry().AddShipped();

    /// <summary>How many types it holds.</summary>
    public int Count => byType.Count;

    /// <summary>Every type, in name order.</summary>
    public IEnumerable<IQuestObjective> Types =>
        byType.Values.OrderBy(type => type.Type, StringComparer.Ordinal);

    /// <summary>Adds a type.</summary>
    /// <param name="objective">The type.</param>
    /// <returns>The registry, so calls chain.</returns>
    /// <exception cref="ArgumentException">Two types claim the same name.</exception>
    public QuestObjectiveRegistry Add(IQuestObjective objective) {
        ArgumentNullException.ThrowIfNull(objective);

        if (!byType.TryAdd(objective.Type, objective)) {
            throw new ArgumentException(
                $"Two objective types are called '{objective.Type}'. A definition's type: names one of "
                + "them and there is no way to say which.",
                nameof(objective)
            );
        }

        return this;
    }

    /// <summary>Adds the ten shipped types.</summary>
    /// <returns>The registry, so calls chain.</returns>
    public QuestObjectiveRegistry AddShipped() {
        foreach (var objective in QuestObjectives.Shipped) {
            Add(objective);
        }

        return this;
    }

    /// <summary>Finds a type.</summary>
    /// <param name="type">Its name.</param>
    /// <returns>It, or null.</returns>
    public IQuestObjective? Find(string? type) =>
        type is null ? null : byType.GetValueOrDefault(type);
}
