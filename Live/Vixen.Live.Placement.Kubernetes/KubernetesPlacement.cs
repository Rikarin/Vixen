// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using k8s.Models;

namespace Vixen.Live.Placement;

/// <summary>What <see cref="KubernetesPlacement" /> creates, and where.</summary>
public sealed record KubernetesPlacementOptions {
    /// <summary>The label a realm pod carries its shard id under.</summary>
    public const string ShardLabel = "live.vixenengine.org/shard";

    /// <summary>The label a realm pod carries its owner under.</summary>
    public const string OwnerLabel = "live.vixenengine.org/owner";

    /// <summary>The realm's image.</summary>
    public string Image { get; init; } = "";

    /// <summary>Which namespace realm pods go in.</summary>
    public string Namespace { get; init; } = "default";

    /// <summary>Which orchestrator these pods belong to.</summary>
    public string Owner { get; init; } = "vixen";

    /// <summary>Arguments to put before the encoded <see cref="RealmSpec" />.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Variables to set in every realm pod.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The per-node port range realms take a <c>hostPort</c> from.</summary>
    /// <remarks>
    ///     ⚠ <b>The one piece of cluster configuration this design requires</b>, and doc 27 M5 names
    ///     it as such: the range has to be open on the nodes' firewall, and nothing here can check
    ///     that. A realm placed on a closed port is a shard that reports ready and that nobody can
    ///     reach — which looks exactly like a network problem and is one.
    /// </remarks>
    public PortPool Ports { get; init; } = new(30000, 30099);

    /// <summary>The orchestrator's own pod, for the owner reference.</summary>
    /// <remarks>
    ///     <para>
    ///         ADR-019: an owner reference gives the one thing a Deployment's controller was wanted
    ///         for — garbage collection if the orchestrator itself is destroyed — without any of the
    ///         behaviour that makes a controller wrong here. A Deployment would restart a realm that
    ///         exited on purpose, and its rolling update is the wrong shape for § Upgrades: draining
    ///         a realm means moving <em>players</em>, not terminating a pod when a readiness probe
    ///         flips.
    ///     </para>
    ///     <para>
    ///         Null means no owner reference, which is right for an orchestrator running outside the
    ///         cluster and wrong for one running inside it — a pod with no owner survives its
    ///         orchestrator's namespace being deleted.
    ///     </para>
    /// </remarks>
    public PodIdentity? Self { get; init; }

    /// <summary>How long a pod's process gets after its termination signal.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="StopMode.Drain" /> waits before deleting.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Called with every line a realm writes.</summary>
    public Action<RealmInstanceId, string>? Output { get; init; }
}

/// <summary>The orchestrator's own pod, as the downward API reports it.</summary>
/// <param name="Name">Its name, from <c>metadata.name</c>.</param>
/// <param name="Uid">Its uid, from <c>metadata.uid</c> — an owner reference needs both.</param>
public readonly record struct PodIdentity(string Name, string Uid) {
    /// <summary>Reads it from the environment a pod template sets.</summary>
    /// <param name="read">How to read a variable, or null for the process's own.</param>
    /// <returns>The identity, or null if the downward API was not wired.</returns>
    /// <remarks>
    ///     A pod does not know its own name unless it is told, so this expects the two lines of
    ///     downward API every Kubernetes deployment writes:
    ///     <code>
    ///     env:
    ///       - name: VIXEN_POD_NAME
    ///         valueFrom: { fieldRef: { fieldPath: metadata.name } }
    ///       - name: VIXEN_POD_UID
    ///         valueFrom: { fieldRef: { fieldPath: metadata.uid } }
    ///     </code>
    ///     Missing is not an error — it is an orchestrator running outside the cluster — but it does
    ///     mean the realm pods it creates outlive it.
    /// </remarks>
    public static PodIdentity? FromEnvironment(Func<string, string?>? read = null) {
        var lookup = read ?? Environment.GetEnvironmentVariable;
        var name = lookup("VIXEN_POD_NAME");
        var uid = lookup("VIXEN_POD_UID");

        return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uid) ? null : new PodIdentity(name, uid);
    }
}

