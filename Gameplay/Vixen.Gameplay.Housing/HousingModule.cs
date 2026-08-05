// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Housing;

/// <summary>The housing library as a module a game composes.</summary>
public sealed class HousingModule : IGameplayModule {
    /// <summary>The tag every house-granted state sits under.</summary>
    public const string HouseRoot = "House";

    /// <inheritdoc />
    public string Name => "Housing";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<PlotDefinition>()
            .Definition<FurnitureDefinition>()
            .Tag(HouseRoot);
    }
}
