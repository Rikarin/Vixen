// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Placement.Tests;

/// <summary>The backend that always answers, and every way a realm can fail to come up.</summary>
public sealed class ProcessPlacementTests {
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    static RealmSpec Spec(RealmEndpoint endpoint = default) =>
        new() {
            Shard = ShardId.New(),
            Key = new("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE)),
            Endpoint = endpoint.Host is { Length: > 0 } ? endpoint : new("", 0),
            Capacity = new(100, 120)
        };

    static ProcessPlacementOptions Options(PortPool? ports = null) =>
        new() {
            Executable = "dotnet",
            Arguments = ["MyGame.Realm.dll"],
            Ports = ports ?? new(7800, 7809),
            DrainTimeout = TimeSpan.FromMilliseconds(50),
            StopGrace = TimeSpan.FromMilliseconds(50)
        };

    [Fact]
    public async Task ItIsAlwaysAvailableAndSaysWhatItWouldLaunch() {
        using var placement = new ProcessPlacement(Options(), new FakeProcessHost());

        var probe = await placement.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(probe.Available);
        Assert.Equal(ProcessPlacement.BackendName, probe.Backend);
        Assert.Contains("dotnet", probe.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARealmIsHandedABoundEndpointItNeverHadToAskFor() {
        var processes = new FakeProcessHost();
        using var placement = new ProcessPlacement(Options(), processes);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        Assert.True(instance.Endpoint.IsValid);
        Assert.Equal("127.0.0.1", instance.Endpoint.Host);
        Assert.InRange(instance.Endpoint.Port, 7800, 7809);

        // The process was told, on its own command line, exactly where placement will send clients.
        // A realm that bound port zero and reported back would leave a window in which the
        // orchestrator has a shard it cannot address.
        Assert.Equal(instance.Endpoint, processes.Last.Spec.Endpoint);
        Assert.Equal(instance.Shard, processes.Last.Spec.Shard);
        Assert.Equal("MyGame.Realm.dll", processes.Last.Request.Arguments[0]);
        Assert.Equal(RealmSpec.ArgumentName, processes.Last.Request.Arguments[1]);
    }

    [Fact]
    public async Task ANamedPortIsHonouredRatherThanSecondGuessed() {
        var ports = new PortPool(7800, 7809);
        var processes = new FakeProcessHost();
        using var placement = new ProcessPlacement(Options(ports), processes);

        var instance = await placement.StartAsync(
            Spec(new("10.0.0.4", 30001)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new RealmEndpoint("10.0.0.4", 30001), instance.Endpoint);
        Assert.Equal(0, ports.RentedCount);
    }

    [Fact]
    public async Task StartingIsNotBeingReady() {
        var processes = new FakeProcessHost();
        using var placement = new ProcessPlacement(Options(), processes);
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        var started = await watch.NextAsync();

        Assert.Equal(PlacementEventKind.Started, started.Kind);
        Assert.Equal(instance.Id, started.Instance);

        // A slow map load must not look like a failed start, so readiness is the realm's word and
        // arrives whenever it arrives.
        processes.Last.SayReady();

        var ready = await watch.NextAsync();

        Assert.Equal(PlacementEventKind.Ready, ready.Kind);
        Assert.Equal(instance.Endpoint, ready.Endpoint);
    }

    [Fact]
    public async Task OrdinaryLoggingIsNotAReadySignalAndIsStillForwarded() {
        var lines = new List<string>();
        var processes = new FakeProcessHost();

        using var placement = new ProcessPlacement(
            Options() with { Output = (_, line) => lines.Add(line) },
            processes
        );

        await using var watch = new PlacementWatch(placement);
        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);
        await watch.NextAsync();

        processes.Last.Say("info: loading maps/queensdale");
        processes.Last.Say("warn: the navmesh is stale");

        Assert.Equal(2, lines.Count);
        Assert.False(watch.HasPending);
    }

    [Fact]
    public async Task AnExitNobodyAskedForIsLost() {
        var processes = new FakeProcessHost();
        using var placement = new ProcessPlacement(Options(), processes);
        await using var watch = new PlacementWatch(placement);

        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);
        await watch.NextAsync();

        processes.Last.Exit(code: 139);

        var lost = await watch.NextAsync();

        Assert.Equal(PlacementEventKind.Lost, lost.Kind);
        Assert.Contains("without being asked", lost.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShardThatEmptiedAndStoppedOnItsOwnIsNotLost() {
        var processes = new FakeProcessHost();
        using var placement = new ProcessPlacement(Options(), processes);
        await using var watch = new PlacementWatch(placement);

        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);
        await watch.NextAsync();

        // Doc 27 § Shard kinds: a public shard lives while it is populated, plus a grace. Exiting
        // zero when the last player left is the ordinary end of one, not an incident.
        processes.Last.Exit(code: 0);

        Assert.Equal(PlacementEventKind.Stopped, (await watch.NextAsync()).Kind);
    }

    [Fact]
    public async Task DrainingAsksRatherThanKills() {
        var processes = new FakeProcessHost();

        using var placement = new ProcessPlacement(
            Options() with { DrainTimeout = TimeSpan.FromMinutes(15) },
            processes
        );

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await placement.StopAsync(instance.Id, StopMode.Drain, TestContext.Current.CancellationToken);

        Assert.Equal([RealmSignals.Drain], processes.Last.Input);
        Assert.False(processes.Last.HasExited);
        Assert.False(processes.Last.WasKilled);
    }

    [Fact]
    public async Task ARealmThatWillNotGoIsKilledWhenThePatienceRunsOut() {
        var processes = new FakeProcessHost();
        using var placement = new ProcessPlacement(Options(), processes);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await placement.StopAsync(instance.Id, StopMode.Drain, TestContext.Current.CancellationToken);
        await Eventually(() => processes.Last.WasKilled);
    }

    [Fact]
    public async Task StoppingSomethingThatIsAlreadyGoneIsNotAnError() {
        using var placement = new ProcessPlacement(Options(), new FakeProcessHost());

        await placement.StopAsync(
            new("no-such-process"),
            StopMode.Immediate,
            TestContext.Current.CancellationToken
        );

        await placement.StopAsync(default, StopMode.Drain, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APortIsGivenBackWhenTheRealmStopsAndCanBeUsedAgain() {
        var ports = new PortPool(7800, 7801);
        var processes = new FakeProcessHost();
        using var placement = new ProcessPlacement(Options(ports), processes);
        await using var watch = new PlacementWatch(placement);

        var first = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await watch.NextAsync();
        Assert.Equal(1, ports.RentedCount);

        processes.Last.Exit();
        await watch.NextAsync();

        await Eventually(() => ports.RentedCount == 0);

        var second = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        // Round-robin rather than lowest-free: a realm that has just stopped may still have datagrams
        // in flight toward it, and reusing its port immediately would deliver them to its successor.
        Assert.NotEqual(first.Endpoint.Port, second.Endpoint.Port);
    }

    [Fact]
    public async Task AFailedStartDoesNotLeakThePortItWouldHaveUsed() {
        var ports = new PortPool(7800, 7800);
        var processes = new FakeProcessHost { Refuse = new InvalidOperationException("no such file") };
        using var placement = new ProcessPlacement(Options(ports), processes);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.StartAsync(Spec(), TestContext.Current.CancellationToken)
        );

        // The one machine where starts fail is the one machine where somebody is watching, and a
        // launcher that leaked a port per failure would run out of range in front of them.
        Assert.Equal(0, ports.RentedCount);
    }

    [Fact]
    public async Task AnExhaustedRangeSaysSoRatherThanStartingSomethingUnreachable() {
        var ports = new PortPool(7800, 7800);
        using var placement = new ProcessPlacement(Options(ports), new FakeProcessHost());

        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.StartAsync(Spec(), TestContext.Current.CancellationToken)
        );

        Assert.Contains("exhausted", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALauncherWithNoExecutableRefusesWithSomethingActionable() {
        using var placement = new ProcessPlacement(new ProcessPlacementOptions(), new FakeProcessHost());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.StartAsync(Spec(), TestContext.Current.CancellationToken)
        );

        Assert.Contains("ProcessPlacementOptions.Executable", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASpecThatIsNotRunnableIsRefusedBeforeAnythingIsStarted() {
        var processes = new FakeProcessHost();
        using var placement = new ProcessPlacement(Options(), processes);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.StartAsync(new RealmSpec(), TestContext.Current.CancellationToken)
        );

        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task ListIsWhatIsRunningAndNothingElse() {
        var processes = new FakeProcessHost();
        using var placement = new ProcessPlacement(Options(), processes);
        await using var watch = new PlacementWatch(placement);

        var first = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);
        var second = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        var listed = await placement.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, instance => instance.Id == first.Id);
        Assert.Contains(listed, instance => instance.Id == second.Id);

        processes.Started.First().Exit();
        await Eventually(async () => (await placement.ListAsync(TestContext.Current.CancellationToken)).Count == 1);
    }

    [Fact]
    public async Task DisposingTakesTheFleetWithIt() {
        var processes = new FakeProcessHost();
        var placement = new ProcessPlacement(Options(), processes);

        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);
        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        placement.Dispose();
        placement.Dispose();

        // A launcher that exited leaving realms holding UDP ports is the thing that makes a
        // developer reboot. Surviving a launcher is what the Kubernetes backend's owner reference is
        // for, and it says so explicitly.
        Assert.All(processes.Started, process => Assert.True(process.WasKilled));

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await placement.StartAsync(Spec(), TestContext.Current.CancellationToken)
        );
    }

    static async Task Eventually(Func<bool> condition) {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline) {
            if (condition()) {
                return;
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The condition was still false after {Patience}.");
    }

    static async Task Eventually(Func<Task<bool>> condition) {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline) {
            if (await condition()) {
                return;
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The condition was still false after {Patience}.");
    }
}