/// <summary>Realms as pods. ADR-019's first backend.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A Pod, not a Deployment and not a StatefulSet.</b> Realms are not fungible replicas:
///         each has an identity, a map, a population, a version and a lifetime that ends when the
///         last player leaves. A Deployment's controller would restart a realm that exited on
///         purpose, and its rolling update is the wrong shape for doc 27 § Upgrades — draining a
///         realm means moving <em>players</em>, not terminating a pod when a readiness probe flips.
///         An owner reference on the orchestrator's own pod gives the one thing the controller was
///         wanted for, which is garbage collection.
///     </para>
///     <para>
///         ⚠ <b><c>hostPort</c>, not a <c>Service</c> per pod.</b> A Service per realm puts kube-proxy
///         and conntrack between the player and the simulation — the gateway problem in a different
///         hat, and the one doc 27 § The routing question spends a page rejecting. It also consumes a
///         cluster IP per shard. The cost is that the node port range is cluster configuration, which
///         M5 names as this design's one prerequisite.
///     </para>
/// </remarks>
public sealed class KubernetesPlacement : IRealmPlacement, IDisposable {
    /// <summary>What this backend calls itself in a <see cref="PlacementProbe" />.</summary>
    public const string BackendName = "kubernetes";

    readonly Dictionary<string, Running> running = new(StringComparer.Ordinal);
    readonly List<Channel<PlacementEvent>> watchers = [];
    readonly CancellationTokenSource lifetime = new();
    readonly Lock gate = new();
    readonly KubernetesPlacementOptions options;
    readonly IClusterApi cluster;
    readonly TimeProvider time;

    bool disposed;

    /// <summary>Stands a backend up.</summary>
    /// <param name="options">What to create and where.</param>
    /// <param name="cluster">The API server.</param>
    /// <param name="time">The clock, or null for the system's.</param>
    /// <exception cref="ArgumentNullException">Either required argument is null.</exception>
    public KubernetesPlacement(
        KubernetesPlacementOptions options,
        IClusterApi cluster,
        TimeProvider? time = null
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cluster);

        this.options = options;
        this.cluster = cluster;
        this.time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<PlacementProbe> ProbeAsync(CancellationToken cancellation) {
        string version;

        try {
            version = await cluster.VersionAsync(cancellation).ConfigureAwait(false);
        } catch (Exception failure) when (failure is HttpRequestException or IOException or InvalidOperationException) {
            return new(false, BackendName, failure.Message);
        }

        if (version.Length == 0) {
            return new(false, BackendName, "the API server did not say what it is");
        }

        return options.Image.Length == 0
            ? new(false, BackendName, $"Kubernetes {version}, but no image is configured")
            : new(true, BackendName, $"Kubernetes {version}, running {options.Image} in {options.Namespace}");
    }

    /// <inheritdoc />
    public async ValueTask<RealmInstance> StartAsync(RealmSpec spec, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(spec);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (options.Image.Length == 0) {
            throw new InvalidOperationException(
                "This backend has no image. Set KubernetesPlacementOptions.Image to the realm's image."
            );
        }

        if (!spec.IsValid) {
            throw new InvalidOperationException($"`{spec}` is not a runnable spec.");
        }

        if (spec.Endpoint.Port > 0) {
            throw new InvalidOperationException(
                $"`{spec.Endpoint}` names a port, and this backend allocates them: a hostPort has to come "
                + "from the range the cluster's firewall was opened for. Leave the endpoint unbound."
            );
        }

        if (!options.Ports.TryRent(out var port)) {
            throw new InvalidOperationException($"{options.Ports} — the node port range is exhausted.");
        }

        V1Pod created;

        try {
            created = await cluster
                .CreatePodAsync(Describe(spec, port), options.Namespace, cancellation)
                .ConfigureAwait(false);
        } catch {
            options.Ports.Return(port);

            throw;
        }

        var name = created.Metadata?.Name ?? "";

        // ⚠ The address is not known yet, and cannot be: the scheduler has not placed the pod, so
        // there is no node and therefore no external IP. It arrives with the Ready event, which is
        // the only moment the endpoint is both known and useful.
        var instance = new RealmInstance(new(name), spec.Shard, new("", port), BackendName, time.GetUtcNow());
        var record = new Running(instance, port);

        lock (gate) {
            running[name] = record;
        }

        Publish(new(PlacementEventKind.Started, instance.Id, instance.Shard, RealmEndpoint.None, "scheduled"));

        _ = FollowAsync(record);

        return instance;
    }

