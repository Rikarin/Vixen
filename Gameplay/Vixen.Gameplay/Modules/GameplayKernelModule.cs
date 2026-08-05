// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay;

/// <summary>The kernel as a module, so that the engine's own types arrive the way a game's do.</summary>
/// <remarks>
///     <para>
///         It declares one thing — <see cref="EffectDefinition" /> — because that is the only
///         definition type the kernel owns. Everything else it provides is a type a caller
///         constructs rather than a registration.
///     </para>
///     <para>
///         <b>It exists to be used, not to be assumed.</b> A game that composes no modules and expects
///         <c>!EffectDefinition</c> to resolve would get a content build that reads a tag nothing
///         declared; making the kernel arrive through the same
///         <see cref="GameplayConfig.Use{TModule}()" /> as everything else is what makes the
///         composition report complete.
///     </para>
/// </remarks>
public sealed class GameplayKernelModule : IGameplayModule {
    /// <inheritdoc />
    public string Name => "Gameplay";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Definition<EffectDefinition>();
    }
}
