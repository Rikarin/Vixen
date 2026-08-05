// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Ai;

/// <summary>The gameplay AI library as a module a game composes.</summary>
/// <remarks>
///     One definition type, which is how much of doc 28's AI section is actually left: threat and
///     aggro are <c>Vixen.Gameplay.Combat</c>'s and the planners are <c>Core/Vixen.Ai</c>'s. A leash
///     is authored on whatever spawns the creature rather than as a definition of its own.
/// </remarks>
public sealed class GameplayAiModule : IGameplayModule {
    /// <summary>Being on the way home.</summary>
    public const string Evading = "State.Evading";

    /// <inheritdoc />
    public string Name => "GameplayAi";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<SpawnTableDefinition>()
            .Tag(Evading);
    }
}
