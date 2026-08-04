// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Vixen.Live.Placement;

/// <summary>What <see cref="DockerPlacement" /> runs, and where.</summary>
public sealed record DockerPlacementOptions {
    /// <summary>The label a realm container carries its shard id under.</summary>
    /// <remarks>
    ///     ⚠ <b>How a restarted orchestrator finds the fleet it left running.</b> ADR-019 calls for
    ///     "one container per realm, labelled with the realm id" for exactly this: grain state says
    ///     which shards should exist, the daemon says which containers do, and a label is the join
    ///     between them.
    /// </remarks>
    public const string ShardLabel = "org.vixenengine.shard";

    /// <summary>The label a realm container carries its owner under.</summary>
    /// <remarks>
    ///     So that two orchestrators sharing one daemon — a developer's laptop running two games —
    ///     do not reconcile each other's containers into oblivion.
    /// </remarks>
    public const string OwnerLabel = "org.vixenengine.owner";

    /// <summary>The realm's image.</summary>
    public string Image { get; init; } = "";

    /// <summary>Which orchestrator these containers belong to.</summary>
    public string Owner { get; init; } = "vixen";

    /// <summary>Arguments to put before the encoded <see cref="RealmSpec" />.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Variables to set in every realm container.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The address clients are given when a spec did not name one.</summary>
    /// <remarks>
    ///     The Docker host's own address, and this backend cannot work it out: a container's view of
    ///     the world does not include what a player can reach. Loopback is right for a laptop and
    ///     wrong for anything else, which is why it is a setting rather than a detection.
    /// </remarks>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>The range realm ports are taken from.</summary>
    public PortPool Ports { get; init; } = new();

    /// <summary>How long <see cref="StopMode.Drain" /> waits before killing.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long the process gets after its stop signal.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether a stopped container is removed.</summary>
    /// <remarks>
    ///     On, because a fleet that spawns and retires shards all day would otherwise leave a
    ///     thousand exited containers behind by the evening. Off is what a developer wants when the
    ///     interesting thing is why a realm died — the logs go with the container.
    /// </remarks>
    public bool RemoveWhenStopped { get; init; } = true;

    /// <summary>Called with every line a realm writes, for whoever is doing the logging.</summary>
    public Action<RealmInstanceId, string>? Output { get; init; }
}

/// <summary>Realms as containers. ADR-019's second backend.</summary>
/// <remarks>
///     <para>
///         The same shape as <c>ProcessPlacement</c> — a port from a pool, the spec on the
///         command line, ready from the process's own stdout — with the daemon doing the starting and
///         the port publishing. What differs is worth stating:
///     </para>
///     <para>
///         ⚠ <b>There is no stdin, and there does not need to be.</b> <c>Placement.Process</c> writes
///         <c>vixen-realm drain</c> to a realm because doc 27 § Cost's L0 has no control plane to say
///         it over. A Docker deployment is an L1 deployment by construction: it has an orchestrator,
///         and a realm learns to drain from the reply to its own heartbeat. So
///         <see cref="StopMode.Drain" /> here is "wait for it to finish and kill it if it will not",
///         and the polite half happens a layer up.
///     </para>
///     <para>
///         ⚠ <b>A realm container never restarts itself.</b> <c>RestartPolicy: no</c>, because doc 27
///         § Health makes recovery a <em>placement</em> rather than a resurrection — a container the
///         daemon brought back would be a shard the cluster had already written off, returning with
///         no players and no map.
///     </para>
/// </remarks>
public sealed class DockerPlacement : IRealmPlacement, IDisposable {
    /// <summary>What this backend calls itself in a <see cref="PlacementProbe" />.</summary>
    public const string BackendName = "docker";

    readonly Dictionary<string, Running> running = new(StringComparer.Ordinal);
    readonly List<Channel<PlacementEvent>> watchers = [];
    readonly CancellationTokenSource lifetime = new();
    readonly Lock gate = new();
    readonly DockerPlacementOptions options;
    readonly DockerEngine engine;
    readonly bool ownsEngine;
    readonly TimeProvider time;

    bool disposed;

