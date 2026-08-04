// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live;

/// <summary>Which map instance. The question the whole control plane is keyed by.</summary>
/// <remarks>
///     <para>
///         A shard is a map being simulated: one process, one scene, a population. This names the
///         shard rather than the process holding it, and the distinction is the one that makes
///         recovery expressible — doc 27 § Health says a lost shard is replaced by a
///         <em>placement</em>, not a resurrection, so the new process gets a new
///         <see cref="RealmInstanceId" /> and, for anything that survives, the same
///         <see cref="ShardId" /> never comes back.
///     </para>
///     <para>
///         A GUID because it is minted by whoever decides a shard should exist, and in a cluster
///         that is several machines at once. A counter would need a coordinator to hand out
///         identities before the coordinator that hands out shards had decided anything.
///     </para>
/// </remarks>
/// <param name="Value">The identity. <see cref="Guid.Empty" /> is <see cref="None" />.</param>
public readonly record struct ShardId(Guid Value) {
    /// <summary>No shard.</summary>
    public static ShardId None => default;

    /// <summary>Whether this names a shard at all.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>Mints one.</summary>
    /// <returns>A shard id nobody else has.</returns>
    public static ShardId New() => new(Guid.NewGuid());

    /// <summary>Reads one back.</summary>
    /// <param name="text">What <see cref="ToString" /> wrote.</param>
    /// <param name="shard">The shard, on success.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out ShardId shard) {
        if (Guid.TryParse(text, out var value)) {
            shard = new(value);

            return true;
        }

        shard = None;

        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value == Guid.Empty ? "no shard" : Value.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Which process, as the placement backend names it.</summary>
/// <remarks>
///     <para>
///         A string rather than a GUID, because it is the backend's own handle and every backend
///         already has one that is more useful than anything this layer could mint: a Kubernetes pod
///         name, a Docker container id, a process id. Printing it into a log is what lets an operator
///         reach the thing with <c>kubectl</c> or <c>docker</c> or <c>kill</c>, which is the whole
///         point of the value.
///     </para>
///     <para>
///         ⚠ <b>Not a <see cref="ShardId" />, and losing the distinction loses the recovery story.</b>
///         One shard may be carried by several instances over its life — a crash and a replacement,
///         a version rollout — and one instance never carries two shards.
///     </para>
///     <para>
///         ⚠ <b><c>default</c> carries a null <see cref="Value" />.</b> A struct's property
///         initialisers do not run for <c>default(T)</c>, so the normalisation below covers every
///         instance somebody constructed and none that arrived out of a zeroed field or an
///         uninitialised array. Every member here is written to survive that — ask
///         <see cref="IsValid" /> rather than <c>Value.Length</c>, which is the same discipline
///         <c>ImmutableArray&lt;T&gt;.IsDefault</c> exists for.
///     </para>
/// </remarks>
/// <param name="Value">The backend's handle. Empty is <see cref="None" />.</param>
public readonly record struct RealmInstanceId(string Value) {
    /// <summary>No instance.</summary>
    public static RealmInstanceId None => new("");

    /// <summary>The backend's handle. Null only on <c>default</c>; see the type's remarks.</summary>
    public string Value { get; } = Value ?? "";

    /// <summary>Whether this names an instance at all.</summary>
    public bool IsValid => !string.IsNullOrEmpty(Value);

    /// <inheritdoc />
    public override string ToString() => IsValid ? Value : "no instance";
}

/// <summary>Who, durably — an account and the character it is playing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not <c>Vixen.Net.Sessions.PlayerId</c>, and the two are not convertible.</b> That one
///         numbers a player <em>within a session</em>: it survives a dropped connection and it does
///         not survive the session, because it is an index into one server's table. This one is who
///         the database thinks they are, and it is the same value on every realm they ever visit.
///         Doc 27 § Grains keys <c>IPlayerGrain</c> by exactly this pair.
///     </para>
///     <para>
///         Both halves, not just the account: a lease is per character, because two characters on
///         one account are two sets of inventory and an MMO lets you play the second while the first
///         is parked in a city. Keying the lease by account would make that a duplication bug
///         (ADR-021) rather than a Tuesday.
///     </para>
/// </remarks>
/// <param name="Account">The account. <see cref="Guid.Empty" /> means nobody.</param>
/// <param name="Character">The character on it.</param>
public readonly record struct PlayerKey(Guid Account, Guid Character) {
    /// <summary>Nobody.</summary>
    public static PlayerKey None => default;

    /// <summary>Whether this names a player at all.</summary>
    public bool IsValid => Account != Guid.Empty && Character != Guid.Empty;

    /// <summary>Reads one back.</summary>
    /// <param name="text">What <see cref="ToString" /> wrote.</param>
    /// <param name="player">The player, on success.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out PlayerKey player) {
        player = None;

        if (text is null) {
            return false;
        }

        var separator = text.IndexOf('/', StringComparison.Ordinal);

        if (separator < 0
            || !Guid.TryParse(text.AsSpan(0, separator), out var account)
            || !Guid.TryParse(text.AsSpan(separator + 1), out var character)) {
            return false;
        }

        player = new(account, character);

        return true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        IsValid
            ? string.Create(CultureInfo.InvariantCulture, $"{Account:D}/{Character:D}")
            : "nobody";
}
