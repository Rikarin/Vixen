// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Pvp;

/// <summary>The PvP library as a module a game composes.</summary>
/// <remarks>
///     One definition type. The two tags it declares are the ones a rule in C# asks about — being
///     flagged, and being in a match — because a tag content never happens to mention resolves to an
///     empty range and quietly matches nothing.
/// </remarks>
public sealed class PvpModule : IGameplayModule {
    /// <summary>The tag every PvP tag sits under.</summary>
    public const string Root = "Pvp";

    /// <summary>Being attackable in the open world.</summary>
    public const string Flagged = "Pvp.Flagged";

    /// <summary>Being in a match.</summary>
    public const string InMatch = "Pvp.InMatch";

    /// <inheritdoc />
    public string Name => "Pvp";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<PvpMapDefinition>()
            .Tag(Root)
            .Tag(Flagged)
            .Tag(InMatch);
    }
}