    /// <summary>Stands a backend up on the platform's default endpoint.</summary>
    /// <param name="options">What to run and where.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options" /> is null.</exception>
    public DockerPlacement(DockerPlacementOptions options) : this(options, new DockerEngine(), ownsEngine: true) { }

    /// <summary>Stands a backend up on an engine the caller built.</summary>
    /// <param name="options">What to run and where.</param>
    /// <param name="engine">The Engine API client.</param>
    /// <param name="ownsEngine">Whether disposing this disposes it.</param>
    /// <param name="time">The clock, or null for the system's.</param>
    /// <exception cref="ArgumentNullException">Either required argument is null.</exception>
    public DockerPlacement(
        DockerPlacementOptions options,
        DockerEngine engine,
        bool ownsEngine = false,
        TimeProvider? time = null
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engine);

        this.options = options;
        this.engine = engine;
        this.ownsEngine = ownsEngine;
        this.time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<PlacementProbe> ProbeAsync(CancellationToken cancellation) {
        var ping = await engine.PingAsync(cancellation).ConfigureAwait(false);

        if (!ping.Reachable) {
            return new(false, BackendName, ping.Detail);
        }

        return options.Image.Length == 0
            ? new(false, BackendName, $"{ping.Detail}, but no image is configured")
            : new(true, BackendName, $"{ping.Detail}, running {options.Image}");
    }

