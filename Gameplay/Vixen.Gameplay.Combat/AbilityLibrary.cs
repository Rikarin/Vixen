// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Combat;

/// <summary>One resource an ability spends, resolved.</summary>
/// <param name="Attribute">Which resource.</param>
/// <param name="Amount">How much.</param>
public readonly record struct AbilityCost(AttributeId Attribute, float Amount);

/// <summary>An ability with its names resolved: the form a cast runs on.</summary>
public sealed class AbilityTemplate {
    readonly AbilityCost[] costs;
    readonly GameplayTag[] tags;
    readonly EffectTemplate[] toTarget;
    readonly EffectTemplate[] toSelf;

    internal AbilityTemplate(
        AbilityDefinition definition,
        AbilityCost[] costs,
        RequirementSet requirements,
        GameplayTag[] tags,
        GameplayTag school,
        AttributeId scalesWith,
        EffectTemplate[] toTarget,
        EffectTemplate[] toSelf
    ) {
        Definition = definition;
        this.costs = costs;
        Requirements = requirements;
        this.tags = tags;
        School = school;
        ScalesWith = scalesWith;
        this.toTarget = toTarget;
        this.toSelf = toSelf;
    }

    /// <summary>What it was compiled from.</summary>
    public AbilityDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>What it is aimed at.</summary>
    public AbilityTargeting Targeting => Definition.Targeting;

    /// <summary>How long it takes to cast, in seconds.</summary>
    public float CastTime => MathF.Max(0f, Definition.CastTime);

    /// <summary>Whether it is a channel rather than a cast.</summary>
    /// <remarks>
    ///     A cast wins over a channel when a definition sets both, because an ability that casts and
    ///     then channels is not a thing anybody ships and guessing which was meant would be worse
    ///     than picking. <c>AbilityLibrary.Problems</c> reports it.
    /// </remarks>
    public bool IsChannel => CastTime <= 0f && Definition.ChannelTime > 0f;

    /// <summary>How long it channels for, in seconds, or zero.</summary>
    public float ChannelTime => IsChannel ? Definition.ChannelTime : 0f;

    /// <summary>How often a channel ticks, never below a hundredth of a second.</summary>
    public float ChannelPeriod => MathF.Max(0.01f, Definition.ChannelPeriod);

    /// <summary>How long before it can be used again, in seconds.</summary>
    public float Cooldown => MathF.Max(0f, Definition.Cooldown);

    /// <summary>How many uses it banks, never below one.</summary>
    public int Charges => Math.Max(1, Definition.Charges);

    /// <summary>Whether using it starts the caster's global cooldown.</summary>
    public bool TriggersGlobalCooldown => Definition.TriggersGlobalCooldown;

    /// <summary>Whether the global cooldown stops it being used.</summary>
    public bool RespectsGlobalCooldown => Definition.RespectsGlobalCooldown;

    /// <summary>How far, in metres. Zero is unlimited.</summary>
    public float Range => MathF.Max(0f, Definition.Range);

    /// <summary>What it costs.</summary>
    public ReadOnlySpan<AbilityCost> Costs => costs;

    /// <summary>What has to be true of the caster.</summary>
    public RequirementSet Requirements { get; }

    /// <summary>What this ability is. What a silence blocks.</summary>
    public ReadOnlySpan<GameplayTag> Tags => tags;

    /// <summary>What kind of damage it does, or <see cref="GameplayTag.None" />.</summary>
    public GameplayTag School { get; }

    /// <summary>Which of the caster's stats its damage scales with, or <see cref="AttributeId.None" />.</summary>
    public AttributeId ScalesWith { get; }

    /// <summary>What it does to what it hits, or null.</summary>
    public DamageDefinition? Damage => Definition.Damage;

    /// <summary>The effects it puts on what it hits.</summary>
    public ReadOnlySpan<EffectTemplate> AppliesToTarget => toTarget;

    /// <summary>The effects it puts on the caster.</summary>
    public ReadOnlySpan<EffectTemplate> AppliesToSelf => toSelf;

    /// <summary>Whether the caster needs something selected.</summary>
    public bool NeedsTarget => Targeting is AbilityTargeting.Target or AbilityTargeting.Ground;

    /// <summary>What its damage is worth against a caster's stats, before the pipeline.</summary>
    /// <param name="caster">Whoever is casting, or null for the world.</param>
    /// <returns>The amount.</returns>
    public float BaseAmount(GameplaySubject? caster) {
        if (Damage is not { } damage) {
            return 0f;
        }

        var scaling = ScalesWith.IsSome && caster is not null
            ? caster.Attributes.ValueOf(ScalesWith) * damage.Coefficient
            : 0f;

        return MathF.Max(0f, damage.Amount + scaling);
    }
}

