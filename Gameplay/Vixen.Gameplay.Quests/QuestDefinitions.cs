// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Quests;

/// <summary>How often a quest can be done.</summary>
public enum QuestRepeat {
    /// <summary>Once ever.</summary>
    Once,

    /// <summary>Once per daily reset.</summary>
    Daily,

    /// <summary>Once per weekly reset.</summary>
    Weekly,

    /// <summary>As often as somebody likes.</summary>
    Always
}

/// <summary>One thing a quest gives, by address.</summary>
/// <remarks>
///     ⚠ <b>An address and a count, and this library never resolves either.</b> A quest that granted
///     an <c>ItemInstance</c> would need <c>Vixen.Gameplay.Items</c>, one that moved currency would
///     need the economy, and a game with quests would carry both whether or not it has items. Doc 28's
///     spine says a reward is content referenced by address and applied by whoever owns the thing —
///     so <see cref="QuestReward" /> is a list a caller reads, not a transaction this performs.
/// </remarks>
[DataContract("QuestGrant")]
public sealed class QuestGrantDefinition {
    /// <summary>The address of what is given — an item, a currency, a faction.</summary>
    public string Def { get; set; } = string.Empty;

    /// <summary>How much of it.</summary>
    public int Count { get; set; } = 1;
}

/// <summary>What turning a quest in pays.</summary>
[DataContract("QuestReward")]
public sealed class QuestRewardDefinition {
    /// <summary>Experience.</summary>
    public int Experience { get; set; }

    /// <summary>Items, always given.</summary>
    public List<QuestGrantDefinition> Items { get; set; } = [];

    /// <summary>Currencies, always given.</summary>
    public List<QuestGrantDefinition> Currencies { get; set; } = [];

    /// <summary>Faction standing, always given.</summary>
    public List<QuestGrantDefinition> Reputation { get; set; } = [];

    /// <summary>Items to pick one of, or empty when there is no choice to make.</summary>
    public List<QuestGrantDefinition> Choices { get; set; } = [];
}

/// <summary>One objective: a type, a count, and what it is counting.</summary>
/// <remarks>
///     <para>
///         <b>A <em>type</em> plus parameters, which is doc 28's phrasing and the reason a game can add
///         one.</b> <see cref="Type" /> names an <see cref="IQuestObjective" /> in the registry; every
///         other field here is a parameter that type may or may not read. The engine ships ten and a
///         game's eleventh is a class in its own assembly.
///     </para>
///     <para>
///         ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///         <see cref="ModifierDefinition" />.
///     </para>
/// </remarks>
[DataContract("QuestObjective")]
public sealed class QuestObjectiveDefinition {
    /// <summary>Which objective type — <c>Kill</c>, <c>Collect</c>, or a game's own.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>What the tracker says — <c>Undead slain</c>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>How many are needed. For <c>Survive</c>, how many seconds.</summary>
    public int Count { get; set; } = 1;

    /// <summary>The address of exactly what counts, or empty for anything the tags allow.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Tag prefixes the subject must match at least one of — <c>Creature.Undead</c>.</summary>
    public List<string> TargetTags { get; set; } = [];

    /// <summary>Tag prefixes the subject must match none of.</summary>
    public List<string> ExcludeTags { get; set; } = [];

    /// <summary>The address of the map it counts in, or empty for anywhere.</summary>
    public string Scene { get; set; } = string.Empty;

    /// <summary>Whether the stage completes without it.</summary>
    public bool Optional { get; set; }

    /// <summary>Whether the tracker shows it before it has been started.</summary>
    public bool Hidden { get; set; }
}

/// <summary>One stage of a quest: a set of objectives and what finishing them means.</summary>
[DataContract("QuestStage")]
public sealed class QuestStageDefinition {
    /// <summary>What it is called within its quest. Unique there.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What the tracker's heading says.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What the journal's body says.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Its objectives.</summary>
    public List<QuestObjectiveDefinition> Objectives { get; set; } = [];

    /// <summary>How long it may take, in seconds. Zero for no limit.</summary>
    /// <remarks>
    ///     ⚠ <b>Running out fails the quest rather than the stage.</b> A timed escort that reset to the
    ///     top of the stage would be a stage nobody could ever fail, and doc 28's failure paths are
    ///     what an event chain is made of.
    /// </remarks>
    public float TimeLimit { get; set; }
}

/// <summary>A quest: stages of objectives, what it takes to accept it, and what it pays.</summary>
[DataContract("QuestDefinition")]
public sealed record QuestDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What the giver says.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>What has to be true to accept it.</summary>
    public List<RequirementDefinition> Requirements { get; set; } = [];

    /// <summary>Its stages, in order.</summary>
    public List<QuestStageDefinition> Stages { get; set; } = [];

    /// <summary>What turning it in pays.</summary>
    public QuestRewardDefinition Rewards { get; set; } = new();

    /// <summary>
    ///     What having turned it in is — <c>Quest.Completed.Prologue</c>. Empty for a quest nothing
    ///     asks about.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A quest chain is a requirement, not a second mechanism.</b> "You must have finished the
    ///     prologue" is <c>HasTag(Quest.Completed.Prologue)</c>, evaluated by the same algebra a vendor
    ///     and an ability use — so the client greys the giver out for the same reason the realm refuses
    ///     the accept, and a designer who can write a vendor condition can already write a quest chain.
    /// </remarks>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Tags being on it grants, dropped when it is turned in or abandoned.</summary>
    public List<string> GrantsTags { get; set; } = [];

    /// <summary>How often it can be done.</summary>
    public QuestRepeat Repeat { get; set; }

    /// <summary>Whether a party member can be handed it.</summary>
    public bool Shareable { get; set; }

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var tag in GrantsTags) {
            tags.Add(tag);
        }

        foreach (var requirement in Requirements) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }

        foreach (var stage in Stages) {
            foreach (var objective in stage.Objectives) {
                foreach (var tag in objective.TargetTags) {
                    tags.Add(tag);
                }

                foreach (var tag in objective.ExcludeTags) {
                    tags.Add(tag);
                }
            }
        }
    }
}
