// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Instances;

/// <summary>The instance library as a module a game composes.</summary>
/// <remarks>
///     One definition type and no stats. A difficulty's scalars are numbers a spawner multiplies by,
///     not modifiers: putting them in the attribute algebra would let a dispel remove heroic mode.
/// </remarks>
public sealed class InstanceModule : IGameplayModule {
    /// <summary>The tag every difficulty's tag sits under.</summary>
    public const string DifficultyRoot = "Instance";

    /// <inheritdoc />
    public string Name => "Instances";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<InstanceDefinition>()
            .Tag(DifficultyRoot);
    }
}
