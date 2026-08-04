// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Items;

namespace Vixen.Gameplay.Inventory;

/// <summary>The container algebra as a module a game composes.</summary>
/// <remarks>
///     <para>
///         <b>No definition types, which is worth a sentence.</b> A bag is not authored content — how
///         many slots a character's bags have is a game's rule and usually a progression one, and a
///         <c>.vxdef</c> for it would be a definition with one number that nothing else can vary.
///         What <em>is</em> authored is the item that goes in it, which is
///         <see cref="ItemsModule" />'s.
///     </para>
///     <para>
///         It declares the slot-tag root instead, because an equipment set is a container whose slots
///         are tags and a game that authors no item mentioning <c>Item.Slot</c> would have no such
///         tags in its table.
///     </para>
/// </remarks>
public sealed class InventoryModule : IGameplayModule {
    /// <summary>The tag every equipment slot's tag sits under.</summary>
    public const string SlotRoot = "Item.Slot";

    /// <inheritdoc />
    public string Name => "Inventory";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .DependsOn<GameplayKernelModule>()
            .DependsOn<ItemsModule>()
            .Tag(SlotRoot);
    }
}