    /// <inheritdoc />
    public ValueTask StopAsync(RealmInstanceId instance, StopMode mode, CancellationToken cancellation) {
        ObjectDisposedException.ThrowIf(disposed, this);

        Running? record;

        lock (gate) {
            running.TryGetValue(instance.Value ?? "", out record);
        }

        if (record is null) {
            return ValueTask.CompletedTask;
        }

        record.Asked = true;

        _ = StopAsync(record, mode == StopMode.Drain ? options.DrainTimeout : TimeSpan.Zero);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RealmInstance>> ListAsync(CancellationToken cancellation) {
        var pods = await cluster
            .ListPodsAsync(
                options.Namespace,
                $"{KubernetesPlacementOptions.OwnerLabel}={options.Owner}",
                cancellation
            )
            .ConfigureAwait(false);

        var instances = new List<RealmInstance>(pods.Count);

        foreach (var pod in pods) {
            if (pod.Metadata?.Labels is not { } labels
                || !labels.TryGetValue(KubernetesPlacementOptions.ShardLabel, out var label)
                || !ShardId.TryParse(label, out var shard)) {
                continue;
            }

            instances.Add(
                new(
                    new(pod.Metadata.Name ?? ""),
                    shard,
                    await AddressAsync(pod, PortOf(pod), cancellation).ConfigureAwait(false),
                    BackendName,
                    time.GetUtcNow()
                )
            );
        }

        return instances;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PlacementEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellation
    ) {
        var channel = Channel.CreateUnbounded<PlacementEvent>(new() { SingleReader = true, SingleWriter = false });

        lock (gate) {
            watchers.Add(channel);
        }

        try {
            await foreach (var placement in channel.Reader.ReadAllAsync(cancellation).ConfigureAwait(false)) {
                yield return placement;
            }
        } finally {
            lock (gate) {
                watchers.Remove(channel);
            }
        }
    }

    /// <summary>Stops following pods. Does not delete them.</summary>
    /// <remarks>
    ///     Like the Docker backend and unlike the process one: a pod that outlives the orchestrator
    ///     which created it is a shard still serving players. What eventually collects it is the
    ///     owner reference, when the orchestrator's own pod is destroyed rather than merely
    ///     restarted.
    /// </remarks>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        lifetime.Cancel();

        List<Channel<PlacementEvent>> listening;

        lock (gate) {
            listening = [.. watchers];
            watchers.Clear();
            running.Clear();
        }

        foreach (var channel in listening) {
            channel.Writer.TryComplete();
        }

        lifetime.Dispose();
    }