    /// <inheritdoc />
    public async ValueTask<RealmInstance> StartAsync(RealmSpec spec, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(spec);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (options.Image.Length == 0) {
            throw new InvalidOperationException(
                "This backend has no image. Set DockerPlacementOptions.Image to the realm's image."
            );
        }

        if (!spec.IsValid) {
            throw new InvalidOperationException($"`{spec}` is not a runnable spec.");
        }

        var (bound, rented) = Bind(spec.Endpoint);
        var started = spec with { Endpoint = bound };

        string id;

        try {
            id = await engine.CreateAsync(
                    new(
                        options.Image,
                        [.. options.Arguments, .. started.ToCommandLine()],
                        new Dictionary<string, string>(StringComparer.Ordinal) {
                            [DockerPlacementOptions.ShardLabel] = spec.Shard.Value.ToString("D"),
                            [DockerPlacementOptions.OwnerLabel] = options.Owner
                        },
                        options.Environment,
                        bound.Port
                    ),
                    cancellation
                )
                .ConfigureAwait(false);

            await engine.StartAsync(id, cancellation).ConfigureAwait(false);
        } catch {
            // The port goes back before the exception leaves — the machine where starts fail is the
            // one where somebody is watching, and a launcher that leaked a port per failure would
            // exhaust its range in front of them.
            if (rented) {
                options.Ports.Return(bound.Port);
            }

            throw;
        }

        var instance = new RealmInstance(new(id), spec.Shard, bound, BackendName, time.GetUtcNow());
        var record = new Running(instance, rented);

        lock (gate) {
            running[id] = record;
        }

        Publish(new(PlacementEventKind.Started, instance.Id, instance.Shard, bound, "created"));

        _ = FollowAsync(record);
        _ = WatchExitAsync(record);

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

        // Drain waits — the orchestrator is what moves the players, and doc 27 § Drain's hard
        // deadline is fifteen minutes because a raid finishing is what draining politely means.
        _ = StopAsync(record, mode == StopMode.Drain ? options.DrainTimeout : TimeSpan.Zero);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<RealmInstance>> ListAsync(CancellationToken cancellation) {
        // The daemon rather than the dictionary, because the whole use of this call is finding what
        // an orchestrator left behind when it restarted.
        var containers = await engine
            .ListAsync($"{DockerPlacementOptions.OwnerLabel}={options.Owner}", cancellation)
            .ConfigureAwait(false);

        var instances = new List<RealmInstance>(containers.Count);

        foreach (var container in containers) {
            if (!container.Labels.TryGetValue(DockerPlacementOptions.ShardLabel, out var label)
                || !ShardId.TryParse(label, out var shard)) {
                continue;
            }

            RealmEndpoint endpoint;

            lock (gate) {
                endpoint = running.TryGetValue(container.Id, out var known)
                    ? known.Instance.Endpoint
                    : RealmEndpoint.None;
            }

            instances.Add(new(new(container.Id), shard, endpoint, BackendName, time.GetUtcNow()));
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

    /// <summary>Stops following containers. Does not stop the containers.</summary>
    /// <remarks>
    ///     ⚠ <b>The opposite of <c>ProcessPlacement.Dispose</c>, and deliberately.</b> A child process
    ///     that outlived its launcher is an orphan holding a UDP port; a container that outlives the
    ///     orchestrator which created it is a shard that is still serving players, and the daemon will
    ///     still be there when the orchestrator comes back. That is what the labels are for.
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

        if (ownsEngine) {
            engine.Dispose();
        }
    }

    (RealmEndpoint Endpoint, bool Rented) Bind(RealmEndpoint requested) {
        var host = requested.Host is { Length: > 0 } named ? named : options.Host;

        if (requested.Port > 0) {
            return (new(host, requested.Port), false);
        }

        if (!options.Ports.TryRent(out var port)) {
            throw new InvalidOperationException($"{options.Ports} — the range is exhausted.");
        }

        return (new(host, port), true);
    }

    async Task FollowAsync(Running record) {
        try {
            await foreach (var line in engine
                .FollowAsync(record.Instance.Id.Value, lifetime.Token)
                .ConfigureAwait(false)) {
                options.Output?.Invoke(record.Instance.Id, line);

                if (!record.Ready && RealmSignals.TryReadReady(line, out var endpoint)) {
                    record.Ready = true;

                    Publish(
                        new(PlacementEventKind.Ready, record.Instance.Id, record.Instance.Shard, endpoint, "ready")
                    );
                }
            }
        } catch (Exception failure) when (failure is OperationCanceledException or IOException or HttpRequestException) {
            // The stream ended with the container, or with this backend. Neither is news: the exit
            // is reported by WatchExitAsync, which is the thing that knows why.
        }
    }

    async Task WatchExitAsync(Running record) {
        int code;

        try {
            code = await engine.WaitAsync(record.Instance.Id.Value, lifetime.Token).ConfigureAwait(false);
        } catch (Exception failure) when (failure is OperationCanceledException or IOException or HttpRequestException) {
            return;
        }

        lock (gate) {
            if (!running.Remove(record.Instance.Id.Value)) {
                return;
            }
        }

        if (record.Rented) {
            options.Ports.Return(record.Instance.Endpoint.Port);
        }

        var expected = record.Asked || code == 0;

        Publish(
            new(
                expected ? PlacementEventKind.Stopped : PlacementEventKind.Lost,
                record.Instance.Id,
                record.Instance.Shard,
                record.Instance.Endpoint,
                expected ? $"exited with {code}" : $"exited with {code} without being asked"
            )
        );

        if (options.RemoveWhenStopped) {
            try {
                await engine.RemoveAsync(record.Instance.Id.Value, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception failure) when (failure is InvalidOperationException or IOException or HttpRequestException) {
                // A container that could not be removed is a container an operator will find. It is
                // not worth failing anything over, and the event above has already been published.
            }
        }
    }

    async Task StopAsync(Running record, TimeSpan patience) {
        try {
            if (patience > TimeSpan.Zero) {
                await engine.WaitAsync(record.Instance.Id.Value, lifetime.Token)
                    .WaitAsync(patience, time)
                    .ConfigureAwait(false);

                return;
            }
        } catch (TimeoutException) {
            // It would not go. At this point the launcher is the only thing left that can end it.
        } catch (Exception failure) when (failure is OperationCanceledException or IOException or HttpRequestException) {
            return;
        }

        try {
            await engine.StopAsync(record.Instance.Id.Value, options.StopGrace, lifetime.Token).ConfigureAwait(false);
        } catch (Exception failure) when (failure is InvalidOperationException or IOException or HttpRequestException or OperationCanceledException) {
            // Already gone, or the daemon went away. WatchExitAsync reports whichever it was.
        }
    }

    void Publish(PlacementEvent placement) {
        lock (gate) {
            foreach (var channel in watchers) {
                channel.Writer.TryWrite(placement);
            }
        }
    }

    sealed class Running(RealmInstance instance, bool rented) {
        public RealmInstance Instance { get; } = instance;

        public bool Rented { get; } = rented;

        public volatile bool Asked;

        public volatile bool Ready;
    }
}
