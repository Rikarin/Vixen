// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Exploration;

/// <summary>The exploration library as a module a game composes.</summary>
public sealed class ExplorationModule : IGameplayModule {
    /// <summary>The tag every discovery sits under.</summary>
    public const string DiscoveredRoot = "Discovered";

    /// <summary>The tag every finished map sits under.</summary>
    public const string CompletionRoot = "Completion";

    /// <inheritdoc />
    public string Name => "Exploration";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<MapDefinition>()
            .Tag(DiscoveredRoot)
            .Tag(CompletionRoot);
    }
}
