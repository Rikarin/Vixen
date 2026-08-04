// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Quests;

/// <summary>One thing a reward gives, with its address resolved.</summary>
/// <param name="Def">What is given.</param>
/// <param name="Address">Its address, kept so a report can name it.</param>
/// <param name="Count">How much.</param>
public readonly record struct QuestGrant(DefId Def, string Address, int Count);

/// <summary>What turning a quest in pays, compiled.</summary>
/// <remarks>
///     ⚠ <b>A list, not a transaction.</b> This library never gives anybody anything: it says what is
///     owed and the caller — which knows about containers, currencies and factions, and which is on
///     the realm — pays it. Doc 28's authority table puts quest rewards in the grain for exactly that
///     reason, and a quest library that handed out items would be deciding a durable question from the
///     wrong process.
/// </remarks>
public sealed class QuestReward {
    readonly QuestGrant[] items;
    readonly QuestGrant[] currencies;
    readonly QuestGrant[] reputation;
    readonly QuestGrant[] choices;

    internal QuestReward(
        int experience,
        QuestGrant[] items,
        QuestGrant[] currencies,
        QuestGrant[] reputation,
        QuestGrant[] choices
    ) {
        Experience = experience;
        this.items = items;
        this.currencies = currencies;
        this.reputation = reputation;
        this.choices = choices;
    }

    /// <summary>A reward that pays nothing.</summary>
    public static QuestReward None { get; } = new(0, [], [], [], []);

    /// <summary>How much experience.</summary>
    public int Experience { get; }

    /// <summary>Items, always given.</summary>
    public ReadOnlySpan<QuestGrant> Items => items;

    /// <summary>Currencies, always given.</summary>
    public ReadOnlySpan<QuestGrant> Currencies => currencies;

    /// <summary>Faction standing, always given.</summary>
    public ReadOnlySpan<QuestGrant> Reputation => reputation;

    /// <summary>Items to pick one of, empty when there is no choice.</summary>
    public ReadOnlySpan<QuestGrant> Choices => choices;

    /// <summary>Whether a turn-in has to name a choice.</summary>
    public bool NeedsChoice => choices.Length > 0;

    /// <summary>Whether an index names one of the choices.</summary>
    /// <param name="choice">The index, or −1 for none.</param>
    /// <returns>Whether the turn-in may proceed with it.</returns>
    /// <remarks>
    ///     ⚠ <b>An index is checked rather than trusted, because it arrives from a client.</b> An
    ///     unchecked one is an out-of-range read at best and "pick reward number nine of three" at
    ///     worst, and this is the only number in a turn-in the player chooses.
    /// </remarks>
    public bool IsValidChoice(int choice) => NeedsChoice ? (uint)choice < (uint)choices.Length : choice < 0;
}

/// <summary>One stage, compiled.</summary>
public sealed class StageTemplate {
    readonly ObjectiveTemplate[] objectives;

    internal StageTemplate(QuestStageDefinition definition, int index, ObjectiveTemplate[] objectives, int required) {
        Definition = definition;
        Index = index;
        this.objectives = objectives;
        Required = required;
    }

    /// <summary>What it was compiled from.</summary>
    public QuestStageDefinition Definition { get; }

    /// <summary>Which stage of its quest it is.</summary>
    public int Index { get; }

    /// <summary>What it is called within its quest.</summary>
    public string Id => Definition.Id;

    /// <summary>What the tracker's heading says.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>How long it may take, or zero.</summary>
    public float TimeLimit => MathF.Max(0f, Definition.TimeLimit);

    /// <summary>Its objectives, in the order they were authored.</summary>
    public ReadOnlySpan<ObjectiveTemplate> Objectives => objectives;

    /// <summary>How many objectives it has.</summary>
    public int Count => objectives.Length;

    /// <summary>How many of them have to be finished for it to be done — the ones that are not optional.</summary>
    public int Required { get; }
}

/// <summary>One quest, compiled.</summary>
public sealed class QuestTemplate {
    readonly StageTemplate[] stages;
    readonly GameplayTag[] grantsTags;