    /// <summary>The pod a shard becomes.</summary>
    /// <param name="spec">What the realm is to be.</param>
    /// <param name="port">The <c>hostPort</c> it was given.</param>
    /// <returns>The pod, ready to create.</returns>
    /// <remarks>
    ///     Public because it is the thing an operator wants to see before trusting a fleet with a
    ///     cluster: <c>vixen live explain</c> printing this is a `kubectl apply --dry-run` somebody
    ///     can read.
    /// </remarks>
    public V1Pod Describe(RealmSpec spec, int port) {
        ArgumentNullException.ThrowIfNull(spec);

        // ⚠ The spec the pod is handed names a host it can bind, not the one a client is told, and
        // on this backend those are never the same. A pod cannot know its node's external address —
        // the scheduler has not placed it — and the realm binds every interface regardless. What a
        // client is told is computed when the Ready event fires and the node is known.
        //
        // The empty host this used to carry produced a spec `RealmSpec.TryDecode` refuses, so the
        // realm exited with "this process is not a realm" and the pod looked like a bad image.
        var bound = spec with {
            Endpoint = new(spec.Endpoint.Host is { Length: > 0 } named ? named : "0.0.0.0", port)
        };

        return new() {
            Metadata = new() {
                // A shard id is a guid and a pod name is a DNS label, so the id goes in a label and
                // the name is derived from it. Generated rather than chosen, because two shards of
                // one map must never collide and the API server is better at that than this is.
                Name = $"realm-{spec.Shard.Value:N}"[..Math.Min(63, 6 + 32)],
                NamespaceProperty = options.Namespace,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal) {
                    [KubernetesPlacementOptions.ShardLabel] = spec.Shard.Value.ToString("D"),
                    [KubernetesPlacementOptions.OwnerLabel] = options.Owner,
                    ["app.kubernetes.io/name"] = "vixen-realm",
                    ["app.kubernetes.io/managed-by"] = "vixen-live"
                },
                OwnerReferences = options.Self is { } self
                    ?
                    [
                        new V1OwnerReference {
                            ApiVersion = "v1",
                            Kind = "Pod",
                            Name = self.Name,
                            Uid = self.Uid,

                            // Not a controller reference: this pod is owned, not managed. A
                            // controller reference would tell other controllers to keep their hands
                            // off, which is true, and would also invite one to reconcile it, which
                            // is exactly what must not happen to a realm.
                            Controller = false,
                            BlockOwnerDeletion = false
                        }
                    ]
                    : null
            },
            Spec = new() {
                // ⚠ Never. A pod that restarted its realm would be a shard the cluster had already
                // written off, coming back with no players and no map — doc 27 § Health makes
                // recovery a placement rather than a resurrection.
                RestartPolicy = "Never",
                TerminationGracePeriodSeconds = (long)options.StopGrace.TotalSeconds,
                Containers =
                [
                    new() {
                        Name = "realm",
                        Image = options.Image,
                        Args = [.. options.Arguments, .. bound.ToCommandLine()],
                        Ports =
                        [
                            new V1ContainerPort {
                                ContainerPort = port,
                                HostPort = port,
                                Protocol = "UDP",
                                Name = "realm"
                            }
                        ],
                        Env =
                        [
                            .. options.Environment.Select(entry => new V1EnvVar {
                                    Name = entry.Key,
                                    Value = entry.Value
                                }
                            )
                        ]
                    }
                ]
            }
        };
    }

    async Task FollowAsync(Running record) {
        var name = record.Instance.Id.Value;

        try {
            await foreach (var line in cluster
                .FollowLogAsync(name, options.Namespace, lifetime.Token)
                .ConfigureAwait(false)) {
                options.Output?.Invoke(record.Instance.Id, line);

                if (record.Ready || !RealmSignals.TryReadReady(line, out _)) {
                    continue;
                }

                record.Ready = true;

                // ⚠ The realm's own idea of where it bound is inside the pod's network namespace, so
                // it is not the address a player can reach — which is the one difference from every
                // other backend. What a client is told is the node's external address and the
                // hostPort the scheduler honoured.
                var pod = await cluster.ReadPodAsync(name, options.Namespace, lifetime.Token).ConfigureAwait(false);
                var endpoint = pod is null
                    ? RealmEndpoint.None
                    : await AddressAsync(pod, record.Port, lifetime.Token).ConfigureAwait(false);

                record.Endpoint = endpoint;

                Publish(new(PlacementEventKind.Ready, record.Instance.Id, record.Instance.Shard, endpoint, "ready"));
            }
        } catch (Exception failure) when (failure is OperationCanceledException or IOException or HttpRequestException) {
            // The stream ended with the pod, or with this backend.
        }

        await FinishAsync(record).ConfigureAwait(false);
    }

    async Task FinishAsync(Running record) {
        lock (gate) {
            if (!running.Remove(record.Instance.Id.Value)) {
                return;
            }
        }

        options.Ports.Return(record.Port);

        V1Pod? pod = null;

        try {
            pod = await cluster
                .ReadPodAsync(record.Instance.Id.Value, options.Namespace, CancellationToken.None)
                .ConfigureAwait(false);
        } catch (Exception failure) when (failure is IOException or HttpRequestException or OperationCanceledException) {
            // The cluster is not answering, which is its own kind of lost.
        }

        var phase = pod?.Status?.Phase ?? "";

        // Succeeded is a realm whose last player left and which stopped; Failed is one that did not.
        // Asked covers the case where the orchestrator deleted it, which reads as neither.
        var expected = record.Asked || string.Equals(phase, "Succeeded", StringComparison.Ordinal);

        Publish(
            new(
                expected ? PlacementEventKind.Stopped : PlacementEventKind.Lost,
                record.Instance.Id,
                record.Instance.Shard,
                record.Endpoint,
                expected ? $"pod {phase}" : $"pod {(phase.Length == 0 ? "gone" : phase)} without being asked"
            )
        );
    }

    async Task StopAsync(Running record, TimeSpan patience) {
        var deadline = time.GetUtcNow() + patience;

        // Waiting for a drain is waiting for the realm to exit on its own, which the follow loop is
        // already watching for. Polling the clock rather than the API server keeps this to one call
        // at the end.
        while (patience > TimeSpan.Zero && time.GetUtcNow() < deadline) {
            lock (gate) {
                if (!running.ContainsKey(record.Instance.Id.Value)) {
                    return;
                }
            }

            try {
                await Task.Delay(TimeSpan.FromSeconds(1), time, lifetime.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            }
        }

        try {
            await cluster
                .DeletePodAsync(record.Instance.Id.Value, options.Namespace, options.StopGrace, lifetime.Token)
                .ConfigureAwait(false);
        } catch (Exception failure) when (failure is IOException or HttpRequestException or OperationCanceledException) {
            // The follow loop reports what actually happened to it.
        }
    }

    async Task<RealmEndpoint> AddressAsync(V1Pod pod, int port, CancellationToken cancellation) {
        var node = pod.Spec?.NodeName;

        if (!string.IsNullOrEmpty(node)) {
            var read = await cluster.ReadNodeAsync(node, cancellation).ConfigureAwait(false);

            // External first: a player is outside the cluster, and an internal address is one only
            // the cluster can reach. Falling back to it is right for a single-node development
            // cluster and useless in production, which is why the fallback is visible in the log
            // rather than silent — a shard nobody can reach reports its address either way.
            var addresses = read?.Status?.Addresses ?? [];

            var external = addresses.FirstOrDefault(entry => string.Equals(entry.Type, "ExternalIP", StringComparison.Ordinal))
                ?? addresses.FirstOrDefault(entry => string.Equals(entry.Type, "InternalIP", StringComparison.Ordinal));

            if (external?.Address is { Length: > 0 } address) {
                return new(address, port);
            }
        }

        return pod.Status?.HostIP is { Length: > 0 } host ? new(host, port) : RealmEndpoint.None;
    }

    static int PortOf(V1Pod pod) =>
        pod.Spec?.Containers?.FirstOrDefault()?.Ports?.FirstOrDefault()?.HostPort ?? 0;

    void Publish(PlacementEvent placement) {
        lock (gate) {
            foreach (var channel in watchers) {
                channel.Writer.TryWrite(placement);
            }
        }
    }

    sealed class Running(RealmInstance instance, int port) {
        public RealmInstance Instance { get; } = instance;

        public int Port { get; } = port;

        public RealmEndpoint Endpoint { get; set; } = RealmEndpoint.None;

        public volatile bool Asked;

        public volatile bool Ready;
    }
}
