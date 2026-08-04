// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live;
using Vixen.Live.Cluster;
using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>`vixen live`, driven through the same parser a person uses.</summary>
/// <remarks>
///     ⚠ <b>What is under test is the formatting and the argument handling, and neither needs a
///     silo.</b> <c>ILiveOperations</c> is the seam — the same shape <c>IFleetDirectory</c> takes in
///     the gate — so these run in milliseconds on every push rather than against a cluster somebody
///     has to stand up.
/// </remarks>
public sealed class LiveCommandTests : IDisposable {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly RealmVersion Running = new("0.1.0", 0xC0FFEE);

    readonly FakeFleet fleet = new();
    readonly StringWriter output = new();

    public LiveCommandTests() => VixenCommand.LiveOperationsFactory = _ => fleet;

    public void Dispose() {
        VixenCommand.LiveOperationsFactory = null;
        output.Dispose();
    }

    [Fact]
    public void Live_is_a_verb_with_the_four_that_can_be_answered() {
        var live = Assert.Single(VixenCommand.Create().Subcommands, command => command.Name == "live");

        Assert.Equal(
            ["drain", "explain", "status", "upgrade"],
            live.Subcommands.Select(command => command.Name).OrderBy(name => name, StringComparer.Ordinal)
        );
    }

    [Fact]
    public async Task Status_lists_the_shards() {
        fleet.Shards = [Shard(ShardState.Ready, 42), Shard(ShardState.Draining, 3)];

        Assert.Equal(0, await RunAsync("live", "status", "--map", "maps/queensdale", "--region", "eu"));

        var text = output.ToString();

        Assert.Contains("SHARD", text, StringComparison.Ordinal);
        Assert.Contains("Ready", text, StringComparison.Ordinal);
        Assert.Contains("Draining", text, StringComparison.Ordinal);
        Assert.Contains("42", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Not an error. A map with no shards is what every map looks like before anybody plays it,
    ///     and an operator who typed the name wrong wants to be told the name they typed.
    /// </summary>
    [Fact]
    public async Task A_map_with_no_shards_is_not_a_failure() {
        Assert.Equal(0, await RunAsync("live", "status", "--map", "maps/nowhere", "--region", "eu"));
        Assert.Contains("maps/nowhere", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draining_names_the_shard_and_says_what_a_drain_is() {
        var shard = ShardId.New();

        Assert.Equal(0, await RunAsync("live", "drain", "--shard", shard.ToString(), "--reason", "rolling to 0.2.0"));

        Assert.Equal(shard, fleet.Drained);
        Assert.Equal("rolling to 0.2.0", fleet.Reason);

        // The word suggests something more violent than it is, so the command says so.
        Assert.Contains("Nobody is disconnected", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shard_id_that_is_not_one_is_refused_before_the_cluster_is_touched() {
        Assert.Equal(2, await RunAsync("live", "drain", "--shard", "the-big-one"));

        Assert.Equal(ShardId.None, fleet.Drained);
        Assert.Contains("not a shard id", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_prints_what_the_map_remembers() {
        fleet.Explanation = "placed on the one your guild is on";

        var player = new PlayerKey(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(
            0,
            await RunAsync("live", "explain", "--player", player.ToString(), "--map", "maps/queensdale", "--region", "eu")
        );

        Assert.Contains("your guild", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(player, fleet.Asked);
    }

    [Fact]
    public async Task A_player_that_is_not_a_pair_is_refused() {
        Assert.Equal(2, await RunAsync("live", "explain", "--player", "bruna", "--map", "maps/a", "--region", "eu"));
        Assert.Contains("account/character", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upgrade_with_no_version_reads_the_target() {
        fleet.Target = Running;
        fleet.Spread = 0.25;

        Assert.Equal(0, await RunAsync("live", "upgrade", "--region", "eu"));

        var text = output.ToString();

        Assert.Contains(Running.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("25", text, StringComparison.Ordinal);
        Assert.Equal(RealmVersion.None, fleet.SetTo);
    }

    [Fact]
    public async Task A_region_with_no_target_says_what_that_means() {
        Assert.Equal(0, await RunAsync("live", "upgrade", "--region", "eu"));
        Assert.Contains("no target set", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Doc 27 § Upgrades: nothing about the mechanism is directional.</summary>
    [Fact]
    public async Task Upgrade_with_a_version_moves_the_target_and_says_how_to_undo_it() {
        Assert.Equal(0, await RunAsync("live", "upgrade", "--region", "eu", "--version", "0.2.0+00000000deadbeef"));

        Assert.Equal(new("0.2.0", 0xDEADBEEF), fleet.SetTo);
        Assert.Contains("roll back with the same command", output.ToString(), StringComparison.Ordinal);
    }

    async Task<int> RunAsync(params string[] args) =>
        await VixenCommand.Create(output, output).Parse(args).InvokeAsync();

    static ShardReport Shard(ShardState state, int population) =>
        new(
            ShardId.New(),
            new("maps/queensdale", "eu", Running),
            state,
            new("realm.example", 30000),
            new("pod-1"),
            population,
            new(50, 60),
            Noon,
            Noon
        );

    sealed class FakeFleet : ILiveOperations {
        public IReadOnlyList<ShardReport> Shards { get; set; } = [];

        public ShardId Drained { get; private set; } = ShardId.None;

        public string Reason { get; private set; } = "";

        public PlayerKey Asked { get; private set; }

        public string Explanation { get; set; } = "nothing is held";

        public RealmVersion Target { get; set; }

        public double Spread { get; set; }

        public RealmVersion SetTo { get; private set; }

        public Task<IReadOnlyList<ShardReport>> ShardsAsync(ShardKey key, CancellationToken cancellation) =>
            Task.FromResult(Shards);

        public Task DrainAsync(ShardId shard, string reason, CancellationToken cancellation) {
            Drained = shard;
            Reason = reason;

            return Task.CompletedTask;
        }

        public Task<string> ExplainAsync(ShardKey key, PlayerKey player, CancellationToken cancellation) {
            Asked = player;

            return Task.FromResult(Explanation);
        }

        public Task<(RealmVersion Target, double Spread)> RolloutAsync(string region, CancellationToken cancellation) =>
            Task.FromResult((Target, Spread));

        public Task SetTargetAsync(string region, RealmVersion version, CancellationToken cancellation) {
            SetTo = version;

            return Task.CompletedTask;
        }
    }
}
