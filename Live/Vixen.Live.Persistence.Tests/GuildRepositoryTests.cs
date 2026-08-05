// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Xunit;

namespace Vixen.Live.Persistence.Tests;

/// <summary>
///     The repository doc 27 § Persistence names and L3 slice one deferred: "a repository's
///     single-writer discipline comes from the grain that owns the aggregate ... It lands with the
///     grain." <c>IGuildGrain</c> landed, so this can.
/// </summary>
public sealed class GuildRepositoryTests : IAsyncDisposable {
    static readonly PlayerKey Leader = new(Guid.NewGuid(), Guid.NewGuid());
    static readonly PlayerKey Member = new(Guid.NewGuid(), Guid.NewGuid());

    readonly MemoryPersistence store = new();
    readonly Guid id = Guid.NewGuid();

    static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => store.DisposeAsync();

    GuildRow Row(uint revision = 1, string name = "The Fellowship", params GuildMemberRow[] members) =>
        new(
            id,
            "guilds/charter",
            name,
            members.Length > 0 ? [.. members] : [new(Leader, 0, DateTimeOffset.UnixEpoch)],
            ImmutableDictionary<int, string>.Empty,
            DateTimeOffset.UnixEpoch,
            revision
        );

    [Fact]
    public async Task AGuildComesBackAsItWasWritten() {
        var row = Row() with { RankNames = ImmutableDictionary<int, string>.Empty.Add(1, "Champion") };

        Assert.Equal(WriteOutcome.Written, await store.Guilds.WriteAsync(row, Token));

        // Compared whole, which only passes because the equality is hand-written: a record's
        // generated Equals compares an ImmutableArray by reference.
        Assert.Equal(row, await store.Guilds.ReadAsync(id, Token));
    }

    [Fact]
    public async Task AGuildNobodyWroteIsNullRatherThanAnError() =>
        Assert.Null(await store.Guilds.ReadAsync(Guid.NewGuid(), Token));

    [Fact]
    public async Task AWriteAtAStaleRevisionIsSuperseded() {
        // ⚠ The fence, and it is the revision rather than a lease epoch because a guild has no
        // lease — its single writer is the grain's own turn. What this guards is a stale writer
        // after a reactivation.
        await store.Guilds.WriteAsync(Row(revision: 5), Token);

        Assert.Equal(WriteOutcome.Superseded, await store.Guilds.WriteAsync(Row(revision: 5), Token));
        Assert.Equal(WriteOutcome.Superseded, await store.Guilds.WriteAsync(Row(revision: 4), Token));
        Assert.Equal(WriteOutcome.Written, await store.Guilds.WriteAsync(Row(revision: 6), Token));
    }

    [Fact]
    public async Task TwoGuildsMayNotShareAName() {
        await store.Guilds.WriteAsync(Row(), Token);

        var other = Row() with { Id = Guid.NewGuid() };

        Assert.Equal(WriteOutcome.Taken, await store.Guilds.WriteAsync(other, Token));
    }

    [Fact]
    public async Task RenamingAGuildFreesItsOldName() {
        await store.Guilds.WriteAsync(Row(), Token);
        await store.Guilds.WriteAsync(Row(revision: 2, name: "The Company"), Token);

        var other = Row() with { Id = Guid.NewGuid() };

        Assert.Equal(WriteOutcome.Written, await store.Guilds.WriteAsync(other, Token));
    }

    [Fact]
    public async Task TheRosterIsQueryableWhichIsWhyItIsRowsAndNotABlob() {
        // ⚠ Doc 27 § Persistence: gameplay data kept in grain storage "cannot be queried by anything
        // else — including the support tool, the economy dashboard and the analytics job". "Which
        // guilds is this account in" is a query.
        await store.Guilds.WriteAsync(
            Row(members: [new(Leader, 0, DateTimeOffset.UnixEpoch), new(Member, 2, DateTimeOffset.UnixEpoch)]),
            Token
        );

        Assert.Single(await store.Guilds.ForAccountAsync(Leader.Account, Token));
        Assert.Single(await store.Guilds.ForAccountAsync(Member.Account, Token));
        Assert.Empty(await store.Guilds.ForAccountAsync(Guid.NewGuid(), Token));
    }

    [Fact]
    public async Task TheRosterIsReplacedWholesaleRatherThanMerged() {
        await store.Guilds.WriteAsync(
            Row(members: [new(Leader, 0, DateTimeOffset.UnixEpoch), new(Member, 2, DateTimeOffset.UnixEpoch)]),
            Token
        );

        await store.Guilds.WriteAsync(Row(revision: 2, members: [new(Leader, 0, DateTimeOffset.UnixEpoch)]), Token);

        var stored = await store.Guilds.ReadAsync(id, Token);

        Assert.Single(stored!.Members);
        Assert.Empty(await store.Guilds.ForAccountAsync(Member.Account, Token));
    }

    [Fact]
    public async Task AnEndedGuildTakesItsRosterWithIt() {
        await store.Guilds.WriteAsync(Row(members: [new(Leader, 0, DateTimeOffset.UnixEpoch)]), Token);

        Assert.True(await store.Guilds.DeleteAsync(id, Token));
        Assert.Null(await store.Guilds.ReadAsync(id, Token));
        Assert.Empty(await store.Guilds.ForAccountAsync(Leader.Account, Token));

        // And its name is free again.
        Assert.Equal(WriteOutcome.Written, await store.Guilds.WriteAsync(Row() with { Id = Guid.NewGuid() }, Token));
    }

    [Fact]
    public async Task DeletingSomethingThatIsNotThereSaysSo() =>
        Assert.False(await store.Guilds.DeleteAsync(Guid.NewGuid(), Token));

    [Fact]
    public async Task GuildsComeBackOldestFirst() {
        var first = Row() with { Id = Guid.NewGuid(), Founded = DateTimeOffset.UnixEpoch, Name = "First" };
        var second = Row() with {
            Id = Guid.NewGuid(), Founded = DateTimeOffset.UnixEpoch.AddDays(1), Name = "Second"
        };

        await store.Guilds.WriteAsync(second, Token);
        await store.Guilds.WriteAsync(first, Token);

        Assert.Equal(
            ["First", "Second"],
            (await store.Guilds.ForAccountAsync(Leader.Account, Token)).Select(row => row.Name)
        );
    }

    [Fact]
    public async Task TheSchemaHasAStepForThis() {
        // The migration is a step of its own rather than an edit to step 1: a deployment that has
        // already run 1 must not be asked to run it again.
        Assert.Contains(Schema.Steps, step => step.Version == 2);
        Assert.Contains(Schema.Steps, step => step.Statements.Any(sql => sql.Contains("live_guild_member", StringComparison.Ordinal)));

        Assert.Equal(2, await store.MigrateAsync(Token));
    }
}
