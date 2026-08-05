// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live.Persistence;

/// <summary>The whole of persistence in a process. What every test in this tier runs against.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is <c>Vixen.Net.Transport.Local</c>'s place in doc 27's testing story, and
///         <c>Placement.Process</c>'s.</b> § Testing asks for a conservation oracle over thousands of
///         randomised concurrent transfers, aborts and crashes; nobody runs that against a database
///         on every push, so nobody runs it. Here it is a unit test that finishes in a second, and
///         the property it proves — that the semantics conserve value — is a property of the
///         semantics rather than of Postgres.
///     </para>
///     <para>
///         ⚠ <b>It is not a deployment target.</b> Nothing here survives the process, and doc 27
///         M-Q3's <i>one implementation behind an interface</i> means <see cref="SqlPersistence" />
///         is the shipped one. A game that ran on this would lose every character on restart, which
///         is why the type name says what it is.
///     </para>
///     <para>
///         One lock over everything, deliberately. A store whose whole state is three dictionaries
///         has no contention worth optimising, and a coarse lock is how the atomicity the interface
///         promises — an intent applies whole or not at all — is obviously correct rather than
///         argued.
///     </para>
/// </remarks>
public sealed class MemoryPersistence : IPersistence, IAccountRepository, IPlayerRepository, IGuildRepository, ILedger {
    readonly Lock gate = new();

    readonly Dictionary<Guid, AccountRecord> accounts = [];
    readonly Dictionary<string, Guid> handles = new(StringComparer.Ordinal);
    readonly Dictionary<PlayerKey, PlayerRecord> players = [];
    readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<PlayerKey, long> fences = [];

    readonly Dictionary<Guid, GuildRow> guilds = [];
    readonly HashSet<string> guildNames = new(StringComparer.OrdinalIgnoreCase);

    readonly List<LedgerEntry> journal = [];
    readonly Dictionary<(LedgerAccount Account, AssetId Asset), long> balances = [];
    readonly Dictionary<IdempotencyKey, long> applied = [];

    /// <summary>The store's own clock, so a test can make "recorded at" deterministic.</summary>
    /// <remarks>
    ///     Doc 27 § Persistence keeps the realm's clock and the store's apart on purpose — a realm's
    ///     idea of when it decided something is evidence, and the order rows landed in is fact. Both
    ///     are on <see cref="LedgerEntry" />; this is the second one.
    /// </remarks>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public IAccountRepository Accounts => this;

    /// <inheritdoc />
    public IPlayerRepository Players => this;

    /// <inheritdoc />
    public IGuildRepository Guilds => this;

    /// <inheritdoc />
    public ILedger Ledger => this;