    internal QuestTemplate(
        QuestDefinition definition,
        RequirementSet requirements,
        StageTemplate[] stages,
        QuestReward reward,
        GameplayTag tag,
        GameplayTag[] grantsTags
    ) {
        Definition = definition;
        Requirements = requirements;
        this.stages = stages;
        Reward = reward;
        Tag = tag;
        this.grantsTags = grantsTags;
    }

    /// <summary>What it was compiled from.</summary>
    public QuestDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What has to be true to accept it.</summary>
    public RequirementSet Requirements { get; }

    /// <summary>Its stages, in order.</summary>
    public ReadOnlySpan<StageTemplate> Stages => stages;

    /// <summary>What turning it in pays.</summary>
    public QuestReward Reward { get; }

    /// <summary>What having turned it in is, or <see cref="GameplayTag.None" />.</summary>
    public GameplayTag Tag { get; }

    /// <summary>Tags being on it grants.</summary>
    public ReadOnlySpan<GameplayTag> GrantsTags => grantsTags;

    /// <summary>How often it can be done.</summary>
    public QuestRepeat Repeat => Definition.Repeat;

    /// <summary>Whether a party member can be handed it.</summary>
    public bool IsShareable => Definition.Shareable;
}

/// <summary>Every quest and dynamic event a build knows, compiled once.</summary>
public sealed class QuestLibrary {
    readonly Dictionary<uint, QuestTemplate> quests;
    readonly Dictionary<uint, DynamicEventTemplate> events;
    readonly string[] problems;

