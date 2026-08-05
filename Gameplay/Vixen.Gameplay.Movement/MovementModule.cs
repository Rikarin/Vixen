// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Movement;

/// <summary>The movement library as a module a game composes.</summary>
public sealed class MovementModule : IGameplayModule {
    /// <summary>Being aboard anything.</summary>
    public const string Mounted = "State.Mounted";

    /// <summary>Being the one steering.</summary>
    public const string Driving = "State.Driving";

    /// <inheritdoc />
    public string Name => "Movement";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<VehicleDefinition>()
            .Tag(Mounted)
            .Tag(Driving);
    }
}
