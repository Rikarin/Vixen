// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Live.Persistence;

/// <summary>The tables, and the ordered list of statements that produce them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Migrations are a numbered list of statements, not a framework.</b> Each
///         <see cref="Migration" /> is applied once, in order, inside one transaction with the row that
///         records it — so a migration that fails leaves the version where it was rather than
///         half-applied. There is no down migration: rolling a schema backwards on a live economy is
///         a decision with a person attached to it, and a tool that offers to do it automatically is
///         a tool that will.
///     </para>
///     <para>
///         The dialect is PostgreSQL, which doc 27 M-Q3 settles: <i>one implementation behind an
///         interface</i>. <c>bigserial</c>, <c>uuid</c>, <c>bytea</c> and <c>on conflict</c> are all
///         load-bearing here, and pretending otherwise would produce SQL that is portable in the
///         sense that it is worse everywhere.
///     </para>
/// </remarks>
public static class Schema {
    /// <summary>The version <see cref="Steps" /> brings a database to.</summary>
    public const int Version = 2;

    /// <summary>Where the applied version is recorded.</summary>
    public const string VersionTable = "live_schema";

    /// <summary>One numbered migration.</summary>
    /// <param name="Version">What the database is at once it has run.</param>
    /// <param name="Note">What it does, for the log and for whoever reads the table later.</param>
    /// <param name="Statements">The statements, in order.</param>
    public sealed record Migration(int Version, string Note, ImmutableArray<string> Statements);

    /// <summary>Makes the table the version is read from. Run before anything can be read.</summary>
    /// <remarks>
    ///     Not a <see cref="Migration" />, because a migration is something whose having-run is recorded here —
    ///     and the first one would have nowhere to record it. This statement is idempotent on its
    ///     own, which is what lets it be the one thing outside the numbering.
    /// </remarks>
    public static string CreateVersionTable { get; } =
        $"""
         create table if not exists {VersionTable} (
             version    integer      primary key,
             note       text         not null,
             applied_at timestamptz  not null
         )
         """;

    /// <summary>Every migration, in order.</summary>
    public static ImmutableArray<Migration> Steps { get; } = [
        new(
            1,
            "accounts, characters, the journal and its balances",
            [
                """
                create table if not exists live_account (
                    id        uuid         primary key,
                    handle    text         not null unique,
                    created   timestamptz  not null,
                    suspended boolean      not null default false
                )
                """,

                // The fence is `lease_epoch`, and it is on the row it fences rather than in a table
                // of its own: a write and its fence check have to be one statement, and the cheapest
                // way to make them one is for them to be one row.
                """
                create table if not exists live_player (
                    account     uuid         not null,
                    "character" uuid         not null,
                    name        text         not null unique,
                    created     timestamptz  not null,
                    last_seen   timestamptz  not null,
                    region      text         not null,
                    home_map    text         not null,
                    lease_epoch bigint       not null,
                    profile     bytea        not null,
                    primary key (account, "character")
                )
                """,
                """
                create index if not exists live_player_account on live_player (account, created)
                """,

                // Append-only. There is no update statement against this table anywhere in this
                // assembly, and that is the property the support tool's answers rest on.
                """
                create table if not exists live_ledger (
                    sequence     bigserial    primary key,
                    op_account   uuid         not null,
                    op_character uuid         not null,
                    op_kind      text         not null,
                    op_id        text         not null,
                    account      text         not null,
                    asset        text         not null,
                    delta        bigint       not null,
                    balance      bigint       not null,
                    at           timestamptz  not null,
                    recorded     timestamptz  not null,
                    detail       text         not null
                )
                """,
                """
                create index if not exists live_ledger_account on live_ledger (account, sequence desc)
                """,
                """
                create index if not exists live_ledger_asset on live_ledger (asset, sequence desc)
                """,
                """
                create index if not exists live_ledger_recorded on live_ledger (recorded desc)
                """,

                // The idempotency key, as a primary key. A duplicate delivery is a unique violation
                // the store turns into `Replayed`, which is the whole mechanism: the database
                // refuses the second write rather than the application remembering not to make it.
                """
                create table if not exists live_ledger_op (
                    op_account     uuid    not null,
                    op_character   uuid    not null,
                    op_kind        text    not null,
                    op_id          text    not null,
                    first_sequence bigint  not null,
                    primary key (op_account, op_character, op_kind, op_id)
                )
                """,

                // A projection of live_ledger, maintained in the same transaction, so that reading a
                // balance is a lookup rather than a scan over a character's whole history. What
                // makes it safe to believe is `ReconcileAsync`, which is the only reason a cached
                // aggregate belongs anywhere near an economy.
                """
                create table if not exists live_balance (
                    account  text    not null,
                    asset    text    not null,
                    quantity bigint  not null,
                    primary key (account, asset)
                )
                """
            ]
        ),
        new(
            2,
            "guilds and their rosters",
            [
                // `revision` is the fence, and it is on the row it fences for `live_player`'s
                // reason: a write and its check have to be one statement. It is a revision rather
                // than a lease epoch because a guild has no lease — see IGuildGrain. What it guards
                // against is a stale writer after a reactivation, not two realms.
                """
                create table if not exists live_guild (
                    id       uuid         primary key,
                    charter  text         not null,
                    name     text         not null unique,
                    founded  timestamptz  not null,
                    revision bigint       not null
                )
                """,

                // The roster is rows and not a blob, which is why this is a repository at all:
                // "which guilds is this account in" is a query, and grain storage cannot answer one.
                """
                create table if not exists live_guild_member (
                    guild       uuid         not null references live_guild (id) on delete cascade,
                    account     uuid         not null,
                    "character" uuid         not null,
                    rank        integer      not null,
                    joined      timestamptz  not null,
                    primary key (guild, account, "character")
                )
                """,
                """
                create index if not exists live_guild_member_account on live_guild_member (account, joined)
                """,

                // Per-guild rank names, which are the one part of a charter that is genuinely the
                // guild's rather than the content's.
                """
                create table if not exists live_guild_rank (
                    guild uuid    not null references live_guild (id) on delete cascade,
                    rank  integer not null,
                    name  text    not null,
                    primary key (guild, rank)
                )
                """
            ]
        )
    ];
}
