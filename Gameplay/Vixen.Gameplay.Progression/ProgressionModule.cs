// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Progression;

/// <summary>The progression library as a module a game composes.</summary>
/// <remarks>
///     Five definition types and no stats. A level is not an attribute — it is a durable number a
///     requirement asks about, and making it one would put it in the modifier algebra where a buff
///     could raise it.
/// </remarks>
public sealed class ProgressionModule : IGameplayModule {
    /// <summary>The tag every profession's tag sits under.</summary>
    public const string ProfessionRoot = "Profession";

    /// <summary>The tag every faction's tag sits under.</summary>
    public const string FactionRoot = "Faction";

    /// <summary>The tag every specialisation's tag sits under.</summary>
    public const string SpecialisationRoot = "Specialisation";

    /// <inheritdoc />
    public string Name => "Progression";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<ExperienceCurveDefinition>()
            .Definition<TalentTreeDefinition>()
            .Definition<SpecialisationDefinition>()
            .Definition<ProfessionDefinition>()
            .Definition<ReputationDefinition>()
            .Tag(ProfessionRoot)
            .Tag(FactionRoot)
            .Tag(SpecialisationRoot);
    }
}
