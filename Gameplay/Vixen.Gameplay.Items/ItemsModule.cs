// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Items;

/// <summary>The items library as a module a game composes.</summary>
/// <remarks>
///     Four definition types and no systems: an item does nothing on its own, and everything that
///     happens to one happens inside a container, which is <c>Vixen.Gameplay.Inventory</c>'s.
/// </remarks>
public sealed class ItemsModule : IGameplayModule {
    /// <inheritdoc />
    public string Name => "Items";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<ItemDefinition>()
            .Definition<ItemRarityDefinition>()
            .Definition<AffixDefinition>()
            .Definition<AffixPoolDefinition>();
    }
}
