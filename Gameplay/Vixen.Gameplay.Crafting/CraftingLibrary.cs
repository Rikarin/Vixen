// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Gameplay.Crafting;

/// <summary>What a craft would do.</summary>
/// <param name="Recipe">Which recipe.</param>
/// <param name="Consumed">What to take, by address.</param>
/// <param name="Produced">What to give, by address.</param>
/// <param name="Quality">Whether it came out better than usual.</param>
/// <param name="SkillGained">How much the crafter learned.</param>
/// <remarks>
///     ⚠ <b>A list, not a transaction — the same rule the whole framework has by now.</b> Nothing here
///     moves an item; the caller's containers do, and doc 28's spine is what keeps a crafting library
///     from depending on an inventory.
/// </remarks>
public readonly record struct CraftingResult(
    DefId Recipe,
    IReadOnlyList<RecipeItem> Consumed,
    IReadOnlyList<RecipeItem> Produced,
    bool Quality,
    int SkillGained
);

/// <summary>Every recipe a build knows, compiled once.</summary>
public sealed class CraftingLibrary {
    readonly Dictionary<uint, Recipe> recipes;
    readonly Dictionary<string, Recipe> bySignature;
    readonly string[] problems;

    CraftingLibrary(Dictionary<uint, Recipe> recipes, Dictionary<string, Recipe> bySignature, string[] problems) {
        this.recipes = recipes;
        this.bySignature = bySignature;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static CraftingLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every recipe, in address order.</summary>
    public IEnumerable<Recipe> Recipes =>
        recipes.Values.OrderBy(recipe => recipe.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static CraftingLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var recipes = new Dictionary<uint, Recipe>();
        var bySignature = new Dictionary<string, Recipe>(StringComparer.Ordinal);

        foreach (var definition in catalog.OfType<RecipeDefinition>()) {
            if (definition.Inputs.Count == 0) {
                problems.Add($"'{definition.Address}' takes nothing, so it makes something out of air.");
            }

            if (definition.Outputs.Count == 0) {
                problems.Add($"'{definition.Address}' makes nothing, so it only destroys things.");
            }

            if (definition.SkillCap > 0 && definition.SkillCap < definition.SkillRequired) {
                problems.Add(
                    $"'{definition.Address}' stops teaching at {definition.SkillCap} and cannot be made "
                    + $"until {definition.SkillRequired}, so it never teaches anything."
                );
            }

            var inputs = Items(definition.Inputs);
            var outputs = Items(definition.Outputs);
            var signature = SignatureOf(inputs);

            var recipe = new Recipe(
                definition,
                tags.Resolve(definition.Profession),
                definition.Station.Length > 0 ? tags.RangeOf(definition.Station) : GameplayTagRange.Empty,
                RequirementSet.Compile(definition.Requires, tags),
                inputs,
                outputs,
                signature
            );

            recipes.Add(definition.Id.Value, recipe);

            if (definition.Source != RecipeSource.Discovered) {
                continue;
            }

            if (!bySignature.TryAdd(signature, recipe)) {
                problems.Add(
                    $"'{definition.Address}' is discovered from the same inputs as "
                    + $"'{bySignature[signature].Definition.Address}', so only one of them can ever be found."
                );
            }
        }

        return new(recipes, bySignature, [.. problems]);

        static RecipeItem[] Items(List<RecipeItemDefinition> items) => [
            .. items
                .Select(item => new RecipeItem(DefId.From(item.Item), item.Item, Math.Max(1, item.Count)))
                .OrderBy(item => item.Item.Value)
        ];
    }

    /// <summary>Finds a recipe.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Recipe? Find(DefId id) => recipes.GetValueOrDefault(id.Value);

    /// <summary>Finds the discoverable recipe a set of ingredients makes, if there is one.</summary>
    /// <param name="ingredients">What was put in.</param>
    /// <returns>The recipe, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>An exact match, not a superset.</b> Matching a subset would mean throwing everything in
    ///     the pot discovers every recipe at once, which is not experimentation — it is a button. The
    ///     inputs are sorted by id at compile time so the order somebody adds them in does not matter.
    /// </remarks>
    public Recipe? Discover(IReadOnlyList<RecipeItem> ingredients) {
        ArgumentNullException.ThrowIfNull(ingredients);

        return bySignature.GetValueOrDefault(SignatureOf([.. ingredients.OrderBy(item => item.Item.Value)]));
    }

    static string SignatureOf(IReadOnlyList<RecipeItem> items) =>
        string.Join(
            ',',
            items.Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.Item.Value}x{item.Count}"))
        );
}

/// <summary>One character's crafting: what they know, and what they can make right now.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28: "nothing here is technically hard; the value is in it being <em>the same</em>
///         system as gathering and using the same requirement algebra".</b> A station is a tag query,
///         so a forge and an enchanted forge both satisfy one recipe; a skill is a number a
///         <c>ProgressionState</c> already answers; a gate is a <see cref="RequirementSet" />.
///     </para>
///     <para>
///         ⚠ <b>It consumes nothing and produces nothing.</b> <see cref="Craft" /> reports what must
///         move, exactly as <c>QuestJournal.TurnIn</c> and the economy's intents do.
///     </para>
/// </remarks>
public sealed class Crafter {
    readonly HashSet<uint> known = [];

