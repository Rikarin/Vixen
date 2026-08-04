// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay;

/// <summary>An <see cref="EffectDefinition" /> with every name resolved: the form the frame runs.</summary>
/// <remarks>
///     <para>
///         <b>Compiled once, shared by every instance of the effect on every target.</b> A definition
///         holds strings because a designer wrote it; a template holds
///         <see cref="GameplayTagRange" />s and <see cref="AttributeId" />s because a hundred stacks
///         of burning on forty targets must not resolve a string. Nothing here is per-target — that
///         is <see cref="ActiveEffect" />.
///     </para>
///     <para>
///         ⚠ <b>A tag the table does not have compiles to an empty range and matches nothing</b>,
///         rather than failing the compile. A definition that mentions a tag no content baked is a
///         content bug, and the place that reports it is the content build; a realm that refused to
///         load the effect would turn one misspelling into an ability that throws.
///     </para>
/// </remarks>
public sealed class EffectTemplate {
    readonly Modifier[] modifiers;
    readonly GameplayTag[] tags;
    readonly GameplayTag[] granted;
    readonly GameplayTagRange[] blocked;
    readonly GameplayTagRange[] immunities;
    readonly GameplayTagRange[] cancelOn;

    EffectTemplate(
        EffectDefinition definition,
        Modifier[] modifiers,
        GameplayTag[] tags,
        GameplayTag[] granted,
        GameplayTagRange[] blocked,
        GameplayTagRange[] immunities,
        GameplayTagRange[] cancelOn
    ) {
        Definition = definition;
        this.modifiers = modifiers;
        this.tags = tags;
        this.granted = granted;
        this.blocked = blocked;
        this.immunities = immunities;
        this.cancelOn = cancelOn;
    }

    /// <summary>What it was compiled from.</summary>
    public EffectDefinition Definition { get; }

    /// <summary>The definition's id — what the wire carries and a saved row stores.</summary>
    public DefId Id => Definition.Id;

    /// <summary>How long one application lasts, in seconds. Zero or less is forever.</summary>
    public float Duration => Definition.Duration;

    /// <summary>Whether it lasts until something removes it.</summary>
    public bool IsInfinite => Definition.Duration <= 0f;

    /// <summary>How often it ticks, in seconds. Zero is never.</summary>
    public float Period => Definition.Period;

    /// <summary>What a second application does.</summary>
    public EffectStacking Stacking => Definition.Stacking;

    /// <summary>How high <see cref="EffectStacking.StackTo" /> counts, never below one.</summary>
    public int MaximumStacks => Math.Max(1, Definition.MaximumStacks);

    /// <summary>What it does to the target's stats, per stack. <see cref="Modifier.Source" /> is unset.</summary>
    public ReadOnlySpan<Modifier> Modifiers => modifiers;

    /// <summary>What this effect is, for an immunity to match.</summary>
    public ReadOnlySpan<GameplayTag> Tags => tags;

    /// <summary>What the target has while it is active.</summary>
    public ReadOnlySpan<GameplayTag> GrantedTags => granted;

    /// <summary>What the target may not do while it is active.</summary>
    public ReadOnlySpan<GameplayTagRange> BlockedTags => blocked;

    /// <summary>What may not be applied to the target while it is active.</summary>
    public ReadOnlySpan<GameplayTagRange> Immunities => immunities;

    /// <summary>Which gameplay events end it.</summary>
    public ReadOnlySpan<GameplayTagRange> CancelOn => cancelOn;

    /// <summary>Resolves a definition against a tag table.</summary>
    /// <param name="definition">The authored effect.</param>
    /// <param name="tags">The table its tag names are numbered against.</param>
    /// <returns>The template.</returns>
    public static EffectTemplate Compile(EffectDefinition definition, GameplayTagTable tags) {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(tags);

        var resolved = new Modifier[definition.Modifiers.Count];

        for (var index = 0; index < resolved.Length; index++) {
            var modifier = definition.Modifiers[index];

            resolved[index] = new(
                AttributeId.From(modifier.Attribute),
                modifier.Op,
                modifier.Value,
                ModifierSource.None
            );
        }

        return new(
            definition,
            resolved,
            Resolve(tags, definition.Tags),
            Resolve(tags, definition.GrantedTags),
            Ranges(tags, definition.BlockedTags),
            Ranges(tags, definition.Immunities),
            Ranges(tags, definition.CancelOn)
        );
    }

    /// <summary>Whether this effect is one of the kinds a set of immunity ranges covers.</summary>
    /// <param name="ranges">The immunity ranges.</param>
    /// <returns>Whether any of this effect's own tags falls in any of them.</returns>
    public bool MatchedBy(ReadOnlySpan<GameplayTagRange> ranges) {
        foreach (var range in ranges) {
            foreach (var tag in tags) {
                if (range.Contains(tag)) {
                    return true;
                }
            }
        }

        return false;
    }

    static GameplayTag[] Resolve(GameplayTagTable table, List<string> names) {
        if (names.Count == 0) {
            return [];
        }

        var resolved = new GameplayTag[names.Count];

        for (var index = 0; index < resolved.Length; index++) {
            resolved[index] = table.Resolve(names[index]);
        }

        return resolved;
    }

    static GameplayTagRange[] Ranges(GameplayTagTable table, List<string> names) {
        if (names.Count == 0) {
            return [];
        }

        var resolved = new GameplayTagRange[names.Count];

        for (var index = 0; index < resolved.Length; index++) {
            resolved[index] = table.RangeOf(names[index]);
        }

        return resolved;
    }
}
