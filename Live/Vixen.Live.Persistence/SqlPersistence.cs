// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Vixen.Live.Persistence;

/// <summary>The shipped one: ADO.NET, PostgreSQL dialect, one transaction per intent.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A <see cref="DbDataSource" /> rather than a driver package, and that is a deliberate
///         line.</b> Doc 27 M-Q3 says one implementation, and this is it — the SQL in
///         <see cref="Schema" /> is Postgres and does not pretend otherwise. What this assembly does
///         <em>not</em> do is reference Npgsql: the caller constructs
///         <c>NpgsqlDataSource.Create(connectionString)</c> and hands it over. The gain is that a
///         game engine does not pin a database driver's version for every game that links it, and
///         that connection pooling, logging, tracing and TLS are configured where the deployment
///         already configures them rather than through options this layer would have to mirror.
///     </para>
///     <para>
///         ⚠ <b>What a test can and cannot say about this class.</b> Every semantic doc 27
///         § Persistence names — idempotency, the fence, conservation — is asserted against
///         <see cref="MemoryPersistence" /> on every push, because those are properties of the
///         semantics. Whether a real PostgreSQL accepts these statements is a question only a real
///         PostgreSQL answers, and it belongs on the same nightly leg as the <c>kind</c> and Docker
///         placement backends. That is the same honest split <c>IClusterApi</c> makes.
///     </para>
/// </remarks>
/// <param name="source">The connection source. Postgres, whoever's driver.</param>
/// <param name="ownsSource">Whether disposing this should dispose that.</param>
public sealed class SqlPersistence(DbDataSource source, bool ownsSource = false)
    : IPersistence, IAccountRepository, IPlayerRepository, ILedger {
    readonly DbDataSource source = source ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc />
    public IAccountRepository Accounts => this;

    /// <inheritdoc />
    public IPlayerRepository Players => this;

    /// <inheritdoc />
    public ILedger Ledger => this;

    /// <inheritdoc />
    public async Task<int> MigrateAsync(CancellationToken cancellation) {
        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        await Execute(connection, null, Schema.CreateVersionTable, cancellation).ConfigureAwait(false);

        var at = 0;

        await using (var command = Text(connection, null, $"select coalesce(max(version), 0) from {Schema.VersionTable}")) {
            at = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false) ?? 0,
                CultureInfo.InvariantCulture
            );
        }

        foreach (var step in Schema.Steps) {
            if (step.Version <= at) {
                continue;
            }

            // The statements and the row saying they ran are one transaction, so a migration that
            // fails halfway leaves a database that will try the whole step again rather than one
            // that believes it is at a version it is not.
            await using var transaction = await connection.BeginTransactionAsync(cancellation).ConfigureAwait(false);

            foreach (var statement in step.Statements) {
                await Execute(connection, transaction, statement, cancellation).ConfigureAwait(false);
            }

            await Execute(
                    connection,
                    transaction,
                    $"insert into {Schema.VersionTable} (version, note, applied_at) values (@version, @note, @at)",
                    cancellation,
                    ("version", step.Version),
                    ("note", step.Note),
                    ("at", DateTimeOffset.UtcNow)
                )
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellation).ConfigureAwait(false);

            at = step.Version;
        }

        return at;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (ownsSource) {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Accounts ────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AccountRecord?> ReadAsync(Guid id, CancellationToken cancellation) {
        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        return await ReadAccount(
                connection,
                "select id, handle, created, suspended from live_account where id = @id",
                cancellation,
                ("id", id)
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AccountRecord?> ByHandleAsync(string handle, CancellationToken cancellation) {
        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        return await ReadAccount(
                connection,
                "select id, handle, created, suspended from live_account where handle = @handle",
                cancellation,
                ("handle", handle ?? "")
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(AccountRecord Account, bool Created)> EnsureAsync(
        string handle,
        DateTimeOffset now,
        CancellationToken cancellation
    ) {
        ArgumentException.ThrowIfNullOrEmpty(handle);

        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        var id = Guid.NewGuid();

        // `on conflict do nothing` plus a read, rather than a read plus an insert: two gates racing a
        // first login is the ordinary case on a launch day, and the loser of that race must end up
        // with the winner's account rather than with an error or a second one.
        await Execute(
                connection,
                null,
                """
                insert into live_account (id, handle, created, suspended)
                values (@id, @handle, @created, false)
                on conflict (handle) do nothing
                """,
                cancellation,
                ("id", id),
                ("handle", handle),
                ("created", now)
            )
            .ConfigureAwait(false);

        var account = await ReadAccount(
                connection,
                "select id, handle, created, suspended from live_account where handle = @handle",
                cancellation,
                ("handle", handle)
            )
            .ConfigureAwait(false);

        return account is null
            ? throw new InvalidOperationException($"The account for handle '{handle}' vanished between insert and read.")
            : (account, account.Id == id);
    }

    /// <inheritdoc />
    public async Task<WriteOutcome> SetSuspendedAsync(Guid id, bool suspended, CancellationToken cancellation) {
        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        var rows = await Execute(
                connection,
                null,
                "update live_account set suspended = @suspended where id = @id",
                cancellation,
                ("suspended", suspended),
                ("id", id)
            )
            .ConfigureAwait(false);

        return rows > 0 ? WriteOutcome.Written : WriteOutcome.Missing;
    }

    // ── Characters ──────────────────────────────────────────────────────────────────────────────

    const string PlayerColumns =
        "account, \"character\", name, created, last_seen, region, home_map, lease_epoch, profile";

    /// <inheritdoc />
    public async Task<PlayerRecord?> ReadAsync(PlayerKey key, CancellationToken cancellation) {
        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);
        await using var command = Text(
            connection,
            null,
            $"select {PlayerColumns} from live_player where account = @account and \"character\" = @character",
            ("account", key.Account),
            ("character", key.Character)
        );
        await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        return await reader.ReadAsync(cancellation).ConfigureAwait(false) ? Player(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlayerRecord>> ForAccountAsync(Guid account, CancellationToken cancellation) {
        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);
        await using var command = Text(
            connection,
            null,
            $"select {PlayerColumns} from live_player where account = @account order by created",
            ("account", account)
        );
        await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        List<PlayerRecord> rows = [];

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false)) {
            rows.Add(Player(reader));
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<WriteOutcome> CreateAsync(PlayerRecord record, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        var rows = await Execute(
                connection,
                null,
                $"""
                 insert into live_player ({PlayerColumns})
                 values (@account, @character, @name, @created, @last_seen, @region, @home_map, @epoch, @profile)
                 on conflict do nothing
                 """,
                cancellation,
                ("account", record.Key.Account),
                ("character", record.Key.Character),
                ("name", record.Name),
                ("created", record.Created),
                ("last_seen", record.LastSeen),
                ("region", record.Region),
                ("home_map", record.HomeMap),
                ("epoch", record.LeaseEpoch),
                ("profile", record.Profile.ToArray())
            )
            .ConfigureAwait(false);

        return rows > 0 ? WriteOutcome.Written : WriteOutcome.Taken;
    }

    /// <inheritdoc />
    public async Task<WriteOutcome> WriteAsync(PlayerRecord record, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        // The fence is the `where` clause. Reading the epoch and then writing would be the same
        // check with a race in the middle, and the race is exactly the transfer this guards.
        var rows = await Execute(
                connection,
                null,
                """
                update live_player
                   set name = @name, last_seen = @last_seen, region = @region,
                       home_map = @home_map, lease_epoch = @epoch, profile = @profile
                 where account = @account and "character" = @character and lease_epoch <= @epoch
                """,
                cancellation,
                ("name", record.Name),
                ("last_seen", record.LastSeen),
                ("region", record.Region),
                ("home_map", record.HomeMap),
                ("epoch", record.LeaseEpoch),
                ("profile", record.Profile.ToArray()),
                ("account", record.Key.Account),
                ("character", record.Key.Character)
            )
            .ConfigureAwait(false);

        if (rows > 0) {
            return WriteOutcome.Written;
        }

        // Nothing updated is two different situations, and the caller has to tell them apart: a
        // missing character is a bug, a superseded one is a transfer that already happened.
        return await ReadAsync(record.Key, cancellation).ConfigureAwait(false) is null
            ? WriteOutcome.Missing
            : WriteOutcome.Superseded;
    }

    /// <inheritdoc />
    public async Task<long> FenceAsync(PlayerKey key, CancellationToken cancellation) {
        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);
        await using var command = Text(
            connection,
            null,
            "select coalesce(max(lease_epoch), 0) from live_player where account = @account and \"character\" = @character",
            ("account", key.Account),
            ("character", key.Character)
        );

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false) ?? 0L,
            CultureInfo.InvariantCulture
        );
    }

    // ── The journal ─────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<LedgerResult> AppendAsync(LedgerIntent intent, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(intent);

        if (!intent.Key.IsValid) {
            return new(LedgerVerdict.Unbalanced, 0, "the operation names nobody");
        }

        if (!intent.IsBalanced()) {
            return new(LedgerVerdict.Unbalanced, 0, "the movements do not sum to zero");
        }

        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        // Serializable, and it is worth saying why the cheaper levels are wrong. The check that a
        // character can afford what is being taken reads a balance the same transaction then writes;
        // under read-committed two concurrent spends both read the old balance and both succeed,
        // which is the overdraft this whole design exists to make impossible.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellation)
            .ConfigureAwait(false);

        // Claiming the idempotency key first means a duplicate delivery does no work at all: the
        // insert loses, and the row that won tells us where the original landed.
        var claimed = await Execute(
                connection,
                transaction,
                """
                insert into live_ledger_op (op_account, op_character, op_kind, op_id, first_sequence)
                values (@account, @character, @kind, @op, 0)
                on conflict do nothing
                """,
                cancellation,
                ("account", intent.Key.Player.Account),
                ("character", intent.Key.Player.Character),
                ("kind", intent.Key.Kind),
                ("op", intent.Key.Operation)
            )
            .ConfigureAwait(false);

        if (claimed == 0) {
            await using var existing = Text(
                connection,
                transaction,
                """
                select first_sequence from live_ledger_op
                 where op_account = @account and op_character = @character
                   and op_kind = @kind and op_id = @op
                """,
                ("account", intent.Key.Player.Account),
                ("character", intent.Key.Player.Character),
                ("kind", intent.Key.Kind),
                ("op", intent.Key.Operation)
            );

            var sequence = Convert.ToInt64(
                await existing.ExecuteScalarAsync(cancellation).ConfigureAwait(false) ?? 0L,
                CultureInfo.InvariantCulture
            );

            await transaction.RollbackAsync(cancellation).ConfigureAwait(false);

            return new(LedgerVerdict.Replayed, sequence);
        }

        await using (var fence = Text(
            connection,
            transaction,
            "select coalesce(max(lease_epoch), 0) from live_player where account = @account and \"character\" = @character",
            ("account", intent.Key.Player.Account),
            ("character", intent.Key.Player.Character)
        )) {
            var held = Convert.ToInt64(
                await fence.ExecuteScalarAsync(cancellation).ConfigureAwait(false) ?? 0L,
                CultureInfo.InvariantCulture
            );

            if (intent.LeaseEpoch < held) {
                await transaction.RollbackAsync(cancellation).ConfigureAwait(false);

                return new(LedgerVerdict.Superseded, 0, $"epoch {intent.LeaseEpoch} is below the fence at {held}");
            }
        }

        var recorded = DateTimeOffset.UtcNow;
        var first = 0L;

        foreach (var movement in intent.Movements) {
            var account = movement.Account.ToString();
            var asset = movement.Asset.ToString();

            long balance;

            await using (var upsert = Text(
                connection,
                transaction,
                """
                insert into live_balance (account, asset, quantity) values (@account, @asset, @delta)
                on conflict (account, asset) do update set quantity = live_balance.quantity + @delta
                returning quantity
                """,
                ("account", account),
                ("asset", asset),
                ("delta", movement.Delta)
            )) {
                balance = Convert.ToInt64(
                    await upsert.ExecuteScalarAsync(cancellation).ConfigureAwait(false) ?? 0L,
                    CultureInfo.InvariantCulture
                );
            }

            if (movement.Account.IsPlayer && balance < 0) {
                await transaction.RollbackAsync(cancellation).ConfigureAwait(false);

                return new(
                    LedgerVerdict.Insufficient,
                    0,
                    $"{movement.Account} would hold {balance} {movement.Asset}"
                );
            }

            await using var insert = Text(
                connection,
                transaction,
                """
                insert into live_ledger
                    (op_account, op_character, op_kind, op_id, account, asset, delta, balance, at, recorded, detail)
                values
                    (@op_account, @op_character, @kind, @op, @account, @asset, @delta, @balance, @at, @recorded, @detail)
                returning sequence
                """,
                ("op_account", intent.Key.Player.Account),
                ("op_character", intent.Key.Player.Character),
                ("kind", intent.Key.Kind),
                ("op", intent.Key.Operation),
                ("account", account),
                ("asset", asset),
                ("delta", movement.Delta),
                ("balance", balance),
                ("at", intent.At),
                ("recorded", recorded),
                ("detail", intent.Detail ?? "")
            );

            var sequence = Convert.ToInt64(
                await insert.ExecuteScalarAsync(cancellation).ConfigureAwait(false) ?? 0L,
                CultureInfo.InvariantCulture
            );

            if (first == 0) {
                first = sequence;
            }
        }

        await Execute(
                connection,
                transaction,
                """
                update live_ledger_op set first_sequence = @first
                 where op_account = @account and op_character = @character and op_kind = @kind and op_id = @op
                """,
                cancellation,
                ("first", first),
                ("account", intent.Key.Player.Account),
                ("character", intent.Key.Player.Character),
                ("kind", intent.Key.Kind),
                ("op", intent.Key.Operation)
            )
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellation).ConfigureAwait(false);

        return new(LedgerVerdict.Applied, first);
    }

    /// <inheritdoc />
    public async Task<long> BalanceAsync(LedgerAccount account, AssetId asset, CancellationToken cancellation) {
        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);
        await using var command = Text(
            connection,
            null,
            "select coalesce(sum(quantity), 0) from live_balance where account = @account and asset = @asset",
            ("account", account.ToString()),
            ("asset", asset.ToString())
        );

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false) ?? 0L,
            CultureInfo.InvariantCulture
        );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<AssetId, long>> HoldingsAsync(
        LedgerAccount account,
        CancellationToken cancellation
    ) {
        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);
        await using var command = Text(
            connection,
            null,
            "select asset, quantity from live_balance where account = @account and quantity <> 0",
            ("account", account.ToString())
        );
        await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        Dictionary<AssetId, long> holdings = [];

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false)) {
            holdings[new(reader.GetString(0))] = reader.GetInt64(1);
        }

        return holdings;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LedgerEntry>> HistoryAsync(LedgerQuery query, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(query);

        List<(string Name, object? Value)> arguments = [];
        var where = new System.Text.StringBuilder(" where 1 = 1");

        if (query.Account.IsValid) {
            where.Append(" and account = @account");
            arguments.Add(("account", query.Account.ToString()));
        }

        if (query.Asset.IsValid) {
            where.Append(" and asset = @asset");
            arguments.Add(("asset", query.Asset.ToString()));
        }

        if (query.Operation.IsValid) {
            where.Append(
                " and op_account = @op_account and op_character = @op_character and op_kind = @kind and op_id = @op"
            );
            arguments.Add(("op_account", query.Operation.Player.Account));
            arguments.Add(("op_character", query.Operation.Player.Character));
            arguments.Add(("kind", query.Operation.Kind));
            arguments.Add(("op", query.Operation.Operation));
        }

        if (query.From is { } from) {
            where.Append(" and recorded >= @from");
            arguments.Add(("from", from));
        }

        if (query.Until is { } until) {
            where.Append(" and recorded < @until");
            arguments.Add(("until", until));
        }

        arguments.Add(("limit", Math.Max(0, query.Limit)));

        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);
        await using var command = Text(
            connection,
            null,
            "select sequence, op_account, op_character, op_kind, op_id, account, asset, delta, balance, "
            + "at, recorded, detail from live_ledger"
            + where
            + " order by sequence desc limit @limit",
            [.. arguments]
        );
        await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        List<LedgerEntry> rows = [];

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false)) {
            rows.Add(
                new(
                    reader.GetInt64(0),
                    new(new(reader.GetGuid(1), reader.GetGuid(2)), reader.GetString(3), reader.GetString(4)),
                    Account(reader.GetString(5)),
                    new(reader.GetString(6)),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetFieldValue<DateTimeOffset>(9),
                    reader.GetFieldValue<DateTimeOffset>(10),
                    reader.GetString(11)
                )
            );
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LedgerDiscrepancy>> ReconcileAsync(CancellationToken cancellation) {
        await using var connection = await source.OpenConnectionAsync(cancellation).ConfigureAwait(false);

        // A full outer join, because the two ways for this to be wrong are a balance the journal does
        // not support and a journal the balance table has never heard of, and the second is the one
        // that would otherwise go unnoticed.
        await using var command = Text(
            connection,
            null,
            """
            select coalesce(b.account, j.account), coalesce(b.asset, j.asset),
                   coalesce(b.quantity, 0), coalesce(j.total, 0)
              from live_balance b
              full outer join (
                    select account, asset, sum(delta) as total from live_ledger group by account, asset
              ) j on j.account = b.account and j.asset = b.asset
             where coalesce(b.quantity, 0) <> coalesce(j.total, 0)
            """
        );
        await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        List<LedgerDiscrepancy> wrong = [];

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false)) {
            wrong.Add(new(Account(reader.GetString(0)), new(reader.GetString(1)), reader.GetInt64(2), reader.GetInt64(3)));
        }

        return wrong;
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────

    static LedgerAccount Account(string text) =>
        LedgerAccount.TryParse(text, out var account) ? account : LedgerAccount.Nowhere;

    static PlayerRecord Player(DbDataReader reader) =>
        new(
            new(reader.GetGuid(0), reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetFieldValue<byte[]>(8)
        );

    static async Task<AccountRecord?> ReadAccount(
        DbConnection connection,
        string sql,
        CancellationToken cancellation,
        params (string Name, object? Value)[] arguments
    ) {
        await using var command = Text(connection, null, sql, arguments);
        await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        return await reader.ReadAsync(cancellation).ConfigureAwait(false)
            ? new(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetBoolean(3)
            )
            : null;
    }

    static async Task<int> Execute(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellation,
        params (string Name, object? Value)[] arguments
    ) {
        await using var command = Text(connection, transaction, sql, arguments);

        return await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
    }

    static DbCommand Text(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] arguments
    ) {
        var command = connection.CreateCommand();

        command.CommandText = sql;
        command.Transaction = transaction;

        foreach (var (name, value) in arguments) {
            var parameter = command.CreateParameter();

            parameter.ParameterName = name;

            // Normalised to UTC because `timestamptz` is a point in time and several drivers refuse a
            // DateTimeOffset carrying an offset rather than silently converting it. Recording the
            // offset the caller happened to be in would be recording the caller's timezone, which is
            // not a fact about when anything happened.
            parameter.Value = value switch {
                DateTimeOffset moment => moment.ToUniversalTime(),
                null => DBNull.Value,
                _ => value
            };

            command.Parameters.Add(parameter);
        }

        return command;
    }
}
