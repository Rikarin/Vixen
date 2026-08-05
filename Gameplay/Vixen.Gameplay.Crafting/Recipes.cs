// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Crafting;

/// <summary>How a recipe is come by.</summary>
public enum RecipeSource {
    /// <summary>Known from the start.</summary>
    Known,

    /// <summary>Bought, or given by a quest.</summary>
    Taught,

    /// <summary>Found by putting the right things in the pot.</summary>
    Discovered
}

/// <summary>Why a craft was refused.</summary>
public enum CraftingRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>This build has no such recipe.</summary>
    Unknown,

    /// <summary>They have not learned it.</summary>
    NotLearned,

    /// <summary>They are not at the right station.</summary>
    WrongStation,

    /// <summary>They do not have the inputs.</summary>
    Missing,

    /// <summary>A requirement is not met, or their skill is too low.</summary>
    Requirements
}

/// <summary>One thing a recipe takes or makes.</summary>
[DataContract("RecipeItem")]
public sealed class RecipeItemDefinition {
    /// <summary>The address of what.</summary>
    public string Item { get; set; } = string.Empty;

    /// <summary>How many.</summary>
    public int Count { get; set; } = 1;
}

/// <summary>A recipe: what goes in, where, and what comes out.</summary>
[DataContract("RecipeDefinition")]
public sealed record RecipeDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Which profession's it is — <c>Profession.Smithing</c>. Also the skill it reads.</summary>
    public string Profession { get; set; } = string.Empty;

    /// <summary>What station it needs — <c>Interactable.Station.Forge</c>. Empty for anywhere.</summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>What it takes.</summary>
    public List<RecipeItemDefinition> Inputs { get; set; } = [];

    /// <summary>What it makes.</summary>
    public List<RecipeItemDefinition> Outputs { get; set; } = [];

    /// <summary>How it is come by.</summary>
    public RecipeSource Source { get; set; }

    /// <summary>The skill at which it can first be made.</summary>
    public int SkillRequired { get; set; }

    /// <summary>The skill at which it stops teaching anything.</summary>
    /// <remarks>
    ///     ⚠ <b>The other end of the band, and it is what makes skill gain fall off.</b> A recipe that
    ///     taught the same amount for ever would let somebody max a profession on the cheapest thing
    ///     they can make, which is the grind every crafting system is trying not to be.
    /// </remarks>
    public int SkillCap { get; set; }

    /// <summary>How much skill one success is worth at the bottom of the band.</summary>
    public int SkillGain { get; set; } = 1;

    /// <summary>How likely a better result is, from zero to one.</summary>
    public float QualityChance { get; set; }

    /// <summary>What else has to be true.</summary>
    public List<RequirementDefinition> Requires { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Profession.Length > 0) {
            tags.Add(Profession);
        }

        if (Station.Length > 0) {
            tags.Add(Station);
        }

        foreach (var requirement in Requires) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }
    }
}

/// <summary>One input or output, resolved.</summary>
/// <param name="Item">What.</param>
/// <param name="Address">Its address, kept so a report can name it.</param>
/// <param name="Count">How many.</param>
public readonly record struct RecipeItem(DefId Item, string Address, int Count);

/// <summary>A recipe with its names resolved.</summary>
public sealed class Recipe {
    readonly RecipeItem[] inputs;
    readonly RecipeItem[] outputs;

    internal Recipe(
        RecipeDefinition definition,
        GameplayTag profession,
        GameplayTagRange station,
        RequirementSet requirements,
        RecipeItem[] inputs,
        RecipeItem[] outputs,
        string signature
    ) {
        Definition = definition;
        Profession = profession;
        Station = station;
        Requirements = requirements;
        this.inputs = inputs;
        this.outputs = outputs;
        Signature = signature;
    }

    /// <summary>What it was compiled from.</summary>
    public RecipeDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>Whose profession it is, and what a skill lookup is keyed by.</summary>
    public GameplayTag Profession { get; }

    /// <summary>What station it needs, or an empty range for anywhere.</summary>
    public GameplayTagRange Station { get; }

    /// <summary>What else has to be true.</summary>
    public RequirementSet Requirements { get; }

    /// <summary>What it takes.</summary>
    public ReadOnlySpan<RecipeItem> Inputs => inputs;

    /// <summary>What it makes.</summary>
    public ReadOnlySpan<RecipeItem> Outputs => outputs;

    /// <summary>How it is come by.</summary>
    public RecipeSource Source => Definition.Source;

    /// <summary>The skill at which it can first be made.</summary>
    public int SkillRequired => Math.Max(0, Definition.SkillRequired);

    /// <summary>The skill at which it stops teaching.</summary>
    public int SkillCap => Math.Max(SkillRequired, Definition.SkillCap);

    /// <summary>Its inputs as one comparable string. What discovery matches on.</summary>
    public string Signature { get; }

    /// <summary>How much skill one success is worth at a given skill.</summary>
    /// <param name="skill">Theirs.</param>
    /// <returns>The gain, never below zero.</returns>
    /// <remarks>
    ///     ⚠ <b>It falls linearly to nothing across the band and does not stop abruptly.</b> A cliff
    ///     makes the last point before it the only one worth making, and everybody makes exactly that
    ///     one thing until the number changes.
    /// </remarks>
    public int GainAt(int skill) {
        if (skill >= SkillCap) {
            return 0;
        }

        if (skill <= SkillRequired || SkillCap == SkillRequired) {
            return Math.Max(0, Definition.SkillGain);
        }

        var through = (double)(skill - SkillRequired) / (SkillCap - SkillRequired);

        return Math.Max(0, (int)Math.Ceiling(Definition.SkillGain * (1d - through)));
    }
}