    /// <summary>Makes one.</summary>
    /// <param name="library">Where the recipes come from.</param>
    /// <param name="skills">What answers "how good are they at this", or null.</param>
    public Crafter(CraftingLibrary library, IRequirementContext? skills = null) {
        ArgumentNullException.ThrowIfNull(library);

        Library = library;
        Skills = skills;
    }

    /// <summary>Where the recipes come from.</summary>
    public CraftingLibrary Library { get; }

    /// <summary>What answers their skill and their requirements.</summary>
    public IRequirementContext? Skills { get; }

    /// <summary>How many recipes they have learned beyond the ones everybody knows.</summary>
    public int Learned => known.Count;

    /// <summary>Raised when a recipe is learned, however it was.</summary>
    public event Action<Recipe>? Discovered;

    /// <summary>Whether they know a recipe.</summary>
    /// <param name="recipe">Which one.</param>
    /// <returns>Whether they do.</returns>
    public bool Knows(Recipe recipe) {
        ArgumentNullException.ThrowIfNull(recipe);

        return recipe.Source == RecipeSource.Known || known.Contains(recipe.Id.Value);
    }

    /// <summary>Teaches them one.</summary>
    /// <param name="recipe">Which one.</param>
    /// <returns>Whether it was new to them.</returns>
    public bool Learn(Recipe recipe) {
        ArgumentNullException.ThrowIfNull(recipe);

        if (!known.Add(recipe.Id.Value)) {
            return false;
        }

        Discovered?.Invoke(recipe);

        return true;
    }

    /// <summary>How good they are at a recipe's profession.</summary>
    /// <param name="recipe">Which one.</param>
    /// <returns>Their skill, or zero.</returns>
    public int SkillIn(Recipe recipe) {
        ArgumentNullException.ThrowIfNull(recipe);

        if (Skills is null || recipe.Definition.Profession.Length == 0) {
            return 0;
        }

        return Skills.TryGetValue(AttributeId.From(recipe.Definition.Profession), out var skill) ? (int)skill : 0;
    }

    /// <summary>Whether they can make something, and why not.</summary>
    /// <param name="recipe">Which one.</param>
    /// <param name="stations">The tags of whatever they are standing at.</param>
    /// <param name="holdings">How much of each thing they have, by asset id.</param>
    /// <returns>The refusal, or <see cref="CraftingRefusal.None" />.</returns>
    public CraftingRefusal CanCraft(Recipe recipe, GameplayTagSet? stations, IReadOnlyDictionary<uint, int> holdings) {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(holdings);

        if (!Knows(recipe)) {
            return CraftingRefusal.NotLearned;
        }

        // A tag query, so a forge and an enchanted forge both satisfy one recipe.
        if (recipe.Station.IsSome && stations?.ContainsAny(recipe.Station) != true) {
            return CraftingRefusal.WrongStation;
        }

        if (SkillIn(recipe) < recipe.SkillRequired) {
            return CraftingRefusal.Requirements;
        }

        if (Skills is not null && !recipe.Requirements.IsMetBy(Skills)) {
            return CraftingRefusal.Requirements;
        }

        foreach (ref readonly var input in recipe.Inputs) {
            if (holdings.GetValueOrDefault(input.Item.Value) < input.Count) {
                return CraftingRefusal.Missing;
            }
        }

        return CraftingRefusal.None;
    }

    /// <summary>Makes something, and says what to move.</summary>
    /// <param name="recipe">Which one.</param>
    /// <param name="stations">The tags of whatever they are standing at.</param>
    /// <param name="holdings">How much of each thing they have.</param>
    /// <param name="attempt">What makes this craft distinct — a counter, or an event id.</param>
    /// <param name="result">What must move.</param>
    /// <returns>The refusal, or <see cref="CraftingRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>The quality roll is seeded from the recipe and the attempt, so it is reproducible.</b>
    ///     The same property the loot library gives a drop, and for the same reason: "the log says it
    ///     came out ordinary" has to be answerable.
    /// </remarks>
    public CraftingRefusal Craft(
        Recipe recipe,
        GameplayTagSet? stations,
        IReadOnlyDictionary<uint, int> holdings,
        ulong attempt,
        out CraftingResult result
    ) {
        result = default;

        var refusal = CanCraft(recipe, stations, holdings);

        if (refusal != CraftingRefusal.None) {
            return refusal;
        }

        var random = GameplayRandom.For(attempt, recipe.Id.Value);
        var quality = recipe.Definition.QualityChance > 0f && random.Chance(recipe.Definition.QualityChance);
        var skill = SkillIn(recipe);

        result = new(recipe.Id, recipe.Inputs.ToArray(), recipe.Outputs.ToArray(), quality, recipe.GainAt(skill));

        return CraftingRefusal.None;
    }

    /// <summary>Tries to find a recipe by putting things in the pot.</summary>
    /// <param name="ingredients">What was put in.</param>
    /// <param name="recipe">What was found, or null.</param>
    /// <returns>Whether anything was found that they did not already know.</returns>
    /// <remarks>
    ///     ⚠ <b>Discovering something teaches it and consumes nothing here.</b> Whether a failed
    ///     experiment costs the ingredients is a game's decision, and it is one the caller makes with
    ///     the containers in hand.
    /// </remarks>
    public bool TryDiscover(IReadOnlyList<RecipeItem> ingredients, out Recipe? recipe) {
        recipe = Library.Discover(ingredients);

        return recipe is not null && Learn(recipe);
    }
}
