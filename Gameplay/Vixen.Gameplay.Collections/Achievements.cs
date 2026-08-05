// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Collections;

/// <summary>One thing that has to happen a number of times.</summary>
/// <remarks>
///     <para>
///         <b>Where this diverges from doc 28's G-Q3, and why.</b> G-Q3 answers that achievements
///         belong in Collections because <em>"an achievement is an unlock with criteria, criteria are
///         tag queries, and the state shape is identical"</em>. The first and third are exactly right
///         and this library is built on them. The second covers "have all five keys" and does not
///         cover "kill thirty boars" — a tag query is a standing test and cannot count. So an
///         achievement here has both: <see cref="AchievementDefinition.Requires" /> is the standing
///         half, in the kernel's requirement algebra rather than a bare tag query so it can also ask
///         about an attribute, and criteria are the counted half.
///     </para>
///     <para>
///         <b>The counted half rides the kernel's event bus, which anticipated it.</b>
///         <c>GameplayEventBus</c>'s own remarks name achievements and collections as the listeners it
///         exists for. Nothing posts to achievements; combat posts kills and this counts the ones it
///         was asked about.
///     </para>
///     <para>
///         ⚠ <b>The tag query here is over the <em>subject's</em> tags, not the player's.</b> "Kill
///         thirty undead" filters the victim. The player's own standing is
///         <see cref="AchievementDefinition.Requires" />, and keeping the two apart is what stops
///         "kill thirty things while poisoned" from silently meaning "kill thirty poisoned things".
///     </para>
/// </remarks>
[DataContract("AchievementCriterion")]
public sealed class AchievementCriterionDefinition {
    /// <summary>What it says in the UI — "Slay thirty undead in Queensdale".</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The verb prefix it counts — <c>Event.Kill</c>.</summary>
    public string Verb { get; set; } = string.Empty;

    /// <summary>The exact subject, by address, or empty for any.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The map, by address, or empty for anywhere.</summary>
    public string Scene { get; set; } = string.Empty;

    /// <summary>Prefixes the subject must all match.</summary>
    public List<string> All { get; set; } = [];

    /// <summary>Prefixes the subject must match at least one of.</summary>
    public List<string> Any { get; set; } = [];

    /// <summary>Prefixes the subject must match none of.</summary>
    public List<string> None { get; set; } = [];

    /// <summary>How many it takes.</summary>
    public int Count { get; set; } = 1;
}

/// <summary>Something worth doing, and what doing it gives.</summary>
[DataContract("AchievementDefinition")]
public sealed record AchievementDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What it says under the name.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>What it is worth.</summary>
    public int Points { get; set; } = 10;

    /// <summary>Whether it stays out of the list until it is earned.</summary>
    public bool Hidden { get; set; }

    /// <summary>What earning it grants — <c>Earned.Slayer</c>. Empty for one nothing asks about.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>What has to happen, and how often. All of them.</summary>
    public List<AchievementCriterionDefinition> Criteria { get; set; } = [];

    /// <summary>What has to be true of the player as well.</summary>
    /// <remarks>
    ///     G-Q3's tag queries, in the kernel's requirement algebra. An achievement with these and no
    ///     criteria is the pure standing kind — "own fifty mounts" is one prefix count.
    /// </remarks>
    public List<RequirementDefinition> Requires { get; set; } = [];

    /// <summary>What earning it unlocks, by address.</summary>
    public List<string> Unlocks { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var criterion in Criteria) {
            if (criterion.Verb.Length > 0) {
                tags.Add(criterion.Verb);
            }

            foreach (var name in criterion.All.Concat(criterion.Any).Concat(criterion.None)) {
                if (name.Length > 0) {
                    tags.Add(name);
                }
            }
        }

        foreach (var requirement in Requires) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }
    }
}

/// <summary>A criterion with its names resolved into a filter.</summary>
public sealed class AchievementCriterion {
    internal AchievementCriterion(AchievementCriterionDefinition definition, int index, GameplayEventFilter filter) {
        Definition = definition;
        Index = index;
        Filter = filter;
    }

    /// <summary>What it was compiled from.</summary>
    public AchievementCriterionDefinition Definition { get; }

    /// <summary>Which of its achievement's criteria it is.</summary>
    public int Index { get; }

    /// <summary>What it says in the UI.</summary>
    public string Description => Definition.Description;

    /// <summary>Which events count.</summary>
    public GameplayEventFilter Filter { get; }

    /// <summary>How many it takes, never below one.</summary>
    public int Count => Math.Max(1, Definition.Count);
}

/// <summary>An achievement with its criteria compiled.</summary>
public sealed class Achievement {
    readonly AchievementCriterion[] criteria;
    readonly DefId[] unlocks;

    internal Achievement(
        AchievementDefinition definition,
        GameplayTag tag,
        AchievementCriterion[] criteria,
        RequirementSet requirements,
        DefId[] unlocks
    ) {
        Definition = definition;
        Tag = tag;
        this.criteria = criteria;
        Requirements = requirements;
        this.unlocks = unlocks;
    }

    /// <summary>What it was compiled from.</summary>
    public AchievementDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What it is worth, never below zero.</summary>
    public int Points => Math.Max(0, Definition.Points);

    /// <summary>Whether it stays out of the list until it is earned.</summary>
    public bool IsHidden => Definition.Hidden;

    /// <summary>What earning it grants.</summary>
    public GameplayTag Tag { get; }

    /// <summary>What has to happen.</summary>
    public ReadOnlySpan<AchievementCriterion> Criteria => criteria;

    /// <summary>What has to be true of the player.</summary>
    public RequirementSet Requirements { get; }

    /// <summary>What earning it unlocks.</summary>
    public ReadOnlySpan<DefId> Unlocks => unlocks;
}
