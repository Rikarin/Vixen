// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Combat;

namespace Vixen.Gameplay.Shooting;

/// <summary>The shooting library as a module a game composes.</summary>
/// <remarks>
///     Depends on Combat, because what a bullet <em>does</em> is the damage pipeline — a headshot is
///     a Crit-stage rule and armour is a Mitigate one, and a shooter that reimplemented those would
///     have two sets of tested edge cases.
/// </remarks>
public sealed class ShootingModule : IGameplayModule {
    /// <summary>The tag every weapon's own tag sits under.</summary>
    public const string WeaponRoot = "Weapon";

    /// <inheritdoc />
    public string Name => "Shooting";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .DependsOn<CombatModule>()
            .Definition<WeaponDefinition>()
            .Tag(WeaponRoot);
    }
}
