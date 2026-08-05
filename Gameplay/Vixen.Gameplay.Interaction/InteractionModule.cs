// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Interaction;

/// <summary>The interaction library as a module a game composes.</summary>
public sealed class InteractionModule : IGameplayModule {
    /// <summary>The tag every interactable's tag sits under.</summary>
    public const string Root = "Interactable";

    /// <summary>Being in the middle of using something.</summary>
    public const string Channelling = "State.Channelling";

    /// <inheritdoc />
    public string Name => "Interaction";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<InteractableDefinition>()
            .Tag(Root)
            .Tag(Channelling);
    }
}
