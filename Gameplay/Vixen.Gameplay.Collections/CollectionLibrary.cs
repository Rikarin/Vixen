// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Collections;

/// <summary>Every collectible and every achievement a build knows, compiled once.</summary>
public sealed class CollectionLibrary {
    readonly Dictionary<uint, Collectible> collectibles;
    readonly Dictionary<uint, Achievement> achievements;
    readonly Collectible[] byAddress;
    readonly Achievement[] achievementsByAddress;
    readonly string[] problems;

    CollectionLibrary(
        Dictionary<uint, Collectible> collectibles,
        Dictionary<uint, Achievement> achievements,
        string[] problems
    ) {
        this.collectibles = collectibles;
        this.achievements = achievements;
        this.problems = problems;

        // Materialised once. A record settles achievements on every unlock, and re-sorting a build's
        // whole achievement list inside that loop is the kind of thing nobody profiles until it is
        // three thousand rows.
        byAddress = [.. collectibles.Values.OrderBy(entry => entry.Definition.Address, StringComparer.Ordinal)];
        achievementsByAddress = [
            .. achievements.Values.OrderBy(entry => entry.Definition.Address, StringComparer.Ordinal)
        ];

        Points = achievementsByAddress.Sum(achievement => achievement.Points);
    }

    /// <summary>A library with nothing in it.</summary>
    public static CollectionLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every collectible, in address order.</summary>
    public ReadOnlySpan<Collectible> Collectibles => byAddress;

    /// <summary>Every achievement, in address order.</summary>
    public ReadOnlySpan<Achievement> Achievements => achievementsByAddress;

    /// <summary>Every point in the build. What a completion percentage divides by.</summary>
    public int Points { get; }

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static CollectionLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var collectibles = new Dictionary<uint, Collectible>();
        var achievements = new Dictionary<uint, Achievement>();

        foreach (var definition in catalog.OfType<CollectibleDefinition>()) {
            if (definition.Kind == CollectibleKind.Appearance && definition.Slot.Length == 0) {
                problems.Add(
                    $"'{definition.Address}' is an appearance with no slot, so the wardrobe has nothing "
                    + "to put it in front of."
                );
            }

            if (definition.Kind != CollectibleKind.Appearance && definition.Slot.Length > 0) {
                problems.Add(
                    $"'{definition.Address}' is a {definition.Kind} with a slot, which only an appearance "
                    + "has anywhere to use."
                );
            }

            collectibles.Add(
                definition.Id.Value,
                new(
                    definition,
                    tags.Resolve(definition.Tag),
                    tags.Resolve(definition.Slot),
                    RequirementSet.Compile(definition.Requires, tags)
                )
            );
        }

        foreach (var definition in catalog.OfType<AchievementDefinition>()) {
            if (definition.Criteria.Count == 0 && definition.Requires.Count == 0) {
                problems.Add(
                    $"'{definition.Address}' asks for nothing at all, so it is earned the first time "
                    + "anybody looks at it."
                );
            }

            var criteria = new AchievementCriterion[definition.Criteria.Count];

            for (var index = 0; index < criteria.Length; index++) {
                var criterion = definition.Criteria[index];
                var filter = new GameplayEventFilter(
                    tags.RangeOf(criterion.Verb),
                    criterion.Subject.Length > 0 ? DefId.From(criterion.Subject) : DefId.None,
                    criterion.Scene.Length > 0 ? DefId.From(criterion.Scene) : DefId.None,
                    GameplayTagQuery.Resolve(tags, criterion.All, criterion.Any, criterion.None)
                );

                // ⚠ The kernel's empty-range trap, caught rather than left as an achievement that
                // silently never advances — but the reachable half of it is the criterion with no
                // verb at all. A *misspelt* verb cannot be caught here: CollectTags hands every verb
                // a criterion names to the content build, which bakes it, so it resolves. What that
                // costs is real and is recorded in the README: a criterion counting a verb nothing
                // ever posts is undetectable without a list of what the composition posts.
                if (criterion.Verb.Length == 0) {
                    problems.Add(
                        $"'{definition.Address}' criterion {index} has no verb, so nothing will ever "
                        + "advance it."
                    );
                } else if (!filter.IsSome) {
                    problems.Add(
                        $"'{definition.Address}' criterion {index} counts '{criterion.Verb}', which is not "
                        + "a tag in this build — so nothing will ever advance it."
                    );
                }

                if (criterion.Count <= 0) {
                    problems.Add(
                        $"'{definition.Address}' criterion {index} asks for {criterion.Count} of "
                        + $"'{criterion.Verb}', which is a criterion that is already done."
                    );
                }

                criteria[index] = new(criterion, index, filter);
            }

            var unlocks = new List<DefId>();

            foreach (var address in definition.Unlocks) {
                var id = DefId.From(address);

                if (!collectibles.ContainsKey(id.Value) && !catalog.Contains(id)) {
                    problems.Add($"'{definition.Address}' unlocks '{address}', which is not in this build.");
                }

                unlocks.Add(id);
            }

            achievements.Add(
                definition.Id.Value,
                new(
                    definition,
                    tags.Resolve(definition.Tag),
                    criteria,
                    RequirementSet.Compile(definition.Requires, tags),
                    [.. unlocks]
                )
            );
        }

        // ⚠ An achievement that requires the tag it grants can never be earned, and it reads perfectly
        // well in a spreadsheet. Reported here because nothing at runtime would ever say so.
        foreach (var achievement in achievements.Values) {
            if (!achievement.Tag.IsSome) {
                continue;
            }

            foreach (var requirement in achievement.Requirements.Requirements) {
                if (requirement.Kind == RequirementKind.HasTag && requirement.Range.Contains(achievement.Tag)) {
                    problems.Add(
                        $"'{achievement.Definition.Address}' requires the tag it grants, so earning it is "
                        + "its own precondition."
                    );

                    break;
                }
            }
        }

        return new(collectibles, achievements, [.. problems]);
    }

    /// <summary>Finds a collectible.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Collectible? Find(DefId id) => collectibles.GetValueOrDefault(id.Value);

    /// <summary>Finds an achievement.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Achievement? FindAchievement(DefId id) => achievements.GetValueOrDefault(id.Value);

    /// <summary>Every collectible of one kind, in address order.</summary>
    /// <param name="kind">Which sort.</param>
    /// <returns>Them.</returns>
    public IEnumerable<Collectible> OfKind(CollectibleKind kind) =>
        byAddress.Where(collectible => collectible.Kind == kind);
}
