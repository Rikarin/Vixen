// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Xunit;

namespace Vixen.Live.Placement.Tests;

/// <summary>What the client sends, what it reads back, and what the backend does about it.</summary>
public sealed class DockerPlacementTests : IDisposable {
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    readonly FakeEngine daemon = new();

    /// <summary>The handler is a disposable and the analyser is right to say so.</summary>
    public void Dispose() => daemon.Dispose();

    static RealmSpec Spec() =>
        new() {
            Shard = ShardId.New(),
            Key = new("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE)),
            Endpoint = new("", 0),
            Capacity = new(100, 120)
        };

    static DockerPlacementOptions Options(PortPool? ports = null) =>
        new() {
            Image = "mygame/realm:0.1.0",
            Owner = "queensdale",
            Ports = ports ?? new(7800, 7809),
            DrainTimeout = TimeSpan.FromMilliseconds(50),
            RemoveWhenStopped = false
        };

    DockerPlacement Placement(PortPool? ports = null) =>
        new(Options(ports), new DockerEngine(daemon), ownsEngine: true);

    [Fact]
    public async Task TheProbeAsksTheDaemonAndSaysWhatItFound() {
        using var placement = Placement();

        var probe = await placement.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(probe.Available);
        Assert.Equal(DockerPlacement.BackendName, probe.Backend);
        Assert.Contains("24.0.7", probe.Detail, StringComparison.Ordinal);
        Assert.Contains("mygame/realm:0.1.0", probe.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADaemonThatIsNotThereIsAnAnswerRatherThanAnException() {
        daemon.Version = null;

        using var placement = Placement();

        var probe = await placement.ProbeAsync(TestContext.Current.CancellationToken);

        // Every backend is probed at startup and the first that answers wins, so "Docker is not
        // installed" has to be a `false` rather than something to catch.
        Assert.False(probe.Available);
        Assert.NotEqual("", probe.Detail);
    }

    [Fact]
    public async Task ARealmContainerCarriesTheSpecTheLabelsAndThePublishedPort() {
        using var placement = Placement();

        var spec = Spec();
        var instance = await placement.StartAsync(spec, TestContext.Current.CancellationToken);

        Assert.InRange(instance.Endpoint.Port, 7800, 7809);

        var body = JsonDocument.Parse(daemon.Last.Body).RootElement;
        var command = body.GetProperty("cmd").EnumerateArray().Select(entry => entry.GetString()).ToList();

        // The spec on the command line, exactly as every other backend sends it.
        Assert.Contains(RealmSpec.ArgumentName, command, StringComparer.Ordinal);
        Assert.True(RealmSpec.TryRead(command!, _ => null, out var sent, out var why), why);
        Assert.Equal(instance.Endpoint, sent!.Endpoint);
        Assert.Equal(spec.Shard, sent.Shard);

        // The labels a restarted orchestrator finds the fleet by.
        Assert.Equal(spec.Shard.Value.ToString("D"), daemon.Last.Labels[DockerPlacementOptions.ShardLabel]);
        Assert.Equal("queensdale", daemon.Last.Labels[DockerPlacementOptions.OwnerLabel]);

        // The port, published on the host as well as exposed in the container.
        var port = $"{instance.Endpoint.Port}/udp";

        Assert.True(body.GetProperty("exposedPorts").TryGetProperty(port, out _));

        var bound = body.GetProperty("hostConfig").GetProperty("portBindings").GetProperty(port);

        Assert.Equal(
            instance.Endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bound[0].GetProperty("hostPort").GetString()
        );
    }

    [Fact]
    public async Task ARealmContainerNeverRestartsItself() {
        using var placement = Placement();

        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        // Doc 27 § Health: recovery is a placement, not a resurrection. A container the daemon
        // brought back would be a shard the cluster had already written off, returning with no
        // players and no map.
        var policy = JsonDocument.Parse(daemon.Last.Body)
            .RootElement.GetProperty("hostConfig")
            .GetProperty("restartPolicy")
            .GetProperty("name")
            .GetString();

        Assert.Equal("no", policy);
    }

    [Fact]
    public async Task ItIsNotATtySoTheLogStreamCanBeToldApart() {
        using var placement = Placement();

        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        // With a TTY the daemon merges stdout and stderr into one unframed stream, and a realm's
        // ready line becomes a substring match on everything it ever prints.
        Assert.False(JsonDocument.Parse(daemon.Last.Body).RootElement.GetProperty("tty").GetBoolean());
    }

    [Fact]
    public async Task ReadyArrivesThroughTheFramedLogStream() {
        using var placement = Placement();
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        Assert.Equal(PlacementEventKind.Started, (await watch.NextAsync()).Kind);

        // Two ordinary lines and then the signal, so the demultiplexer is exercised rather than
        // stepped over.
        daemon.Say(instance.Id.Value, "info: loading maps/queensdale");
        daemon.Say(instance.Id.Value, "warn: the navmesh is stale");
        daemon.Say(instance.Id.Value, RealmSignals.FormatReady(instance.Endpoint));

        var ready = await watch.NextAsync();

        Assert.Equal(PlacementEventKind.Ready, ready.Kind);
        Assert.Equal(instance.Endpoint, ready.Endpoint);
    }

    [Fact]
    public async Task AnExitNobodyAskedForIsLost() {
        using var placement = Placement();
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await watch.NextAsync();

        daemon.Exit(instance.Id.Value, 139);

        var lost = await watch.NextAsync();

        Assert.Equal(PlacementEventKind.Lost, lost.Kind);
        Assert.Contains("without being asked", lost.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShardThatEmptiedAndStoppedIsNotLost() {
        using var placement = Placement();
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await watch.NextAsync();

        daemon.Exit(instance.Id.Value, 0);

        Assert.Equal(PlacementEventKind.Stopped, (await watch.NextAsync()).Kind);
    }

    [Fact]
    public async Task DrainingWaitsAndThenStops() {
        using var placement = Placement();
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await watch.NextAsync();

        // ⚠ The polite half happens a layer up: a Docker deployment has an orchestrator, and the
        // realm learns to drain from the reply to its own heartbeat. This is only the deadline.
        await placement.StopAsync(instance.Id, StopMode.Drain, TestContext.Current.CancellationToken);

        var stopped = await watch.NextAsync();

        Assert.Equal(PlacementEventKind.Stopped, stopped.Kind);
        Assert.Contains(daemon.Requests, path => path.EndsWith($"/{instance.Id.Value}/stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APortIsGivenBackWhenTheContainerStops() {
        var ports = new PortPool(7800, 7801);

        using var placement = Placement(ports);
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await watch.NextAsync();
        Assert.Equal(1, ports.RentedCount);

        daemon.Exit(instance.Id.Value, 0);
        await watch.NextAsync();

        await Eventually(() => ports.RentedCount == 0);
    }

    [Fact]
    public async Task AFailedCreateDoesNotLeakThePortItWouldHaveUsed() {
        var ports = new PortPool(7800, 7800);

        daemon.Version = "24.0.7";

        using var placement = new DockerPlacement(
            Options(ports) with { Image = "mygame/realm:0.1.0" },
            new DockerEngine(new RefusingEngine()),
            ownsEngine: true
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.StartAsync(Spec(), TestContext.Current.CancellationToken)
        );

        Assert.Equal(0, ports.RentedCount);
    }

    [Fact]
    public async Task ListingIsAskedOfTheDaemonRatherThanRememberedFromTheDictionary() {
        using var placement = Placement();

        var spec = Spec();

        await placement.StartAsync(spec, TestContext.Current.CancellationToken);

        var listed = await placement.ListAsync(TestContext.Current.CancellationToken);

        // The whole use of this call is finding what an orchestrator left running when it restarted,
        // so an in-memory answer would be the one thing it must not give.
        Assert.Equal(spec.Shard, Assert.Single(listed).Shard);
        Assert.Contains(daemon.Requests, path => path.EndsWith("/containers/json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABackendWithNoImageRefusesWithSomethingActionable() {
        using var placement = new DockerPlacement(
            Options() with { Image = "" },
            new DockerEngine(daemon),
            ownsEngine: true
        );

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.StartAsync(Spec(), TestContext.Current.CancellationToken)
        );

        Assert.Contains("DockerPlacementOptions.Image", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposingLeavesTheContainersRunning() {
        var placement = Placement();

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        placement.Dispose();
        placement.Dispose();

        // ⚠ The opposite of ProcessPlacement, deliberately. A child process that outlived its
        // launcher is an orphan holding a UDP port; a container that outlives the orchestrator which
        // created it is a shard still serving players, and the labels are how the next orchestrator
        // finds it.
        Assert.Equal("running", daemon.Containers.Single(entry => entry.Id == instance.Id.Value).State);
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

    /// <summary>A daemon that refuses to create anything, which is how a bad image is injected.</summary>
    sealed class RefusingEngine : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) {
                    Content = new StringContent("{\"message\":\"No such image\"}")
                }
            );
    }
}
