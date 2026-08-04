// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Items;

namespace Vixen.Gameplay.Loot;

/// <summary>The loot library as a module a game composes.</summary>
public sealed class LootModule : IGameplayModule {
    /// <inheritdoc />
    public string Name => "Loot";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .DependsOn<ItemsModule>()
            .Definition<LootTableDefinition>()
            .Definition<SalvageDefinition>();
    }
}