/// <summary>Every ability a build knows, compiled once against a catalog.</summary>
public sealed class AbilityLibrary {
    readonly Dictionary<uint, AbilityTemplate> abilities;
    readonly string[] problems;

    AbilityLibrary(Dictionary<uint, AbilityTemplate> abilities, string[] problems) {
        this.abilities = abilities;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static AbilityLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>How many abilities it holds.</summary>
    public int Count => abilities.Count;

    /// <summary>Every ability, in address order.</summary>
    public IEnumerable<AbilityTemplate> All =>
        abilities.Values.OrderBy(ability => ability.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles every ability in a catalog, and the effects they name.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    /// <remarks>
    ///     ⚠ <b>An effect named by three abilities is compiled once.</b> Three templates would be
    ///     three stacking identities for one buff, so an ability applying it and a talent applying it
    ///     would refresh each other's rather than each other.
    /// </remarks>
    public static AbilityLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var effects = new Dictionary<uint, EffectTemplate>();

        foreach (var definition in catalog.OfType<EffectDefinition>()) {
            effects.Add(definition.Id.Value, EffectTemplate.Compile(definition, tags));
        }

        var compiled = new Dictionary<uint, AbilityTemplate>();

        foreach (var definition in catalog.OfType<AbilityDefinition>()) {
            if (definition.CastTime > 0f && definition.ChannelTime > 0f) {
                problems.Add(
                    $"'{definition.Address}' has both a cast time and a channel time. An ability that "
                    + "casts and then channels is not a thing; the cast time wins."
                );
            }

            if (definition.NeedsTargetByMode() && definition.Range <= 0f) {
                problems.Add($"'{definition.Address}' is aimed at something and has no range.");
            }

            if (definition.Damage is { School.Length: > 0 } damage && !tags.TryResolve(damage.School, out _)) {
                problems.Add($"'{definition.Address}' deals '{damage.School}', which is not a tag here.");
            }

            compiled.Add(
                definition.Id.Value,
                new(
                    definition,
                    [.. definition.Costs.Select(cost => new AbilityCost(AttributeId.From(cost.Attribute), cost.Amount))],
                    RequirementSet.Compile(definition.Requirements, tags),
                    [.. definition.Tags.Select(tags.Resolve)],
                    definition.Damage is { School.Length: > 0 } school ? tags.Resolve(school.School) : GameplayTag.None,
                    definition.Damage is { ScalesWith.Length: > 0 } scaling
                        ? AttributeId.From(scaling.ScalesWith)
                        : AttributeId.None,
                    Resolve(definition, definition.AppliesToTarget, effects, problems),
                    Resolve(definition, definition.AppliesToSelf, effects, problems)
                )
            );
        }

        return new(compiled, [.. problems]);
    }

    /// <summary>Finds an ability.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public AbilityTemplate? Find(DefId id) => abilities.GetValueOrDefault(id.Value);

    /// <summary>Finds an ability by the address it was authored at.</summary>
    /// <param name="address">The address.</param>
    /// <returns>It, or null.</returns>
    public AbilityTemplate? Find(string address) => Find(DefId.From(address));

    /// <summary>Finds an ability, and refuses to carry on without it.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It.</returns>
    /// <exception cref="DefinitionNotFoundException">This build has no such ability.</exception>
    public AbilityTemplate Get(DefId id) =>
        Find(id) ?? throw new DefinitionNotFoundException($"{id} is not an ability this build knows.");

    static EffectTemplate[] Resolve(
        AbilityDefinition ability,
        List<string> addresses,
        Dictionary<uint, EffectTemplate> effects,
        List<string> problems
    ) {
        if (addresses.Count == 0) {
            return [];
        }

        var resolved = new List<EffectTemplate>(addresses.Count);

        foreach (var address in addresses) {
            if (effects.TryGetValue(DefId.From(address).Value, out var effect)) {
                resolved.Add(effect);
            } else {
                problems.Add($"'{ability.Address}' applies '{address}', which is not an effect in this build.");
            }
        }

        return [.. resolved];
    }
}

static class AbilityDefinitionExtensions {
    public static bool NeedsTargetByMode(this AbilityDefinition definition) =>
        definition.Targeting is AbilityTargeting.Target or AbilityTargeting.Ground;
}
