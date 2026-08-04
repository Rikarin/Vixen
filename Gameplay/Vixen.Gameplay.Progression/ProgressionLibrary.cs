// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Progression;

/// <summary>An experience curve with its table filled in.</summary>
public sealed class ExperienceCurve {
    readonly int[] thresholds;

    internal ExperienceCurve(ExperienceCurveDefinition definition, int[] thresholds) {
        Definition = definition;
        this.thresholds = thresholds;
    }

    /// <summary>The curve every game gets when it authors none: eighty levels of gentle growth.</summary>
    public static ExperienceCurve Default { get; } = Compile(new());

    /// <summary>What it was compiled from.</summary>
    public ExperienceCurveDefinition Definition { get; }

    /// <summary>The highest level anybody reaches.</summary>
    public int MaximumLevel => thresholds.Length + 1;

    /// <summary>Compiles a curve, filling the table out of the formula where it runs short.</summary>
    /// <param name="definition">What was authored.</param>
    /// <returns>The curve.</returns>
    public static ExperienceCurve Compile(ExperienceCurveDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var levels = Math.Max(1, definition.MaximumLevel);
        var thresholds = new int[levels - 1];
        var running = MathF.Max(1f, definition.Base);
        var growth = MathF.Max(1f, definition.Growth);

        for (var level = 0; level < thresholds.Length; level++) {
            if (level < definition.Thresholds.Count) {
                thresholds[level] = Math.Max(1, definition.Thresholds[level]);
                running = thresholds[level] * growth;

                continue;
            }

            thresholds[level] = Math.Max(1, (int)running);
            running *= growth;
        }

        return new(definition, thresholds);
    }

    /// <summary>What it costs to go from one level to the next.</summary>
    /// <param name="level">The level being left, counting from one.</param>
    /// <returns>The experience, or zero at the maximum.</returns>
    public int CostOf(int level) =>
        level >= 1 && level <= thresholds.Length ? thresholds[level - 1] : 0;

    /// <summary>What it costs to get from level one to a level.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The experience.</returns>
    public long TotalTo(int level) {
        var total = 0L;

        for (var step = 1; step < Math.Min(level, MaximumLevel); step++) {
            total += thresholds[step - 1];
        }

        return total;
    }
}

/// <summary>A profession or a faction with its ranks compiled and sorted.</summary>
/// <typeparam name="TDefinition">Which of the two.</typeparam>
public sealed class RankedTrack<TDefinition> where TDefinition : Definition {
    readonly (int Threshold, GameplayTag Tag, string Name)[] ranks;

    internal RankedTrack(
        TDefinition definition,
        GameplayTag tag,
        AttributeId value,
        int minimum,
        int maximum,
        (int Threshold, GameplayTag Tag, string Name)[] ranks
    ) {
        Definition = definition;
        Tag = tag;
        Value = value;
        Minimum = minimum;
        Maximum = maximum;
        this.ranks = ranks;
    }

    /// <summary>What it was compiled from.</summary>
    public TDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What having it at all is.</summary>
    public GameplayTag Tag { get; }

    /// <summary>What a requirement calls its number — the same string as <see cref="Tag" />.</summary>
    public AttributeId Value { get; }

    /// <summary>The lowest it goes.</summary>
    public int Minimum { get; }

    /// <summary>The highest it goes.</summary>
    public int Maximum { get; }

    /// <summary>Its ranks, lowest first.</summary>
    public ReadOnlySpan<(int Threshold, GameplayTag Tag, string Name)> Ranks => ranks;

