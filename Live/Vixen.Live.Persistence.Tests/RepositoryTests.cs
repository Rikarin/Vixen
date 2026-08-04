// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.Live.Persistence.Tests;

/// <summary>Accounts and characters, and the fence that makes one writer one writer.</summary>
public class RepositoryTests {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_first_login_makes_an_account_and_the_second_finds_it() {
        await using var store = new MemoryPersistence();

        var (first, created) = await store.Accounts.EnsureAsync("steam:76561198000000000", Noon, TestContext.Current.CancellationToken);
        var (again, createdAgain) = await store.Accounts.EnsureAsync("steam:76561198000000000", Noon, TestContext.Current.CancellationToken);

        Assert.True(created);
        Assert.False(createdAgain);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first, await store.Accounts.ReadAsync(first.Id, TestContext.Current.CancellationToken));
        Assert.Equal(first, await store.Accounts.ByHandleAsync("steam:76561198000000000", TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     Two gates racing a first login is the ordinary case on a launch day, and the loser must
    ///     end up with the winner's account rather than with a second one.
    /// </summary>
    [Fact]
    public async Task Concurrent_first_logins_produce_one_account() {
        await using var store = new MemoryPersistence();

        var races = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.Accounts.EnsureAsync("oidc:alice", Noon, TestContext.Current.CancellationToken)))
        );

        Assert.Single(races.Select(race => race.Account.Id).Distinct());
        Assert.Single(races, race => race.Created);
    }

    [Fact]
    public async Task An_unknown_handle_is_null_rather_than_an_error() {
        await using var store = new MemoryPersistence();

        Assert.Null(await store.Accounts.ByHandleAsync("nobody", TestContext.Current.CancellationToken));
        Assert.Null(await store.Accounts.ReadAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Equal(WriteOutcome.Missing, await store.Accounts.SetSuspendedAsync(Guid.NewGuid(), true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Suspension_is_a_field_and_reads_back() {
        await using var store = new MemoryPersistence();

        var (account, _) = await store.Accounts.EnsureAsync("oidc:bob", Noon, TestContext.Current.CancellationToken);

        Assert.Equal(WriteOutcome.Written, await store.Accounts.SetSuspendedAsync(account.Id, true, TestContext.Current.CancellationToken));

        var read = await store.Accounts.ReadAsync(account.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.True(read.Suspended);
    }

    [Fact]
    public async Task Characters_come_back_for_their_account_oldest_first() {
        await using var store = new MemoryPersistence();

        var account = Guid.NewGuid();
        var other = Guid.NewGuid();

        await store.Players.CreateAsync(Character(account, "Bruna", Noon), TestContext.Current.CancellationToken);
        await store.Players.CreateAsync(Character(account, "Aleks", Noon.AddDays(1)), TestContext.Current.CancellationToken);
        await store.Players.CreateAsync(Character(other, "Someone else", Noon), TestContext.Current.CancellationToken);

        var mine = await store.Players.ForAccountAsync(account, TestContext.Current.CancellationToken);

        Assert.Equal(["Bruna", "Aleks"], mine.Select(row => row.Name));
    }

    [Fact]
    public async Task A_name_is_taken_once() {
        await using var store = new MemoryPersistence();

        Assert.Equal(WriteOutcome.Written, await store.Players.CreateAsync(Character(Guid.NewGuid(), "Bruna", Noon), TestContext.Current.CancellationToken));
        Assert.Equal(WriteOutcome.Taken, await store.Players.CreateAsync(Character(Guid.NewGuid(), "bruna", Noon), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writing_a_character_who_does_not_exist_is_missing_rather_than_a_create() {
        await using var store = new MemoryPersistence();

        Assert.Equal(WriteOutcome.Missing, await store.Players.WriteAsync(Character(Guid.NewGuid(), "Ghost", Noon), TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     The fence is monotonic and raised by use. A write at epoch <i>n</i> refuses every later
    ///     write below <i>n</i>, forever — which is what makes a realm that lost its lease mid-combat
    ///     harmless rather than something anybody has to notice in time.
    /// </summary>
    [Fact]
    public async Task The_fence_only_ever_rises() {
        await using var store = new MemoryPersistence();

        var record = Character(Guid.NewGuid(), "Bruna", Noon);

        await store.Players.CreateAsync(record, TestContext.Current.CancellationToken);

        Assert.Equal(1, await store.Players.FenceAsync(record.Key, TestContext.Current.CancellationToken));

        Assert.Equal(WriteOutcome.Written, await store.Players.WriteAsync(record with { LeaseEpoch = 4 }, TestContext.Current.CancellationToken));
        Assert.Equal(4, await store.Players.FenceAsync(record.Key, TestContext.Current.CancellationToken));

        Assert.Equal(WriteOutcome.Superseded, await store.Players.WriteAsync(record with { LeaseEpoch = 3 }, TestContext.Current.CancellationToken));
        Assert.Equal(WriteOutcome.Superseded, await store.Players.WriteAsync(record with { LeaseEpoch = 1 }, TestContext.Current.CancellationToken));
        Assert.Equal(4, await store.Players.FenceAsync(record.Key, TestContext.Current.CancellationToken));

        // The same epoch is allowed: one realm writing repeatedly under one lease is the common case.
        Assert.Equal(WriteOutcome.Written, await store.Players.WriteAsync(record with { LeaseEpoch = 4 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_profile_round_trips_by_value() {
        await using var store = new MemoryPersistence();

        var record = Character(Guid.NewGuid(), "Bruna", Noon) with {
            Profile = Encoding.UTF8.GetBytes("{\"chapter\":7}")
        };

        await store.Players.CreateAsync(record, TestContext.Current.CancellationToken);

        var read = await store.Players.ReadAsync(record.Key, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(record, read);
        Assert.Equal("{\"chapter\":7}", Encoding.UTF8.GetString(read.Profile.Span));
    }

    [Fact]
    public async Task An_unwritten_character_has_no_fence() {
        await using var store = new MemoryPersistence();

        Assert.Equal(0, await store.Players.FenceAsync(new(Guid.NewGuid(), Guid.NewGuid()), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Migrating_a_memory_store_answers_the_schemas_version() {
        await using var store = new MemoryPersistence();

        Assert.Equal(Schema.Version, await store.MigrateAsync(TestContext.Current.CancellationToken));
    }

    static PlayerRecord Character(Guid account, string name, DateTimeOffset created) =>
        new(
            new(account, Guid.NewGuid()),
            name,
            created,
            created,
            "eu",
            "maps/queensdale",
            1,
            ReadOnlyMemory<byte>.Empty
        );
}
