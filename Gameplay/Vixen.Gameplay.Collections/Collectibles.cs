// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Collections;

/// <summary>What sort of thing somebody has collected.</summary>
/// <remarks>
///     ⚠ <b>The kind changes nothing about how it is stored, and that is doc 28's whole claim
///     here:</b> pets, mounts, appearances, titles and toys are <em>"all one mechanism: a set of
///     unlocked <c>DefId</c>s with an unlock source recorded"</em>. What the kind is for is the
///     collection screen's tabs and the "how many mounts have I got" count — not a second code path.
/// </remarks>
public enum CollectibleKind {
    /// <summary>Something that follows you about.</summary>
    Pet,

    /// <summary>Something you ride. Owning it; sitting on it is <c>Vixen.Gameplay.Movement</c>'s.</summary>
    Mount,

    /// <summary>A look a worn item can be shown as.</summary>
    Appearance,

    /// <summary>Something written after a name.</summary>
    Title,

    /// <summary>Something with a button and no consequences.</summary>
    Toy,

    /// <summary>Anything else worth counting.</summary>
    Cosmetic
}

/// <summary>How somebody came by something.</summary>
/// <remarks>
///     Recorded because doc 28 asks for it, and because it is the first question support asks. It is
///     deliberately coarse: <em>which</em> boss, quest or purchase is <see cref="Unlock.From" />.
/// </remarks>
public enum UnlockSource {
    /// <summary>Nobody wrote it down. What a save from before this field looks like.</summary>
    Unknown,

    /// <summary>It dropped.</summary>
    Loot,

    /// <summary>A quest gave it.</summary>
    Quest,

    /// <summary>An achievement gave it.</summary>
    Achievement,

    /// <summary>It was bought.</summary>
    Purchase,

    /// <summary>It was made.</summary>
    Craft,

    /// <summary>An event, a season or a promotion gave it.</summary>
    Promotion,

    /// <summary>Somebody with a console gave it.</summary>
    Grant
}

/// <summary>One thing somebody has, and where it came from.</summary>
/// <param name="Collectible">What.</param>
/// <param name="Source">How.</param>
/// <param name="From">What exactly — the boss, the quest, the achievement. <see cref="DefId.None" /> for nothing in particular.</param>
/// <param name="Order">The nth thing they unlocked.</param>
/// <remarks>
///     ⚠ <b>An order rather than a timestamp.</b> Nothing in this library has a clock, for the same
///     reason nothing in <c>Vixen.Gameplay.Housing</c> does — a collection is a durable row that
///     spends almost all of its life hibernating. A caller that wants dates stamps them on the way
///     into its own store; a counter is what makes "in the order I got them" replay identically.
/// </remarks>
public readonly record struct Unlock(DefId Collectible, UnlockSource Source, DefId From, int Order);

/// <summary>One thing that can be collected.</summary>
[DataContract("CollectibleDefinition")]
public sealed record CollectibleDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What sort it is.</summary>
    public CollectibleKind Kind { get; set; }

    /// <summary>What having it grants — <c>Collected.Mount.Gryphon</c>. Empty for one nothing asks about.</summary>
    /// <remarks>
    ///     How an achievement for owning fifty mounts is written without a reference to anything: the
    ///     tag is granted on the unlock and the achievement's requirement asks about the prefix.
    /// </remarks>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Which equipment slot an appearance replaces — <c>Slot.Head</c>. Empty for everything else.</summary>
    /// <remarks>
    ///     A slot is a tag, which is doc 28's amendment under Items: it is asked about hierarchically
    ///     and never sorted.
    /// </remarks>
    public string Slot { get; set; } = string.Empty;

    /// <summary>Whether it stays out of the collection screen until it is unlocked.</summary>
    public bool Hidden { get; set; }

    /// <summary>What has to be true to use it. Not to unlock it — that is whoever grants it.</summary>
    public List<RequirementDefinition> Requires { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        if (Slot.Length > 0) {
            tags.Add(Slot);
        }

        foreach (var requirement in Requires) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }
    }
}

/// <summary>A collectible with its names resolved.</summary>
public sealed class Collectible {
    internal Collectible(CollectibleDefinition definition, GameplayTag tag, GameplayTag slot, RequirementSet requirements) {
        Definition = definition;
        Tag = tag;
        Slot = slot;
        Requirements = requirements;
    }

    /// <summary>What it was compiled from.</summary>
    public CollectibleDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What sort it is.</summary>
    public CollectibleKind Kind => Definition.Kind;

    /// <summary>What having it grants.</summary>
    public GameplayTag Tag { get; }

    /// <summary>Which slot an appearance replaces.</summary>
    public GameplayTag Slot { get; }

    /// <summary>Whether it stays hidden until it is unlocked.</summary>
    public bool IsHidden => Definition.Hidden;

    /// <summary>What has to be true to use it.</summary>
    public RequirementSet Requirements { get; }
}
