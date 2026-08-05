// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Crafting;

/// <summary>The crafting library as a module a game composes.</summary>
/// <remarks>
///     One definition type and no tags of its own. A recipe's profession tag is
///     <c>Vixen.Gameplay.Progression</c>'s and its station tag is whatever the world calls that forge;
///     minting a third vocabulary here would give a designer two names for each.
/// </remarks>
public sealed class CraftingModule : IGameplayModule {
    /// <inheritdoc />
    public string Name => "Crafting";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<RecipeDefinition>();
    }
}
