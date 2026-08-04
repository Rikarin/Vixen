// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Progression;

/// <summary>How much experience each level costs.</summary>
/// <remarks>
///     <para>
///         <b>A table <em>and</em> a formula, because a designer wants both.</b> The first twenty
///         levels of any game are hand-tuned and the last eighty are a curve; a library that offered
///         only one of those makes somebody maintain a generated table by hand or wrap a formula
///         around twenty special cases.
///     </para>
///     <para>
///         ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///         <see cref="ModifierDefinition" />.
///     </para>
/// </remarks>
[DataContract("ExperienceCurveDefinition")]
public sealed record ExperienceCurveDefinition : Definition {
    /// <summary>The highest level anybody reaches.</summary>
    public int MaximumLevel { get; set; } = 80;

    /// <summary>What each level costs, from level one upwards. Shorter than the maximum falls through to the formula.</summary>
    public List<int> Thresholds { get; set; } = [];

    /// <summary>What level one costs, when the table has run out.</summary>
    public float Base { get; set; } = 1000f;

    /// <summary>What each level multiplies the last by, when the table has run out.</summary>
    public float Growth { get; set; } = 1.15f;
}

/// <summary>One thing a talent node needs before it can be taken.</summary>
[DataContract("TalentPrerequisite")]
public sealed class TalentPrerequisiteDefinition {
    /// <summary>Which node, by its id within the tree.</summary>
    public string Node { get; set; } = string.Empty;

    /// <summary>How many ranks of it.</summary>
    public int Ranks { get; set; } = 1;
}

/// <summary>One node of a talent tree.</summary>
[DataContract("TalentNode")]
public sealed class TalentNodeDefinition {
    /// <summary>What it is called within its tree. Unique there.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How many times it can be taken.</summary>
    public int MaximumRanks { get; set; } = 1;

    /// <summary>What each rank costs.</summary>
    public int CostPerRank { get; set; } = 1;

    /// <summary>How many points must be spent in this tree before it opens. The row gate.</summary>
    /// <remarks>
    ///     ⚠ <b>A total, not an order.</b> "Five points anywhere in this tree" is a property of the
    ///     finished allocation and can be checked from it; "you must have taken A before B" is a
    ///     property of a <em>sequence</em> and cannot. Since the server validates the whole
    ///     allocation rather than each click, only the first kind is expressible — which is a feature,
    ///     because the second kind is unverifiable after a respec anyway.
    /// </remarks>
    public int RequiredPoints { get; set; }

    /// <summary>What else must be taken first.</summary>
    public List<TalentPrerequisiteDefinition> Requires { get; set; } = [];

    /// <summary>What it does to the character's stats, per rank taken.</summary>
    public List<ModifierDefinition> Modifiers { get; set; } = [];

    /// <summary>Tags having any rank of it grants.</summary>
    public List<string> GrantsTags { get; set; } = [];
}

/// <summary>A talent tree: a DAG of nodes with costs, ranks and prerequisites.</summary>
[DataContract("TalentTreeDefinition")]
public sealed record TalentTreeDefinition : Definition {
    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Its nodes.</summary>
    public List<TalentNodeDefinition> Nodes { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var node in Nodes) {
            foreach (var tag in node.GrantsTags) {
                tags.Add(tag);
            }
        }
    }
}

/// <summary>A class specialisation: one of a set, chosen and changeable.</summary>
/// <remarks>
///     A definition rather than a talent node, because a specialisation is a <em>choice among
///     alternatives</em> and a tree is a set of independent yeses. Modelling it as a node would need
///     an "and none of these others" rule the tree does not have.
/// </remarks>
[DataContract("SpecialisationDefinition")]
public sealed record SpecialisationDefinition : Definition {
    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What having it is — <c>Specialisation.Fire</c>.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>The address of the talent tree it unlocks, or empty for none.</summary>
    public string TalentTree { get; set; } = string.Empty;

    /// <summary>What has to be true to choose it.</summary>
    public List<RequirementDefinition> Requirements { get; set; } = [];

    /// <summary>What choosing it does to the character's stats.</summary>
    public List<ModifierDefinition> Modifiers { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var requirement in Requirements) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }
    }
}

/// <summary>One step of a profession's skill line.</summary>
[DataContract("ProfessionTier")]
public sealed class ProfessionTierDefinition {
    /// <summary>What it is called in the UI — <c>Journeyman</c>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The skill at which it starts.</summary>
    public int Skill { get; set; }

    /// <summary>What reaching it is — <c>Profession.Smithing.Journeyman</c>.</summary>
    public string Tag { get; set; } = string.Empty;
}

/// <summary>A profession: one skill number and the tiers it passes through.</summary>
[DataContract("ProfessionDefinition")]
public sealed record ProfessionDefinition : Definition {
    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What having it at all is — <c>Profession.Smithing</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The same string names the tag and the number.</b> A requirement can then say
    ///     <c>HasTag(Profession.Smithing)</c> or <c>Profession.Smithing >= 300</c> and mean the
    ///     obvious thing by both, without a second vocabulary for "the name of the skill value".
    /// </remarks>
    public string Tag { get; set; } = string.Empty;

    /// <summary>The highest skill anybody reaches.</summary>
    public int MaximumSkill { get; set; } = 500;

    /// <summary>Its tiers, in ascending order of skill.</summary>
    public List<ProfessionTierDefinition> Tiers { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var tier in Tiers) {
            if (tier.Tag.Length > 0) {
                tags.Add(tier.Tag);
            }
        }
    }
}

/// <summary>One standing with a faction.</summary>
[DataContract("ReputationRank")]
public sealed class ReputationRankDefinition {
    /// <summary>What it is called in the UI — <c>Honoured</c>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The standing at which it starts. May be negative.</summary>
    public int Threshold { get; set; }

    /// <summary>What being at it is — <c>Faction.Ebonhawke.Honoured</c>.</summary>
    public string Tag { get; set; } = string.Empty;
}

/// <summary>A faction: one standing number and the ranks it passes through.</summary>
[DataContract("ReputationDefinition")]
public sealed record ReputationDefinition : Definition {
    /// <summary>What it is called in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What having any standing with it is — <c>Faction.Ebonhawke</c>. Also names the number.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>The worst it goes. Negative, for a faction that can be angered.</summary>
    public int Minimum { get; set; }

    /// <summary>The best it goes.</summary>
    public int Maximum { get; set; } = 42000;

    /// <summary>Its ranks, in ascending order of standing.</summary>
    public List<ReputationRankDefinition> Ranks { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var rank in Ranks) {
            if (rank.Tag.Length > 0) {
                tags.Add(rank.Tag);
            }
        }
    }
}
