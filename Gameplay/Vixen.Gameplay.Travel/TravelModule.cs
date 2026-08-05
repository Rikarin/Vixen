// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Travel;

/// <summary>The travel library as a module a game composes.</summary>
/// <remarks>
///     One definition type and no tags of its own. What unlocks a waypoint is somebody else's tag —
///     a discovery's, a quest's, a purchase's — and minting a <c>Travel.Unlocked</c> root here would
///     give a designer two names for the same fact.
/// </remarks>
public sealed class TravelModule : IGameplayModule {
    /// <inheritdoc />
    public string Name => "Travel";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<TravelPointDefinition>();
    }
}
