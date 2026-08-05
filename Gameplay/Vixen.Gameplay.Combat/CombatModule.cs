// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Combat;

/// <summary>The combat library as a module a game composes.</summary>
/// <remarks>
///     ⚠ <b>It declares the stats the shipped damage rules read, which no content mentions.</b>
///     Health, crit chance and the rest are asked about by C# rather than authored, so without this
///     they would be absent from the layout and every rule reading them would silently see zero — the
///     same failure a tag only code knows about has, and the reason
///     <see cref="GameplayModuleBuilder.Tag" /> exists.
/// </remarks>
public sealed class CombatModule : IGameplayModule {
    /// <summary>The tag every ability's own tag sits under, so a silence has something to block.</summary>
    public const string AbilityRoot = "Ability";

    /// <summary>The tag every damage school sits under.</summary>
    public const string DamageRoot = "Damage";

    /// <summary>The tag a dead thing has.</summary>
    public const string DeadTag = "State.Dead";

    /// <inheritdoc />
    public string Name => "Combat";

    /// <summary>What a character starts with, before anything equips or buffs them.</summary>
    public float BaseHealth { get; set; } = 1000f;

    /// <summary>What fraction of hits crit, before anything raises it.</summary>
    public float BaseCriticalChance { get; set; } = 0.05f;

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<AbilityDefinition>()
            .Attribute("Health", BaseHealth, 0f)
            .Attribute("MaximumHealth", BaseHealth, 0f)
            .Attribute("CritChance", BaseCriticalChance, 0f, 1f)
            .Attribute("CritMultiplier", 2f, 1f)
            .Attribute("Absorb", 0f, 0f)
            .Tag(AbilityRoot)
            .Tag(DamageRoot)
            .Tag(DeadTag);
    }
}
