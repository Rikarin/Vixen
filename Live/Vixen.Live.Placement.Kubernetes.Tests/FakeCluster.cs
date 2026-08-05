// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using k8s.Models;

namespace Vixen.Live.Placement.Tests;

/// <summary>A Kubernetes cluster that is two dictionaries.</summary>
/// <remarks>
///     ⚠ <b>The API server is faked; the pod is not.</b> Everything asserted here is about the object
///     this backend <em>builds</em> — a Pod rather than a Deployment, a hostPort rather than a
///     Service, an owner reference that is not a controller reference — and those are exactly the
///     decisions ADR-019 spends its length on. Whether a real API server accepts them is the nightly
///     <c>kind</c> leg's question, which doc 27 § Testing already puts there.
/// </remarks>
sealed class FakeCluster : IClusterApi {
    readonly ConcurrentDictionary<string, V1Pod> pods = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Channel<string>> logs = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, V1Node> nodes = new(StringComparer.Ordinal);

    /// <summary>What the API server says it is, or null to refuse.</summary>
    public string? Version { get; set; } = "v1.31.2";

    /// <summary>Whether a pod's log streams at all.</summary>
    /// <remarks>
    ///     ⚠ <b>False is not "the realm said nothing" — it is the 400 a real API server answers with
    ///     while the container has not started.</b> A pod that is being scheduled or pulling has no
    ///     log to read and says so with an error, not an empty stream, which is why a backend that
    ///     treated the end of that stream as the end of the pod would report every realm Lost
    ///     milliseconds after placing it.
    /// </remarks>
    public bool LogsReadable { get; set; } = true;

    /// <summary>Every pod it holds.</summary>
    public IReadOnlyCollection<V1Pod> Pods => [.. pods.Values];

    /// <summary>The most recently created one.</summary>
    public V1Pod Last { get; private set; } = new();

    /// <summary>Names the pods that were deleted, in order.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>Puts a node in the cluster.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="external">Its external address, or null for none.</param>
    /// <param name="internalAddress">Its internal one.</param>
    public void AddNode(string name, string? external, string internalAddress = "10.244.0.7") {
        List<V1NodeAddress> addresses = [new() { Type = "InternalIP", Address = internalAddress }];

        if (external is not null) {
            addresses.Add(new() { Type = "ExternalIP", Address = external });
        }

        nodes[name] = new() {
            Metadata = new() { Name = name },
            Status = new() { Addresses = addresses }
        };
    }

    /// <summary>Schedules a pod onto a node, as the scheduler would.</summary>
    /// <param name="name">Which pod.</param>
    /// <param name="node">Which node.</param>
    /// <param name="hostIp">The node's own address, as the pod's status reports it.</param>
    public void Schedule(string name, string node, string hostIp = "10.244.0.7") {
        var pod = pods[name];

        pod.Spec!.NodeName = node;
        pod.Status = new() { Phase = "Running", HostIP = hostIp };
    }

    /// <summary>Says a line as a realm would.</summary>
    /// <param name="name">Which pod.</param>
    /// <param name="line">What it wrote.</param>
    public void Say(string name, string line) => logs[name].Writer.TryWrite(line);

    /// <summary>Leaves a pod's container waiting, the way a kubelet reports one that has not run.</summary>
    /// <param name="name">Which pod.</param>
    /// <param name="reason">The waiting reason, as <c>ContainerCreating</c> or <c>CreateContainerError</c>.</param>
    /// <param name="message">What the kubelet said about it, if anything.</param>
    public void Waiting(string name, string reason, string message = "") {
        var pod = pods[name];

        pod.Status = new() {
            Phase = "Pending",
            ContainerStatuses =
            [
                new() {
                    Name = "realm",
                    Ready = false,
                    Image = pod.Spec!.Containers[0].Image,
                    ImageID = "",
                    RestartCount = 0,
                    State = new() { Waiting = new() { Reason = reason, Message = message } }
                }
            ]
        };
    }

    /// <summary>Ends a pod.</summary>
    /// <param name="name">Which pod.</param>
    /// <param name="phase"><c>Succeeded</c> or <c>Failed</c>.</param>
    /// <param name="reason">What the container status calls it, or empty for no container status.</param>
    /// <param name="code">The exit code that went with it.</param>
    public void Finish(string name, string phase, string reason = "", int code = 0) {
        if (pods.TryGetValue(name, out var pod)) {
            pod.Status ??= new();
            pod.Status.Phase = phase;

            if (reason.Length > 0) {
                pod.Status.ContainerStatuses =
                [
                    new() {
                        Name = "realm",
                        Ready = false,
                        Image = pod.Spec!.Containers[0].Image,
                        ImageID = "",
                        RestartCount = 0,
                        State = new() { Terminated = new() { Reason = reason, ExitCode = code } }
                    }
                ];
            }
        }

        logs[name].Writer.TryComplete();
    }

    /// <inheritdoc />
    public Task<string> VersionAsync(CancellationToken cancellation) =>
        Version is null
            ? Task.FromException<string>(new HttpRequestException("no route to the API server"))
            : Task.FromResult(Version);

    /// <inheritdoc />
    public Task<V1Pod> CreatePodAsync(V1Pod pod, string space, CancellationToken cancellation) {
        var name = pod.Metadata!.Name!;

        pods[name] = pod;
        logs[name] = Channel.CreateUnbounded<string>();
        Last = pod;

        return Task.FromResult(pod);
    }

    /// <inheritdoc />
    public Task<V1Pod?> ReadPodAsync(string name, string space, CancellationToken cancellation) =>
        Task.FromResult(pods.GetValueOrDefault(name));

    /// <inheritdoc />
    public Task DeletePodAsync(string name, string space, TimeSpan grace, CancellationToken cancellation) {
        Deleted.Add(name);
        Finish(name, "Succeeded");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<V1Pod>> ListPodsAsync(string space, string selector, CancellationToken cancellation) {
        var wanted = selector.Split('=');

        return Task.FromResult<IReadOnlyList<V1Pod>>(
            [
                .. pods.Values.Where(pod =>
                    pod.Metadata?.Labels is { } labels
                    && labels.TryGetValue(wanted[0], out var value)
                    && string.Equals(value, wanted[1], StringComparison.Ordinal)
                )
            ]
        );
    }

    /// <inheritdoc />
    public Task<V1Node?> ReadNodeAsync(string name, CancellationToken cancellation) =>
        Task.FromResult(nodes.GetValueOrDefault(name));

    /// <inheritdoc />
    public async IAsyncEnumerable<string> FollowLogAsync(
        string name,
        string space,
        [EnumeratorCancellation] CancellationToken cancellation
    ) {
        if (!LogsReadable || !logs.TryGetValue(name, out var channel)) {
            yield break;
        }

        while (true) {
            string line;

            try {
                line = await channel.Reader.ReadAsync(cancellation).ConfigureAwait(false);
            } catch (Exception failure) when (failure is ChannelClosedException or OperationCanceledException) {
                yield break;
            }

            yield return line;
        }
    }
}