    /// <summary>Which rank a value is at.</summary>
    /// <param name="value">The skill or the standing.</param>
    /// <returns>Its index, or −1 when it is below the lowest rank.</returns>
    /// <remarks>
    ///     ⚠ <b>The highest rank whose threshold has been reached, walked from the top.</b> A search
    ///     from the bottom that stopped at the first threshold *above* the value gets the answer
    ///     right only when the ranks are sorted, and gets it silently wrong when a designer adds one
    ///     out of order — which is why they are sorted at compile time and this walks down.
    /// </remarks>
    public int RankAt(int value) {
        for (var index = ranks.Length - 1; index >= 0; index--) {
            if (value >= ranks[index].Threshold) {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>A specialisation with its names resolved.</summary>
public sealed class Specialisation {
    readonly Modifier[] modifiers;

    internal Specialisation(
        SpecialisationDefinition definition,
        GameplayTag tag,
        DefId tree,
        RequirementSet requirements,
        Modifier[] modifiers
    ) {
        Definition = definition;
        Tag = tag;
        TalentTree = tree;
        Requirements = requirements;
        this.modifiers = modifiers;
    }

    /// <summary>What it was compiled from.</summary>
    public SpecialisationDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What having it is.</summary>
    public GameplayTag Tag { get; }

    /// <summary>Which talent tree it unlocks, or <see cref="DefId.None" />.</summary>
    public DefId TalentTree { get; }

    /// <summary>What has to be true to choose it.</summary>
    public RequirementSet Requirements { get; }

    /// <summary>What choosing it does to the character's stats.</summary>
    public ReadOnlySpan<Modifier> Modifiers => modifiers;
}

/// <summary>Every progression definition a build knows, compiled once.</summary>
public sealed class ProgressionLibrary {
    readonly Dictionary<uint, TalentTree> trees;
    readonly Dictionary<uint, RankedTrack<ProfessionDefinition>> professions;
    readonly Dictionary<uint, RankedTrack<ReputationDefinition>> reputations;
    readonly Dictionary<uint, Specialisation> specialisations;
    readonly string[] problems;

    ProgressionLibrary(
        ExperienceCurve curve,
        Dictionary<uint, TalentTree> trees,
        Dictionary<uint, RankedTrack<ProfessionDefinition>> professions,
        Dictionary<uint, RankedTrack<ReputationDefinition>> reputations,
        Dictionary<uint, Specialisation> specialisations,
        string[] problems
    ) {
        Curve = curve;
        this.trees = trees;
        this.professions = professions;
        this.reputations = reputations;
        this.specialisations = specialisations;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it, and the default curve.</summary>
    public static ProgressionLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>The experience curve. The default one when the catalog authored none.</summary>
    public ExperienceCurve Curve { get; }

    /// <summary>Every talent tree, in address order.</summary>
    public IEnumerable<TalentTree> Trees =>
        trees.Values.OrderBy(tree => tree.Definition.Address, StringComparer.Ordinal);

    /// <summary>Every profession, in address order.</summary>
    public IEnumerable<RankedTrack<ProfessionDefinition>> Professions =>
        professions.Values.OrderBy(track => track.Definition.Address, StringComparer.Ordinal);

    /// <summary>Every faction, in address order.</summary>
    public IEnumerable<RankedTrack<ReputationDefinition>> Reputations =>
        reputations.Values.OrderBy(track => track.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static ProgressionLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();

        var curves = catalog.OfType<ExperienceCurveDefinition>().ToList();

        if (curves.Count > 1) {
            problems.Add(
                $"This build has {curves.Count} experience curves. A character has one level, so it has "
                + "one curve; the first by address wins."
            );
        }

        var curve = curves.Count > 0 ? ExperienceCurve.Compile(curves[0]) : ExperienceCurve.Default;

        var trees = new Dictionary<uint, TalentTree>();

        foreach (var definition in catalog.OfType<TalentTreeDefinition>()) {
            trees.Add(definition.Id.Value, CompileTree(definition, tags, problems));
        }

        var professions = new Dictionary<uint, RankedTrack<ProfessionDefinition>>();

        foreach (var definition in catalog.OfType<ProfessionDefinition>()) {
            professions.Add(
                definition.Id.Value,
                new(
                    definition,
                    tags.Resolve(definition.Tag),
                    AttributeId.From(definition.Tag),
                    0,
                    Math.Max(0, definition.MaximumSkill),
                    Sorted(definition.Tiers.Select(tier => (tier.Skill, tags.Resolve(tier.Tag), tier.DisplayName)))
                )
            );
        }

        var reputations = new Dictionary<uint, RankedTrack<ReputationDefinition>>();

        foreach (var definition in catalog.OfType<ReputationDefinition>()) {
            reputations.Add(
                definition.Id.Value,
                new(
                    definition,
                    tags.Resolve(definition.Tag),
                    AttributeId.From(definition.Tag),
                    definition.Minimum,
                    definition.Maximum,
                    Sorted(definition.Ranks.Select(rank => (rank.Threshold, tags.Resolve(rank.Tag), rank.DisplayName)))
                )
            );
        }

        var specialisations = new Dictionary<uint, Specialisation>();

        foreach (var definition in catalog.OfType<SpecialisationDefinition>()) {
            if (definition.TalentTree.Length > 0 && !trees.ContainsKey(DefId.From(definition.TalentTree).Value)) {
                problems.Add(
                    $"'{definition.Address}' unlocks talent tree '{definition.TalentTree}', which is not in "
                    + "this build."
                );
            }

            specialisations.Add(
                definition.Id.Value,
                new(
                    definition,
                    tags.Resolve(definition.Tag),
                    DefId.From(definition.TalentTree),
                    RequirementSet.Compile(definition.Requirements, tags),
                    [
                        .. definition.Modifiers.Select(
                            modifier => new Modifier(
                                AttributeId.From(modifier.Attribute),
                                modifier.Op,
                                modifier.Value,
                                ModifierSource.None
                            )
                        )
                    ]
                )
            );
        }

        return new(curve, trees, professions, reputations, specialisations, [.. problems]);
    }

    /// <summary>Finds a talent tree.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public TalentTree? FindTree(DefId id) => trees.GetValueOrDefault(id.Value);

    /// <summary>Finds a profession.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public RankedTrack<ProfessionDefinition>? FindProfession(DefId id) => professions.GetValueOrDefault(id.Value);

    /// <summary>Finds a faction.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public RankedTrack<ReputationDefinition>? FindReputation(DefId id) => reputations.GetValueOrDefault(id.Value);

    /// <summary>Finds a specialisation.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Specialisation? FindSpecialisation(DefId id) => specialisations.GetValueOrDefault(id.Value);

    /// <summary>Every specialisation, in address order.</summary>
    public IEnumerable<Specialisation> Specialisations =>
        specialisations.Values.OrderBy(entry => entry.Definition.Address, StringComparer.Ordinal);

    static (int, GameplayTag, string)[] Sorted(IEnumerable<(int Threshold, GameplayTag Tag, string Name)> ranks) {
        var sorted = ranks.ToArray();

        Array.Sort(sorted, static (left, right) => left.Threshold.CompareTo(right.Threshold));

        return sorted;
    }

    static TalentTree CompileTree(TalentTreeDefinition definition, GameplayTagTable tags, List<string> problems) {
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < definition.Nodes.Count; index++) {
            var id = definition.Nodes[index].Id;

            if (id.Length == 0) {
                problems.Add($"'{definition.Address}' has a node with no id, so nothing can refer to it.");

                continue;
            }

            if (!byId.TryAdd(id, index)) {
                problems.Add($"'{definition.Address}' has two nodes called '{id}'.");
            }
        }

        var nodes = new TalentNode[definition.Nodes.Count];

        for (var index = 0; index < nodes.Length; index++) {
            var node = definition.Nodes[index];
            var requires = new List<(int, int)>(node.Requires.Count);

            foreach (var prerequisite in node.Requires) {
                if (!byId.TryGetValue(prerequisite.Node, out var required)) {
                    problems.Add($"'{definition.Address}' node '{node.Id}' needs '{prerequisite.Node}', which is not in it.");

                    continue;
                }

                requires.Add((required, Math.Max(1, prerequisite.Ranks)));
            }

            nodes[index] = new(
                node,
                index,
                [.. node.GrantsTags.Select(tags.Resolve)],
                [
                    .. node.Modifiers.Select(
                        modifier => new Modifier(
                            AttributeId.From(modifier.Attribute),
                            modifier.Op,
                            modifier.Value,
                            ModifierSource.None
                        )
                    )
                ],
                [.. requires]
            );
        }

        // ⚠ A cycle is refused rather than tolerated: two nodes each needing the other are two nodes
        // nobody can ever take, and a tree whose gate is unreachable looks like a balance decision
        // rather than a mistake. A depth-first walk over the prerequisite edges finds it.
        var state = new byte[nodes.Length];

        for (var index = 0; index < nodes.Length; index++) {
            if (HasCycle(nodes, state, index)) {
                problems.Add(
                    $"'{definition.Address}' has a cycle in its prerequisites, so some of its nodes can "
                    + "never be taken."
                );

                break;
            }
        }

        return new(definition, nodes, byId);
    }

    static bool HasCycle(TalentNode[] nodes, byte[] state, int index) {
        if (state[index] == 1) {
            return true;
        }

        if (state[index] == 2) {
            return false;
        }

        state[index] = 1;

        foreach (var (required, _) in nodes[index].Requires) {
            if (HasCycle(nodes, state, required)) {
                return true;
            }
        }

        state[index] = 2;

        return false;
    }
}
