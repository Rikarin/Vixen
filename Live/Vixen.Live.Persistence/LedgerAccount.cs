// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live.Persistence;

/// <summary>One end of a movement of value: a character, or a part of the world.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The world has accounts, and that is what makes conservation checkable.</b> Doc 27
///         § Persistence says every movement of value is a ledger row; it does not say what a loot
///         drop moves value <em>from</em>. If the answer is "nowhere", then every intent that creates
///         an item and every intent that destroys one is an exception to the sum-to-zero rule, and a
///         rule with exceptions cannot be a database constraint. So a drop is a transfer out of
///         <c>world/loot</c>, a vendor sale is a transfer into <c>world/vendor</c>, and the invariant
///         becomes total: <b>every intent's deltas sum to zero, per asset, always.</b>
///     </para>
///     <para>
///         The cost is a handful of named accounts whose balances go steadily negative, and that is
///         not a defect — <c>world/loot</c>'s balance is exactly how much of an asset has entered the
///         economy, which is the number an economy dashboard is built to show and which no other
///         schema gives you for free. Doc 28 § Economy is the consumer.
///     </para>
///     <para>
///         ⚠ <b><c>default</c> is <see cref="Nowhere" /> and carries a null <see cref="World" />.</b>
///         A struct's property initialisers do not run for <c>default(T)</c>, the same wart
///         <c>RealmInstanceId</c> documents, so every member here survives it.
///     </para>
/// </remarks>
public readonly record struct LedgerAccount {
    /// <summary>The world account a drop comes out of.</summary>
    public const string Loot = "world/loot";

    /// <summary>The world account a vendor sale goes into.</summary>
    public const string Vendor = "world/vendor";

    /// <summary>The world account currency is destroyed into — repair costs, taxes, fees.</summary>
    public const string Sink = "world/sink";

    /// <summary>The world account mail and auction escrow is held in while in flight.</summary>
    public const string Escrow = "world/escrow";

    /// <summary>Whose account, when it is a character's.</summary>
    public PlayerKey Player { get; init; }

    /// <summary>Which part of the world, when it is not. Empty for a character.</summary>
    public string World { get; init; }

    /// <summary>Neither. The value a zeroed field holds, and never a valid end of a movement.</summary>
    public static LedgerAccount Nowhere => default;

    /// <summary>Whether this names an account at all.</summary>
    public bool IsValid => Player.IsValid ^ !string.IsNullOrEmpty(World);

    /// <summary>Whether a character holds it.</summary>
    public bool IsPlayer => Player.IsValid;

    /// <summary>A character's account.</summary>
    /// <param name="player">Which character.</param>
    /// <returns>The account.</returns>
    public static LedgerAccount Of(PlayerKey player) => new() { Player = player, World = "" };

    /// <summary>A named part of the world.</summary>
    /// <param name="name">Its name — <see cref="Loot" />, <see cref="Vendor" /> or a game's own.</param>
    /// <returns>The account.</returns>
    /// <remarks>
    ///     A game may name its own; the four constants are the ones this layer needs a word for.
    ///     <b>Names are not validated against a list</b>, deliberately: a closed set would mean doc
    ///     28's economy could not add a faucet without changing this assembly, and a typo shows up in
    ///     the dashboard as an account with one entry in it rather than as silent loss.
    /// </remarks>
    public static LedgerAccount Of(string name) => new() { Player = PlayerKey.None, World = name ?? "" };

    /// <summary>Reads one back.</summary>
    /// <param name="text">What <see cref="ToString" /> wrote.</param>
    /// <param name="account">The account, on success.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out LedgerAccount account) {
        account = Nowhere;

        if (string.IsNullOrEmpty(text)) {
            return false;
        }

        if (PlayerKey.TryParse(text, out var player) && player.IsValid) {
            account = Of(player);

            return true;
        }

        account = Of(text);

        return account.IsValid;
    }

    /// <summary>Whether two accounts are the same one.</summary>
    /// <param name="other">The other account.</param>
    /// <returns>Whether they are equal.</returns>
    /// <remarks>Hand-written so <c>default</c> compares equal to a constructed empty one.</remarks>
    public bool Equals(LedgerAccount other) =>
        Player == other.Player && string.Equals(World ?? "", other.World ?? "", StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Player, World ?? "");

    /// <inheritdoc />
    public override string ToString() =>
        IsPlayer ? Player.ToString()
        : string.IsNullOrEmpty(World) ? "nowhere"
        : World;
}

/// <summary>What moved. An address, because in this engine there is no other kind of name.</summary>
/// <remarks>
///     <para>
///         ADR-013: an asset is named by its addressable address — <c>items/greatsword</c>,
///         <c>currency/gold</c>. That is deliberate rather than convenient. The support tool's
///         question is "what happened to my sword", and answering it means joining the ledger to the
///         thing the player is looking at in their bag; a numeric id minted by the database would
///         need a second registry mapping it back to content, which is the registry
///         <c>NetworkSceneId</c> and the prefab id already refuse to have.
///     </para>
///     <para>
///         ⚠ <b>An asset is a <em>kind</em>, not an instance.</b> A ledger row says "three of
///         <c>items/potion</c>", not "this potion". Doc 28 owns instances — a sword with rolled
///         stats is an entity with a durable id — and the ledger's job is the quantity that moved,
///         because that is the quantity conservation is a statement about.
///     </para>
/// </remarks>
/// <param name="Address">The addressable address. Empty is <see cref="None" />.</param>
public readonly record struct AssetId(string Address) {
    /// <summary>Nothing.</summary>
    public static AssetId None => new("");

    /// <summary>The address. Null only on <c>default</c>.</summary>
    public string Address { get; } = Address ?? "";

    /// <summary>Whether this names an asset at all.</summary>
    public bool IsValid => !string.IsNullOrEmpty(Address);

    /// <summary>Whether two ids name the same asset.</summary>
    /// <param name="other">The other id.</param>
    /// <returns>Whether they are equal.</returns>
    public bool Equals(AssetId other) =>
        string.Equals(Address ?? "", other.Address ?? "", StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => (Address ?? "").GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => IsValid ? Address : "nothing";
}

/// <summary>One leg of a transaction: this account's holding of this asset changes by this much.</summary>
/// <param name="Account">Whose.</param>
/// <param name="Asset">Of what.</param>
/// <param name="Delta">By how much. Negative is out.</param>
public readonly record struct AssetMovement(LedgerAccount Account, AssetId Asset, long Delta) {
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Account} {Delta:+#;-#;0} {Asset}");
}
