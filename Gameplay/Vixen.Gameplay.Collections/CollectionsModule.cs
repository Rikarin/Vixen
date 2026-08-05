// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Collections;

/// <summary>The collections library as a module a game composes.</summary>
public sealed class CollectionsModule : IGameplayModule {
    /// <summary>The tag every unlock sits under.</summary>
    public const string CollectedRoot = "Collected";

    /// <summary>The tag every earned achievement sits under.</summary>
    public const string EarnedRoot = "Earned";

    /// <inheritdoc />
    public string Name => "Collections";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<CollectibleDefinition>()
            .Definition<AchievementDefinition>()
            .Tag(CollectedRoot)
            .Tag(EarnedRoot);
    }
}
