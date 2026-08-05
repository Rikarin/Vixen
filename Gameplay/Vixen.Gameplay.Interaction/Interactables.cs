// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Gameplay.Interaction;

/// <summary>Who a node's remaining uses belong to.</summary>
public enum InteractionInstancing {
    /// <summary>Everybody's. First come, first served.</summary>
    Shared,

    /// <summary>Each player's own. Guild Wars 2's answer to node-stealing.</summary>
    PerPlayer
}

/// <summary>What stops a channel.</summary>
[Flags]
public enum InterruptOn {
    /// <summary>Nothing but cancelling.</summary>
    Nothing = 0,

    /// <summary>Being hit.</summary>
    Damage = 1,

    /// <summary>Walking away.</summary>
    Movement = 2,

    /// <summary>Either.</summary>
    Anything = Damage | Movement
}

/// <summary>Why an interaction was refused, or how it ended.</summary>
public enum InteractionRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>This build has no such interactable.</summary>
    Unknown,

    /// <summary>It has been used up and has not come back.</summary>
    Depleted,

    /// <summary>Somebody else is on it.</summary>
    Claimed,

    /// <summary>A requirement is not met.</summary>
    Requirements,

    /// <summary>They are already channelling something.</summary>
    Busy,

    /// <summary>They are not channelling anything.</summary>
    NotChannelling,

    /// <summary>The channel has not finished.</summary>
    Unfinished,

    /// <summary>Something interrupted it.</summary>
    Interrupted
}

/// <summary>Something a player can use: a node, a chest, a door, a lever, a forge.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 § Interaction: "one channelled-interaction system with different
///         definitions".</b> Mining a node, smelting at a forge, opening a chest, reading a book,
///         flipping a lever and picking a herb differ in a duration, a tag and a result.
///     </para>
///     <para>
///         ⚠ <b>Every member is settable</b>, for the YAML binder's reason — see
///         <see cref="ModifierDefinition" />.
///     </para>
/// </remarks>
[DataContract("InteractableDefinition")]
public sealed record InteractableDefinition : Definition {
    /// <summary>What it is called.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What it is — <c>Interactable.Node.Ore</c>. Also what a station requirement asks for.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>What the prompt says — <c>Mine</c>.</summary>
    public string Verb { get; set; } = string.Empty;

    /// <summary>How long using it takes, in seconds. Zero for an instant one.</summary>
    public float ChannelSeconds { get; set; } = 2f;

    /// <summary>What stops it.</summary>
    public InterruptOn Interrupts { get; set; } = InterruptOn.Anything;

    /// <summary>How many times it may be used before it is gone. Zero for a lever.</summary>
    public int Uses { get; set; } = 1;

    /// <summary>How long until it comes back, in seconds. Zero for never.</summary>
    public float RespawnSeconds { get; set; } = 60f;

    /// <summary>Whose the remaining uses are.</summary>
    public InteractionInstancing Instancing { get; set; }

    /// <summary>The address of what using it yields — a loot table, usually. Empty for a door.</summary>
    public string Yields { get; set; } = string.Empty;

    /// <summary>What has to be true to use it.</summary>
    public List<RequirementDefinition> Requires { get; set; } = [];

    /// <summary>Tags using it grants — an attunement, a flag on a door.</summary>
    public List<string> GrantsTags { get; set; } = [];

    /// <inheritdoc />
    public override void CollectTags(ICollection<string> tags) {
        ArgumentNullException.ThrowIfNull(tags);

        if (Tag.Length > 0) {
            tags.Add(Tag);
        }

        foreach (var tag in GrantsTags) {
            tags.Add(tag);
        }

        foreach (var requirement in Requires) {
            if (requirement.Kind != RequirementKind.Value && requirement.Subject.Length > 0) {
                tags.Add(requirement.Subject);
            }
        }
    }
}

/// <summary>An interactable with its names resolved.</summary>
public sealed class Interactable {
    readonly GameplayTag[] grants;

    internal Interactable(
        InteractableDefinition definition,
        GameplayTag tag,
        DefId yields,
        RequirementSet requirements,
        GameplayTag[] grants
    ) {
        Definition = definition;
        Tag = tag;
        Yields = yields;
        Requirements = requirements;
        this.grants = grants;
    }

    /// <summary>What it was compiled from.</summary>
    public InteractableDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is called.</summary>
    public string DisplayName => Definition.DisplayName;

    /// <summary>What it is.</summary>
    public GameplayTag Tag { get; }

    /// <summary>What using it yields.</summary>
    public DefId Yields { get; }

    /// <summary>How long using it takes, never below zero.</summary>
    public float ChannelSeconds => MathF.Max(0f, Definition.ChannelSeconds);

    /// <summary>What stops it.</summary>
    public InterruptOn Interrupts => Definition.Interrupts;

    /// <summary>How many times it may be used, or zero for a lever.</summary>
    public int Uses => Math.Max(0, Definition.Uses);

    /// <summary>Whether it can be used for ever.</summary>
    public bool IsUnlimited => Uses == 0;

    /// <summary>How long until it comes back.</summary>
    public float RespawnSeconds => MathF.Max(0f, Definition.RespawnSeconds);

    /// <summary>Whose the remaining uses are.</summary>
    public InteractionInstancing Instancing => Definition.Instancing;

    /// <summary>What has to be true to use it.</summary>
    public RequirementSet Requirements { get; }

    /// <summary>What using it grants.</summary>
    public ReadOnlySpan<GameplayTag> Grants => grants;
}

/// <summary>Every interactable a build knows, compiled once.</summary>
public sealed class InteractionLibrary {
    readonly Dictionary<uint, Interactable> interactables;
    readonly string[] problems;

    InteractionLibrary(Dictionary<uint, Interactable> interactables, string[] problems) {
        this.interactables = interactables;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static InteractionLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Everything, in address order.</summary>
    public IEnumerable<Interactable> Interactables =>
        interactables.Values.OrderBy(entry => entry.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static InteractionLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var interactables = new Dictionary<uint, Interactable>();

        foreach (var definition in catalog.OfType<InteractableDefinition>()) {
            if (definition.Uses == 0 && definition.RespawnSeconds > 0f) {
                problems.Add(
                    $"'{definition.Address}' never runs out and has a respawn timer, so the timer does "
                    + "nothing."
                );
            }

            if (definition.Uses > 0 && definition.RespawnSeconds <= 0f && definition.Yields.Length > 0) {
                problems.Add(
                    $"'{definition.Address}' yields something, runs out and never comes back — which is a "
                    + "one-off chest if that is what was meant, and a node nobody can farm if it was not."
                );
            }

            if (definition.Instancing == InteractionInstancing.PerPlayer && definition.Uses == 0) {
                problems.Add(
                    $"'{definition.Address}' is instanced per player and never runs out, so the instancing "
                    + "does nothing."
                );
            }

            interactables.Add(
                definition.Id.Value,
                new(
                    definition,
                    tags.Resolve(definition.Tag),
                    DefId.From(definition.Yields),
                    RequirementSet.Compile(definition.Requires, tags),
                    [.. definition.GrantsTags.Select(tags.Resolve).Where(tag => tag.IsSome)]
                )
            );
        }

        return new(interactables, [.. problems]);
    }

    /// <summary>Finds one.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Interactable? Find(DefId id) => interactables.GetValueOrDefault(id.Value);
}