    QuestLibrary(
        Dictionary<uint, QuestTemplate> quests,
        Dictionary<uint, DynamicEventTemplate> events,
        QuestObjectiveRegistry objectives,
        string[] problems
    ) {
        this.quests = quests;
        this.events = events;
        Objectives = objectives;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static QuestLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Which objective types it was compiled against.</summary>
    public QuestObjectiveRegistry Objectives { get; }

    /// <summary>Every quest, in address order.</summary>
    public IEnumerable<QuestTemplate> Quests =>
        quests.Values.OrderBy(quest => quest.Definition.Address, StringComparer.Ordinal);

    /// <summary>Every dynamic event, in address order.</summary>
    public IEnumerable<DynamicEventTemplate> Events =>
        events.Values.OrderBy(entry => entry.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <param name="objectives">Which objective types exist, or the shipped ten.</param>
    /// <returns>The library.</returns>
    public static QuestLibrary Compile(DefinitionCatalog catalog, QuestObjectiveRegistry? objectives = null) {
        ArgumentNullException.ThrowIfNull(catalog);

        var registry = objectives ?? QuestObjectiveRegistry.Default;
        var tags = catalog.Tags;
        var problems = new List<string>();

        var quests = new Dictionary<uint, QuestTemplate>();

        foreach (var definition in catalog.OfType<QuestDefinition>()) {
            quests.Add(definition.Id.Value, CompileQuest(definition, tags, registry, problems));
        }

        var events = new Dictionary<uint, DynamicEventTemplate>();

        foreach (var definition in catalog.OfType<DynamicEventDefinition>()) {
            events.Add(definition.Id.Value, DynamicEventTemplate.Compile(definition, tags, registry, problems));
        }

        // ⚠ Chain edges are checked after every event is compiled, not while each one is. An event
        // chain is a graph and a graph's edges point forwards as often as backwards — "retake the
        // camp" names the event that will start it again — so checking an edge as it is read would
        // report every forward reference in the file as missing.
        foreach (var entry in events.Values) {
            foreach (var next in entry.OnSuccess) {
                if (!events.ContainsKey(next.Def.Value)) {
                    problems.Add(
                        $"'{entry.Definition.Address}' continues to '{next.Address}' on success, which is "
                        + "not a dynamic event in this build."
                    );
                }
            }

            foreach (var next in entry.OnFailure) {
                if (!events.ContainsKey(next.Def.Value)) {
                    problems.Add(
                        $"'{entry.Definition.Address}' continues to '{next.Address}' on failure, which is "
                        + "not a dynamic event in this build."
                    );
                }
            }
        }

        return new(quests, events, registry, [.. problems]);
    }

    /// <summary>Finds a quest.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public QuestTemplate? FindQuest(DefId id) => quests.GetValueOrDefault(id.Value);

    /// <summary>Finds a dynamic event.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public DynamicEventTemplate? FindEvent(DefId id) => events.GetValueOrDefault(id.Value);

    internal static ObjectiveTemplate[] CompileObjectives(
        string owner,
        string stage,
        IReadOnlyList<QuestObjectiveDefinition> definitions,
        GameplayTagTable tags,
        QuestObjectiveRegistry registry,
        List<string> problems
    ) {
        var compiled = new List<ObjectiveTemplate>(definitions.Count);

        foreach (var definition in definitions) {
            if (registry.Find(definition.Type) is not { } kind) {
                problems.Add(
                    $"'{owner}' {stage} wants an objective of type '{definition.Type}', which this build "
                    + "has no IQuestObjective for."
                );

                continue;
            }

            var scene = DefId.From(definition.Scene);
            var target = DefId.From(definition.Target);
            var query = definition.TargetTags.Count == 0 && definition.ExcludeTags.Count == 0
                ? null
                : GameplayTagQuery.Resolve(tags, any: definition.TargetTags, none: definition.ExcludeTags);

            var verb = kind.Verb.Length == 0 ? GameplayTagRange.Empty : tags.RangeOf(kind.Verb);

            if (kind.Verb.Length > 0 && !verb.IsSome) {
                problems.Add(
                    $"'{owner}' {stage} counts '{kind.Verb}', which is not a tag in this build — so nothing "
                    + "can ever advance it. QuestModule declares the shipped verbs; a game's own "
                    + "objective type has to declare its own."
                );
            }

            foreach (var name in definition.TargetTags) {
                if (!tags.RangeOf(name).IsSome) {
                    problems.Add($"'{owner}' {stage} counts things tagged '{name}', which is not a tag in this build.");
                }
            }

            var fail = kind.FailVerb.Length > 0 ? tags.RangeOf(kind.FailVerb) : GameplayTagRange.Empty;

            compiled.Add(
                new(
                    definition,
                    kind,
                    new(verb, target, scene, query),
                    new(fail, DefId.None, scene)
                )
            );
        }

        return [.. compiled];
    }

    static QuestTemplate CompileQuest(
        QuestDefinition definition,
        GameplayTagTable tags,
        QuestObjectiveRegistry registry,
        List<string> problems
    ) {
        if (definition.Stages.Count == 0) {
            problems.Add($"'{definition.Address}' has no stages, so accepting it would finish it.");
        }

        var stages = new StageTemplate[definition.Stages.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < stages.Length; index++) {
            var stage = definition.Stages[index];

            if (stage.Id.Length > 0 && !ids.Add(stage.Id)) {
                problems.Add($"'{definition.Address}' has two stages called '{stage.Id}'.");
            }

            var objectives = CompileObjectives(
                definition.Address,
                $"stage '{(stage.Id.Length > 0 ? stage.Id : index.ToString(System.Globalization.CultureInfo.InvariantCulture))}'",
                stage.Objectives,
                tags,
                registry,
                problems
            );

            var required = objectives.Count(objective => !objective.IsOptional);

            if (objectives.Length > 0 && required == 0) {
                problems.Add(
                    $"'{definition.Address}' stage {index} has only optional objectives, so it would "
                    + "complete the moment it started."
                );
            }

            stages[index] = new(stage, index, objectives, required);
        }

        return new(
            definition,
            RequirementSet.Compile(definition.Requirements, tags),
            stages,
            CompileReward(definition.Rewards),
            tags.Resolve(definition.Tag),
            [.. definition.GrantsTags.Select(tags.Resolve).Where(tag => tag.IsSome)]
        );
    }

    static QuestReward CompileReward(QuestRewardDefinition? definition) {
        if (definition is null) {
            return QuestReward.None;
        }

        return new(
            Math.Max(0, definition.Experience),
            Grants(definition.Items),
            Grants(definition.Currencies),
            Grants(definition.Reputation),
            Grants(definition.Choices)
        );

        static QuestGrant[] Grants(List<QuestGrantDefinition> grants) =>
            [.. grants.Select(grant => new QuestGrant(DefId.From(grant.Def), grant.Def, grant.Count))];
    }
}
