// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Placement.Tests;

/// <summary>The pod this backend builds, against a real API server.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything else in this project is asserted against <c>FakeCluster</c>, and its own
///         remark says why: the API server is faked and the pod is not.</b> Every decision ADR-019
///         spends its length on — a Pod rather than a Deployment, a <c>hostPort</c> rather than a
///         Service, an owner reference that is not a controller reference — is about the object this
///         builds, and those are worth asserting on every push. What a fake cannot answer is whether
///         a real API server <em>accepts</em> that object: whether the schema validates, whether the
///         owner reference resolves, whether the port is admissible, and whether the pod reaches
///         Running. This is the <c>kind</c> leg doc 27 § Testing puts those on.
///     </para>
///     <para>
///         ⚠ <b>Skipped without <c>VIXEN_KUBECONFIG</c>.</b> Pointing this at whatever kubeconfig a
///         developer happens to have would place realms into whatever context that file points at,
///         which is the exact accident <c>ClusterApi.Connect</c>'s in-cluster-first rule exists to
///         prevent. So it takes an explicit path and nothing else.
///     </para>
/// </remarks>
public class ClusterApiTests {
    /// <summary>The kubeconfig the nightly leg writes for its `kind` cluster.</summary>
    const string Variable = "VIXEN_KUBECONFIG";

    /// <summary>Where the leg puts its pods, so a failed clean-up is one namespace to drop.</summary>
    const string Namespace = "vixen-nightly";

    [Fact]
    public async Task The_api_server_answers_the_probe() {
        using var placement = Open();

        var probe = await placement.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(probe.Available, probe.Detail);
    }

    /// <summary>
    ///     ⚠ The question a fake cannot ask: does a real API server take this object? Everything about
    ///     the pod's shape is asserted elsewhere; what is asserted here is only that the cluster said
    ///     yes to it and gave it back.
    /// </summary>
    [Fact]
    public async Task A_pod_is_accepted_listed_and_deleted() {
        using var placement = Open();

        var shard = ShardId.New();

        var spec = new RealmSpec {
            Shard = shard,
            Key = new("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE)),
            Endpoint = new("0.0.0.0", 0),
            Capacity = new(10, 12),
            TickRate = 30
        };

        var instance = await placement.StartAsync(spec, TestContext.Current.CancellationToken);

        try {
            Assert.True(instance.Id.IsValid);

            var listed = await placement.ListAsync(TestContext.Current.CancellationToken);

            Assert.Contains(listed, found => found.Shard == shard);
        } finally {
            await placement.StopAsync(instance.Id, StopMode.Immediate, CancellationToken.None);
        }
    }

    static KubernetesPlacement Open() {
        var kubeconfig = Environment.GetEnvironmentVariable(Variable);

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(kubeconfig) || !File.Exists(kubeconfig),
            $"{Variable} names no readable kubeconfig, so no cluster is being driven. The nightly leg "
            + "writes one for its `kind` cluster; a laptop is expected not to, because pointing this "
            + "at whatever context a developer has is how realms end up somewhere real."
        );

        var client = new k8s.Kubernetes(
            k8s.KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfig)
        );

        return new(
            new() {
                // A tiny image that sits still, so a pod that is never cleaned up costs a few
                // megabytes rather than a crash loop. What is under test is the API server's
                // acceptance of the object, not what runs inside it.
                Image = "registry.k8s.io/pause:3.10",
                Namespace = Namespace,
                Ports = new(31000, 31099)
            },
            new ClusterApi(client)
        );
    }
}
