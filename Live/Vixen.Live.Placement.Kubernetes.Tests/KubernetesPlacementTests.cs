// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Placement.Tests;

/// <summary>The object ADR-019 spends its length arguing for, asserted.</summary>
public sealed class KubernetesPlacementTests {
    readonly FakeCluster cluster = new();

    static RealmSpec Spec() =>
        new() {
            Shard = ShardId.New(),
            Key = new("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE)),
            Endpoint = default,
            Capacity = new(100, 120)
        };

    static KubernetesPlacementOptions Options(PortPool? ports = null, PodIdentity? self = null) =>
        new() {
            Image = "mygame/realm:0.1.0",
            Namespace = "queensdale",
            Owner = "eu-fleet",
            Ports = ports ?? new(30000, 30009),
            Self = self,
            DrainTimeout = TimeSpan.FromMilliseconds(50)
        };

    KubernetesPlacement Placement(PortPool? ports = null, PodIdentity? self = null) =>
        new(Options(ports, self), cluster);

    // ── The probe ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheProbeAsksTheApiServerAndSaysWhatItFound() {
        using var placement = Placement();

        var probe = await placement.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(probe.Available);
        Assert.Equal(KubernetesPlacement.BackendName, probe.Backend);
        Assert.Contains("v1.31.2", probe.Detail, StringComparison.Ordinal);
        Assert.Contains("queensdale", probe.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClusterThatIsNotThereIsAnAnswerRatherThanAnException() {
        cluster.Version = null;

        using var placement = Placement();

        var probe = await placement.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(probe.Available);
        Assert.NotEqual("", probe.Detail);
    }

    // ── The pod ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARealmIsAPodAndNotADeployment() {
        using var placement = Placement();

        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        // ⚠ ADR-019's central decision. Realms are not fungible replicas: each has an identity, a
        // map, a population and a lifetime that ends when the last player leaves. A Deployment's
        // controller would restart one that exited on purpose.
        Assert.Equal("Never", cluster.Last.Spec!.RestartPolicy);
        Assert.Single(cluster.Last.Spec.Containers);
    }

    [Fact]
    public async Task ThePortIsAHostPortAndNotAService() {
        using var placement = Placement();

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        var port = cluster.Last.Spec!.Containers[0].Ports!.Single();

        // A Service per realm puts kube-proxy and conntrack between the player and the simulation —
        // the gateway problem in a different hat, which doc 27 § The routing question spends a page
        // rejecting — and consumes a cluster IP per shard.
        Assert.Equal("UDP", port.Protocol);
        Assert.Equal(port.ContainerPort, port.HostPort);
        Assert.InRange(port.HostPort!.Value, 30000, 30009);
        Assert.Equal(port.HostPort, instance.Endpoint.Port);
    }

    [Fact]
    public async Task ThePodCarriesTheSpecAndTheLabelsAFleetIsFoundBy() {
        using var placement = Placement();

        var spec = Spec();

        await placement.StartAsync(spec, TestContext.Current.CancellationToken);

        var args = cluster.Last.Spec!.Containers[0].Args!.ToList();

        Assert.True(RealmSpec.TryRead(args, _ => null, out var sent, out var why), why);
        Assert.Equal(spec.Shard, sent!.Shard);
        Assert.Equal(cluster.Last.Spec.Containers[0].Ports![0].HostPort, sent.Endpoint.Port);

        var labels = cluster.Last.Metadata!.Labels!;

        Assert.Equal(spec.Shard.Value.ToString("D"), labels[KubernetesPlacementOptions.ShardLabel]);
        Assert.Equal("eu-fleet", labels[KubernetesPlacementOptions.OwnerLabel]);
    }

    [Fact]
    public async Task TheOwnerReferenceIsNotAControllerReference() {
        using var placement = Placement(self: new PodIdentity("orchestrator-0", "abc-123"));

        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        var owner = cluster.Last.Metadata!.OwnerReferences!.Single();

        Assert.Equal("orchestrator-0", owner.Name);
        Assert.Equal("abc-123", owner.Uid);

        // ⚠ Owned, not managed. A controller reference tells other controllers to keep their hands
        // off — true — and also invites one to reconcile it, which is exactly what must not happen to
        // a realm. What the reference buys is garbage collection if the orchestrator is destroyed,
        // which is the one thing a Deployment was wanted for.
        Assert.False(owner.Controller);
        Assert.False(owner.BlockOwnerDeletion);
    }

    [Fact]
    public async Task AnOrchestratorOutsideTheClusterOwnsNothing() {
        using var placement = Placement();

        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        // Right for an orchestrator running outside, and worth knowing about: those pods survive it.
        Assert.Null(cluster.Last.Metadata!.OwnerReferences);
    }

    [Fact]
    public void TheOrchestratorsIdentityComesFromTheDownwardApi() {
        Assert.Null(PodIdentity.FromEnvironment(_ => null));
        Assert.Null(PodIdentity.FromEnvironment(name => name == "VIXEN_POD_NAME" ? "orchestrator-0" : null));

        var identity = PodIdentity.FromEnvironment(
            name => name switch {
                "VIXEN_POD_NAME" => "orchestrator-0",
                "VIXEN_POD_UID" => "abc-123",
                _ => null
            }
        );

        Assert.Equal(new PodIdentity("orchestrator-0", "abc-123"), identity);
    }

    // ── The address ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheAddressIsTheNodesExternalOneAndArrivesWithReady() {
        using var placement = Placement();
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        // ⚠ Started carries no endpoint, and cannot: the scheduler has not placed the pod, so there
        // is no node and therefore no address.
        var started = await watch.NextAsync();

        Assert.Equal(PlacementEventKind.Started, started.Kind);
        Assert.False(started.Endpoint.IsValid);

        cluster.AddNode("node-3", external: "203.0.113.9");
        cluster.Schedule(instance.Id.Value, "node-3");
        cluster.Say(instance.Id.Value, RealmSignals.FormatReady(new("10.244.3.1", 30000)));

        var ready = await watch.NextAsync();

        // And the realm's own idea of where it bound is inside the pod's network namespace, so it is
        // not what a client is told. This is the one place a backend overrules the realm.
        Assert.Equal(PlacementEventKind.Ready, ready.Kind);
        Assert.Equal("203.0.113.9", ready.Endpoint.Host);
        Assert.Equal(instance.Endpoint.Port, ready.Endpoint.Port);
    }