    /// <summary>How many rows the journal holds.</summary>
    public int JournalLength {
        get {
            lock (gate) {
                return journal.Count;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>There is no schema. Answers with the version <see cref="Schema" /> is at, so a test
    /// that migrates before using a store behaves the same on either implementation.</remarks>
    public Task<int> MigrateAsync(CancellationToken cancellation) => Task.FromResult(Schema.Version);

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        lock (gate) {
            accounts.Clear();
            handles.Clear();
            players.Clear();
            names.Clear();
            fences.Clear();
            journal.Clear();
            balances.Clear();
            applied.Clear();
        }

        return ValueTask.CompletedTask;
    }

    // ── Accounts ────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<AccountRecord?> ReadAsync(Guid id, CancellationToken cancellation) {
        lock (gate) {
            return Task.FromResult(accounts.GetValueOrDefault(id));
        }
    }

    /// <inheritdoc />
    public Task<AccountRecord?> ByHandleAsync(string handle, CancellationToken cancellation) {
        lock (gate) {
            return Task.FromResult(
                handles.TryGetValue(handle ?? "", out var id) ? accounts.GetValueOrDefault(id) : null
            );
        }
    }

    /// <inheritdoc />
    public Task<(AccountRecord Account, bool Created)> EnsureAsync(
        string handle,
        DateTimeOffset now,
        CancellationToken cancellation
    ) {
        ArgumentException.ThrowIfNullOrEmpty(handle);

        lock (gate) {
            if (handles.TryGetValue(handle, out var existing)) {
                return Task.FromResult((accounts[existing], false));
            }

            var account = new AccountRecord(Guid.NewGuid(), handle, now, false);

            accounts[account.Id] = account;
            handles[handle] = account.Id;

            return Task.FromResult((account, true));
        }
    }

    /// <inheritdoc />
    public Task<WriteOutcome> SetSuspendedAsync(Guid id, bool suspended, CancellationToken cancellation) {
        lock (gate) {
            if (!accounts.TryGetValue(id, out var account)) {
                return Task.FromResult(WriteOutcome.Missing);
            }

            accounts[id] = account with { Suspended = suspended };

            return Task.FromResult(WriteOutcome.Written);
        }
    }

    // ── Characters ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<PlayerRecord?> ReadAsync(PlayerKey key, CancellationToken cancellation) {
        lock (gate) {
            return Task.FromResult(players.GetValueOrDefault(key));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PlayerRecord>> ForAccountAsync(Guid account, CancellationToken cancellation) {
        lock (gate) {
            return Task.FromResult<IReadOnlyList<PlayerRecord>>(
                [.. players.Values.Where(row => row.Key.Account == account).OrderBy(row => row.Created)]
            );
        }
    }

    /// <inheritdoc />
    public Task<WriteOutcome> CreateAsync(PlayerRecord record, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(record);

        lock (gate) {
            if (players.ContainsKey(record.Key) || !names.Add(record.Name)) {
                return Task.FromResult(WriteOutcome.Taken);
            }

            players[record.Key] = record;
            fences[record.Key] = record.LeaseEpoch;

            return Task.FromResult(WriteOutcome.Written);
        }
    }

    /// <inheritdoc />
    public Task<WriteOutcome> WriteAsync(PlayerRecord record, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(record);

        lock (gate) {
            if (!players.ContainsKey(record.Key)) {
                return Task.FromResult(WriteOutcome.Missing);
            }

            if (record.LeaseEpoch < fences.GetValueOrDefault(record.Key)) {
                return Task.FromResult(WriteOutcome.Superseded);
            }

            players[record.Key] = record;
            fences[record.Key] = record.LeaseEpoch;

            return Task.FromResult(WriteOutcome.Written);
        }
    }

    /// <inheritdoc />
    public Task<long> FenceAsync(PlayerKey key, CancellationToken cancellation) {
        lock (gate) {
            return Task.FromResult(fences.GetValueOrDefault(key));
        }
    }

    // ── The journal ─────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<LedgerResult> AppendAsync(LedgerIntent intent, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(intent);

        lock (gate) {
            if (!intent.Key.IsValid) {
                return Task.FromResult(new LedgerResult(LedgerVerdict.Unbalanced, 0, "the operation names nobody"));
            }

            // Before anything else, and before the balance check in particular: a replay must be
            // free even when the balances have since moved past being able to afford it again.
            if (applied.TryGetValue(intent.Key, out var already)) {
                return Task.FromResult(new LedgerResult(LedgerVerdict.Replayed, already));
            }

            if (!intent.IsBalanced()) {
                return Task.FromResult(
                    new LedgerResult(LedgerVerdict.Unbalanced, 0, "the movements do not sum to zero")
                );
            }

            if (intent.LeaseEpoch < fences.GetValueOrDefault(intent.Key.Player)) {
                return Task.FromResult(
                    new LedgerResult(
                        LedgerVerdict.Superseded,
                        0,
                        $"epoch {intent.LeaseEpoch} is below the fence at {fences[intent.Key.Player]}"
                    )
                );
            }

            foreach (var movement in intent.Movements) {
                var slot = (movement.Account, movement.Asset);

                // A world account is a faucet and is allowed to go negative — its balance is how much
                // of an asset has entered the economy. A character's is not: an overdrawn inventory
                // is the duplication bug wearing a minus sign.
                if (movement.Account.IsPlayer && balances.GetValueOrDefault(slot) + movement.Delta < 0) {
                    return Task.FromResult(
                        new LedgerResult(
                            LedgerVerdict.Insufficient,
                            0,
                            $"{movement.Account} holds {balances.GetValueOrDefault(slot)} {movement.Asset}"
                        )
                    );
                }
            }

            var recorded = Clock();
            var first = journal.Count == 0 ? 1 : journal[^1].Sequence + 1;
            var sequence = first;

            foreach (var movement in intent.Movements) {
                var slot = (movement.Account, movement.Asset);
                var balance = balances.GetValueOrDefault(slot) + movement.Delta;

                balances[slot] = balance;

                journal.Add(
                    new(
                        sequence++,
                        intent.Key,
                        movement.Account,
                        movement.Asset,
                        movement.Delta,
                        balance,
                        intent.At,
                        recorded,
                        intent.Detail
                    )
                );
            }

            applied[intent.Key] = first;
            fences[intent.Key.Player] = Math.Max(fences.GetValueOrDefault(intent.Key.Player), intent.LeaseEpoch);

            return Task.FromResult(new LedgerResult(LedgerVerdict.Applied, first));
        }
    }

    /// <inheritdoc />
    public Task<long> BalanceAsync(LedgerAccount account, AssetId asset, CancellationToken cancellation) {
        lock (gate) {
            return Task.FromResult(balances.GetValueOrDefault((account, asset)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<AssetId, long>> HoldingsAsync(
        LedgerAccount account,
        CancellationToken cancellation
    ) {
        lock (gate) {
            return Task.FromResult<IReadOnlyDictionary<AssetId, long>>(
                balances
                    .Where(slot => slot.Key.Account == account && slot.Value != 0)
                    .ToDictionary(slot => slot.Key.Asset, slot => slot.Value)
            );
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LedgerEntry>> HistoryAsync(LedgerQuery query, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(query);

        lock (gate) {
            IEnumerable<LedgerEntry> rows = journal;

            if (query.Account.IsValid) {
                rows = rows.Where(row => row.Account == query.Account);
            }

            if (query.Asset.IsValid) {
                rows = rows.Where(row => row.Asset == query.Asset);
            }

            if (query.Operation.IsValid) {
                rows = rows.Where(row => row.Key == query.Operation);
            }

            if (query.From is { } from) {
                rows = rows.Where(row => row.Recorded >= from);
            }

            if (query.Until is { } until) {
                rows = rows.Where(row => row.Recorded < until);
            }

            return Task.FromResult<IReadOnlyList<LedgerEntry>>(
                [.. rows.OrderByDescending(row => row.Sequence).Take(Math.Max(0, query.Limit))]
            );
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LedgerDiscrepancy>> ReconcileAsync(CancellationToken cancellation) {
        lock (gate) {
            var sums = new Dictionary<(LedgerAccount, AssetId), long>();

            foreach (var row in journal) {
                var slot = (row.Account, row.Asset);

                sums[slot] = sums.GetValueOrDefault(slot) + row.Delta;
            }

            List<LedgerDiscrepancy> wrong = [];

            foreach (var slot in balances.Keys.Union(sums.Keys)) {
                var stored = balances.GetValueOrDefault(slot);
                var journalled = sums.GetValueOrDefault(slot);

                if (stored != journalled) {
                    wrong.Add(new(slot.Item1, slot.Item2, stored, journalled));
                }
            }

            return Task.FromResult<IReadOnlyList<LedgerDiscrepancy>>(wrong);
        }
    }

    // ── Guilds ──────────────────────────────────────────────────────────────────────────────────
    //
    // ⚠ Explicit, and the reason is a real collision rather than a style choice: IGuildRepository's
    // ReadAsync(Guid) and ForAccountAsync(Guid) have the same signatures as IAccountRepository's and
    // IPlayerRepository's and differ only in return type, which C# cannot overload on. One class
    // implementing every repository is this file's whole shape, so the two that clash are explicit
    // and reached through the Guilds property — which is how a caller reaches them anyway.

    /// <inheritdoc />
    Task<GuildRow?> IGuildRepository.ReadAsync(Guid id, CancellationToken cancellation) {
        lock (gate) {
            return Task.FromResult(guilds.GetValueOrDefault(id));
        }
    }

    /// <inheritdoc />
    Task<IReadOnlyList<GuildRow>> IGuildRepository.ForAccountAsync(Guid account, CancellationToken cancellation) {
        lock (gate) {
            return Task.FromResult<IReadOnlyList<GuildRow>>([
                .. guilds.Values
                    .Where(guild => guild.Members.Any(member => member.Player.Account == account))
                    .OrderBy(guild => guild.Founded)
            ]);
        }
    }

    /// <inheritdoc />
    Task<WriteOutcome> IGuildRepository.WriteAsync(GuildRow row, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(row);

        lock (gate) {
            if (guilds.TryGetValue(row.Id, out var stored)) {
                // ⚠ The revision the caller read at, compared here rather than by the caller. Reading
                // it and then writing would be the same check with the race in the middle.
                if (stored.Revision >= row.Revision) {
                    return Task.FromResult(WriteOutcome.Superseded);
                }

                if (!string.Equals(stored.Name, row.Name, StringComparison.OrdinalIgnoreCase)
                    && !guildNames.Add(row.Name)) {
                    return Task.FromResult(WriteOutcome.Taken);
                }

                guildNames.Remove(stored.Name);
            } else if (!guildNames.Add(row.Name)) {
                return Task.FromResult(WriteOutcome.Taken);
            }

            guilds[row.Id] = row;

            return Task.FromResult(WriteOutcome.Written);
        }
    }

    /// <inheritdoc />
    Task<bool> IGuildRepository.DeleteAsync(Guid id, CancellationToken cancellation) {
        lock (gate) {
            if (!guilds.Remove(id, out var stored)) {
                return Task.FromResult(false);
            }

            guildNames.Remove(stored.Name);

            return Task.FromResult(true);
        }
    }

}
