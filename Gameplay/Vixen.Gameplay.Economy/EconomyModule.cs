// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Economy;

/// <summary>The economy library as a module a game composes.</summary>
/// <remarks>
///     Two definition types and no stats. A currency is not an attribute: it is a durable number a
///     requirement asks about, and making it one would put it in the modifier algebra where a buff
///     could mint gold.
/// </remarks>
public sealed class EconomyModule : IGameplayModule {
    /// <summary>The tag every currency's tag sits under.</summary>
    public const string CurrencyRoot = "Currency";

    /// <inheritdoc />
    public string Name => "Economy";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .Definition<CurrencyDefinition>()
            .Definition<VendorDefinition>()
            .Tag(CurrencyRoot);
    }
}