    [Fact]
    public async Task AClusterWithNoExternalAddressesFallsBackToTheInternalOne() {
        using var placement = Placement();
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await watch.NextAsync();

        // A single-node development cluster, where the fallback is right; in production it is a
        // shard nobody outside can reach, and the endpoint says so rather than being empty.
        cluster.AddNode("node-1", external: null, internalAddress: "10.244.0.7");
        cluster.Schedule(instance.Id.Value, "node-1");
        cluster.Say(instance.Id.Value, RealmSignals.FormatReady(new("10.244.3.1", 30000)));

        Assert.Equal("10.244.0.7", (await watch.NextAsync()).Endpoint.Host);
    }

    [Fact]
    public async Task ANodeThisOrchestratorMayNotReadFallsBackToThePodsHostIp() {
        using var placement = Placement();
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await watch.NextAsync();

        // No AddNode: the RBAC does not let this orchestrator read Node objects, which is a cluster
        // whose roles have not been told about it. A fleet that refused to place anything because of
        // that would be a very confusing outage.
        cluster.Schedule(instance.Id.Value, "node-9", hostIp: "192.0.2.44");
        cluster.Say(instance.Id.Value, RealmSignals.FormatReady(new("10.244.3.1", 30000)));

        Assert.Equal("192.0.2.44", (await watch.NextAsync()).Endpoint.Host);
    }

    // ── The end of a shard ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APodThatSucceededIsStopped() {
        using var placement = Placement();
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await watch.NextAsync();

        cluster.Finish(instance.Id.Value, "Succeeded");

        Assert.Equal(PlacementEventKind.Stopped, (await watch.NextAsync()).Kind);
    }

    [Fact]
    public async Task APodThatFailedIsLost() {
        using var placement = Placement();
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await watch.NextAsync();

        cluster.Finish(instance.Id.Value, "Failed");

        var lost = await watch.NextAsync();

        Assert.Equal(PlacementEventKind.Lost, lost.Kind);
        Assert.Contains("without being asked", lost.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingDeletesThePodAndGivesThePortBack() {
        var ports = new PortPool(30000, 30001);

        using var placement = Placement(ports);
        await using var watch = new PlacementWatch(placement);

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        await watch.NextAsync();
        Assert.Equal(1, ports.RentedCount);

        await placement.StopAsync(instance.Id, StopMode.Immediate, TestContext.Current.CancellationToken);

        Assert.Equal(PlacementEventKind.Stopped, (await watch.NextAsync()).Kind);
        Assert.Contains(instance.Id.Value, cluster.Deleted, StringComparer.Ordinal);
        Assert.Equal(0, ports.RentedCount);
    }

    // ── Refusals ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASpecThatNamesItsOwnPortIsRefused() {
        using var placement = Placement();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.StartAsync(
                Spec() with { Endpoint = new("10.0.0.4", 7777) },
                TestContext.Current.CancellationToken
            )
        );

        // A hostPort has to come from the range the cluster's firewall was opened for — doc 27 M5's
        // one prerequisite — so a caller choosing its own would produce a shard nobody can reach.
        Assert.Contains("Leave the endpoint unbound", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExhaustedNodePortRangeSaysSo() {
        var ports = new PortPool(30000, 30000);

        using var placement = Placement(ports);

        await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.StartAsync(Spec(), TestContext.Current.CancellationToken)
        );

        Assert.Contains("node port range is exhausted", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABackendWithNoImageRefusesWithSomethingActionable() {
        using var placement = new KubernetesPlacement(Options() with { Image = "" }, cluster);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.StartAsync(Spec(), TestContext.Current.CancellationToken)
        );

        Assert.Contains("KubernetesPlacementOptions.Image", failure.Message, StringComparison.Ordinal);
    }

    // ── Reconciliation ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListingAsksTheApiServerSoARestartFindsTheFleet() {
        using var placement = Placement();

        var spec = Spec();

        await placement.StartAsync(spec, TestContext.Current.CancellationToken);

        var listed = await placement.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(spec.Shard, Assert.Single(listed).Shard);
    }

    [Fact]
    public async Task DisposingLeavesThePodsRunning() {
        var placement = Placement();

        var instance = await placement.StartAsync(Spec(), TestContext.Current.CancellationToken);

        placement.Dispose();
        placement.Dispose();

        // A pod that outlives the orchestrator which created it is a shard still serving players.
        // What eventually collects it is the owner reference, when that orchestrator is destroyed
        // rather than merely restarted.
        Assert.Empty(cluster.Deleted);
        Assert.Contains(cluster.Pods, pod => pod.Metadata!.Name == instance.Id.Value);
    }
}
